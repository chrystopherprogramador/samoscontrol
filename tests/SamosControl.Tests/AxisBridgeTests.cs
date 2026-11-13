using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Tests;

public class AxisBridgeTests
{
    [Fact]
    public async Task Situacao_CountsOverdueAndPastDuePending_MatchingDocumentByDigits()
    {
        using var host = new TestHost(new() { ["AXIS_EFI_TOKEN"] = "tok" });
        using var scope = host.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var c = new Customer { Name = "ACME", Document = "12.345.678/0001-90" };
        var outro = new Customer { Name = "Outro", Document = "99.999.999/0001-99" };
        db.Customers.AddRange(c, outro);
        await db.SaveChangesAsync();
        var past = DateTime.SpecifyKind(AppTime.Now.Date.AddDays(-3), DateTimeKind.Utc);
        var future = DateTime.SpecifyKind(AppTime.Now.Date.AddDays(5), DateTimeKind.Utc);
        db.Charges.AddRange(
            new Charge { CustomerId = c.Id, Amount = 100, DueDate = past, Status = ChargeStatus.Vencido, Kind = "IMPLANTACAO" },
            new Charge { CustomerId = c.Id, Amount = 50, DueDate = past, Status = ChargeStatus.Pendente, Kind = "MENSALIDADE" },
            new Charge { CustomerId = c.Id, Amount = 70, DueDate = future, Status = ChargeStatus.Pendente, Kind = "IMPLANTACAO" },
            new Charge { CustomerId = c.Id, Amount = 30, DueDate = past, Status = ChargeStatus.Pago, Kind = "MENSALIDADE" },
            new Charge { CustomerId = outro.Id, Amount = 999, DueDate = past, Status = ChargeStatus.Vencido });
        await db.SaveChangesAsync();

        var s = await scope.ServiceProvider.GetRequiredService<AxisBridgeService>().SituacaoAsync("12345678000190");

        Assert.Equal(2, s.Vencidas);
        Assert.Equal(150m, s.ValorVencido);
    }
}
