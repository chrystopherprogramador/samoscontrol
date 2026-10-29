using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

/// <summary>Who is performing the current operation (null user = system/worker).</summary>
public interface ICurrentUser
{
    int? UserId { get; }
    string? UserName { get; }
    string? Ip { get; }
}

public class SystemUser : ICurrentUser
{
    public int? UserId => null;
    public string? UserName => "sistema";
    public string? Ip => null;
}

/// <summary>Optional realtime push (SignalR in Web; no-op in Worker).</summary>
public interface INotificationPublisher
{
    Task PublishAsync(Notification n);
}

public class NullNotificationPublisher : INotificationPublisher
{
    public Task PublishAsync(Notification n) => Task.CompletedTask;
}

public static class AppTime
{
    public static TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    public static void Configure(string? id)
    {
        try { Zone = string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { Zone = TimeZoneInfo.Utc; }
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public static DateTime Now => ToLocal(DateTime.UtcNow);
    public static DateTime TodayStartUtc => ToUtc(Now.Date);
    public static DateTime MonthStartUtc => ToUtc(new DateTime(Now.Year, Now.Month, 1));
    public static DateTime WeekStartUtc => ToUtc(Now.Date.AddDays(-(((int)Now.DayOfWeek + 6) % 7)));
}

public class SettingsService : ISettingsReader
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _cfg;
    private readonly IDataProtector _protector;
    private Dictionary<string, AppSetting> _cache = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public SettingsService(IServiceScopeFactory scopes, IConfiguration cfg, IDataProtectionProvider dp)
    {
        _scopes = scopes; _cfg = cfg; _protector = dp.CreateProtector("SamosControl.Settings.v1");
    }

    private Dictionary<string, AppSetting> Snapshot()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _loadedAt).TotalSeconds > 10)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    _cache = scope.ServiceProvider.GetRequiredService<AppDbContext>().AppSettings.AsNoTracking().ToDictionary(x => x.Key);
                }
                catch { /* database not ready yet: fall back to environment */ }
                _loadedAt = DateTime.UtcNow;
            }
            return _cache;
        }
    }

    /// <summary>Database value wins; environment variable (same key) is the fallback.</summary>
    public string? Get(string key)
    {
        if (Snapshot().TryGetValue(key, out var s) && !string.IsNullOrEmpty(s.Value))
        {
            if (!s.IsSecret) return s.Value;
            try { return _protector.Unprotect(s.Value); } catch (CryptographicException) { return null; }
        }
        var env = _cfg[key];
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public string Source(string key) =>
        Snapshot().TryGetValue(key, out var s) && !string.IsNullOrEmpty(s.Value) ? "painel"
        : !string.IsNullOrWhiteSpace(_cfg[key]) ? "ambiente" : "-";

    public bool Has(string key) => !string.IsNullOrWhiteSpace(Get(key));

    public async Task SetAsync(AppDbContext db, string key, string? value)
    {
        var isSecret = SettingKeys.Secrets.Contains(key);
        var row = await db.AppSettings.FindAsync(key);
        if (row is null) { row = new AppSetting { Key = key }; db.AppSettings.Add(row); }
        row.IsSecret = isSecret;
        row.Value = string.IsNullOrEmpty(value) ? null : isSecret ? _protector.Protect(value) : value;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        lock (_lock) _loadedAt = DateTime.MinValue;
    }

    public static string Mask(string? secret) =>
        string.IsNullOrEmpty(secret) ? "" : secret.Length <= 4 ? "••••" : "••••••••" + secret[^3..];
}

public class AuditService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    public AuditService(AppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task LogAsync(string module, string action, string? entity = null, object? entityId = null,
        string? oldData = null, string? newData = null, int? userId = null, string? userName = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId ?? _user.UserId, UserName = userName ?? _user.UserName, Module = module, Action = action,
            Entity = entity, EntityId = entityId?.ToString(), OldData = Trim(oldData), NewData = Trim(newData), Ip = _user.Ip
        });
        await _db.SaveChangesAsync();
    }

    private static string? Trim(string? s) => s is { Length: > 4000 } ? s[..4000] : s;
}

public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly INotificationPublisher _pub;
    public NotificationService(AppDbContext db, INotificationPublisher pub) { _db = db; _pub = pub; }

    public async Task<Notification> CreateAsync(NotificationType type, NotificationSource source, string title, string? message = null, string? link = null, int? userId = null)
    {
        var n = new Notification { Type = type, Source = source, Title = title, Message = message, Link = link, UserId = userId };
        _db.Notifications.Add(n);
        await _db.SaveChangesAsync();
        try { await _pub.PublishAsync(n); } catch { /* realtime is best effort */ }
        return n;
    }

    public Task<int> UnreadCountAsync(int userId) =>
        _db.Notifications.CountAsync(n => (n.UserId == null || n.UserId == userId)
            && !_db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId)
            && n.CreatedAt > DateTime.UtcNow.AddDays(-30));

    public async Task MarkReadAsync(int userId, int? id)
    {
        var q = _db.Notifications.Where(n => (n.UserId == null || n.UserId == userId)
            && !_db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId));
        if (id.HasValue) q = q.Where(n => n.Id == id);
        foreach (var nid in await q.Select(n => n.Id).ToListAsync())
            _db.NotificationReads.Add(new NotificationRead { NotificationId = nid, UserId = userId });
        await _db.SaveChangesAsync();
    }
}

/// <summary>Persistent file storage rooted at STORAGE_PATH (mount a volume there).</summary>
public class FileStorage
{
    public string Root { get; }
    public FileStorage(IConfiguration cfg)
    {
        Root = Path.GetFullPath(cfg["STORAGE_PATH"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "storage"));
        Directory.CreateDirectory(Root);
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root, StringComparison.Ordinal)) throw new InvalidOperationException("Caminho inválido.");
        return full;
    }

    public async Task SaveAsync(string relative, byte[] data)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data);
    }

    public Task<byte[]> ReadAsync(string relative) => File.ReadAllBytesAsync(Resolve(relative));
    public bool Exists(string relative) => File.Exists(Resolve(relative));
}

public class PermissionService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    public PermissionService(IServiceScopeFactory scopes, IMemoryCache cache) { _scopes = scopes; _cache = cache; }

    public record UserAccess(bool Active, HashSet<string> Permissions);

    public async Task<UserAccess> GetAsync(int userId)
    {
        if (_cache.TryGetValue<UserAccess>("acc:" + userId, out var hit) && hit is not null) return hit;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.IsActive, x.RoleId }).FirstOrDefaultAsync();
        var acc = u is null ? new UserAccess(false, new())
            : new UserAccess(u.IsActive, (await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == u.RoleId).Select(p => p.Permission).ToListAsync()).ToHashSet());
        _cache.Set("acc:" + userId, acc, TimeSpan.FromSeconds(20));
        return acc;
    }

    public async Task<bool> HasAsync(int userId, string permission)
    {
        var a = await GetAsync(userId);
        return a.Active && a.Permissions.Contains(permission);
    }

    public void Invalidate(int userId) => _cache.Remove("acc:" + userId);
}

public record LoginResult(bool Success, User? User, string? Error);

public class AuthService
{
    public const int MaxFailures = 5;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private static readonly PasswordHasherShim Hasher = new();
    public AuthService(AppDbContext db, AuditService audit) { _db = db; _audit = audit; }

    public static string Hash(string password) => Hasher.Hash(password);
    public static bool Verify(string hash, string password) => Hasher.Verify(hash, password);

    public static string? ValidatePassword(string? p)
    {
        if (string.IsNullOrEmpty(p) || p.Length < 10) return "A senha deve ter ao menos 10 caracteres.";
        if (!p.Any(char.IsLetter) || !p.Any(char.IsDigit)) return "A senha deve conter letras e números.";
        return null;
    }

    public async Task<LoginResult> LoginAsync(string email, string password)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        var user = await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == email);
        // Same generic message for unknown user / wrong password to avoid account enumeration.
        const string generic = "E-mail ou senha inválidos.";
        if (user is null)
        {
            Hasher.Verify(Hasher.Hash("dummy-password-1"), password ?? ""); // equalize timing
            await _audit.LogAsync("auth", "login_failed", "User", null, newData: email, userName: email);
            return new(false, null, generic);
        }
        if (user.LockoutEnd > DateTime.UtcNow)
        {
            await _audit.LogAsync("auth", "login_locked", "User", user.Id, userId: user.Id, userName: user.Email);
            return new(false, null, "Conta temporariamente bloqueada. Tente novamente mais tarde.");
        }
        if (!user.IsActive || !Verify(user.PasswordHash, password ?? ""))
        {
            if (user.IsActive && ++user.FailedLogins >= MaxFailures)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
                user.FailedLogins = 0;
            }
            await _db.SaveChangesAsync();
            await _audit.LogAsync("auth", "login_failed", "User", user.Id, userId: user.Id, userName: user.Email);
            return new(false, null, generic);
        }
        user.FailedLogins = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("auth", "login", "User", user.Id, userId: user.Id, userName: user.Email);
        return new(true, user, null);
    }

    /// <summary>Generates a one-time reset token (returned raw once; only its hash is stored).</summary>
    public async Task<string?> CreatePasswordResetAsync(int userId)
    {
        var u = await _db.Users.FindAsync(userId);
        if (u is null) return null;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        u.PasswordResetTokenHash = Sha(token);
        u.PasswordResetExpires = DateTime.UtcNow.AddHours(2);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "password_reset_requested", "User", u.Id);
        return token;
    }

    public async Task<string?> ResetPasswordAsync(string token, string newPassword)
    {
        var err = ValidatePassword(newPassword);
        if (err != null) return err;
        var hash = Sha(token ?? "");
        var u = await _db.Users.FirstOrDefaultAsync(x => x.PasswordResetTokenHash == hash && x.PasswordResetExpires > DateTime.UtcNow);
        if (u is null) return "Link inválido ou expirado.";
        u.PasswordHash = Hash(newPassword);
        u.PasswordResetTokenHash = null;
        u.PasswordResetExpires = null;
        u.FailedLogins = 0; u.LockoutEnd = null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("users", "password_reset", "User", u.Id, userId: u.Id, userName: u.Email);
        return null;
    }

    public static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>PBKDF2 hasher from Microsoft.Extensions.Identity.Core.</summary>
internal class PasswordHasherShim
{
    private readonly Microsoft.AspNetCore.Identity.PasswordHasher<object> _h = new();
    public string Hash(string p) => _h.HashPassword(new object(), p);
    public bool Verify(string hash, string p)
    {
        try
        {
            var r = _h.VerifyHashedPassword(new object(), hash, p);
            return r != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
        }
        catch (FormatException) { return false; }
    }
}
