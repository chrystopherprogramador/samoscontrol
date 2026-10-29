using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SamosControl.Infrastructure;

/// <summary>Used only by `dotnet ef` tooling.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING")
                 ?? "Host=localhost;Database=samos;Username=postgres;Password=design-time-only";
        var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
        return new AppDbContext(opts);
    }
}
