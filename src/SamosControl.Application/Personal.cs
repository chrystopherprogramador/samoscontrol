using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Application;

/// <summary>Tarefas, lembretes, notas e gastos pessoais de cada usuário (tudo isolado por UserId).</summary>
public class PersonalService
{
    private static readonly CultureInfo BR = new("pt-BR");
    private readonly AppDbContext _db;
    private readonly WhatsAppService _wa;
    private readonly NotificationService _notif;
    private readonly ILogger<PersonalService> _log;
    public PersonalService(AppDbContext db, WhatsAppService wa, NotificationService n, ILogger<PersonalService> log) { _db = db; _wa = wa; _notif = n; _log = log; }

    public static string Money(decimal v) => v.ToString("C2", BR);

    public static int? RepeatToMinutes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = RuleBasedInterpreter.Fold(s);
        if (int.TryParse(t, out var n) && n > 0) return n;
        if (t.StartsWith("diari") || t == "todo dia" || t == "daily") return 1440;
        if (t.StartsWith("seman") || t == "weekly") return 10080;
        if (t.StartsWith("mensal") || t == "monthly") return 43200;
        return null;
    }

    public static string Fmt(PersonalTask t)
    {
        var sb = new StringBuilder($"#{t.Id} ");
        sb.Append(t.Done ? "✅ " : t.Priority >= 2 ? "🔴 " : t.Priority == 1 ? "🟠 " : "▫️ ");
        sb.Append(t.Title);
        var when = t.DueAt ?? t.RemindAt;
        if (when.HasValue) sb.Append(" — " + AppTime.ToLocal(when.Value).ToString("dd/MM HH:mm"));
        if (t.RepeatMinutes.HasValue) sb.Append(t.RepeatMinutes == 1440 ? " 🔁 diária" : t.RepeatMinutes == 10080 ? " 🔁 semanal" : t.RepeatMinutes == 43200 ? " 🔁 mensal" : " 🔁");
        return sb.ToString();
    }

    public async Task<PersonalTask> AddTaskAsync(int userId, string title, DateTime? dueUtc, DateTime? remindUtc, int priority, string? category, int? repeatMinutes, string? notes)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Informe o título da tarefa.");
        var t = new PersonalTask { UserId = userId, Title = title.Trim().Truncate(300), DueAt = dueUtc, RemindAt = remindUtc ?? dueUtc, Priority = Math.Clamp(priority, 0, 2), Category = category?.Trim().Truncate(60), RepeatMinutes = repeatMinutes, Notes = notes?.Trim().Truncate(2000) };
        _db.PersonalTasks.Add(t);
        await _db.SaveChangesAsync();
        return t;
    }

    /// <summary>Resolve uma tarefa por id (#12 ou 12) ou por trecho do título entre as pendentes.</summary>
    public async Task<(PersonalTask? Task, string? Error)> FindTaskAsync(int userId, string? idOrTitle, bool includeDone = false)
    {
        if (string.IsNullOrWhiteSpace(idOrTitle)) return (null, "Informe qual tarefa (número ou parte do título).");
        var s = idOrTitle.Trim().TrimStart('#');
        var q = _db.PersonalTasks.Where(t => t.UserId == userId && (includeDone || !t.Done));
        if (int.TryParse(s, out var id))
        {
            var byId = await q.FirstOrDefaultAsync(t => t.Id == id);
            if (byId != null) return (byId, null);
        }
        var low = s.ToLower();
        var list = await q.Where(t => t.Title.ToLower().Contains(low)).OrderBy(t => t.Id).Take(5).ToListAsync();
        if (list.Count == 0) return (null, $"Não achei tarefa '{idOrTitle}'.");
        var exact = list.FirstOrDefault(t => t.Title.ToLower() == low);
        if (list.Count > 1 && exact == null) return (null, "Achei várias:\n" + string.Join("\n", list.Select(Fmt)) + "\nQual delas? (use o número)");
        return (exact ?? list[0], null);
    }

    public async Task<List<PersonalTask>> ListTasksAsync(int userId, string? filter)
    {
        var now = DateTime.UtcNow;
        var q = _db.PersonalTasks.Where(t => t.UserId == userId);
        var f = RuleBasedInterpreter.Fold(filter ?? "pendentes");
        var dayEnd = AppTime.ToUtc(AppTime.Now.Date.AddDays(1));
        if (f.StartsWith("conclu") || f.StartsWith("feita")) q = q.Where(t => t.Done).OrderByDescending(t => t.DoneAt).Take(15);
        else
        {
            q = q.Where(t => !t.Done);
            if (f.StartsWith("hoje")) q = q.Where(t => (t.DueAt ?? t.RemindAt) < dayEnd);
            else if (f.StartsWith("amanha")) q = q.Where(t => (t.DueAt ?? t.RemindAt) >= dayEnd && (t.DueAt ?? t.RemindAt) < dayEnd.AddDays(1));
            else if (f.StartsWith("seman")) q = q.Where(t => (t.DueAt ?? t.RemindAt) < dayEnd.AddDays(7));
            else if (f.StartsWith("atras")) q = q.Where(t => (t.DueAt ?? t.RemindAt) < now);
        }
        return await q.OrderByDescending(t => t.Priority).ThenBy(t => t.DueAt ?? t.RemindAt ?? DateTime.MaxValue).ThenBy(t => t.Id).Take(40).ToListAsync();
    }

    public Task SaveAsync() => _db.SaveChangesAsync();
    public async Task RemoveAsync(PersonalTask t) { _db.PersonalTasks.Remove(t); await _db.SaveChangesAsync(); }

    public async Task CompleteAsync(PersonalTask t)
    {
        if (t.RepeatMinutes is > 0) Advance(t);
        else { t.Done = true; t.DoneAt = DateTime.UtcNow; t.RemindAt = null; }
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private static void Advance(PersonalTask t)
    {
        var step = TimeSpan.FromMinutes(t.RepeatMinutes!.Value);
        var now = DateTime.UtcNow;
        if (t.RemindAt.HasValue) { var n = t.RemindAt.Value; do n += step; while (n <= now); t.RemindAt = n; }
        if (t.DueAt.HasValue) { var n = t.DueAt.Value; do n += step; while (n <= now); t.DueAt = n; }
    }

    // ---------- Notas ----------
    public async Task<PersonalNote> AddNoteAsync(int userId, string? title, string text, string? tag)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Informe o texto da nota.");
        var n = new PersonalNote { UserId = userId, Title = title?.Trim().Truncate(200), Text = text.Trim().Truncate(4000), Tag = tag?.Trim().Truncate(60) };
        _db.PersonalNotes.Add(n);
        await _db.SaveChangesAsync();
        return n;
    }

    // ---------- Gastos ----------
    public async Task<PersonalExpense> AddExpenseAsync(int userId, decimal amount, string description, string? category, bool income, DateTime? whenUtc)
    {
        if (amount <= 0) throw new ArgumentException("Informe um valor válido.");
        var e = new PersonalExpense { UserId = userId, Amount = amount, Description = description.Trim().Truncate(300), Category = category?.Trim().Truncate(60), IsIncome = income, Date = whenUtc ?? DateTime.UtcNow };
        _db.PersonalExpenses.Add(e);
        await _db.SaveChangesAsync();
        return e;
    }

    // ---------- Lembretes e resumo diário (chamado pelo agendador) ----------
    public async Task<int> RunRemindersAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await _db.PersonalTasks.Where(t => !t.Done && t.RemindAt != null && t.RemindAt <= now).OrderBy(t => t.RemindAt).Take(30).ToListAsync(ct);
        foreach (var t in due)
        {
            var text = $"⏰ Lembrete: {t.Title}" + (t.DueAt.HasValue ? $" ({AppTime.ToLocal(t.DueAt.Value):dd/MM HH:mm})" : "") + $"\nResponda \"concluir {t.Id}\" quando terminar ou \"adiar {t.Id} para amanhã 9h\".";
            if (t.RepeatMinutes is > 0) Advance(t); else t.RemindAt = null;
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(t.UserId, "Lembrete: " + t.Title.Truncate(120), text, ct);
        }

        // Resumo da manhã (08h–12h, uma vez por dia por usuário)
        var local = AppTime.Now;
        if (local.Hour is >= 8 and < 12)
        {
            var today = local.ToString("yyyy-MM-dd");
            var users = await _db.Users.Where(u => u.IsActive && u.Phone != null).ToListAsync(ct);
            foreach (var u in users)
            {
                var key = $"briefing:{u.Id}";
                var st = await _db.Set<AppSetting>().FirstOrDefaultAsync(x => x.Key == key, ct);
                if (st?.Value == today) continue;
                var text = await BriefingAsync(u.Id, u.Name);
                if (st == null) _db.Set<AppSetting>().Add(new AppSetting { Key = key, Value = today }); else { st.Value = today; st.UpdatedAt = now; }
                await _db.SaveChangesAsync(ct);
                if (text != null) await NotifyAsync(u.Id, "Seu dia", text, ct, panel: false);
            }
        }
        return due.Count;
    }

    public async Task<string?> BriefingAsync(int userId, string name)
    {
        var tasks = await ListTasksAsync(userId, "hoje");
        if (tasks.Count == 0) return null;
        var now = DateTime.UtcNow;
        var late = tasks.Where(t => (t.DueAt ?? t.RemindAt) < AppTime.TodayStartUtc).ToList();
        var todayList = tasks.Except(late).ToList();
        var sb = new StringBuilder($"☀️ Bom dia, {name.Split(' ')[0]}! Seu dia:\n");
        if (todayList.Count > 0) sb.AppendLine("\nHoje:\n" + string.Join("\n", todayList.Select(Fmt)));
        if (late.Count > 0) sb.AppendLine("\nAtrasadas:\n" + string.Join("\n", late.Select(Fmt)));
        sb.Append("\nÉ só me responder para concluir, adiar ou criar novas tarefas.");
        return sb.ToString();
    }

    private async Task NotifyAsync(int userId, string title, string text, CancellationToken ct, bool panel = true)
    {
        try { if (panel) await _notif.CreateAsync(NotificationType.Info, NotificationSource.Automation, title, text, null, userId); } catch (Exception ex) { _log.LogDebug(ex, "notificação"); }
        try
        {
            var phone = await _db.Users.Where(u => u.Id == userId).Select(u => u.Phone).FirstOrDefaultAsync(ct);
            if (phone != null && _wa.IsConfigured) await _wa.SendTextAsync(phone, text, null, fromAgent: true, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Falha ao enviar lembrete por WhatsApp"); }
    }
}

// =================== Ferramentas do agente ===================

public abstract class PersonalToolBase : AgentToolBase
{
    protected readonly PersonalService P;
    protected PersonalToolBase(PersonalService p) => P = p;
    public override string Permission => Perm.AiUse;
    protected static int Uid(AgentContext c) => c.UserId ?? throw new InvalidOperationException("Usuário não identificado.");

    protected static DateTime? Local(JsonObject a, string key)
    {
        var s = Arg(a, key);
        if (s == null) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new ArgumentException($"Não entendi a data/hora '{s}'. Diga por exemplo: amanhã às 9h.");
        if (s.Length <= 10) d = d.Date.AddHours(9);
        return AppTime.ToUtc(d);
    }
}

public class AddTaskTool : PersonalToolBase
{
    public AddTaskTool(PersonalService p) : base(p) { }
    public override string Name => "criar_tarefa";
    public override string Description => "Cria tarefa/lembrete pessoal do usuário (compromissos, contas a pagar, coisas a fazer). Avisa no WhatsApp na hora marcada";
    public override string ArgsHelp => "{\"titulo\":\"texto\",\"quando\":\"yyyy-MM-ddTHH:mm local (prazo e hora do aviso)\",\"lembrar_em\":\"yyyy-MM-ddTHH:mm (opcional, se diferente do prazo)\",\"prioridade\":\"normal|alta|urgente\",\"categoria\":\"opcional\",\"repetir\":\"diaria|semanal|mensal|null\",\"detalhes\":\"opcional\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var due = Local(a, "quando");
        var remind = Local(a, "lembrar_em");
        if (due.HasValue && due <= DateTime.UtcNow && remind == null) throw new InvalidOperationException("Esse horário já passou. Para quando devo agendar?");
        var pr = RuleBasedInterpreter.Fold(Arg(a, "prioridade") ?? "");
        var t = await P.AddTaskAsync(Uid(ctx), Arg(a, "titulo") ?? "", due, remind, pr.StartsWith("urg") ? 2 : pr.StartsWith("alt") ? 1 : 0, Arg(a, "categoria"), PersonalService.RepeatToMinutes(Arg(a, "repetir")), Arg(a, "detalhes"));
        return "✅ Anotado: " + PersonalService.Fmt(t) + (t.RemindAt.HasValue ? "\nVou te avisar na hora." : "");
    }
}

public class ListTasksTool : PersonalToolBase
{
    public ListTasksTool(PersonalService p) : base(p) { }
    public override string Name => "listar_tarefas";
    public override string Description => "Lista as tarefas/agenda pessoal do usuário";
    public override string ArgsHelp => "{\"filtro\":\"pendentes|hoje|amanha|semana|atrasadas|concluidas\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var list = await P.ListTasksAsync(Uid(ctx), Arg(a, "filtro"));
        if (list.Count == 0) return "Nenhuma tarefa " + (Arg(a, "filtro") ?? "pendente") + ". 🎉";
        return $"Suas tarefas ({list.Count}):\n" + string.Join("\n", list.Select(PersonalService.Fmt));
    }
}

public class CompleteTaskTool : PersonalToolBase
{
    public CompleteTaskTool(PersonalService p) : base(p) { }
    public override string Name => "concluir_tarefa";
    public override string Description => "Marca uma tarefa pessoal como concluída (tarefas repetidas avançam para a próxima data)";
    public override string ArgsHelp => "{\"tarefa\":\"número ou parte do título\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (t, err) = await P.FindTaskAsync(Uid(ctx), Arg(a, "tarefa"));
        if (t == null) return err!;
        var rep = t.RepeatMinutes is > 0;
        await P.CompleteAsync(t);
        return rep ? $"✅ Feita! Próxima ocorrência: {PersonalService.Fmt(t)}" : $"✅ Concluída: {t.Title}";
    }
}

public class RescheduleTaskTool : PersonalToolBase
{
    public RescheduleTaskTool(PersonalService p) : base(p) { }
    public override string Name => "adiar_tarefa";
    public override string Description => "Muda o prazo/horário de aviso de uma tarefa pessoal";
    public override string ArgsHelp => "{\"tarefa\":\"número ou parte do título\",\"quando\":\"yyyy-MM-ddTHH:mm local\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var when = Local(a, "quando") ?? throw new InvalidOperationException("Para quando devo adiar?");        if (when <= DateTime.UtcNow) throw new InvalidOperationException("Esse horário já passou.");
        var (t, err) = await P.FindTaskAsync(Uid(ctx), Arg(a, "tarefa"));
        if (t == null) return err!;
        t.DueAt = when; t.RemindAt = when;
        await P.SaveAsync();
        return "📅 Reagendada: " + PersonalService.Fmt(t);
    }
}

public class DeleteTaskTool : PersonalToolBase
{
    public DeleteTaskTool(PersonalService p) : base(p) { }
    public override string Name => "apagar_tarefa";
    public override string Description => "Apaga uma tarefa pessoal (exige confirmação)";
    public override string ArgsHelp => "{\"tarefa\":\"número ou parte do título\"}";
    public override bool RequiresConfirmation => true;
    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var (t, err) = await P.FindTaskAsync(Uid(ctx), Arg(a, "tarefa"), includeDone: true);
        return t == null ? new(err, a, "") : new(null, new JsonObject { ["id"] = t.Id }, "Apagar a tarefa: " + PersonalService.Fmt(t) + "?");
    }
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (t, err) = await P.FindTaskAsync(Uid(ctx), Arg(a, "id"), includeDone: true);
        if (t == null) return err!;
        await P.RemoveAsync(t);
        return "🗑️ Tarefa apagada.";
    }
}

public class AddNoteTool : PersonalToolBase
{
    public AddNoteTool(PersonalService p) : base(p) { }
    public override string Name => "anotar";
    public override string Description => "Salva uma nota/ideia/lista (ex.: lista de compras, senha de wifi, ideia de negócio)";
    public override string ArgsHelp => "{\"texto\":\"conteúdo\",\"titulo\":\"opcional\",\"tag\":\"opcional\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var n = await P.AddNoteAsync(Uid(ctx), Arg(a, "titulo"), Arg(a, "texto") ?? "", Arg(a, "tag"));
        return $"📝 Nota #{n.Id} salva" + (n.Tag != null ? $" ({n.Tag})" : "") + ".";
    }
}

public class SearchNotesTool : PersonalToolBase
{
    private readonly AppDbContext _db;
    public SearchNotesTool(PersonalService p, AppDbContext db) : base(p) => _db = db;
    public override string Name => "buscar_notas";
    public override string Description => "Busca/lista as notas pessoais salvas";
    public override string ArgsHelp => "{\"busca\":\"palavra opcional\",\"tag\":\"opcional\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var uid = Uid(ctx);
        var q = _db.PersonalNotes.Where(n => n.UserId == uid);
        if (Arg(a, "busca")?.ToLower() is { } s) q = q.Where(n => n.Text.ToLower().Contains(s) || (n.Title != null && n.Title.ToLower().Contains(s)));
        if (Arg(a, "tag")?.ToLower() is { } tg) q = q.Where(n => n.Tag != null && n.Tag.ToLower() == tg);
        var list = await q.OrderByDescending(n => n.Id).Take(10).ToListAsync(ct);
        if (list.Count == 0) return "Nenhuma nota encontrada.";
        return string.Join("\n\n", list.Select(n => $"📝 #{n.Id}{(n.Title != null ? " " + n.Title : "")}{(n.Tag != null ? $" [{n.Tag}]" : "")} — {AppTime.ToLocal(n.CreatedAt):dd/MM}\n{n.Text}"));
    }
}

public class DeleteNoteTool : PersonalToolBase
{
    private readonly AppDbContext _db;
    public DeleteNoteTool(PersonalService p, AppDbContext db) : base(p) => _db = db;
    public override string Name => "apagar_nota";
    public override string Description => "Apaga uma nota pessoal pelo número (exige confirmação)";
    public override string ArgsHelp => "{\"id\":12}";
    public override bool RequiresConfirmation => true;
    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var uid = Uid(ctx);
        if (!int.TryParse(Arg(a, "id")?.TrimStart('#'), out var id)) return new("Informe o número da nota.", a, "");
        var n = await _db.PersonalNotes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
        return n == null ? new("Nota não encontrada.", a, "") : new(null, new JsonObject { ["id"] = id }, $"Apagar a nota #{id}: \"{n.Text.Truncate(80)}\"?");
    }
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var uid = Uid(ctx); var id = int.Parse(Arg(a, "id")!);
        var n = await _db.PersonalNotes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
        if (n == null) return "Nota não encontrada.";
        _db.PersonalNotes.Remove(n); await _db.SaveChangesAsync(ct);
        return "🗑️ Nota apagada.";
    }
}

public class AddExpenseTool : PersonalToolBase
{
    public AddExpenseTool(PersonalService p) : base(p) { }
    public override string Name => "registrar_gasto";
    public override string Description => "Registra um gasto ou uma receita pessoal (ex.: gastei 50 no almoço)";
    public override string ArgsHelp => "{\"valor\":50.00,\"descricao\":\"almoço\",\"categoria\":\"alimentação\",\"tipo\":\"gasto|receita\",\"data\":\"yyyy-MM-dd opcional\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        if (!decimal.TryParse(Arg(a, "valor")?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || v <= 0) throw new InvalidOperationException("Qual foi o valor?");
        var inc = RuleBasedInterpreter.Fold(Arg(a, "tipo") ?? "").StartsWith("rec");
        var when = Local(a, "data");
        var e = await P.AddExpenseAsync(Uid(ctx), v, Arg(a, "descricao") ?? (inc ? "Receita" : "Gasto"), Arg(a, "categoria"), inc, when);
        return $"{(inc ? "💰 Receita" : "💸 Gasto")} registrado: {PersonalService.Money(e.Amount)} — {e.Description}{(e.Category != null ? $" ({e.Category})" : "")}";
    }
}

public class ExpenseSummaryTool : PersonalToolBase
{
    private readonly AppDbContext _db;
    public ExpenseSummaryTool(PersonalService p, AppDbContext db) : base(p) => _db = db;
    public override string Name => "resumo_gastos";
    public override string Description => "Resumo dos gastos e receitas pessoais por período e categoria";
    public override string ArgsHelp => "{\"periodo\":\"hoje|semana|mes|todos\"}";
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var uid = Uid(ctx);
        var (from, label) = Period(Arg(a, "periodo") ?? "mes");
        var q = _db.PersonalExpenses.Where(e => e.UserId == uid);
        if (from.HasValue) q = q.Where(e => e.Date >= from);
        var rows = await q.ToListAsync(ct);
        if (rows.Count == 0) return $"Nenhum lançamento {label}.";
        var spent = rows.Where(r => !r.IsIncome).Sum(r => r.Amount);
        var inc = rows.Where(r => r.IsIncome).Sum(r => r.Amount);
        var sb = new StringBuilder($"💸 Gastos {label}: {PersonalService.Money(spent)}\n💰 Receitas: {PersonalService.Money(inc)}\nSaldo: {PersonalService.Money(inc - spent)}");
        var cats = rows.Where(r => !r.IsIncome).GroupBy(r => r.Category ?? "sem categoria").OrderByDescending(g => g.Sum(r => r.Amount)).Take(6);
        sb.Append("\n\nPor categoria:\n" + string.Join("\n", cats.Select(g => $"• {g.Key}: {PersonalService.Money(g.Sum(r => r.Amount))}")));
        return sb.ToString();
    }
}
