using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

/// <summary>Knowledge base the AI is allowed to use. The AI asks the owner for what it needs, so it never has to guess.</summary>
public class KnowledgeService
{
    public static readonly (string Topic, string Question)[] Seed =
    {
        ("Empresa", "Em uma frase: o que a Samos vende e para quem?"),
        ("Produto", "Quais são os planos ou pacotes e o que cada um inclui?"),
        ("Preço", "Qual o valor da implantação e da mensalidade de cada plano?"),
        ("Prazo", "Em quanto tempo o cliente começa a usar depois de fechar o contrato?"),
        ("Contrato", "Existe fidelidade ou multa de cancelamento? Qual o prazo mínimo do contrato?"),
        ("Pagamento", "Quais formas de pagamento são aceitas e quando acontece a primeira cobrança?"),
        ("Diferenciais", "Quais são os 3 principais diferenciais em relação aos concorrentes?"),
        ("Cliente ideal", "Quais segmentos de empresa são o cliente ideal? Para quem o produto NÃO serve?"),
        ("Provas", "Quais resultados ou cases reais posso citar? (somente o que é verdadeiro e autorizado)"),
        ("Suporte", "Como funciona o suporte: canais, horário de atendimento e prazo de resposta?"),
        ("Integrações", "O produto se integra com quais ferramentas (WhatsApp, ERP, planilhas, etc.)?"),
        ("Demonstração", "Existe demonstração ou período de teste? Como o lead agenda?"),
        ("Objeções", "O que responder quando o lead diz que está caro?"),
        ("Objeções", "O que responder quando o lead diz que já tem um fornecedor/sistema?"),
        ("Objeções", "O que responder quando o lead diz \"vou pensar\" ou \"depois eu vejo\"?"),
        ("Limites", "O que a IA NUNCA deve prometer, afirmar ou comentar?"),
        ("Tom", "Como a IA deve se apresentar e falar? (formal ou informal, uso de emojis, nome a usar)"),
        ("Transferência", "Em quais situações a IA deve chamar um vendedor humano imediatamente?"),
        ("Contratação", "Quais dados e documentos o cliente precisa fornecer para contratar?"),
        ("Segurança", "Como os dados do cliente são protegidos? Estamos adequados à LGPD?"),
    };

    private readonly AppDbContext _db;
    private readonly IAiProvider _ai;
    public KnowledgeService(AppDbContext db, IAiProvider ai) { _db = db; _ai = ai; }
    public bool AiConfigured => _ai.IsConfigured;

    public async Task EnsureSeedAsync()
    {
        if (await _db.AiKnowledge.AnyAsync()) return;
        foreach (var (t, q) in Seed) _db.AiKnowledge.Add(new AiKnowledge { Topic = t, Question = q });
        await _db.SaveChangesAsync();
    }

    public async Task<List<AiKnowledge>> RelevantAsync(string? query, int max = 14)
    {
        var all = await _db.AiKnowledge.AsNoTracking().Where(k => k.Status == KnowledgeStatus.Respondida && k.Answer != null).ToListAsync();
        if (all.Count <= max * 2 || string.IsNullOrWhiteSpace(query)) return all;
        var words = MapService.Norm(query).Split(' ').Where(w => w.Length > 3).ToHashSet();
        return all.OrderByDescending(k =>
            {
                var text = MapService.Norm(k.Topic + " " + k.Question + " " + k.Answer);
                return words.Count(w => text.Contains(w)) + (k.Topic is "Limites" or "Tom" or "Empresa" ? 5 : 0);
            }).Take(max).ToList();
    }

    public static string AsPrompt(IEnumerable<AiKnowledge> items)
    {
        var sb = new StringBuilder();
        foreach (var k in items) sb.Append("[").Append(k.Topic).Append("] P: ").Append(k.Question).Append("\nR: ").Append(k.Answer).Append("\n\n");
        return sb.Length == 0 ? "(vazia — nenhuma informação foi ensinada ainda)" : sb.ToString();
    }

    /// <summary>The lead asked something the AI could not answer: queue a precise question for the owner.</summary>
    public async Task RegisterGapAsync(string question, int? leadId)
    {
        question = question.Trim();
        if (question.Length < 5) return;
        if (question.Length > 600) question = question[..600];
        var n = MapService.Norm(question);
        var pending = await _db.AiKnowledge.Where(k => k.Status == KnowledgeStatus.Pendente).Select(k => k.Question).ToListAsync();
        if (pending.Any(p => MapService.Norm(p) == n)) return;
        _db.AiKnowledge.Add(new AiKnowledge { Topic = "Lacuna", Question = question, Origin = "Lacuna", LeadId = leadId });
        await _db.SaveChangesAsync();
    }

    /// <summary>Asks the LLM for new, specific questions based on what is already known.</summary>
    public async Task<int> GenerateQuestionsAsync(CancellationToken ct = default)
    {
        if (!_ai.IsConfigured) return 0;
        var rows = await _db.AiKnowledge.AsNoTracking().OrderBy(k => k.Id).ToListAsync(ct);
        var known = new StringBuilder();
        foreach (var k in rows) known.Append(k.Status == KnowledgeStatus.Respondida ? $"[RESPONDIDA] {k.Question} => {k.Answer}\n" : k.Status == KnowledgeStatus.Pendente ? $"[PENDENTE] {k.Question}\n" : "");
        var sys = "Você treina uma IA SDR (primeiro contato comercial por WhatsApp) da empresa Samos. Gere até 8 NOVAS perguntas para o dono responder, para que a IA nunca precise inventar nada. " +
                  "Regras: perguntas objetivas e específicas, uma informação por pergunta, sem repetir as já respondidas ou pendentes. Aprofunde respostas vagas ou incompletas (ex.: se disse um preço mas não o que inclui), " +
                  "cubra objeções, regras de desconto, limites do que prometer, exemplos de mensagens, casos de uso por segmento e quando transferir para humano. " +
                  "Responda APENAS JSON: {\"perguntas\":[{\"topico\":\"curto\",\"pergunta\":\"...\"}]}";
        var raw = await _ai.ChatAsync(new[] { new AiChatMessage("system", sys), new AiChatMessage("user", known.Length == 0 ? "(nada respondido ainda)" : known.ToString()) }, true, ct);
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return 0;
        var added = 0;
        using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
        if (!doc.RootElement.TryGetProperty("perguntas", out var arr) || arr.ValueKind != JsonValueKind.Array) return 0;
        var existing = rows.Select(r => MapService.Norm(r.Question)).ToHashSet();
        foreach (var q in arr.EnumerateArray().Take(8))
        {
            var text = q.TryGetProperty("pergunta", out var p) ? p.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 600 || !existing.Add(MapService.Norm(text))) continue;
            var topic = q.TryGetProperty("topico", out var t) ? t.GetString()?.Trim() : null;
            _db.AiKnowledge.Add(new AiKnowledge { Topic = string.IsNullOrWhiteSpace(topic) ? "Geral" : topic![..Math.Min(100, topic.Length)], Question = text });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Lets the owner test how the AI answers using only the knowledge base.</summary>
    public async Task<(string Reply, bool Gap)> TestAsync(string question, CancellationToken ct = default)
    {
        var kb = await RelevantAsync(question);
        if (!_ai.IsConfigured) return ("Configure o token da IA em Configurações para testar respostas. Base atual: " + kb.Count + " itens respondidos.", false);
        var sys = "Você é o SDR da Samos conversando por WhatsApp. Responda SOMENTE com a BASE DE CONHECIMENTO. Se a informação não estiver nela, NÃO invente: responda gap=true. Máx. 3 frases, português do Brasil.\n\nBASE DE CONHECIMENTO:\n" + AsPrompt(kb) +
                  "\nResponda APENAS JSON: {\"reply\":\"...\",\"gap\":false}";
        var raw = await _ai.ChatAsync(new[] { new AiChatMessage("system", sys), new AiChatMessage("user", question) }, true, ct);
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        try
        {
            using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
            var reply = doc.RootElement.TryGetProperty("reply", out var r) ? r.GetString() ?? "" : "";
            var gap = doc.RootElement.TryGetProperty("gap", out var g) && g.ValueKind == JsonValueKind.True;
            if (gap) await RegisterGapAsync(question, null);
            return (reply, gap);
        }
        catch { return (raw, false); }
    }
}

public record SdrStats(int Total, Dictionary<SdrStage, int> ByStage, int SentToday, List<(int Variant, int Sent, int Replied)> Variants);

/// <summary>Automatic SDR: sends the first contact, follows up, answers with the knowledge base, qualifies and hands the lead to a seller.</summary>
public class SdrService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();
    private static readonly Regex OptOut = new(@"^\s*(pare|parar|sair|stop|cancelar|remov\w*|descadastr\w*|n[aã]o quero( mais)?|n[aã]o me (chame|envie|mande)\w*|n[aã]o tenho interesse)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HandoffWords = new(@"\b(pre[cç]o|valor(es)?|quanto (custa|[eé])|or[cç]amento|proposta|contrato|reuni[aã]o|demonstra[cç][aã]o|atendente|humano|vendedor|especialista|falar com (algu[eé]m|uma pessoa)|tenho interesse|quero (contratar|fechar|saber mais|conhecer))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public const string DefaultVariants = "Olá, {{nome}}! Aqui é da Samos. Vi que a {{empresa}} atua em {{cidade}} e queria entender como vocês cuidam hoje do atendimento e do relacionamento com clientes. Posso te fazer uma pergunta rápida?\n---\nOi, {{nome}}, tudo bem? Falo da Samos. Ajudamos empresas de {{segmento}} a organizar vendas e atendimento. Faz sentido conversarmos 2 minutos por aqui?";
    public const string DefaultFollowUp = "Oi, {{nome}}! Passando só para ver se você conseguiu ver minha mensagem. Se não for o momento, sem problemas — é só me avisar. 🙂";

    private readonly AppDbContext _db;
    private readonly WhatsAppService _wa;
    private readonly IAiProvider _ai;
    private readonly KnowledgeService _kb;
    private readonly NotificationService _notify;
    private readonly ILogger<SdrService> _log;
    public SdrService(AppDbContext db, WhatsAppService wa, IAiProvider ai, KnowledgeService kb, NotificationService notify, ILogger<SdrService> log)
    { _db = db; _wa = wa; _ai = ai; _kb = kb; _notify = notify; _log = log; }

    public static string Render(string template, Lead l)
    {
        var first = (l.Name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string Or(string? v, string fb) => string.IsNullOrWhiteSpace(v) ? fb : v.Trim();
        return template.Replace("{{nome}}", Or(first, "tudo bem"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{empresa}}", Or(l.CompanyName ?? l.Name, "sua empresa"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{cidade}}", Or(l.City, "sua região"), StringComparison.OrdinalIgnoreCase)
            .Replace("{{segmento}}", Or(l.Segment, "diversos segmentos"), StringComparison.OrdinalIgnoreCase);
    }
    public static List<string> SplitVariants(string? v) =>
        Regex.Split(v ?? "", @"^\s*---\s*$", RegexOptions.Multiline).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private Task<WhatsAppMessage> SendAsync(SdrCampaign c, string phone, string text, CancellationToken ct = default)
        => c.SellerId.HasValue ? _wa.SendSellerTextAsync(c.SellerId.Value, phone, text, null, ct) : _wa.SendTextAsync(phone, text, null, true, ct);

    // ---------- Fila ----------
    public async Task<int> EnqueueAsync(SdrCampaign c, int max = 500, CancellationToken ct = default)
    {
        var q = _db.Leads.Where(l => (l.WhatsApp != null && l.WhatsApp != "") || (l.Phone != null && l.Phone != ""));
        if (c.FilterStatus.HasValue) q = q.Where(l => l.Status == c.FilterStatus);
        else q = q.Where(l => l.Status != LeadStatus.Ganho && l.Status != LeadStatus.Perdido);
        if (!string.IsNullOrWhiteSpace(c.FilterState)) { var uf = c.FilterState.Trim().ToUpper(); q = q.Where(l => l.State == uf); }
        if (!string.IsNullOrWhiteSpace(c.FilterSegment)) { var like = $"%{c.FilterSegment.Trim()}%"; q = q.Where(l => l.Segment != null && EF.Functions.ILike(l.Segment, like)); }
        q = q.Where(l => l.SellerId == null || l.SellerId == c.SellerId).Where(l => !_db.SdrContacts.Any(x => x.CampaignId == c.Id && x.LeadId == l.Id));
        var leads = await q.OrderByDescending(l => l.CreatedAt).Take(max * 2).ToListAsync(ct);
        var optOut = (await _db.SdrContacts.Where(x => x.Stage == SdrStage.OptOut).Select(x => x.PhoneKey).ToListAsync(ct)).ToHashSet();
        var seen = (await _db.SdrContacts.Where(x => x.CampaignId == c.Id).Select(x => x.PhoneKey).ToListAsync(ct)).ToHashSet(); var added = 0;
        foreach (var l in leads)
        {
            var phone = PhoneUtil.Normalize(l.WhatsApp) ?? PhoneUtil.Normalize(l.Phone);
            var key = PhoneUtil.Key(phone);
            if (phone == null || string.IsNullOrEmpty(key) || optOut.Contains(key) || !seen.Add(key)) continue;
            _db.SdrContacts.Add(new SdrContact { CampaignId = c.Id, LeadId = l.Id, Phone = phone, PhoneKey = key });
            if (++added >= max) break;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    // ---------- Disparo (chamado pelo agendador a cada ciclo) ----------
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var sent = 0;
        foreach (var c in await _db.SdrCampaigns.Where(x => x.IsActive).ToListAsync(ct))
        {
            try { if (await RunCampaignAsync(c, ct)) sent++; }
            catch (Exception ex) { _log.LogError(ex, "Falha no SDR da campanha {Id}", c.Id); }
        }
        await AlertPendingHandoffsAsync(ct);
        return sent;
    }

    private async Task<bool> RunCampaignAsync(SdrCampaign c, CancellationToken ct)
    {
        var now = DateTime.UtcNow; var local = AppTime.ToLocal(now);
        if (local.DayOfWeek == DayOfWeek.Sunday || local.Hour < c.StartHour || local.Hour >= c.EndHour) return false;
        if (c.NextSendAt.HasValue && c.NextSendAt > now) return false;
        if (c.SellerId.HasValue)
        {
            var st = await _db.Sellers.Where(s => s.Id == c.SellerId).Select(s => s.WaStatus).FirstOrDefaultAsync(ct);
            if (st != "CONECTADO") return false;
        }
        var recentErrors = await _db.SdrContacts.CountAsync(x => x.CampaignId == c.Id && x.Stage == SdrStage.Erro && x.UpdatedAt > now.AddHours(-1), ct);
        if (recentErrors >= 5)
        {
            c.IsActive = false; await _db.SaveChangesAsync(ct);
            await _notify.CreateAsync(NotificationType.Error, NotificationSource.Crm, "SDR pausado por erros", $"A campanha \"{c.Name}\" foi pausada após {recentErrors} falhas de envio na última hora. Verifique o WhatsApp e reative.", "/Sdr");
            return false;
        }
        var today = AppTime.TodayStartUtc;
        if (await _db.SdrContacts.CountAsync(x => x.CampaignId == c.Id && x.LastSentAt >= today, ct) >= c.DailyLimit) return false;

        var follow = await _db.SdrContacts.Include(x => x.Lead)
            .Where(x => x.CampaignId == c.Id && x.Stage == SdrStage.Enviado && x.NextActionAt <= now).OrderBy(x => x.NextActionAt).FirstOrDefaultAsync(ct);
        if (follow != null) return await FollowUpAsync(c, follow, ct);

        var next = await _db.SdrContacts.Include(x => x.Lead).Where(x => x.CampaignId == c.Id && x.Stage == SdrStage.Fila).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (next == null)
        {
            if (c.UpdatedAt < now.AddMinutes(-30) || c.NextSendAt == null) { c.NextSendAt = now.AddMinutes(10); await EnqueueAsync(c, 200, ct); await _db.SaveChangesAsync(ct); }
            return false;
        }
        return await FirstContactAsync(c, next, ct);
    }

    private void Throttle(SdrCampaign c)
    {
        var min = Math.Max(20, c.MinDelaySec); var max = Math.Max(min, c.MaxDelaySec);
        c.NextSendAt = DateTime.UtcNow.AddSeconds(Random.Shared.Next(min, max + 1));
    }

    private async Task<bool> FirstContactAsync(SdrCampaign c, SdrContact ct0, CancellationToken ct)
    {
        var lead = ct0.Lead!;
        try
        {
            var phone = await _wa.ValidateNumberAsync(c.SellerId, ct0.Phone, ct);
            ct0.Phone = phone; ct0.PhoneKey = PhoneUtil.Key(phone) ?? ct0.PhoneKey;
        }
        catch (InvalidOperationException ex)
        {
            ct0.Stage = SdrStage.SemWhatsApp; ct0.Error = ex.Message; await _db.SaveChangesAsync(ct); return false;
        }
        var variants = SplitVariants(c.Variants); if (variants.Count == 0) variants = SplitVariants(DefaultVariants);
        ct0.Variant = Random.Shared.Next(variants.Count);
        try
        {
            await SendAsync(c, ct0.Phone, Render(variants[ct0.Variant], lead), ct);
        }
        catch (Exception ex) { ct0.Stage = SdrStage.Erro; ct0.Error = ex.Message.Truncate(290); Throttle(c); await _db.SaveChangesAsync(ct); return false; }
        var now = DateTime.UtcNow;
        ct0.Stage = SdrStage.Enviado; ct0.Attempts = 1; ct0.LastSentAt = now; ct0.NextActionAt = now.AddHours(c.FollowUpHours);
        if (lead.Status == LeadStatus.Novo) lead.Status = LeadStatus.Contato;
        if (lead.SellerId == null && c.SellerId.HasValue) lead.SellerId = c.SellerId;
        lead.LastInteractionAt = now;
        _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.WhatsApp, Description = "SDR automático: primeiro contato enviado (variação " + (ct0.Variant + 1) + ")" });
        Throttle(c);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> FollowUpAsync(SdrCampaign c, SdrContact x, CancellationToken ct)
    {
        if (x.Attempts > c.MaxFollowUps)
        {
            x.Stage = SdrStage.Encerrado; x.NextActionAt = null; await _db.SaveChangesAsync(ct); return false;
        }
        try { await SendAsync(c, x.Phone, Render(string.IsNullOrWhiteSpace(c.FollowUpText) ? DefaultFollowUp : c.FollowUpText, x.Lead!), ct); }
        catch (Exception ex) { x.Stage = SdrStage.Erro; x.Error = ex.Message.Truncate(290); Throttle(c); await _db.SaveChangesAsync(ct); return false; }
        x.Attempts++; x.LastSentAt = DateTime.UtcNow; x.NextActionAt = DateTime.UtcNow.AddHours(c.FollowUpHours);
        Throttle(c);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ---------- Resposta do lead ----------
    public async Task HandleReplyAsync(int? sellerId, string phone, string text, bool viaAudio = false, CancellationToken ct = default)
    {
        var key = PhoneUtil.Key(phone);
        var contact = await _db.SdrContacts.Include(x => x.Campaign).Include(x => x.Lead)
            .Where(x => x.PhoneKey == key && (x.Stage == SdrStage.Enviado || x.Stage == SdrStage.Respondeu) && x.Campaign!.SellerId == sellerId)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (contact == null) return;
        var gate = Locks.GetOrAdd(contact.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await _db.Entry(contact).ReloadAsync(ct);
            if (contact.Stage is not (SdrStage.Enviado or SdrStage.Respondeu)) return;
            var lastDir = await _db.WhatsAppMessages.Where(m => m.Conversation!.SellerId == sellerId && m.Conversation.Phone.EndsWith(key!))
                .OrderByDescending(m => m.Id).Select(m => (MessageDirection?)m.Direction).FirstOrDefaultAsync(ct);
            if (contact.Stage == SdrStage.Respondeu && lastDir == MessageDirection.Out) return; // já respondido (webhook duplicado)
            var c = contact.Campaign!; var lead = contact.Lead!;
            contact.RepliedAt ??= DateTime.UtcNow; contact.Stage = SdrStage.Respondeu; contact.NextActionAt = null;

            if (OptOut.IsMatch(text))
            {
                contact.Stage = SdrStage.OptOut;
                _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = "Lead pediu para não receber mais contatos (SDR)." });
                await _db.SaveChangesAsync(ct);
                await SafeSendAsync(c, contact.Phone, "Tudo bem, não vamos mais te chamar. Obrigado pelo retorno!", ct);
                return;
            }

            var hist = await LoadHistoryAsync(contact.Phone, c.SellerId, ct);
            AiTurn? turn = null;
            if (c.AiReplies && _ai.IsConfigured && contact.AiTurns < 6)
            {
                try { turn = await AskAiAsync(c, lead, text, hist, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "SDR: IA indisponível, repassando ao vendedor"); }
            }

            var wantsHuman = HandoffWords.IsMatch(text);
            if (turn != null && !string.IsNullOrWhiteSpace(turn.Gap))
            {
                await _kb.RegisterGapAsync(turn.Gap, lead.Id);
                turn = turn with { Handoff = true, Reason = "Pergunta fora da base de conhecimento: " + turn.Gap };
            }
            if (turn != null) { contact.Score = Math.Clamp(turn.Score, 0, 100); if (!string.IsNullOrWhiteSpace(turn.Summary)) contact.Summary = turn.Summary.Truncate(1400); }

            if (turn == null || turn.Handoff || wantsHuman || contact.Score >= 70)
            {
                var reason = turn?.Reason is { Length: > 0 } r ? r : wantsHuman ? "O lead pediu preço/proposta/atendente." : turn == null ? "Sem IA disponível ou limite de respostas automáticas." : "Lead qualificado.";
                await HandoffAsync(c, contact, lead, text, reason, ct);
                return;
            }
            contact.AiTurns++;
            await _db.SaveChangesAsync(ct);
            await Task.Delay(Random.Shared.Next(4000, 9000), ct);
            try { await _wa.SendReplyAsync(c.SellerId, contact.Phone, turn.Reply, viaAudio, null, true, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "SDR: falha ao enviar resposta"); }
        }
        finally { gate.Release(); }
    }

    private async Task SafeSendAsync(SdrCampaign c, string phone, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await SendAsync(c, phone, text, ct); } catch (Exception ex) { _log.LogWarning(ex, "SDR: falha ao enviar resposta"); }
    }

    private record AiTurn(string Reply, int Score, bool Handoff, string? Reason, string? Gap, string? Summary);

    private async Task<List<AiChatMessage>> LoadHistoryAsync(string phone, int? sellerId, CancellationToken ct)
    {
        var key = PhoneUtil.Key(phone);
        var msgs = await _db.WhatsAppMessages.Where(m => m.Conversation!.SellerId == sellerId && m.Conversation.Phone.EndsWith(key!)).OrderByDescending(m => m.Id).Take(12).ToListAsync(ct);
        msgs.Reverse();
        return msgs.Where(m => !string.IsNullOrWhiteSpace(m.Body)).Select(m => new AiChatMessage(m.Direction == MessageDirection.Out ? "assistant" : "user", m.Body!)).ToList();
    }

    private async Task<AiTurn> AskAiAsync(SdrCampaign c, Lead lead, string text, List<AiChatMessage> hist, CancellationToken ct)
    {
        var kb = await _kb.RelevantAsync(text);
        var sys = "Você é o SDR (pré-vendas) da Samos, falando por WhatsApp com um lead. Objetivo: responder com naturalidade, entender se há interesse, qualificar e passar para um vendedor humano.\n" +
                  "REGRAS: respostas curtas (máx. 3 frases), português do Brasil, tom humano, uma pergunta por vez, sem listas. Responda dúvidas SOMENTE com a BASE DE CONHECIMENTO. " +
                  "Se a dúvida não estiver coberta, NÃO invente: preencha knowledge_gap com a pergunta do lead e diga que vai confirmar com a equipe. Nunca invente preço, prazo, desconto ou promessa. Nunca diga que é humano se perguntarem; se perguntarem, diga que é o assistente virtual da Samos. " +
                  "Qualifique aos poucos (se tem a necessidade, quem decide, quando pretende resolver). score (0-100) = interesse e prontidão de compra. handoff=true quando pedir preço/proposta/contrato/reunião/atendente, demonstrar interesse claro ou score>=70.\n" +
                  $"OBJETIVO DA CAMPANHA: {(string.IsNullOrWhiteSpace(c.Pitch) ? "apresentar a Samos e qualificar o interesse" : c.Pitch)}\n" +
                  $"LEAD: nome={lead.Name}; empresa={lead.CompanyName}; cidade={lead.City}/{lead.State}; segmento={lead.Segment}\n\nBASE DE CONHECIMENTO:\n{KnowledgeService.AsPrompt(kb)}\n" +
                  "Responda APENAS JSON: {\"reply\":\"mensagem ao lead\",\"score\":0,\"handoff\":false,\"reason\":\"motivo do repasse\",\"knowledge_gap\":null,\"summary\":\"resumo da conversa em 2 frases\"}";
        var messages = new List<AiChatMessage> { new("system", sys) };
        messages.AddRange(hist);
        if (hist.Count == 0 || hist[^1].Role != "user" || hist[^1].Content != text) messages.Add(new("user", text));
        var raw = await _ai.ChatAsync(messages, true, ct);
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) throw new InvalidOperationException("Resposta da IA inválida.");
        using var doc = JsonDocument.Parse(raw[s..(e + 1)]); var root = doc.RootElement;
        string? S(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var score = root.TryGetProperty("score", out var sc) && sc.TryGetInt32(out var si) ? si : 0;
        var handoff = root.TryGetProperty("handoff", out var h) && h.ValueKind == JsonValueKind.True;
        var gap = S("knowledge_gap"); if (string.IsNullOrWhiteSpace(gap) || gap.Equals("null", StringComparison.OrdinalIgnoreCase)) gap = null;
        return new(S("reply") ?? "", score, handoff, S("reason"), gap, S("summary"));
    }

    // ---------- Repasse ao vendedor ----------
    public async Task<Seller?> PickSellerAsync(Lead lead, CancellationToken ct = default)
    {
        var sellers = await _db.Sellers.Where(s => s.IsActive).ToListAsync(ct);
        if (sellers.Count == 0) return null;
        var uf = lead.State?.Trim().ToUpperInvariant();
        var regional = uf == null ? new List<Seller>() : sellers.Where(s => (s.Regions ?? "").Split(',').Contains(uf)).ToList();
        var pool = regional.Count > 0 ? regional : sellers;
        var since = DateTime.UtcNow.AddDays(-30);
        var counts = await _db.SdrContacts.Where(x => x.HandoffAt >= since && x.HandoffSellerId != null).GroupBy(x => x.HandoffSellerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return pool.OrderBy(s => counts.FirstOrDefault(c => c.Key == s.Id)?.N ?? 0).ThenBy(s => s.Id).First();
    }

    public async Task HandoffAsync(SdrCampaign c, SdrContact contact, Lead lead, string lastMessage, string reason, CancellationToken ct = default)
    {
        var seller = c.SellerId.HasValue ? await _db.Sellers.FindAsync(new object[] { c.SellerId.Value }, ct) : await PickSellerAsync(lead, ct);
        var who = seller == null ? "um especialista da nossa equipe" : seller.Name.Split(' ')[0];
        await SafeSendAsync(c, contact.Phone, c.SellerId.HasValue
            ? "Perfeito! Vou passar a conversa para um especialista da nossa equipe, que continua por aqui com você em instantes."
            : $"Perfeito! Vou chamar {who}, da nossa equipe, para te passar os detalhes. Ele(a) já entra em contato com você.", ct);

        contact.Stage = SdrStage.Repassado; contact.HandoffSellerId = seller?.Id; contact.HandoffAt = DateTime.UtcNow.AddSeconds(5); contact.NextActionAt = null;
        if (seller != null) { lead.SellerId = seller.Id; if (seller.UserId.HasValue) lead.AssignedUserId = seller.UserId; }
        if (lead.Status < LeadStatus.Qualificado) lead.Status = LeadStatus.Qualificado;
        lead.LastInteractionAt = DateTime.UtcNow;
        var summary = string.IsNullOrWhiteSpace(contact.Summary) ? "—" : contact.Summary;
        _db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = $"SDR repassou o lead{(seller != null ? " para " + seller.Name : "")}. Motivo: {reason}. Resumo: {summary}".Truncate(1900) });
        await _db.SaveChangesAsync(ct);

        var title = $"Lead quente: {lead.Name}";
        var body = $"{lead.CompanyName ?? lead.Name} ({lead.City}/{lead.State}) · tel. {contact.Phone}\nMotivo: {reason}\nÚltima msg: \"{lastMessage.Truncate(180)}\"\nResumo: {summary}";
        var link = "/WhatsApp";
        await _notify.CreateAsync(NotificationType.Success, NotificationSource.Crm, title, body, link, seller?.UserId);
        if (seller?.UserId != null) await _notify.CreateAsync(NotificationType.Info, NotificationSource.Crm, $"SDR repassou {lead.Name} para {seller.Name}", reason, "/Leads/Details/" + lead.Id);
        var phone = PhoneUtil.Normalize(seller?.Phone);
        if (phone != null && _wa.IsConfigured)
        {
            try { await _wa.SendTextAsync(phone, $"🔥 *{title}*\n{body}\n\nAbra o chat no SAMOS CONTROL e continue a conversa.", null, true, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "SDR: não foi possível avisar o vendedor por WhatsApp"); }
        }
    }

    private async Task AlertPendingHandoffsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var pending = await _db.SdrContacts.Include(x => x.Lead).Include(x => x.Campaign)
            .Where(x => x.Stage == SdrStage.Repassado && !x.Alerted && x.HandoffAt != null && x.HandoffAt < now.AddMinutes(-5)).Take(50).ToListAsync(ct);
        foreach (var x in pending)
        {
            var wait = x.Campaign!.HandoffAlertMinutes;
            if (x.HandoffAt > now.AddMinutes(-wait)) continue;
            var answered = await _db.WhatsAppMessages.AnyAsync(m => m.Direction == MessageDirection.Out && !m.FromAgent && m.Timestamp > x.HandoffAt && m.Conversation!.Phone.EndsWith(x.PhoneKey), ct);
            x.Alerted = true;
            if (!answered)
                await _notify.CreateAsync(NotificationType.Warning, NotificationSource.Crm, "Lead repassado sem resposta",
                    $"{x.Lead!.Name} foi repassado pelo SDR há mais de {wait} min e ainda não recebeu resposta do vendedor.", "/WhatsApp");
        }
        if (pending.Count > 0) await _db.SaveChangesAsync(ct);
    }

    // ---------- Métricas ----------
    public async Task<SdrStats> StatsAsync(int campaignId)
    {
        var rows = await _db.SdrContacts.Where(x => x.CampaignId == campaignId).Select(x => new { x.Stage, x.Variant, x.Attempts, x.RepliedAt, x.LastSentAt }).ToListAsync();
        var today = AppTime.TodayStartUtc;
        return new(rows.Count, rows.GroupBy(r => r.Stage).ToDictionary(g => g.Key, g => g.Count()), rows.Count(r => r.LastSentAt >= today),
            rows.Where(r => r.Attempts > 0).GroupBy(r => r.Variant).OrderBy(g => g.Key).Select(g => (g.Key, g.Count(), g.Count(r => r.RepliedAt != null))).ToList());
    }
}
