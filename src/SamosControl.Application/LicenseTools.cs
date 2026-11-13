using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SamosControl.Domain;

namespace SamosControl.Application;

/// <summary>Base das ferramentas de licença do Axis: localiza o painel por subdomínio ou nome.</summary>
public abstract class LicenseToolBase : AgentToolBase
{
    protected readonly AxisBridgeService Axis;
    protected LicenseToolBase(AxisBridgeService axis) => Axis = axis;

    protected static string Validade(AxisBridgeService.AxisPainel p) =>
        p.Validade is null ? "sem validade" : $"{p.Validade:dd/MM/yyyy}" + (p.Validade.Value.Date < DateTime.Today ? " (vencida)" : "");

    protected async Task<(AxisBridgeService.AxisPainel? Painel, string? Error)> ResolveAsync(JsonObject a, CancellationToken ct = default)
    {
        var q = Arg(a, "painel")?.Trim().ToLowerInvariant();
        if (q == null) return (null, "Informe o painel (subdomínio ou nome do cliente).");
        List<AxisBridgeService.AxisPainel> all;
        try { all = await Axis.ListPanelsAsync(ct); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) { return (null, "Não consegui consultar o Axis: " + ex.Message); }
        var exact = all.Where(p => p.Subdominio.ToLowerInvariant() == q).ToList();
        var hits = exact.Count > 0 ? exact : all.Where(p => p.Subdominio.ToLowerInvariant().Contains(q) || p.Nome.ToLowerInvariant().Contains(q)).Take(5).ToList();
        if (hits.Count == 0) return (null, $"Não encontrei painel '{Arg(a, "painel")}'.");
        if (hits.Count > 1) return (null, "Encontrei vários painéis: " + string.Join("; ", hits.Select(h => $"{h.Nome} ({h.Subdominio})")) + ". Seja mais específico.");
        return (hits[0], null);
    }
}

public class QueryLicensesTool : LicenseToolBase
{
    public QueryLicensesTool(AxisBridgeService axis) : base(axis) { }
    public override string Name => "consultar_licencas";
    public override string Description => "Lista licenças (painéis) do Axis com validade e valor";
    public override string ArgsHelp => "{\"situacao\":\"vencidas|a_vencer|suspensas|todas\",\"busca\":\"texto opcional\"}";
    public override string Permission => Perm.BillingView;

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var all = await Axis.ListPanelsAsync(ct);
        var today = DateTime.Today;
        var busca = Arg(a, "busca")?.ToLowerInvariant();
        IEnumerable<AxisBridgeService.AxisPainel> q = all;
        if (busca != null) q = q.Where(p => p.Subdominio.ToLowerInvariant().Contains(busca) || p.Nome.ToLowerInvariant().Contains(busca));
        var sit = (Arg(a, "situacao") ?? "todas").ToLowerInvariant();
        q = sit switch
        {
            "vencidas" or "vencida" => q.Where(p => p.Ativo && p.Validade is { } v && v.Date < today),
            "a_vencer" or "avencer" => q.Where(p => p.Ativo && p.Validade is { } v && v.Date >= today && v.Date <= today.AddDays(15)),
            "suspensas" or "suspenso" => q.Where(p => !p.Ativo),
            _ => q
        };
        var list = q.OrderBy(p => p.Validade ?? DateTime.MaxValue).ToList();
        var sb = new StringBuilder($"{list.Count} painel(is) ({sit}).");
        foreach (var p in list.Take(15))
            sb.Append($"\n• {p.Nome} [{p.Subdominio}] — validade {Validade(p)} — {Money(p.ValorMensal)}/mês{(p.Ativo ? "" : " — SUSPENSO")}");
        if (list.Count > 15) sb.Append($"\n… e mais {list.Count - 15}.");
        return sb.ToString();
    }
}

public class RenewLicenseTool : LicenseToolBase
{
    public RenewLicenseTool(AxisBridgeService axis) : base(axis) { }
    public override string Name => "renovar_licenca";
    public override string Description => "Renova a licença de um painel do Axis por N meses (exige confirmação)";
    public override string ArgsHelp => "{\"painel\":\"subdomínio ou nome\",\"meses\":1}";
    public override string Permission => Perm.BillingCreate;
    public override bool RequiresConfirmation => true;

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var meses = int.TryParse(Arg(a, "meses"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 1;
        if (meses is < 1 or > 24) return new("Informe de 1 a 24 meses.", a, "");
        var (p, err) = await ResolveAsync(a);
        if (p == null) return new(err, a, "");
        return new(null, new JsonObject { ["painel"] = p.Subdominio, ["meses"] = meses },
            $"Renovar a licença de {p.Nome} [{p.Subdominio}] por {meses} mês(es)? Validade atual: {Validade(p)}.");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var r = await Axis.LicenseActionAsync(Arg(a, "painel")!, "renovar", int.Parse(Arg(a, "meses")!), ct);
        return "✅ Licença renovada. " + r;
    }
}

public class SetLicenseStatusTool : LicenseToolBase
{
    public SetLicenseStatusTool(AxisBridgeService axis) : base(axis) { }
    public override string Name => "alterar_status_licenca";
    public override string Description => "Suspende ou reativa o painel de um cliente no Axis (exige confirmação)";
    public override string ArgsHelp => "{\"painel\":\"subdomínio ou nome\",\"acao\":\"suspender|reativar\"}";
    public override string Permission => Perm.BillingCreate;
    public override bool RequiresConfirmation => true;

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var acao = (Arg(a, "acao") ?? "").ToLowerInvariant();
        if (acao is not ("suspender" or "reativar")) return new("Informe a ação: suspender ou reativar.", a, "");
        var (p, err) = await ResolveAsync(a);
        if (p == null) return new(err, a, "");
        if ((acao == "suspender") != p.Ativo) return new($"O painel {p.Nome} já está {(p.Ativo ? "ativo" : "suspenso")}.", a, "");
        return new(null, new JsonObject { ["painel"] = p.Subdominio, ["acao"] = acao },
            $"{(acao == "suspender" ? "Suspender" : "Reativar")} o painel de {p.Nome} [{p.Subdominio}]?");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var r = await Axis.LicenseActionAsync(Arg(a, "painel")!, Arg(a, "acao")!, 1, ct);
        return "✅ Feito. " + r;
    }
}

public class ChargeLicenseTool : LicenseToolBase
{
    public ChargeLicenseTool(AxisBridgeService axis) : base(axis) { }
    public override string Name => "cobrar_licenca";
    public override string Description => "Gera a cobrança PIX da mensalidade de um painel do Axis (exige confirmação)";
    public override string ArgsHelp => "{\"painel\":\"subdomínio ou nome\"}";
    public override string Permission => Perm.BillingCreate;
    public override bool RequiresConfirmation => true;

    public override async Task<ToolPlan> PlanAsync(AgentContext ctx, JsonObject a)
    {
        var (p, err) = await ResolveAsync(a);
        if (p == null) return new(err, a, "");
        if (p.Validade is null || p.Documento.Length is not (11 or 14) || p.ValorMensal <= 0)
            return new($"{p.Nome} não tem validade, CPF/CNPJ ou valor mensal cadastrados no Axis.", a, "");
        return new(null, new JsonObject { ["painel"] = p.Subdominio },
            $"Gerar cobrança PIX de {Money(p.ValorMensal)} da licença de {p.Nome} [{p.Subdominio}] (validade {Validade(p)})?");
    }

    public override async Task<string> ExecuteAsync(AgentContext ctx, JsonObject a, CancellationToken ct)
    {
        var (p, err) = await ResolveAsync(a, ct);
        if (p == null) return err!;
        var ch = await Axis.ChargeLicenseAsync(p, ct);
        return ch == null ? "Já existe cobrança para esta validade; nada foi gerado."
            : $"✅ Cobrança #{ch.Id} criada: {Money(ch.Amount)} • venc. {ch.DueDate:dd/MM/yyyy}.\n{ch.InvoiceUrl}";
    }
}
