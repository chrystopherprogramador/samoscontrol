using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Infrastructure;

namespace SamosControl.Tests;

/// <summary>Builds the real Application DI graph on top of EF InMemory with temp storage.</summary>
public sealed class TestHost : IDisposable
{
    public ServiceProvider Root { get; }
    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "samos-tests-" + Guid.NewGuid().ToString("N"));

    public TestHost(Dictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["STORAGE_PATH"] = StoragePath,
            ["APP_URL"] = "https://samos.test",
            ["ASAAS_WEBHOOK_TOKEN"] = "asaas-secret",
            ["EVOLUTION_WEBHOOK_TOKEN"] = "evo-secret"
        };
        if (config != null) foreach (var kv in config) values[kv.Key] = kv.Value;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var dbName = Guid.NewGuid().ToString();

        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<IConfiguration>(cfg);
        s.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        s.AddDataProtection();
        s.AddSamosApplication(cfg, runWorkers: false, configureDb: false);
        configure?.Invoke(s);
        Root = s.BuildServiceProvider();
    }

    public IServiceScope Scope() => Root.CreateScope();

    public void Dispose()
    {
        Root.Dispose();
        try { if (Directory.Exists(StoragePath)) Directory.Delete(StoragePath, true); } catch { }
    }
}
