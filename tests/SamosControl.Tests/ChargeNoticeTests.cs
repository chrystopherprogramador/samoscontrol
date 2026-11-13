using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;

namespace SamosControl.Tests;

public class ChargeNoticeTests
{
    [Fact]
    public void Rules_FollowConfiguredSchedule()
    {
        var cfg = new ChargeNoticeService.Config(true, new() { "EMAIL" }, ChargeNoticeService.Days("3, 1"), true, ChargeNoticeService.Days("2;7"), 9);
        Assert.Equal("before:3", ChargeNoticeService.RuleFor(cfg, -3));
        Assert.Null(ChargeNoticeService.RuleFor(cfg, -2));
        Assert.Equal("due", ChargeNoticeService.RuleFor(cfg, 0));
        Assert.Equal("after:7", ChargeNoticeService.RuleFor(cfg, 7));
        Assert.Null(ChargeNoticeService.RuleFor(cfg, 3));
    }

    [Fact]
    public void Render_DropsPixPlaceholder_EvenWhenPixPayloadIsSet()
    {
        // O código Pix copia-e-cola nunca é enviado em texto puro (gera cobrança bancária repetida no banco);
        // mesmo um template antigo com {pix} deve simplesmente omitir essa linha.
        using var host = new TestHost(new() { ["COBRANCA_MSG_ATRASO"] = "Oi {nome}, {dias} dia(s) de atraso.\nPIX copia e cola:\n{pix}" });
        using var scope = host.Scope();
        var svc = scope.ServiceProvider.GetRequiredService<ChargeNoticeService>();
        var ch = new Charge { Amount = 10, DueDate = DateTime.UtcNow.Date, Customer = new Customer { Name = "Ana" }, PixPayload = "000201" };
        Assert.Equal("Oi Ana, 5 dia(s) de atraso.", svc.Render(ch, 5));
    }

    [Fact]
    public async Task Run_IsDisabledByDefault()
    {
        using var host = new TestHost(new());
        using var scope = host.Scope();
        var r = await scope.ServiceProvider.GetRequiredService<ChargeNoticeService>().RunAsync();
        Assert.Equal(0, r.Sent + r.Failed);
    }
}
