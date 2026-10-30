using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record AgentContext(int? UserId, string UserName, string Channel, string? Phone);
public record ToolPlan(string? Error, JsonObject Args, string Description);

/// <summary>
/// A safe, predefined capability of the agent. The LLM can only choose among these tools and provide arguments;
/// arguments are validated/resolved in <see cref="PlanAsync"/> before anything is executed.
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string ArgsHelp { get; }
    string Permission { get; }
    bool RequiresConfirmation { get; }
    Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject args);
    Task<string> ExecuteAsync(AgentContext ctx, JsonObject args, CancellationToken ct);
}

public abstract class AgentToolBase : IAgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string ArgsHelp => "{}";
    public abstract string Permission { get; }
    public virtual bool RequiresConfirmation => false;
    public virtual Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject args) => Task.FromResult(new ToolPlan(null, args, Description));
    public abstract Task<string> ExecuteAsync(AgentContext ctx, JsonObject args, CancellationToken ct);

    protected static readonly CultureInfo BR = new("pt-BR");
    protected static string Money(decimal v) => v.ToString("C2", BR);
    protected static string? Arg(JsonObject a, string k) => (a[k] as JsonValue)?.ToString() is { Length: > 0 } s ? s : null;

    protected static (DateTime? From, string Label) Period(string? p) => (p?.ToLowerInvariant()) switch
    {
        "hoje" or "today" => (AppTime.TodayStartUtc, "hoje"),
        "semana" or "week" => (AppTime.WeekStartUtc, "nesta semana"),
        "mes" or "mês" or "month" => (AppTime.MonthStartUtc, "neste mês"),
        _ => (null, "no total")
    };
}

public class QueryLeadsTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryLeadsTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_leads";
    public override string Description => "Conta leads por período e status";
    public override string ArgsHelp => "{\"periodo\":\"hoje|semana|mes|todos\"}";
    public override string Permission => Perm.CrmView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (from, label) = Period(Arg(a, "periodo"));
        var q = _db.Leads.AsQueryable();
        if (from.HasValue) q = q.Where(l => l.CreatedAt >= from);
        var total = await q.CountAsync(ct);
        var contacted = await q.CountAsync(l => l.Status != LeadStatus.Novo, ct);
        var by = await q.GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var sb = new StringBuilder($"Entraram {total} leads {label}. {contacted} já foram contatados.");
        if (by.Count > 0) sb.Append("\nPor status: " + string.Join(", ", by.Select(x => $"{x.Key}: {x.N}")));
        return sb.ToString();
    }
}

public class QueryCustomersTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryCustomersTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_clientes";
    public override string Description => "Conta/lista clientes, com busca opcional por nome";
    public override string ArgsHelp => "{\"busca\":\"texto opcional\"}";
    public override string Permission => Perm.CrmView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var s = Arg(a, "busca")?.ToLowerInvariant();
        var q = _db.Customers.Where(c => c.IsActive);
        if (s != null) q = q.Where(c => c.Name.ToLower().Contains(s));
        var total = await q.CountAsync(ct);
        var top = await q.OrderBy(c => c.Name).Take(10).Select(c => c.Name).ToListAsync(ct);
        return $"{total} clientes ativos{(s != null ? $" com '{s}'" : "")}." + (top.Count > 0 ? "\n" + string.Join("\n", top.Select(n => "• " + n)) : "");
    }
}

public class QueryChargesTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryChargesTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_cobrancas";
    public override string Description => "Lista cobranças por situação";
    public override string ArgsHelp => "{\"status\":\"abertas|vencidas|pagas|todas\"}";
    public override string Permission => Perm.BillingView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var st = (Arg(a, "status") ?? "abertas").ToLowerInvariant();
        ChargeStatus? f = st switch { "vencidas" or "vencida" or "inadimplentes" => ChargeStatus.Vencido, "pagas" or "paga" => ChargeStatus.Pago, "abertas" or "aberta" or "pendentes" => ChargeStatus.Pendente, _ => null };
        var q = _db.Charges.Include(c => c.Customer).AsQueryable();
        if (f.HasValue) q = q.Where(c => c.Status == f);
        var total = await q.CountAsync(ct);
        var sum = await q.SumAsync(c => (decimal?)c.Amount, ct) ?? 0;
        var items = await q.OrderBy(c => c.DueDate).Take(10).ToListAsync(ct);
        var sb = new StringBuilder($"{total} cobranças {(f?.ToString().ToLower() ?? "no total")} somando {Money(sum)}.");
        foreach (var c in items) sb.Append($"\n• {c.Customer?.Name} — {Money(c.Amount)} (venc. {c.DueDate:dd/MM/yyyy})");
        if (total > items.Count) sb.Append($"\n… e mais {total - items.Count}.");
        return sb.ToString();
    }
}

public class QueryContractsTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryContractsTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_contratos";
    public override string Description => "Conta contratos por período e situação";
    public override string ArgsHelp => "{\"periodo\":\"hoje|semana|mes|todos\",\"status\":\"assinados|aguardando|todos\"}";
    public override string Permission => Perm.ContractsView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (from, label) = Period(Arg(a, "periodo"));
        var st = (Arg(a, "status") ?? "todos").ToLowerInvariant();
        var q = _db.Contracts.AsQueryable();
        if (st.StartsWith("assin")) { q = q.Where(c => c.Status == ContractStatus.Assinado); if (from.HasValue) q = q.Where(c => c.SignedAt >= from); }
        else
        {
            if (st.StartsWith("aguard")) q = q.Where(c => c.Status == ContractStatus.AguardandoAssinatura);
            if (from.HasValue) q = q.Where(c => c.CreatedAt >= from);
        }
        var n = await q.CountAsync(ct);
        return $"{n} contratos {(st.StartsWith("assin") ? "assinados" : st.StartsWith("aguard") ? "aguardando assinatura" : "criados")} {label}.";
    }
}

public class QueryServersTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryServersTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_servidores";
    public override string Description => "Lista servidores monitorados e a situação dos seus serviços";
    public override string Permission => Perm.MonitoringView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var servers = await _db.Servers.Include(s => s.Services).OrderBy(s => s.Name).ToListAsync(ct);
        if (servers.Count == 0) return "Nenhum servidor cadastrado.";
        return string.Join("\n", servers.Select(s => $"• {s.Name} ({s.Host}): {s.Services.Count(x => x.Status == ServiceStatus.Online)} online, {s.Services.Count(x => x.Status == ServiceStatus.Degraded)} degradados, {s.Services.Count(x => x.Status == ServiceStatus.Offline)} offline"));
    }
}

public class QueryServicesTool : AgentToolBase
{
    private readonly AppDbContext _db;
    public QueryServicesTool(AppDbContext db) => _db = db;
    public override string Name => "consultar_servicos";
    public override string Description => "Lista serviços monitorados (use status=offline para os que estão fora do ar)";
    public override string ArgsHelp => "{\"status\":\"offline|todos\"}";
    public override string Permission => Perm.MonitoringView;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var offlineOnly = (Arg(a, "status") ?? "todos").ToLowerInvariant().StartsWith("off");
        var q = _db.Services.Include(s => s.Server).Where(s => s.IsEnabled);
        if (offlineOnly) q = q.Where(s => s.Status == ServiceStatus.Offline);
        var list = await q.OrderBy(s => s.Name).Take(30).ToListAsync(ct);
        if (list.Count == 0) return offlineOnly ? "✅ Nenhum serviço fora do ar." : "Nenhum serviço monitorado.";
        return (offlineOnly ? "🚨 Serviços fora do ar:\n" : "Serviços:\n") + string.Join("\n", list.Select(s => $"• {s.Server?.Name} / {s.Name}: {s.Status.ToString().ToUpperInvariant()}"));
    }
}

public class CompanySummaryTool : AgentToolBase
{
    private readonly DashboardService _dash;
    public CompanySummaryTool(DashboardService d) => _dash = d;
    public override string Name => "resumo_empresa";
    public override string Description => "Resumo geral: leads, clientes, contratos, cobranças, serviços e tarefas";
    public override string Permission => Perm.DashboardView;
    public override Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct) => _dash.SummaryTextAsync();
}

public class CreateJobTool : AgentToolBase
{
    private readonly AutomationService _auto;
    public CreateJobTool(AutomationService a) => _auto = a;
    public override string Name => "criar_job";
    public override string Description => "Agenda uma automação do SISTEMA (sincronizar leads, relatório, verificar serviços/cobranças). Para lembretes e tarefas pessoais use criar_tarefa";
    public override string ArgsHelp => "{\"tipo\":\"REMINDER|SYNC_LEADS|CHECK_SERVICES|GENERATE_REPORT|CHECK_BILLING\",\"quando\":\"yyyy-MM-ddTHH:mm (horário local)\",\"mensagem\":\"texto do lembrete\",\"recorrencia_minutos\":null}";
    public override string Permission => Perm.AutomationManage;

    public override Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject args) => Task.FromResult(Validate(args));

    private static ToolPlan Validate(JsonObject a)
    {
        if (!Enum.TryParse<JobType>((Arg(a, "tipo") ?? "REMINDER").Replace("_", ""), true, out var type) || type == JobType.SendWhatsApp)
            return new("Tipo de tarefa não permitido.", a, "");
        if (!DateTime.TryParse(Arg(a, "quando"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new("Não entendi quando agendar. Informe data e hora.", a, "");
        var utc = AppTime.ToUtc(local);
        if (utc <= DateTime.UtcNow) return new("O horário informado já passou.", a, "");
        return new(null, a, $"Criar tarefa {type} para {local:dd/MM/yyyy HH:mm}");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var plan = Validate(a);
        if (plan.Error != null) throw new InvalidOperationException(plan.Error);
        var type = Enum.Parse<JobType>((Arg(a, "tipo") ?? "REMINDER").Replace("_", ""), true);
        var local = DateTime.Parse(Arg(a, "quando")!, CultureInfo.InvariantCulture);
        var msg = Arg(a, "mensagem") ?? "Tarefa agendada pelo SamosAgent";
        var cfg = new JsonObject { ["message"] = msg };
        if (ctx.UserId.HasValue) cfg["userId"] = ctx.UserId.Value;
        int? rec = int.TryParse(Arg(a, "recorrencia_minutos"), out var r) && r > 0 ? r : null;
        var job = await _auto.CreateAsync(type == JobType.Reminder ? "Lembrete: " + msg.Truncate(80) : $"{type} (agente)", type, cfg.ToJsonString(), AppTime.ToUtc(local), rec, ctx.UserId);
        return $"✅ Tarefa #{job.Id} criada ({type}) para {local:dd/MM/yyyy HH:mm}.";
    }
}

public class SyncLeadsTool : AgentToolBase
{
    private readonly LeadSyncService _sync;
    public SyncLeadsTool(LeadSyncService s) => _sync = s;
    public override string Name => "sincronizar_leads";
    public override string Description => "Executa a sincronização de leads com o Google Maps Scraper";
    public override string Permission => Perm.AutomationManage;
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var r = await _sync.SyncAsync(ct);
        return r.Status == "NAO_CONFIGURADO" ? "O Google Scraper não está configurado." : $"Sincronização {r.Status}: {r.Found} encontrados, {r.Created} criados, {r.Updated} atualizados, {r.Errors} erros.";
    }
}

public class CreateLeadSearchTool : AgentToolBase
{
    private readonly LeadSyncService _sync;
    private readonly IGoogleMapsScraperIntegration _scraper;
    public CreateLeadSearchTool(LeadSyncService s, IGoogleMapsScraperIntegration g) { _sync = s; _scraper = g; }
    public override string Name => "criar_pesquisa_leads";
    public override string Description => "Cria uma pesquisa no Google Maps Scraper para importar leads (ex.: lojas de roupas em Goiânia)";
    public override string ArgsHelp => "{\"consulta\":\"lojas de roupas\",\"local\":\"Goiânia\"}";
    public override string Permission => Perm.CrmEdit;
    public override bool RequiresConfirmation => true;
    public override Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        if (!_scraper.IsConfigured) return Task.FromResult(new ToolPlan("O Google Scraper não está configurado.", a, ""));
        var q = Arg(a, "consulta");
        if (q == null) return Task.FromResult(new ToolPlan("Informe o que pesquisar.", a, ""));
        return Task.FromResult(new ToolPlan(null, a, $"Pesquisar leads: \"{q}\"{(Arg(a, "local") is { } l ? $" em {l}" : "")}"));
    }
    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var s = await _sync.CreateSearchAsync(Arg(a, "consulta")!, Arg(a, "local"), Arg(a, "consulta"), ct);
        return $"✅ Pesquisa #{s.Id} criada. Os leads serão importados na próxima sincronização.";
    }
}

public class CreateChargeTool : AgentToolBase
{
    private readonly AppDbContext _db;
    private readonly BillingService _billing;
    public CreateChargeTool(AppDbContext db, BillingService b) { _db = db; _billing = b; }
    public override string Name => "criar_cobranca";
    public override string Description => "Gera cobrança PIX para um cliente (exige confirmação)";
    public override string ArgsHelp => "{\"cliente\":\"nome\",\"valor\":2000.00,\"vencimento\":\"yyyy-MM-dd (opcional)\",\"descricao\":\"opcional\"}";
    public override string Permission => Perm.BillingCreate;
    public override bool RequiresConfirmation => true;

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        if (!_billing.IsConfigured) return new("Nenhum provedor de cobrança (Asaas/Efí) está configurado.", a, "");
        var name = Arg(a, "cliente")?.ToLowerInvariant();
        if (name == null) return new("Informe o cliente.", a, "");
        if (!decimal.TryParse(Arg(a, "valor")?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) || v <= 0) return new("Informe um valor válido.", a, "");
        var matches = await _db.Customers.Where(c => c.IsActive && c.Name.ToLower().Contains(name)).OrderBy(c => c.Name).Take(5).ToListAsync();
        if (matches.Count == 0) return new($"Não encontrei cliente '{Arg(a, "cliente")}'.", a, "");
        var exact = matches.FirstOrDefault(c => c.Name.ToLower() == name);
        if (matches.Count > 1 && exact == null) return new("Encontrei vários clientes: " + string.Join("; ", matches.Select(m => m.Name)) + ". Seja mais específico.", a, "");
        var cust = exact ?? matches[0];
        var due = DateTime.TryParse(Arg(a, "vencimento"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : AppTime.Now.Date.AddDays(3);
        var norm = new JsonObject { ["customerId"] = cust.Id, ["valor"] = v, ["vencimento"] = due.ToString("yyyy-MM-dd"), ["descricao"] = Arg(a, "descricao") };
        return new(null, norm, $"Encontrei o cliente {cust.Name}.\nCriar cobrança PIX de {Money(v)} com vencimento em {due:dd/MM/yyyy}?");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var id = int.Parse(Arg(a, "customerId")!);
        var amount = decimal.Parse(Arg(a, "valor")!, CultureInfo.InvariantCulture);
        var due = DateTime.Parse(Arg(a, "vencimento")!, CultureInfo.InvariantCulture);
        var ch = await _billing.CreateChargeAsync(id, null, Arg(a, "descricao"), amount, due, BillingType.Pix, Arg(a, "descricao"), ct);
        return $"✅ Cobrança #{ch.Id} criada: {Money(ch.Amount)} • venc. {ch.DueDate:dd/MM/yyyy}.\n{ch.InvoiceUrl}";
    }
}

public class SendWhatsAppTool : AgentToolBase
{
    private readonly AppDbContext _db;
    private readonly WhatsAppService _wa;
    public SendWhatsAppTool(AppDbContext db, WhatsAppService wa) { _db = db; _wa = wa; }
    public override string Name => "enviar_whatsapp";
    public override string Description => "Envia mensagem de WhatsApp a um cliente/lead (exige confirmação)";
    public override string ArgsHelp => "{\"destinatario\":\"nome do cliente/lead\",\"texto\":\"mensagem\"}";
    public override string Permission => Perm.WhatsAppSend;
    public override bool RequiresConfirmation => true;

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        if (!_wa.IsConfigured) return new("A Evolution API não está configurada.", a, "");
        var to = Arg(a, "destinatario")?.ToLowerInvariant();
        var text = Arg(a, "texto");
        if (to == null || text == null) return new("Informe destinatário e texto.", a, "");
        string? name = null, phone = null;
        var c = await _db.Customers.Where(x => x.Name.ToLower().Contains(to)).Take(2).ToListAsync();
        if (c.Count > 1) return new("Mais de um cliente encontrado. Seja mais específico.", a, "");
        if (c.Count == 1) { name = c[0].Name; phone = c[0].WhatsApp ?? c[0].Phone; }
        else
        {
            var l = await _db.Leads.Where(x => x.Name.ToLower().Contains(to) || (x.CompanyName != null && x.CompanyName.ToLower().Contains(to))).Take(2).ToListAsync();
            if (l.Count > 1) return new("Mais de um lead encontrado. Seja mais específico.", a, "");
            if (l.Count == 1) { name = l[0].Name; phone = l[0].WhatsApp ?? l[0].Phone; }
        }
        if (phone == null || PhoneUtil.Normalize(phone) == null) return new("Não encontrei destinatário com telefone válido.", a, "");
        return new(null, new JsonObject { ["phone"] = PhoneUtil.Normalize(phone), ["texto"] = text }, $"Enviar WhatsApp para {name} ({phone}):\n\"{text}\"");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        await _wa.SendTextAsync(Arg(a, "phone")!, Arg(a, "texto")!, ctx.UserId, fromAgent: true, ct);
        return "✅ Mensagem enviada.";
    }
}
