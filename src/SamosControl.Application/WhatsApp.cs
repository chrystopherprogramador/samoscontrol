using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public class WhatsAppService
{
    private readonly AppDbContext _db;
    private readonly IEvolutionApiIntegration _evo;
    private readonly SettingsService _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WhatsAppService> _log;
    private readonly IEvolutionInstances _inst;

    public WhatsAppService(AppDbContext db, IEvolutionApiIntegration evo, IEvolutionInstances inst, SettingsService settings, IServiceScopeFactory scopes, ILogger<WhatsAppService> log)
    { _db = db; _evo = evo; _inst = inst; _settings = settings; _scopes = scopes; _log = log; }

    public bool IsConfigured => _evo.IsConfigured;
    public bool ServerConfigured => _inst.ServerConfigured;

    public async Task<WhatsAppConversation> GetOrCreateConversationAsync(string phone, string? name = null, int? sellerId = null)
    {
        var p = PhoneUtil.Normalize(phone) ?? throw new ArgumentException("Telefone inválido.");
        var key = PhoneUtil.Key(p);
        var last8 = p[^8..];
        var candidates = await _db.WhatsAppConversations.Where(c => c.SellerId == sellerId && c.Phone.EndsWith(last8)).ToListAsync();
        var conv = candidates.FirstOrDefault(c => PhoneUtil.Key(c.Phone) == key);
        if (conv == null)
        {
            conv = new WhatsAppConversation { Phone = p, ContactName = name, SellerId = sellerId };
            conv.LeadId = (await _db.Leads.Where(l => l.PhoneKey == key).Select(l => (int?)l.Id).FirstOrDefaultAsync());
            var customers = await _db.Customers.Where(c => c.Phone != null || c.WhatsApp != null).Select(c => new { c.Id, c.Name, c.Phone, c.WhatsApp }).ToListAsync();
            var cust = customers.FirstOrDefault(c => PhoneUtil.Key(c.WhatsApp) == key || PhoneUtil.Key(c.Phone) == key);
            conv.CustomerId = cust?.Id;
            conv.ContactName ??= cust?.Name;
            if (conv.ContactName == null && conv.LeadId != null) conv.ContactName = await _db.Leads.Where(l => l.Id == conv.LeadId).Select(l => l.Name).FirstOrDefaultAsync();
            _db.WhatsAppConversations.Add(conv);
            await _db.SaveChangesAsync();
        }
        else if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(conv.ContactName)) conv.ContactName = name;
        return conv;
    }

    public async Task<WhatsAppMessage> SendTextAsync(string phone, string text, int? userId = null, bool fromAgent = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Mensagem vazia.");
        var conv = await GetOrCreateConversationAsync(phone);
        var extId = await _evo.SendTextAsync(conv.Phone, text, ct);
        return await StoreOutAsync(conv, extId, text, "text", userId, fromAgent);
    }

    public async Task<WhatsAppMessage> SendDocumentAsync(string phone, byte[] content, string fileName, string caption, int? userId = null, CancellationToken ct = default)
    {
        var conv = await GetOrCreateConversationAsync(phone);
        var extId = await _evo.SendDocumentAsync(conv.Phone, content, fileName, "application/pdf", caption, ct);
        return await StoreOutAsync(conv, extId, $"[documento] {fileName}" + (string.IsNullOrEmpty(caption) ? "" : $"\n{caption}"), "document", userId, false);
    }

    private async Task<WhatsAppMessage> StoreOutAsync(WhatsAppConversation conv, string? extId, string body, string type, int? userId, bool fromAgent)
    {
        var m = new WhatsAppMessage { ConversationId = conv.Id, ExternalId = extId, Direction = MessageDirection.Out, Body = body, Type = type, Status = "ENVIADA", SentByUserId = userId, FromAgent = fromAgent };
        _db.WhatsAppMessages.Add(m);
        conv.LastMessageAt = m.Timestamp;
        conv.LastMessagePreview = Preview(body, true);
        if (conv.LeadId.HasValue) _db.Interactions.Add(new Interaction { LeadId = conv.LeadId, CustomerId = conv.CustomerId, Type = InteractionType.WhatsApp, Description = "WhatsApp enviado: " + body.Truncate(300), UserId = userId });
        await _db.SaveChangesAsync();
        return m;
    }

    public async Task<WebhookResult> HandleWebhookAsync(string body, string? providedToken)
    {
        var expected = _settings.Get(SettingKeys.EvolutionWebhookToken);
        if (string.IsNullOrEmpty(expected)) return new(503, "Webhook da Evolution não configurado (EVOLUTION_WEBHOOK_TOKEN).");
        if (!WebhookUtil.TokenMatches(expected, providedToken)) return new(401, "Token inválido.");

        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { return new(400, "JSON inválido."); }
        if (root is null) return new(400, "Payload inválido.");

        var instance = _settings.Get(SettingKeys.EvolutionInstance);
        var payloadInstance = (root["instance"] as JsonValue)?.ToString();
        var evName = EvolutionWebhookParser.EventName(root);

        Seller? seller = null;
        var isMain = string.IsNullOrEmpty(payloadInstance)
            || string.Equals(instance, payloadInstance, StringComparison.OrdinalIgnoreCase);
        if (!isMain)
        {
            var lower = payloadInstance!.ToLowerInvariant();
            seller = await _db.Sellers.FirstOrDefaultAsync(s => s.WaInstance != null && s.WaInstance.ToLower() == lower);
            if (seller is null && !string.IsNullOrEmpty(instance)) return new(200, "Instância ignorada.");
        }
        int? sellerId = seller?.Id;
        var scope = seller is null ? "" : $"{seller.WaInstance}:";

        if (seller != null && evName is "CONNECTION_UPDATE")
        {
            var state = (root["data"]?["state"] as JsonValue)?.ToString();
            if (!string.IsNullOrEmpty(state))
            {
                ApplyState(seller, state, null, null);
                await _db.SaveChangesAsync();
            }
            return new(200, "conexão atualizada");
        }

        int processed = 0, duplicates = 0;
        if (evName is "MESSAGES_UPSERT" or "SEND_MESSAGE")
        {
            foreach (var m in EvolutionWebhookParser.ParseMessages(root))
            {
                var ev = await WebhookUtil.RegisterAsync(_db, "evolution", $"{scope}{evName}:{m.ExternalId}", evName, body);
                if (ev is null) { duplicates++; continue; }
                try
                {
                    await StoreIncomingAsync(m, sellerId);
                    ev.Status = WebhookStatus.Processed; ev.ProcessedAt = DateTime.UtcNow;
                    processed++;
                }
                catch (Exception ex)
                {
                    _db.ChangeTracker.Clear();
                    _log.LogError(ex, "Falha ao processar mensagem do webhook Evolution");
                    var e2 = await _db.WebhookEvents.FirstAsync(w => w.Provider == "evolution" && w.ExternalId == $"{scope}{evName}:{m.ExternalId}");
                    e2.Status = WebhookStatus.Failed; e2.Error = ex.Message.Truncate(500);
                }
                await _db.SaveChangesAsync();
            }
        }
        else if (evName == "MESSAGES_UPDATE")
        {
            foreach (var st in EvolutionWebhookParser.ParseStatus(root))
            {
                var ev = await WebhookUtil.RegisterAsync(_db,                 "evolution", $"{scope}{evName}:{st.ExternalId}:{st.Status}", evName, body);
                if (ev is null) { duplicates++; continue; }
                                var msg = await _db.WhatsAppMessages.FirstOrDefaultAsync(x => x.ExternalId == st.ExternalId && x.Conversation!.SellerId == sellerId);
                if (msg != null) msg.Status = st.Status.ToUpperInvariant();
                ev.Status = msg != null ? WebhookStatus.Processed : WebhookStatus.Ignored; ev.ProcessedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                processed++;
            }
        }
        else
        {
            var ev = await WebhookUtil.RegisterAsync(_db, "evolution", "sha:" + ContractService.Sha256(body), evName.Length == 0 ? "unknown" : evName, body);
            if (ev != null) await Ignore(ev);
        }
        return new(200, $"processados={processed}; duplicados={duplicates}");
    }

    private async Task Ignore(WebhookEvent e) { e.Status = WebhookStatus.Ignored; await _db.SaveChangesAsync(); }

    private async Task StoreIncomingAsync(EvolutionIncoming m, int? sellerId = null)
    {
        if (await _db.WhatsAppMessages.AnyAsync(x => x.ExternalId == m.ExternalId && x.Conversation!.SellerId == sellerId)) return; // already stored when sent by us
        var conv = await GetOrCreateConversationAsync(m.Phone, m.FromMe || string.IsNullOrWhiteSpace(m.PushName) ? null : m.PushName, sellerId);
        _db.WhatsAppMessages.Add(new WhatsAppMessage
        {
            ConversationId = conv.Id, ExternalId = m.ExternalId, Direction = m.FromMe ? MessageDirection.Out : MessageDirection.In,
            Type = m.Type, Body = m.Text ?? $"[{m.Type}]", Status = m.FromMe ? "ENVIADA" : "RECEBIDA", Timestamp = m.Timestamp
        });
        conv.LastMessageAt = m.Timestamp;
        conv.LastMessagePreview = Preview(m.Text ?? $"[{m.Type}]", m.FromMe);
        if (!m.FromMe)
        {
            conv.UnreadCount++;
            if (conv.LeadId.HasValue)
            {
                _db.Interactions.Add(new Interaction { LeadId = conv.LeadId, CustomerId = conv.CustomerId, Type = InteractionType.WhatsApp, Description = "WhatsApp recebido: " + (m.Text ?? m.Type).Truncate(300) });
                var lead = await _db.Leads.FindAsync(conv.LeadId.Value);
                if (lead != null) lead.LastInteractionAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();

        if (sellerId is null && !m.FromMe && !string.IsNullOrWhiteSpace(m.Text)) _ = Task.Run(() => DispatchToAgentAsync(m.Phone, m.Text!));
    }

    /// <summary>Only registered, active users (matched by phone) with ai.use can talk to the agent.</summary>
    public async Task DispatchToAgentAsync(string phone, string text)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var key = PhoneUtil.Key(phone);
            var users = await db.Users.Where(u => u.IsActive && u.Phone != null).ToListAsync();
            var user = users.FirstOrDefault(u => PhoneUtil.Key(u.Phone) == key);
            if (user is null) return;
            var perms = scope.ServiceProvider.GetRequiredService<PermissionService>();
            if (!await perms.HasAsync(user.Id, Perm.AiUse)) return;
            var agent = scope.ServiceProvider.GetRequiredService<SamosAgent>();
            var reply = await agent.HandleAsync(text, new AgentContext(user.Id, user.Name, "WHATSAPP", PhoneUtil.Normalize(phone)));
            var wa = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
            await wa.SendTextAsync(phone, reply, user.Id, fromAgent: true);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao responder via agente no WhatsApp"); }
    }

    private static string Preview(string body, bool outgoing)
    {
        var t = body.Replace('\n', ' ').Trim();
        return (outgoing ? "Você: " : "") + (t.Length > 150 ? t[..150] : t);
    }

    // ---------- WhatsApp próprio de cada vendedor (instância Evolution dedicada) ----------

    public static string MapState(string? state, string previous) => (state ?? "").ToLowerInvariant() switch
    {
        "open" => "CONECTADO",
        "connecting" => "CONECTANDO",
        // Antes de escanear o QR a Evolution reporta "close": mantém CONECTANDO.
        _ => previous == "CONECTANDO" ? "CONECTANDO" : "DESCONECTADO"
    };

    private static void ApplyState(Seller s, string? state, string? number, string? profile)
    {
        var next = MapState(state, s.WaStatus);
        if (next == "CONECTADO" && s.WaStatus != "CONECTADO") s.WaConnectedAt = DateTime.UtcNow;
        s.WaStatus = next;
        if (next == "CONECTADO")
        {
            if (!string.IsNullOrEmpty(number)) s.WaNumber = number;
            if (!string.IsNullOrEmpty(profile)) s.WaProfileName = profile;
        }
    }

    private async Task<string> EnsureWebhookTokenAsync()
    {
        var t = _settings.Get(SettingKeys.EvolutionWebhookToken);
        if (!string.IsNullOrWhiteSpace(t)) return t;
        t = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await _settings.SetAsync(_db, SettingKeys.EvolutionWebhookToken, t);
        return t;
    }

    private async Task<Seller> LoadSellerAsync(int sellerId) =>
        await _db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId) ?? throw new KeyNotFoundException("Vendedor não encontrado.");

    /// <summary>Cria a instância (se preciso), configura o webhook e devolve o QR code.</summary>
    public async Task<EvolutionConnectResult> ConnectSellerAsync(int sellerId, string appUrl, CancellationToken ct = default)
    {
        if (!_inst.ServerConfigured) throw new NotConfiguredException("Evolution API (URL e API key em Configurações).");
        var s = await LoadSellerAsync(sellerId);
        var token = await EnsureWebhookTokenAsync();
        var url = appUrl.TrimEnd('/') + "/api/webhooks/evolution";
        s.WaInstance ??= $"samos_seller_{s.Id}";
        var res = await _inst.CreateInstanceAsync(s.WaInstance, url, token, ct);
        if (res.State == "open") ApplyState(s, "open", null, null);
        else s.WaStatus = "CONECTANDO";
        await _db.SaveChangesAsync(ct);
        return res;
    }

    /// <summary>Novo QR code para uma instância já criada.</summary>
    public async Task<EvolutionConnectResult> RefreshQrAsync(int sellerId, CancellationToken ct = default)
    {
        var s = await LoadSellerAsync(sellerId);
        if (s.WaInstance is null) throw new InvalidOperationException("Instância ainda não criada.");
        var res = await _inst.ConnectAsync(s.WaInstance, ct);
        if (res.State == "open") { ApplyState(s, "open", null, null); await _db.SaveChangesAsync(ct); }
        return res;
    }

    public async Task<Seller> RefreshSellerStatusAsync(int sellerId, CancellationToken ct = default)
    {
        var s = await LoadSellerAsync(sellerId);
        if (s.WaInstance is null || !_inst.ServerConfigured) return s;
        var info = await _inst.GetInfoAsync(s.WaInstance, ct);
        ApplyState(s, info.State, info.Number, info.ProfileName);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    public async Task ReconfigureWebhookAsync(int sellerId, string appUrl, CancellationToken ct = default)
    {
        var s = await LoadSellerAsync(sellerId);
        if (s.WaInstance is null) throw new InvalidOperationException("Instância ainda não criada.");
        await _inst.SetWebhookAsync(s.WaInstance, appUrl.TrimEnd('/') + "/api/webhooks/evolution", await EnsureWebhookTokenAsync(), ct);
    }

    public async Task DisconnectSellerAsync(int sellerId, bool delete, CancellationToken ct = default)
    {
        var s = await LoadSellerAsync(sellerId);
        if (s.WaInstance is not null)
        {
            if (delete) { await _inst.DeleteAsync(s.WaInstance, ct); s.WaInstance = null; s.WaNumber = null; s.WaProfileName = null; }
            else await _inst.LogoutAsync(s.WaInstance, ct);
        }
        s.WaStatus = "DESCONECTADO";
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(Seller, WhatsAppConversation)> SellerConvAsync(int sellerId, string phone)
    {
        var s = await LoadSellerAsync(sellerId);
        if (s.WaInstance is null || s.WaStatus != "CONECTADO") throw new InvalidOperationException("O WhatsApp deste vendedor não está conectado.");
        return (s, await GetOrCreateConversationAsync(phone, null, sellerId));
    }

    public async Task<WhatsAppMessage> SendSellerTextAsync(int sellerId, string phone, string text, int? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Mensagem vazia.");
        var (s, conv) = await SellerConvAsync(sellerId, phone);
        var extId = await _inst.SendTextAsync(s.WaInstance!, conv.Phone, text, ct);
        return await StoreOutAsync(conv, extId, text, "text", userId, false);
    }

    public async Task<WhatsAppMessage> SendSellerMediaAsync(int sellerId, string phone, byte[] content, string fileName, string mimeType, string? caption, int? userId, CancellationToken ct = default)
    {
        var (s, conv) = await SellerConvAsync(sellerId, phone);
        var extId = await _inst.SendMediaAsync(s.WaInstance!, conv.Phone, content, fileName, mimeType, caption, ct);
        var kind = mimeType.StartsWith("image/") ? "image" : mimeType.StartsWith("video/") ? "video" : "document";
        var label = kind == "image" ? "📷 Imagem" : kind == "video" ? "🎬 Vídeo" : "📄 " + fileName;
        return await StoreOutAsync(conv, extId, label + (string.IsNullOrEmpty(caption) ? "" : "\n" + caption), kind, userId, false);
    }

    // ---------- Posse de lead e unificação de conversas ----------

    /// <summary>Junta conversas do mesmo número (ex.: com e sem o 9º dígito) dentro do mesmo canal.</summary>
    public async Task MergeDuplicateConversationsAsync()
    {
        var all = await _db.WhatsAppConversations.ToListAsync();
        foreach (var g in all.GroupBy(c => (c.SellerId, Key: PhoneUtil.Key(c.Phone))).Where(g => g.Count() > 1))
        {
            var ordered = g.OrderBy(c => c.Id).ToList();
            var main = ordered[0];
            var best = ordered.Select(c => c.Phone).OrderByDescending(x => x.Length).First();
            var existing = (await _db.WhatsAppMessages.Where(m => m.ConversationId == main.Id && m.ExternalId != null).Select(m => m.ExternalId!).ToListAsync()).ToHashSet();
            foreach (var d in ordered.Skip(1))
            {
                foreach (var m in await _db.WhatsAppMessages.Where(m => m.ConversationId == d.Id).ToListAsync())
                {
                    if (m.ExternalId != null && !existing.Add(m.ExternalId)) _db.WhatsAppMessages.Remove(m);
                    else m.ConversationId = main.Id;
                }
                main.UnreadCount += d.UnreadCount;
                main.ContactName ??= d.ContactName;
                main.LeadId ??= d.LeadId; main.CustomerId ??= d.CustomerId;
                if (d.LastMessageAt > main.LastMessageAt) { main.LastMessageAt = d.LastMessageAt; main.LastMessagePreview = d.LastMessagePreview; }
                _db.WhatsAppConversations.Remove(d);
            }
            await _db.SaveChangesAsync();
            main.Phone = best;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Garante que o vendedor pode abordar o lead. Admin pode reatribuir; outro vendedor não.</summary>
    public async Task ClaimLeadAsync(int leadId, int sellerId, bool isAdmin)
    {
        var lead = await _db.Leads.FindAsync(leadId) ?? throw new KeyNotFoundException("Lead não encontrado.");
        if (lead.SellerId is null || lead.SellerId == sellerId || isAdmin)
        {
            if (lead.SellerId != sellerId)
            {
                lead.SellerId = sellerId;
                _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = "Lead atribuído ao vendedor #" + sellerId });
                await _db.SaveChangesAsync();
            }
            return;
        }
        var owner = await _db.Sellers.Where(s => s.Id == lead.SellerId).Select(s => s.Name).FirstOrDefaultAsync();
        throw new InvalidOperationException($"Este lead já está sendo atendido por {owner ?? "outro vendedor"}. Peça autorização ao administrador.");
    }

    /// <summary>Verifica a posse do lead a partir do telefone (quando ele existe no CRM).</summary>
    public async Task ClaimByPhoneAsync(string phone, int sellerId, bool isAdmin)
    {
        var key = PhoneUtil.Key(phone);
        var lead = await _db.Leads.Where(l => l.PhoneKey == key).FirstOrDefaultAsync();
        if (lead != null) await ClaimLeadAsync(lead.Id, sellerId, isAdmin);
    }

    public async Task RequestLeadAsync(int leadId, int sellerId, IServiceProvider sp)
    {
        var lead = await _db.Leads.FindAsync(leadId) ?? throw new KeyNotFoundException("Lead não encontrado.");
        var seller = await LoadSellerAsync(sellerId);
        var n = sp.GetRequiredService<NotificationService>();
        await n.CreateAsync(NotificationType.Warning, NotificationSource.Crm, "Pedido de liberação de lead",
            $"{seller.Name} quer atender o lead \"{lead.Name}\", hoje com outro vendedor. Abra o lead e use \"Atribuir a\" para autorizar.", $"/Leads/Details/{leadId}");
    }

    public async Task SendChargeLinkAsync(int chargeId, int? userId)
    {
        var ch = await _db.Charges.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == chargeId) ?? throw new KeyNotFoundException();
        var phone = ch.Customer?.WhatsApp ?? ch.Customer?.Phone ?? throw new InvalidOperationException("Cliente sem WhatsApp/telefone.");
        var text = $"Olá, {ch.Customer!.Name}! Segue sua cobrança de {ch.Amount:C2} com vencimento em {ch.DueDate:dd/MM/yyyy}.\n" +
                   (ch.InvoiceUrl != null ? $"Link: {ch.InvoiceUrl}\n" : "") +
                   (ch.PixPayload != null ? $"\nPIX copia e cola:\n{ch.PixPayload}" : "");
        await SendTextAsync(phone, text, userId);
    }
}
