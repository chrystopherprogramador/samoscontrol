using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SamosControl.Application.Voice;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

/// <summary>Agenda uma ligação futura da Roberta para o PRÓPRIO usuário (número cadastrado, autorizado e com consentimento).</summary>
public class ScheduleCallTool : AgentToolBase
{
    private readonly VoiceCallManager _voice;
    private readonly AppDbContext _db;
    private readonly AutomationService _auto;
    public ScheduleCallTool(VoiceCallManager voice, AppDbContext db, AutomationService auto) { _voice = voice; _db = db; _auto = auto; }
    public override string Name => "agendar_ligacao";
    public override string Description => "Agenda uma ligação FUTURA da Roberta para o próprio usuário (ex.: 'me liga às 11h55 para lembrar de X'). Executa sozinha no horário, mesmo sem nova mensagem. Não é lembrete por WhatsApp";
    public override string ArgsHelp => "{\"quando\":\"yyyy-MM-ddTHH:mm (horário local)\",\"mensagem\":\"o que lembrar na ligação\"}";
    public override string Permission => Perm.VoiceCall;

    private async Task<(string? Error, DateTime Local, User? User)> CheckAsync(AgentContext ctx, JsonObject a)
    {
        if (!DateTime.TryParse(Arg(a, "quando"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return ("Não entendi quando ligar. Informe data e hora.", default, null);
        if (AppTime.ToUtc(local) <= DateTime.UtcNow.AddSeconds(30)) return ("O horário informado já passou.", default, null);
        if (string.IsNullOrWhiteSpace(Arg(a, "mensagem"))) return ("Sobre o que devo te lembrar na ligação?", default, null);
        var why = _voice.Readiness();
        if (why != null) return ($"Não consigo agendar ligações agora: {why}", default, null);
        var user = ctx.UserId.HasValue ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ctx.UserId && u.IsActive) : null;
        var key = PhoneUtil.Key(user?.Phone);
        if (user == null || key == null) return ("Seu usuário não tem telefone cadastrado, então não posso agendar a ligação.", default, null);
        var allowed = await _db.VoiceAllowedNumbers.AsNoTracking().AnyAsync(x => x.PhoneKey == key && x.Active && x.ConsentAt != null);
        if (!allowed) return ("Seu número não está na lista de autorizados para ligações (com consentimento). Cadastre em Ligações.", default, null);
        return (null, local, user);
    }

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var (err, local, _) = await CheckAsync(ctx, a);
        return err != null ? new(err, a, "") : new(null, a, $"Ligar para você em {local:dd/MM/yyyy HH:mm}");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (err, local, user) = await CheckAsync(ctx, a);
        if (err != null) throw new InvalidOperationException(err);
        var msg = Arg(a, "mensagem")!;
        var utc = AppTime.ToUtc(local);
        var cfg = new JsonObject { ["userId"] = user!.Id, ["message"] = msg, ["phase"] = "DIAL", ["attempt"] = 1, ["maxAttempts"] = 3, ["retryMinutes"] = 5, ["fallbackWhatsApp"] = "true", ["scheduledFor"] = utc.ToString("O") };
        var job = await _auto.CreateAsync($"Ligação: {msg.Truncate(80)}", JobType.PlaceCall, cfg.ToJsonString(), utc, null, user.Id);
        return $"✅ Ligação #{job.Id} agendada para {local:dd/MM/yyyy HH:mm}. Vou te ligar nesse horário e, se não atender, tento mais 2 vezes e aviso por WhatsApp.";
    }
}

public class ListScheduledCallsTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public ListScheduledCallsTool(AppDbContext db) => _db = db;
    public override string Name => "listar_ligacoes_agendadas";
    public override string Description => "Lista as ligações futuras agendadas do usuário e o resultado das últimas";
    public override string Permission => Perm.VoiceCall;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var jobs = await _db.AutomationJobs.AsNoTracking().Where(j => j.Type == JobType.PlaceCall && j.CreatedByUserId == ctx.UserId).OrderByDescending(j => j.Id).Take(10).ToListAsync(ct);
        if (jobs.Count == 0) return "Você não tem ligações agendadas.";
        return string.Join("\n", jobs.Select(j =>
        {
            var next = j.IsEnabled && j.NextRunAt.HasValue ? $"agendada para {AppTime.ToLocal(j.NextRunAt.Value):dd/MM HH:mm}" : j.LastStatus == ExecutionStatus.Failed ? "não concluída" : j.LastStatus == ExecutionStatus.Success ? "concluída" : "cancelada";
            return $"• #{j.Id} {(JsonNode.Parse(j.ConfigJson ?? "{}") as JsonObject)?["message"]} — {next}";
        }));
    }
}

public class CancelScheduledCallTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public CancelScheduledCallTool(AppDbContext db) => _db = db;
    public override string Name => "cancelar_ligacao_agendada";
    public override string Description => "Cancela uma ligação agendada do usuário";
    public override string ArgsHelp => "{\"id\":123}";
    public override string Permission => Perm.VoiceCall;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        if (!int.TryParse(Arg(a, "id"), out var id)) throw new InvalidOperationException("Informe o número da ligação agendada.");
        var job = await _db.AutomationJobs.FirstOrDefaultAsync(j => j.Id == id && j.Type == JobType.PlaceCall && j.CreatedByUserId == ctx.UserId, ct)
            ?? throw new InvalidOperationException("Não encontrei essa ligação agendada.");
        if (!job.IsEnabled) return $"A ligação #{id} já não está ativa.";
        job.IsEnabled = false; job.NextRunAt = null;
        await _db.SaveChangesAsync(ct);
        return $"✅ Ligação #{id} cancelada.";
    }
}
