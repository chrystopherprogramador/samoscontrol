using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record NoticeRunResult(int Sent, int Failed, int Skipped, string? Note);

/// <summary>
/// Avisos automáticos das cobranças em aberto por WhatsApp e e-mail, conforme a régua configurada em Configurações > Cobrança automática
/// (dias antes, no vencimento e dias depois). Cada regra dispara uma vez por canal; falhas são repetidas até 3 vezes.
/// </summary>
public class ChargeNoticeService
{
    public const string WhatsApp = "WHATSAPP", Email = "EMAIL";
    public const int MaxAttempts = 3, MaxPerRun = 300;

    public const string DefaultBefore = "Olá, {nome}! Passando para lembrar que sua cobrança de {valor} ({descricao}) vence em {vencimento}.\nPagamento: {link}\nPIX copia e cola:\n{pix}";
    public const string DefaultDue = "Olá, {nome}! Sua cobrança de {valor} ({descricao}) vence hoje ({vencimento}).\nPagamento: {link}\nPIX copia e cola:\n{pix}";
    public const string DefaultAfter = "Olá, {nome}! Identificamos que a cobrança de {valor} ({descricao}), vencida em {vencimento}, está em aberto há {dias} dia(s). Se já pagou, desconsidere.\nPagamento: {link}\nPIX copia e cola:\n{pix}";
    public const string DefaultSubject = "Cobrança de {valor} - vencimento {vencimento}";

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly WhatsAppService _wa;
    private readonly IEmailSender _email;

    public ChargeNoticeService(AppDbContext db, SettingsService settings, WhatsAppService wa, IEmailSender email)
    { _db = db; _settings = settings; _wa = wa; _email = email; }

    public sealed record Config(bool Enabled, HashSet<string> Channels, int[] Before, bool OnDue, int[] After, int Hour);

    public Config Load()
    {
        bool Bool(string key, bool def) => _settings.Get(key) is { } v ? v.Trim().ToLowerInvariant() is "true" or "1" or "sim" or "s" : def;
        var channels = (_settings.Get(SettingKeys.NoticeChannels) ?? "whatsapp,email")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToUpperInvariant()).ToHashSet();
        var hour = int.TryParse(_settings.Get(SettingKeys.NoticeHour), out var h) && h is >= 0 and <= 23 ? h : 9;
        return new(Bool(SettingKeys.NoticeEnabled, false), channels, Days(_settings.Get(SettingKeys.NoticeDaysBefore) ?? "3"),
            Bool(SettingKeys.NoticeOnDue, true), Days(_settings.Get(SettingKeys.NoticeDaysAfter) ?? "1,5,10"), hour);
    }

    public static int[] Days(string s) =>
        s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : -1)
         .Where(n => n > 0 && n <= 365).Distinct().OrderBy(n => n).ToArray();

    /// <summary>Which rule (if any) applies to the charge today. daysLate &lt; 0 means not yet due.</summary>
    public static string? RuleFor(Config cfg, int daysLate)
    {
        if (daysLate < 0) return cfg.Before.Contains(-daysLate) ? $"before:{-daysLate}" : null;
        if (daysLate == 0) return cfg.OnDue ? "due" : null;
        return cfg.After.Contains(daysLate) ? $"after:{daysLate}" : null;
    }

    public string Render(Charge ch, int daysLate)
    {
        var tpl = daysLate < 0 ? _settings.Get(SettingKeys.NoticeMsgBefore) ?? DefaultBefore
                : daysLate == 0 ? _settings.Get(SettingKeys.NoticeMsgDue) ?? DefaultDue
                : _settings.Get(SettingKeys.NoticeMsgAfter) ?? DefaultAfter;
        return Fill(tpl, ch, daysLate);
    }

    private static string Fill(string tpl, Charge ch, int daysLate)
    {
        var vars = new Dictionary<string, string?>
        {
            ["nome"] = ch.Customer?.Name, ["valor"] = ContractService.Money(ch.Amount), ["vencimento"] = ch.DueDate.ToString("dd/MM/yyyy"),
            ["descricao"] = ch.ServiceName ?? "serviço", ["link"] = ch.InvoiceUrl, ["pix"] = ch.PixPayload,
            ["dias"] = Math.Abs(daysLate).ToString()
        };
        // A line whose placeholder has no value (e.g. a charge without PIX) is dropped instead of leaving a dangling label.
        var lines = new List<string>();
        foreach (var line in tpl.Replace("\r\n", "\n").Split('\n'))
        {
            var drop = false; var l = line;
            foreach (var (k, v) in vars)
            {
                var ph = "{" + k + "}";
                if (!l.Contains(ph)) continue;
                if (string.IsNullOrWhiteSpace(v)) { drop = true; break; }
                l = l.Replace(ph, v);
            }
            if (!drop) lines.Add(l);
        }
        // Remove a trailing label such as "PIX copia e cola:" left alone after its value line was dropped.
        while (lines.Count > 0 && lines[^1].TrimEnd().EndsWith(':') && lines[^1].Length < 40) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines).Trim();
    }

    private string Subject(Charge ch, int daysLate) => Fill(_settings.Get(SettingKeys.NoticeEmailSubject) ?? DefaultSubject, ch, daysLate);

    private static int DaysLate(Charge ch) => (AppTime.Now.Date - DateTime.SpecifyKind(ch.DueDate, DateTimeKind.Unspecified).Date).Days;

    public async Task<NoticeRunResult> RunAsync(CancellationToken ct = default)
    {
        var cfg = Load();
        if (!cfg.Enabled) return new(0, 0, 0, "Avisos automáticos desativados.");
        if (AppTime.Now.Hour < cfg.Hour) return new(0, 0, 0, $"Aguardando as {cfg.Hour}h.");

        var open = await _db.Charges.Include(c => c.Customer)
            .Where(c => c.Status == ChargeStatus.Pendente || c.Status == ChargeStatus.Vencido).ToListAsync(ct);
        int sent = 0, failed = 0, skipped = 0;
        foreach (var ch in open)
        {
            if (sent + failed >= MaxPerRun) break;
            var late = DaysLate(ch);
            var rule = RuleFor(cfg, late);
            if (rule is null) continue;
            foreach (var channel in new[] { WhatsApp, Email }.Where(cfg.Channels.Contains))
            {
                var r = await TrySendAsync(ch, late, rule, channel, ct);
                if (r == true) sent++; else if (r == false) failed++; else skipped++;
            }
        }
        return new(sent, failed, skipped, null);
    }

    /// <summary>true = sent, false = failed, null = not applicable (already sent, no contact, channel not configured or attempts exhausted).</summary>
    private async Task<bool?> TrySendAsync(Charge ch, int late, string rule, string channel, CancellationToken ct)
    {
        var to = channel == WhatsApp ? ch.Customer?.WhatsApp ?? ch.Customer?.Phone : ch.Customer?.Email;
        if (string.IsNullOrWhiteSpace(to)) return null;
        if (channel == WhatsApp ? !_wa.IsConfigured : !_email.IsConfigured) return null;

        var notice = await _db.ChargeNotices.FirstOrDefaultAsync(n => n.ChargeId == ch.Id && n.Rule == rule && n.Channel == channel, ct);
        if (notice is { Success: true } || notice is { Attempts: >= MaxAttempts }) return null;
        if (notice is null) { notice = new ChargeNotice { ChargeId = ch.Id, Rule = rule, Channel = channel }; _db.ChargeNotices.Add(notice); }

        notice.Attempts++; notice.UpdatedAt = DateTime.UtcNow;
        try
        {
            await DeliverAsync(ch, late, channel, to, ct);
            notice.Success = true; notice.SentAt = DateTime.UtcNow; notice.Error = null;
        }
        catch (Exception ex) when (ex is IntegrationException or InvalidOperationException or HttpRequestException)
        {
            notice.Error = ex.Message.Truncate(500);
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return null; } // concurrent run already recorded this rule
        return notice.Success;
    }

    private async Task DeliverAsync(Charge ch, int late, string channel, string to, CancellationToken ct)
    {
        var text = Render(ch, late);
        if (channel == WhatsApp) await _wa.SendTextAsync(to, text, null, fromAgent: true, ct);
        else await _email.SendAsync(to, Subject(ch, late), text, ct);
    }

    /// <summary>Manual send from the charge screen. Always sends (recorded as manual) and reports failures to the caller.</summary>
    public async Task SendManualAsync(int chargeId, string channel, CancellationToken ct = default)
    {
        var ch = await _db.Charges.Include(c => c.Customer).FirstOrDefaultAsync(c => c.Id == chargeId, ct) ?? throw new KeyNotFoundException();
        if (ch.Status is not (ChargeStatus.Pendente or ChargeStatus.Vencido)) throw new InvalidOperationException("A cobrança não está em aberto.");
        var to = channel == WhatsApp ? ch.Customer?.WhatsApp ?? ch.Customer?.Phone : ch.Customer?.Email;
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException(channel == WhatsApp ? "Cliente sem WhatsApp/telefone." : "Cliente sem e-mail.");
        var late = DaysLate(ch);
        var notice = new ChargeNotice { ChargeId = ch.Id, Channel = channel, Rule = "manual:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), Attempts = 1 };
        try { await DeliverAsync(ch, late, channel, to, ct); notice.Success = true; notice.SentAt = DateTime.UtcNow; }
        catch (Exception ex) when (ex is IntegrationException or InvalidOperationException) { notice.Error = ex.Message.Truncate(500); throw; }
        finally { _db.ChargeNotices.Add(notice); await _db.SaveChangesAsync(CancellationToken.None); }
    }
}
