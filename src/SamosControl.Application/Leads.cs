using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record ImportResult(int Found, int Created, int Updated, int Errors);

public class LeadService
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly ICurrentUser _user;
    public LeadService(AppDbContext db, AuditService audit, ICurrentUser user) { _db = db; _audit = audit; _user = user; }

    public static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static string? WebsiteKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (!u.Contains("://")) u = "http://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    public static void ApplyKeys(Lead l)
    {
        l.PhoneKey = PhoneUtil.Key(l.Phone) ?? PhoneUtil.Key(l.WhatsApp);
        l.WebsiteKey = WebsiteKey(l.Website);
        var n = Slug(l.CompanyName ?? l.Name);
        var a = Slug(l.Address);
        l.NameAddressKey = n.Length > 0 && a.Length > 0 ? AuthService.Sha(n + "|" + a) : null;
        if (string.IsNullOrWhiteSpace(l.GooglePlaceId)) l.GooglePlaceId = null;
    }

    /// <summary>Duplicate detection by Google Place ID, phone, website, then name+address.</summary>
    public async Task<(Lead? Lead, string? Reason)> FindDuplicateAsync(Lead c, int? excludeId = null)
    {
        ApplyKeys(c);
        IQueryable<Lead> q = _db.Leads;
        if (excludeId.HasValue) q = q.Where(x => x.Id != excludeId);
        if (c.GooglePlaceId != null)
        {
            var m = await q.FirstOrDefaultAsync(x => x.GooglePlaceId == c.GooglePlaceId);
            if (m != null) return (m, "Google Place ID");
        }
        var phoneKeys = new[] { PhoneUtil.Key(c.Phone), PhoneUtil.Key(c.WhatsApp) }.Where(x => x != null).ToList();
        if (phoneKeys.Count > 0)
        {
            var m = await q.FirstOrDefaultAsync(x => phoneKeys.Contains(x.PhoneKey));
            if (m != null) return (m, "telefone");
        }
        if (c.WebsiteKey != null)
        {
            var m = await q.FirstOrDefaultAsync(x => x.WebsiteKey == c.WebsiteKey);
            if (m != null) return (m, "website");
        }
        if (c.NameAddressKey != null)
        {
            var m = await q.FirstOrDefaultAsync(x => x.NameAddressKey == c.NameAddressKey);
            if (m != null) return (m, "nome + endereço");
        }
        return (null, null);
    }

    /// <summary>Fills only missing fields of the existing lead. Returns true if anything changed.</summary>
    private static bool Merge(Lead target, Lead src)
    {
        var changed = false;
        void Fill(Func<Lead, string?> get, Action<Lead, string?> set)
        {
            if (string.IsNullOrWhiteSpace(get(target)) && !string.IsNullOrWhiteSpace(get(src))) { set(target, get(src)); changed = true; }
        }
        Fill(l => l.CompanyName, (l, v) => l.CompanyName = v);
        Fill(l => l.Phone, (l, v) => l.Phone = v);
        Fill(l => l.WhatsApp, (l, v) => l.WhatsApp = v);
        Fill(l => l.Email, (l, v) => l.Email = v);
        Fill(l => l.Address, (l, v) => l.Address = v);
        Fill(l => l.City, (l, v) => l.City = v);
        Fill(l => l.State, (l, v) => l.State = v);
        Fill(l => l.Segment, (l, v) => l.Segment = v);
        Fill(l => l.Website, (l, v) => l.Website = v);
        Fill(l => l.GooglePlaceId, (l, v) => l.GooglePlaceId = v);
        if (changed) ApplyKeys(target);
        return changed;
    }

    public async Task<(Lead Lead, bool Created, bool Updated)> CreateOrMergeAsync(Lead candidate)
    {
        var (dup, _) = await FindDuplicateAsync(candidate);
        if (dup != null)
        {
            var upd = Merge(dup, candidate);
            if (upd) await _db.SaveChangesAsync();
            return (dup, false, upd);
        }
        _db.Leads.Add(candidate);
        await _db.SaveChangesAsync();
        return (candidate, true, false);
    }

    public async Task<LeadSource> GetOrCreateSourceAsync(string name)
    {
        var s = await _db.LeadSources.FirstOrDefaultAsync(x => x.Name == name);
        if (s != null) return s;
        s = new LeadSource { Name = name };
        _db.LeadSources.Add(s);
        await _db.SaveChangesAsync();
        return s;
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<ScrapedPlace> places, string? segment, string? location, ILogger? log = null)
    {
        int found = 0, created = 0, updated = 0, errors = 0;
        var source = await GetOrCreateSourceAsync("Google Maps");
        foreach (var p in places)
        {
            found++;
            if (string.IsNullOrWhiteSpace(p.Name)) { errors++; continue; }
            try
            {
                var phone = Trunc(p.Phone, 40);
                var lead = new Lead
                {
                    Name = Trunc(p.Name, 200)!, CompanyName = Trunc(p.Name, 200), Phone = phone, WhatsApp = phone,
                    Email = Trunc(p.Email, 200), Address = Trunc(p.Address, 300), City = Trunc(p.City ?? location, 100),
                    State = p.State is { Length: 2 } ? p.State.ToUpperInvariant() : null,
                    Segment = Trunc(segment ?? p.Category, 100), Website = Trunc(p.Website, 300), GooglePlaceId = Trunc(p.PlaceId, 200),
                    SourceId = source.Id, Status = LeadStatus.Novo
                };
                var r = await CreateOrMergeAsync(lead);
                if (r.Created) created++; else if (r.Updated) updated++;
            }
            catch (Exception ex)
            {
                errors++;
                log?.LogWarning(ex, "Falha ao importar lead {Name}", p.Name);
                _db.ChangeTracker.Clear();
            }
        }
        return new ImportResult(found, created, updated, errors);
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    public async Task AddInteractionAsync(int? leadId, int? customerId, InteractionType type, string text)
    {
        _db.Interactions.Add(new Interaction { LeadId = leadId, CustomerId = customerId, Type = type, Description = text, UserId = _user.UserId, UserName = _user.UserName });
        if (leadId.HasValue)
        {
            var l = await _db.Leads.FindAsync(leadId.Value);
            if (l != null) l.LastInteractionAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    public async Task ChangeStatusAsync(int leadId, LeadStatus status)
    {
        var l = await _db.Leads.FindAsync(leadId) ?? throw new KeyNotFoundException("Lead não encontrado.");
        if (l.Status == status) return;
        var old = l.Status;
        l.Status = status;
        _db.Interactions.Add(new Interaction { LeadId = leadId, Type = InteractionType.Sistema, Description = $"Status alterado: {old} → {status}", UserId = _user.UserId, UserName = _user.UserName });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("crm", "lead_status", "Lead", leadId, old.ToString(), status.ToString());
    }

    public async Task<Customer> ConvertToCustomerAsync(int leadId)
    {
        var l = await _db.Leads.FindAsync(leadId) ?? throw new KeyNotFoundException("Lead não encontrado.");
        if (l.CustomerId.HasValue) return await _db.Customers.FirstAsync(c => c.Id == l.CustomerId);
        var company = string.IsNullOrWhiteSpace(l.CompanyName) ? null : new Company { Name = l.CompanyName!, Website = l.Website };
        var c = new Customer
        {
            Name = l.CompanyName ?? l.Name, Email = l.Email, Phone = l.Phone, WhatsApp = l.WhatsApp, Address = l.Address,
            City = l.City, State = l.State, LeadId = l.Id, Company = company,
            SellerId = l.AssignedUserId == null ? null : await _db.Sellers.Where(s => s.IsActive && s.UserId == l.AssignedUserId).Select(s => (int?)s.Id).FirstOrDefaultAsync()
        };
        if (!string.IsNullOrWhiteSpace(l.Name) && l.Name != c.Name)
            c.Contacts.Add(new Contact { Name = l.Name, Email = l.Email, Phone = l.Phone });
        _db.Customers.Add(c);
        await _db.SaveChangesAsync();
        l.CustomerId = c.Id;
        l.Status = LeadStatus.Ganho;
        _db.Interactions.Add(new Interaction { LeadId = l.Id, CustomerId = c.Id, Type = InteractionType.Sistema, Description = "Lead convertido em cliente", UserId = _user.UserId, UserName = _user.UserName });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("crm", "lead_converted", "Customer", c.Id, newData: $"lead={l.Id}");
        return c;
    }
}

public class LeadSyncService
{
    private readonly AppDbContext _db;
    private readonly IGoogleMapsScraperIntegration _scraper;
    private readonly LeadService _leads;
    private readonly NotificationService _notify;
    private readonly ILogger<LeadSyncService> _log;
    private static readonly TimeSpan StuckAfter = TimeSpan.FromHours(24);
    public LeadSyncService(AppDbContext db, IGoogleMapsScraperIntegration scraper, LeadService leads, NotificationService notify, ILogger<LeadSyncService> log)
    { _db = db; _scraper = scraper; _leads = leads; _notify = notify; _log = log; }

    public async Task<ScraperSearch> CreateSearchAsync(string query, string? location, string? segment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Informe a pesquisa.");
        var created = await _scraper.CreateSearchAsync(query.Trim(), location?.Trim(), ct);
        var s = new ScraperSearch { Query = query.Trim(), Location = location?.Trim(), Segment = segment?.Trim(), ExternalId = created.ExternalId, Status = "ATIVA" };
        _db.ScraperSearches.Add(s);
        await _db.SaveChangesAsync(ct);
        if (created.Items.Count > 0)
        {
            var r = await _leads.ImportAsync(created.Items, s.Segment, s.Location, _log);
            s.ImportedCount += r.Created;
            s.LastSyncAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return s;
    }

    public async Task<LeadSyncRun> SyncAsync(CancellationToken ct = default)
    {
        var run = new LeadSyncRun();
        _db.LeadSyncRuns.Add(run);
        if (!_scraper.IsConfigured)
        {
            run.Status = "NAO_CONFIGURADO"; run.FinishedAt = DateTime.UtcNow; run.Message = "Google Scraper não configurado.";
            await _db.SaveChangesAsync(ct);
            return run;
        }
        await _db.SaveChangesAsync(ct);
        var errs = new List<string>();
        var searches = await _db.ScraperSearches.Where(s => s.Status == "ATIVA" && s.ExternalId != null).ToListAsync(ct);
        foreach (var s in searches)
        {
            try
            {
                var state = await _scraper.GetStatusAsync(s.ExternalId!, ct);
                if (state == ScraperJobStatus.Running)
                {
                    if (DateTime.UtcNow - s.CreatedAt > StuckAfter)
                    {
                        s.Status = "FALHA"; s.LastError = "Pesquisa sem conclusão no scraper após 24h."; run.Errors++;
                        errs.Add($"{s.Query}: {s.LastError}");
                    }
                    continue;
                }
                if (state == ScraperJobStatus.Failed)
                {
                    s.Status = "FALHA"; s.LastError = "O scraper marcou a pesquisa como falha."; run.Errors++;
                    errs.Add($"{s.Query}: {s.LastError}");
                    continue;
                }
                var items = await _scraper.GetResultsAsync(s.ExternalId!, ct);
                var r = await _leads.ImportAsync(items, s.Segment, s.Location, _log);
                run.Found += r.Found; run.Created += r.Created; run.Updated += r.Updated; run.Errors += r.Errors;
                s.ImportedCount += r.Created; s.LastSyncAt = DateTime.UtcNow; s.LastError = null;
                if (state == ScraperJobStatus.Completed) s.Status = "CONCLUIDA"; // imported once; avoids re-downloading every sync
            }
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException)
            {
                run.Errors++; s.LastError = ex.Message; errs.Add($"{s.Query}: {ex.Message}");
                _log.LogWarning("Falha ao sincronizar pesquisa {Id}: {Msg}", s.Id, ex.Message);
            }
        }
        run.FinishedAt = DateTime.UtcNow;
        run.Status = errs.Count == 0 ? "OK" : "COM_ERROS";
        run.Message = errs.Count == 0 ? null : string.Join(" | ", errs).Truncate(1000);
        await _db.SaveChangesAsync(ct);
        if (run.Created > 0)
            await _notify.CreateAsync(NotificationType.Success, NotificationSource.Crm, $"{run.Created} novos leads importados", $"Encontrados: {run.Found}, atualizados: {run.Updated}.", "/Leads");
        return run;
    }
}

public static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
