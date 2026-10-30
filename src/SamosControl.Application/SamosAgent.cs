using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record Intent(string? Tool, JsonObject Args, string? Reply);

/// <summary>
/// Natural-language agent. The LLM (or the rule-based fallback) only selects a predefined tool and its arguments;
/// permissions are checked per user, sensitive tools require explicit CONFIRMAR, and every call is audited in AiActions.
/// </summary>
public class SamosAgent
{
    private static readonly TimeSpan ConfirmationTtl = TimeSpan.FromMinutes(10);
    private readonly AppDbContext _db;
    private readonly IEnumerable<IAgentTool> _tools;
    private readonly IAiProvider _ai;
    private readonly PermissionService _perms;
    private readonly AuditService _audit;
    private readonly ILogger<SamosAgent> _log;

    public SamosAgent(AppDbContext db, IEnumerable<IAgentTool> tools, IAiProvider ai, PermissionService perms, AuditService audit, ILogger<SamosAgent> log)
    { _db = db; _tools = tools; _ai = ai; _perms = perms; _audit = audit; _log = log; }

    public async Task<string> HandleAsync(string message, AgentContext ctx, CancellationToken ct = default)
    {
        message = (message ?? "").Trim();
        if (message.Length == 0) return "Como posso ajudar?";
        if (message.Length > 1000) message = message[..1000];
        _db.AiMessages.Add(new AiMessage { UserId = ctx.UserId, Channel = ctx.Channel, Role = "user", Content = message });
        string reply;
        try { reply = await ProcessAsync(message, ctx, ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha no SamosAgent");
            reply = "Ocorreu um erro ao processar seu pedido. Tente novamente.";
        }
        _db.AiMessages.Add(new AiMessage { UserId = ctx.UserId, Channel = ctx.Channel, Role = "assistant", Content = reply });
        await _db.SaveChangesAsync(ct);
        return reply;
    }

    private async Task<string> ProcessAsync(string message, AgentContext ctx, CancellationToken ct)
    {
        var word = message.Trim().TrimEnd('.', '!').ToUpperInvariant();
        var now = DateTime.UtcNow;
        var pending = await _db.AiActions.Where(a => a.Status == AiActionStatus.AguardandoConfirmacao && a.UserId == ctx.UserId && a.Channel == ctx.Channel)
            .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);
        if (pending != null && pending.ExpiresAt < now) { pending.Status = AiActionStatus.Expirada; await _db.SaveChangesAsync(ct); pending = null; }

        if (word is "CONFIRMAR" or "CONFIRMO" or "SIM")
        {
            if (pending == null) return "Não há nenhuma ação aguardando confirmação.";
            return await ExecutePendingAsync(pending, ctx, ct);
        }
        if (word is "CANCELAR" or "NAO" or "NÃO")
        {
            if (pending == null) return "Não há nenhuma ação pendente.";
            pending.Status = AiActionStatus.Cancelada;
            await _db.SaveChangesAsync(ct);
            return "Ação cancelada.";
        }
        if (pending != null) { pending.Status = AiActionStatus.Cancelada; await _db.SaveChangesAsync(ct); }

        var allowed = new List<IAgentTool>();
        foreach (var t in _tools) if (ctx.UserId.HasValue && await _perms.HasAsync(ctx.UserId.Value, t.Permission)) allowed.Add(t);

        // Sem LLM: um único passo por regras. Com LLM: conversa com memória e até 4 passos (ferramenta -> resultado -> resposta).
        var useLlm = _ai.IsConfigured;
        var chat = new List<AiChatMessage>();
        if (useLlm)
        {
            chat.Add(new AiChatMessage("system", BuildPrompt(allowed, ctx)));
            chat.AddRange(await LoadHistoryAsync(ctx, ct));
            chat.Add(new AiChatMessage("user", message));
        }
        var results = new List<string>();
        string? lastCall = null;
        for (var step = 0; step < 4; step++)
        {
            Intent? intent = null;
            string? raw = null;
            if (useLlm)
            {
                try { raw = await _ai.ChatAsync(chat, true, ct); intent = ParseLlm(raw); }
                catch (Exception ex) when (ex is IntegrationException or JsonException) { _log.LogWarning("LLM indisponível: {Msg}", ex.Message); }
                if (intent == null && step > 0) break;
                if (intent == null) { useLlm = false; }
            }
            if (!useLlm && step == 0) intent = RuleBasedInterpreter.Interpret(message);
            if (intent == null) break;
            if (intent.Tool == null)
                return intent.Reply ?? (results.Count > 0 ? string.Join("\n\n", results) : HelpText(allowed));

            var call = intent.Tool + intent.Args.ToJsonString();
            if (call == lastCall) break; // o modelo repetiu a mesma chamada: encerra com o que já temos
            lastCall = call;

            var (text, final) = await StepAsync(intent, message, allowed, ctx, now, ct);
            if (final) return results.Count > 0 ? string.Join("\n\n", results) + "\n\n" + text : text;
            results.Add(text);
            if (!useLlm) return text;
            chat.Add(new AiChatMessage("assistant", raw!));
            chat.Add(new AiChatMessage("user", $"RESULTADO da ferramenta {intent.Tool}:\n{text}\n\nAgora responda ao usuário em {{\"reply\":\"...\"}}, em português, de forma natural e curta, mantendo TODOS os números, ids (#) e itens do resultado (não resuma listas). Se faltar executar outra ferramenta para concluir o pedido, chame-a."));
        }
        return results.Count > 0 ? string.Join("\n\n", results) : HelpText(allowed);
    }

    /// <summary>Valida, planeja e executa uma ferramenta. final = true quando a resposta deve ir direto ao usuário (erro, negado ou pedido de confirmação).</summary>
    private async Task<(string Text, bool Final)> StepAsync(Intent intent, string message, List<IAgentTool> allowed, AgentContext ctx, DateTime now, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == intent.Tool);
        var action = new AiAction { UserId = ctx.UserId, Channel = ctx.Channel, Phone = ctx.Phone, UserMessage = message, Tool = intent.Tool!, ArgsJson = intent.Args.ToJsonString() };
        if (tool == null) // the LLM asked for something that is not a registered tool
        {
            action.Status = AiActionStatus.Negada; action.Result = "Ferramenta inexistente.";
            await SaveActionAsync(action, ctx);
            return ("Não sei executar isso. " + HelpText(allowed), true);
        }
        if (!allowed.Contains(tool))
        {
            action.Status = AiActionStatus.Negada; action.Result = "Sem permissão.";
            await SaveActionAsync(action, ctx);
            return ("Você não tem permissão para essa operação.", true);
        }
        ToolPlan plan;
        try { plan = await tool.PlanAsync(ctx, intent.Args); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { plan = new ToolPlan("⚠️ " + ex.Message, intent.Args, ""); }
        if (plan.Error != null)
        {
            action.Status = AiActionStatus.Falhou; action.Result = plan.Error;
            await SaveActionAsync(action, ctx);
            return (plan.Error, true);
        }
        action.ArgsJson = plan.Args.ToJsonString();
        if (tool.RequiresConfirmation)
        {
            action.Status = AiActionStatus.AguardandoConfirmacao; action.RequiresConfirmation = true; action.ExpiresAt = now.Add(ConfirmationTtl);
            await SaveActionAsync(action, ctx);
            return (plan.Description + "\n\nResponda CONFIRMAR para executar ou CANCELAR.", true);
        }
        var text = await RunAsync(tool, action, plan.Args, ctx, ct);
        return (text, action.Status == AiActionStatus.Falhou);
    }

    private async Task<List<AiChatMessage>> LoadHistoryAsync(AgentContext ctx, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-12);
        var rows = await _db.AiMessages.Where(m => m.UserId == ctx.UserId && m.Channel == ctx.Channel && m.CreatedAt >= since)
            .OrderByDescending(m => m.Id).Take(10).ToListAsync(ct);
        rows.Reverse();
        // O histórico guarda a resposta final em texto; o modelo vê como {"reply": ...} para manter o formato.
        return rows.Select(m => m.Role == "assistant"
            ? new AiChatMessage("assistant", new JsonObject { ["reply"] = m.Content.Truncate(700) }.ToJsonString())
            : new AiChatMessage("user", m.Content.Truncate(500))).ToList();
    }
    private async Task<string> ExecutePendingAsync(AiAction pending, AgentContext ctx, CancellationToken ct)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == pending.Tool);
        if (tool == null || !ctx.UserId.HasValue || !await _perms.HasAsync(ctx.UserId.Value, tool.Permission))
        {
            pending.Status = AiActionStatus.Negada; await _db.SaveChangesAsync(ct);
            return "Você não tem permissão para essa operação.";
        }
        pending.Status = AiActionStatus.Confirmada;
        await _db.SaveChangesAsync(ct);
        var args = JsonNode.Parse(pending.ArgsJson ?? "{}")!.AsObject();
        return await RunAsync(tool, pending, args, ctx, ct, alreadySaved: true);
    }

    private async Task<string> RunAsync(IAgentTool tool, AiAction action, JsonObject args, AgentContext ctx, CancellationToken ct, bool alreadySaved = false)
    {
        string result;
        try { result = await tool.ExecuteAsync(ctx, args, ct); action.Status = AiActionStatus.Executada; }
        catch (Exception ex) when (ex is IntegrationException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            result = "⚠️ " + ex.Message; action.Status = AiActionStatus.Falhou;
        }
        action.Result = result.Truncate(4000);
        if (alreadySaved) await _db.SaveChangesAsync(ct); else await SaveActionAsync(action, ctx);
        await _audit.LogAsync("ai", tool.Name, "AiAction", action.Id, newData: $"{action.Status}; {action.ArgsJson}", userId: ctx.UserId, userName: ctx.UserName);
        return result;
    }

    private async Task SaveActionAsync(AiAction action, AgentContext ctx)
    {
        if (action.Id == 0) _db.AiActions.Add(action);
        await _db.SaveChangesAsync();
        if (action.Status is AiActionStatus.Negada or AiActionStatus.Falhou or AiActionStatus.AguardandoConfirmacao)
            await _audit.LogAsync("ai", action.Tool, "AiAction", action.Id, newData: $"{action.Status}; {action.ArgsJson}", userId: ctx.UserId, userName: ctx.UserName);
    }

    private static string HelpText(IEnumerable<IAgentTool> tools) =>
        "Posso ajudar com:\n" + string.Join("\n", tools.Select(t => $"• {t.Description}"));

    // ---------- Interpretation ----------
    private static string BuildPrompt(IEnumerable<IAgentTool> tools, AgentContext ctx)
    {
        var now = AppTime.Now;
        var sb = new StringBuilder();
        sb.AppendLine($"Você é o SamosAgent, o assistente pessoal e operacional de {ctx.UserName}, dono/colaborador da empresa Samos. Fale português do Brasil, de forma natural, direta e simpática (pode usar poucos emojis). Você conversa pelo WhatsApp: respostas curtas, sem markdown pesado.");
        sb.AppendLine($"Agora: {now:yyyy-MM-ddTHH:mm} ({now:dddd}, America/Sao_Paulo). Interprete 'hoje', 'amanhã', 'sexta', 'daqui 2 horas', etc. a partir desta data e sempre envie datas no formato yyyy-MM-ddTHH:mm (horário local). Se o usuário não disser hora para um compromisso, use 09:00.");
        sb.AppendLine("Responda SOMENTE com UM JSON por vez:");
        sb.AppendLine("- Para executar algo: {\"tool\":\"nome\",\"args\":{...}}");
        sb.AppendLine("- Para responder/conversar/perguntar algo que falta: {\"reply\":\"texto\"}");
        sb.AppendLine("Regras: (1) Para QUALQUER dado da empresa ou da vida pessoal do usuário (tarefas, notas, gastos, agenda) use ferramenta; nunca invente números, ids ou dados. (2) Lembretes, compromissos, contas a pagar e coisas a fazer do usuário => criar_tarefa (não use criar_job). (3) Use o histórico da conversa para entender referências como 'essa', 'adia pra amanhã', 'e a de ontem?'. (4) Se faltar uma informação indispensável (ex.: o valor de um gasto), pergunte em {\"reply\"}. (5) Pedidos com várias partes: execute uma ferramenta por vez. (6) Ações sensíveis pedem confirmação automaticamente; não peça você mesmo.");
        sb.AppendLine("Ferramentas disponíveis (use exatamente estes nomes):");
        foreach (var t in tools) sb.AppendLine($"- {t.Name}: {t.Description}. args: {t.ArgsHelp}");
        return sb.ToString();
    }
    public static Intent? ParseLlm(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var n = JsonNode.Parse(raw[start..(end + 1)]) as JsonObject;
        if (n == null) return null;
        var tool = (n["tool"] as JsonValue)?.ToString();
        if (!string.IsNullOrWhiteSpace(tool)) return new Intent(tool, n["args"] as JsonObject ?? new JsonObject(), null);
        var reply = (n["reply"] as JsonValue)?.ToString();
        return reply != null ? new Intent(null, new JsonObject(), reply) : null;
    }
}

/// <summary>Deterministic keyword fallback so the assistant works without an LLM.</summary>
public static class RuleBasedInterpreter
{
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static string PeriodOf(string t) => t.Contains("hoje") ? "hoje" : t.Contains("semana") ? "semana" : t.Contains("mes") ? "mes" : "todos";

    public static Intent Interpret(string message)
    {
        var t = Fold(message);
        JsonObject O(params (string, object?)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v is null ? null : JsonValue.Create(v.ToString()); return o; }

        if (Regex.IsMatch(t, @"(como esta a empresa|resumo|panorama)")) return new("resumo_empresa", O(), null);

        var wh = ParseWhen(t);
        var m = Regex.Match(message, @"^\s*(?:conclu[ií]r?|concluí|feito|finalizar|marcar como feit[ao])\s+(?:a tarefa\s+)?#?(.+?)\s*$", RegexOptions.IgnoreCase);
        if (m.Success) return new("concluir_tarefa", O(("tarefa", m.Groups[1].Value)), null);
        if (Regex.IsMatch(t, @"(minhas tarefas|minha agenda|meus lembretes|o que tenho|pendencias|o que preciso fazer)"))
            return new("listar_tarefas", O(("filtro", t.Contains("hoje") ? "hoje" : t.Contains("amanha") ? "amanha" : t.Contains("semana") ? "semana" : "pendentes")), null);
        m = Regex.Match(message, @"(?:gastei|paguei|comprei)\s+R?\$?\s*([\d\.]+(?:,\d{1,2})?)\s*(?:reais\s*)?(?:em|no|na|de|com)?\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success) return new("registrar_gasto", O(("valor", m.Groups[1].Value.Replace(".", "").Replace(',', '.')), ("descricao", m.Groups[2].Value.Trim())), null);
        m = Regex.Match(message, @"^\s*(?:anote|anota|guarde|salve)\s*(?:que|:)?\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success) return new("anotar", O(("texto", m.Groups[1].Value.Trim())), null);
        m = Regex.Match(message, @"(?:me lembre|me lembra|lembrar|me avise|me avisa|lembrete|tarefa)\s*(?:de|para|que|:)?\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success && !t.Contains("sincroniz") && !t.Contains("relatorio"))
        {
            var title = Regex.Replace(m.Groups[1].Value, @"\s*(amanh[ãa]|hoje)?\s*([àa]s\s+\d{1,2}(?:(?::|h)\d{2})?h?)?\s*$", "", RegexOptions.IgnoreCase).Trim();
            return new("criar_tarefa", O(("titulo", title.Length > 0 ? title : m.Groups[1].Value.Trim()), ("quando", wh?.ToString("yyyy-MM-ddTHH:mm"))), null);
        }
        if (Regex.IsMatch(t, @"(crie|criar|agende|agendar)"))
        {
            var when = wh;
            if (when != null)
            {
                var type = t.Contains("sincroniz") || t.Contains("buscar novos leads") ? "SYNC_LEADS" : t.Contains("relatorio") ? "GENERATE_REPORT" : "REMINDER";
                return new("criar_job", O(("tipo", type), ("quando", when.Value.ToString("yyyy-MM-ddTHH:mm")), ("mensagem", message)), null);
            }
        }        if (t.Contains("sincroniz") && t.Contains("lead")) return new("sincronizar_leads", O(), null);

        if ((t.Contains("cobranca") || t.Contains("cobrar")) && Regex.IsMatch(t, @"(gere|gerar|crie|criar|emita|emitir)"))
        {
            var money = Regex.Match(message, @"R\$\s*([\d\.]+(?:,\d{1,2})?|\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase);
            var name = Regex.Match(message, @"(?:para|do|da)\s+(?:o\s+|a\s+)?(?:cliente\s+)?(.+?)\s*$", RegexOptions.IgnoreCase);
            if (money.Success && name.Success)
            {
                var v = money.Groups[1].Value;
                v = v.Contains(',') ? v.Replace(".", "").Replace(',', '.') : Regex.IsMatch(v, @"^\d{1,3}(\.\d{3})+$") ? v.Replace(".", "") : v;
                return new("criar_cobranca", O(("cliente", name.Groups[1].Value.Trim().TrimEnd('.')), ("valor", v)), null);
            }
        }
        if (Regex.IsMatch(t, @"(inadimplent|vencid)")) return new("consultar_cobrancas", O(("status", "vencidas")), null);
        if (t.Contains("cobranca")) return new("consultar_cobrancas", O(("status", t.Contains("paga") ? "pagas" : t.Contains("aberta") ? "abertas" : "todas")), null);
        if (t.Contains("contrato")) return new("consultar_contratos", O(("periodo", PeriodOf(t)), ("status", t.Contains("assinad") ? "assinados" : t.Contains("aguard") ? "aguardando" : "todos")), null);
        if (Regex.IsMatch(t, @"(fora do ar|offline|caido|indisponivel)")) return new("consultar_servicos", O(("status", "offline")), null);
        if (t.Contains("servidor")) return new("consultar_servidores", O(), null);
        if (t.Contains("servico")) return new("consultar_servicos", O(("status", "todos")), null);
        if (t.Contains("lead")) return new("consultar_leads", O(("periodo", PeriodOf(t))), null);
        if (t.Contains("cliente")) return new("consultar_clientes", O(), null);
        return new(null, new JsonObject(), null);
    }

    public static DateTime? ParseWhen(string folded)
    {
        var today = AppTime.Now.Date;
        DateTime? day = folded.Contains("amanha") ? today.AddDays(1) : folded.Contains("hoje") ? today : null;
        var tm = Regex.Match(folded, @"as\s+(\d{1,2})(?:(?::|h)(\d{2}))?");
        if (day == null) return null;
        var hour = tm.Success ? int.Parse(tm.Groups[1].Value) : 8;
        var min = tm.Success && tm.Groups[2].Success ? int.Parse(tm.Groups[2].Value) : 0;
        if (hour > 23 || min > 59) return null;
        return day.Value.AddHours(hour).AddMinutes(min);
    }
}
