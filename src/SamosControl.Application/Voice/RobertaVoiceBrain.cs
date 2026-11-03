using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application.Voice;

/// <summary>Garante Lead + contato SDR para um número (sem criar campanha ativa: o worker de follow-up só olha campanhas ativas).</summary>
public static class VoiceContactResolver
{
    public const string HiddenCampaignName = "Roberta Voz (testes)";

    public static async Task<(Lead Lead, SdrContact Contact)> EnsureAsync(AppDbContext db, string phone, string? label, CancellationToken ct)
    {
        var norm = PhoneUtil.Normalize(phone) ?? throw new ArgumentException("Telefone inválido.");
        var key = PhoneUtil.Key(norm)!;
        var lead = await db.Leads.Where(x => x.PhoneKey == key).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (lead == null)
        {
            lead = new Lead { Name = string.IsNullOrWhiteSpace(label) ? "Contato de teste (voz)" : label.Trim(), Phone = norm, WhatsApp = norm, PhoneKey = key, Status = LeadStatus.Novo, Notes = "Criado para teste de ligação da Roberta." };
            db.Leads.Add(lead);
            await db.SaveChangesAsync(ct);
        }
        var contact = await db.SdrContacts.Where(x => x.PhoneKey == key).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (contact == null)
        {
            var camp = await db.SdrCampaigns.FirstOrDefaultAsync(x => x.Name == HiddenCampaignName, ct);
            if (camp == null)
            {
                camp = new SdrCampaign { Name = HiddenCampaignName, IsActive = false, Mode = SdrMode.Shadow, AiReplies = true, Variants = "", DailyLimit = 0 };
                db.SdrCampaigns.Add(camp);
                await db.SaveChangesAsync(ct);
            }
            contact = new SdrContact { CampaignId = camp.Id, LeadId = lead.Id, Phone = norm, PhoneKey = key, Stage = SdrStage.Respondeu, NextActionAt = null };
            db.SdrContacts.Add(contact);
            await db.SaveChangesAsync(ct);
        }
        return (lead, contact);
    }
}

/// <summary>Cérebro da ligação = o motor comercial da Roberta (mesmo estado/memória do WhatsApp). Um escopo novo por turno evita estado sujo quando um turno é cancelado.</summary>
public class RobertaVoiceBrain : IVoiceBrain
{
    private static readonly Regex Goodbye = new(@"^\W*(tchau|pode desligar|vou desligar|vamos encerrar|pode encerrar|encerra|desliga)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IServiceScopeFactory _scopes;
    private readonly int _leadId, _contactId;
    private readonly bool _inbound, _storeTranscript;
    private readonly string _callLink;
    private readonly ILogger _log;
    private bool _handoff, _optOut, _demoOffered, _demoAccepted, _closed;
    private int _temperature;
    private string? _nextAction, _mainPain;

    public RobertaVoiceBrain(IServiceScopeFactory scopes, int leadId, int contactId, bool inbound, bool storeTranscript, string callLink, ILogger log)
    { _scopes = scopes; _leadId = leadId; _contactId = contactId; _inbound = inbound; _storeTranscript = storeTranscript; _callLink = callLink; _log = log; }

    private static string? PainPhrase(string? code) => code switch
    {
        Pain.StockDivergence => "as diferenças de estoque",
        Pain.Stock => "o controle de estoque",
        Pain.Fiscal => "a parte fiscal",
        Pain.Financial => "o financeiro",
        Pain.Sales => "o PDV e as vendas",
        Pain.System => "o sistema que vocês usam hoje",
        Pain.Customers => "o relacionamento com os clientes",
        Pain.Multistore => "a gestão de várias lojas",
        Pain.Ecommerce => "o e-commerce",
        _ => null
    };

    public async Task<string> OpenerAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lead = await db.Leads.AsNoTracking().FirstAsync(x => x.Id == _leadId, ct);
        var st = await db.ConversationStates.AsNoTracking().FirstOrDefaultAsync(x => x.LeadId == _leadId, ct);
        var first = lead.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var hello = first != null && !first.Equals("Contato", StringComparison.OrdinalIgnoreCase) && !first.Equals("Teste", StringComparison.OrdinalIgnoreCase) ? $"Oi, {first}!" : "Oi!";
        var intro = $"{hello} Aqui é a Roberta, assistente virtual de vendas da Samos.";
        if (_inbound) return intro + " Em que posso te ajudar?";
        var pain = PainPhrase(st?.MainPain);
        if (pain != null) return $"{intro} A gente tinha conversado por mensagem sobre {pain}. Queria entender melhor isso com você. Tem um minutinho agora?";
        if (st is { TurnCount: > 0 }) return $"{intro} A gente já trocou umas mensagens por escrito, e achei que seria mais fácil conversar por voz. Tem um minutinho agora?";
        return $"{intro} Estou ligando rapidinho para entender como vocês cuidam da loja hoje. Tem um minutinho?";
    }

    public async Task<BrainTurn> RespondAsync(string userText, CancellationToken ct)
    {
        if (Goodbye.IsMatch(userText))
        {
            _closed = true;
            return new BrainTurn("Tudo bem, obrigada pelo seu tempo! Se quiser, a gente continua pelo WhatsApp. Tchau!", true, "encerrado_pelo_cliente", nameof(RobertaAction.CloseConversation), false);
        }
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var roberta = sp.GetRequiredService<RobertaService>();
        var contact = await db.SdrContacts.Include(x => x.Lead).FirstAsync(x => x.Id == _contactId, ct);
        var lead = contact.Lead!;
        var hist = await LoadHistoryAsync(db, contact.PhoneKey, ct);
        var p = await roberta.ProposeAsync(contact, lead, userText, hist, viaAudio: true, replyDelay: null, voiceAvailable: false, lastOutboundWasAudio: false, ct: ct, shadow: false, voiceCall: true);
        if (p.Duplicate) return new BrainTurn("Desculpa, pode repetir só a última parte?", false, null, "Duplicate", false);

        if (p.Suggestion != null) { p.Suggestion.Status = SuggestionStatus.Enviada; }
        var action = p.Decision.Action;
        _temperature = p.State.Temperature; _nextAction = p.State.NextAction; _mainPain = p.State.MainPain;
        if (p.State.DemoAccepted) _demoAccepted = true;
        if (action is RobertaAction.OfferDemo or RobertaAction.ScheduleDemo or RobertaAction.AskForMeeting) _demoOffered = true;
        var end = false;
        if (action == RobertaAction.DoNotContact)
        {
            _optOut = true; end = true; contact.Stage = SdrStage.OptOut;
            db.Interactions.Add(new Interaction { LeadId = lead.Id, Type = InteractionType.Sistema, Description = "Lead pediu para não receber mais contatos (ligação da Roberta)." });
        }
        else if (action == RobertaAction.HandoffToSeller) { _handoff = true; end = true; }
        else if (action == RobertaAction.CloseConversation) { _closed = true; end = true; }
        await db.SaveChangesAsync(ct);

        var reply = VoiceText.ForSpeech(p.Reply);
        if (reply.Length == 0)
        {
            // Ações sem texto da LLM (ex.: respeitar tempo) ou IA indisponível: frase segura, nunca silêncio.
            reply = action switch
            {
                RobertaAction.RespectBusy or RobertaAction.PauseConversation => "Entendi, sem problema. Prefere que eu retome pelo WhatsApp em outro momento?",
                _ when end => "Combinado. Vou pedir para alguém do nosso time continuar com você. Obrigada!",
                _ => "Desculpa, não consegui formular uma boa resposta agora. Posso pedir para alguém do nosso time te retornar?"
            };
            if (!end && p.Reply == null) { _handoff = true; end = true; }
        }
        reply = Trim(reply);
        return new BrainTurn(reply, end, null, action.ToString(), p.Reply != null, _handoff, _optOut);
    }

    /// <summary>A fala é curta por natureza: no máximo 3 frases, sem cortar no meio.</summary>
    private static string Trim(string reply)
    {
        var s = VoiceText.Sentences(reply);
        return s.Count <= 3 ? reply : string.Join(" ", s.Take(3));
    }

    private static async Task<List<AiChatMessage>> LoadHistoryAsync(AppDbContext db, string key, CancellationToken ct)
    {
        var msgs = await db.WhatsAppMessages.Where(m => m.Conversation!.Phone.EndsWith(key.Substring(key.Length - 8)))
            .OrderByDescending(m => m.Id).Take(12).ToListAsync(ct);
        msgs.Reverse();
        return msgs.Where(m => !string.IsNullOrWhiteSpace(m.Body)).Select(m => new AiChatMessage(m.Direction == MessageDirection.Out ? "assistant" : "user", m.Body!)).ToList();
    }

    public async Task<BrainSummary> FinishAsync(IReadOnlyList<TranscriptLine> transcript, string endReason, CancellationToken ct)
    {
        var talked = transcript.Any(t => t.Role == "Cliente");
        var outcome = _optOut ? "opt_out" : _handoff ? "transferir_humano" : _demoAccepted ? "demo_aceita" : _demoOffered ? "demo_oferecida"
            : !talked ? "sem_conversa" : _closed ? "encerrado" : _temperature >= 60 ? "qualificado" : "indefinido";
        var sb = new System.Text.StringBuilder($"Ligação da Roberta ({(_inbound ? "recebida" : "realizada")}). Resultado: {outcome}. Término: {endReason}.");
        if (talked)
        {
            if (_mainPain != null) sb.Append($" Dor principal: {_mainPain}.");
            sb.Append($" Temperatura: {_temperature}/100.");
            if (!string.IsNullOrEmpty(_nextAction)) sb.Append($" Próxima ação sugerida: {_nextAction}.");
            sb.Append($" Falas do cliente: {transcript.Count(t => t.Role == "Cliente")}.");
            if (_storeTranscript)
            {
                var last = transcript.LastOrDefault(t => t.Role == "Cliente");
                if (last != null) sb.Append($" Última fala do cliente: \"{(last.Text.Length > 200 ? last.Text[..200] : last.Text)}\".");
            }
        }
        var summary = sb.ToString();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Interactions.Add(new Interaction { LeadId = _leadId, Type = InteractionType.Ligacao, Description = summary });
            var lead = await db.Leads.FirstOrDefaultAsync(x => x.Id == _leadId, ct);
            if (lead != null) lead.LastInteractionAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (_handoff && lead != null)
                await scope.ServiceProvider.GetRequiredService<NotificationService>().CreateAsync(NotificationType.Warning, NotificationSource.Crm,
                    "Roberta passou uma ligação para o time", $"{lead.Name} pediu/precisa de atendimento humano.", _callLink);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Voz: falha ao gravar o resultado no CRM"); }
        return new BrainSummary(outcome, summary);
    }
}
