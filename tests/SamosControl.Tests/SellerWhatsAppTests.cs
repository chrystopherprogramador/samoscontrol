using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using Xunit;

namespace SamosControl.Tests;

public class FakeEvolutionInstances : IEvolutionInstances
{
    public List<(string Instance, string Url, string Token)> Created { get; } = new();
    public List<(string Instance, string Phone, string Text)> Sent { get; } = new();
    public bool ServerConfigured => true;
    public Task<EvolutionConnectResult> CreateInstanceAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default)
    { Created.Add((instance, webhookUrl, webhookToken)); return Task.FromResult(new EvolutionConnectResult("connecting", "data:image/png;base64,AAAA", null)); }
    public Task<EvolutionConnectResult> ConnectAsync(string instance, CancellationToken ct = default) => Task.FromResult(new EvolutionConnectResult("connecting", null, null));
    public Task<EvolutionInstanceInfo> GetInfoAsync(string instance, CancellationToken ct = default) => Task.FromResult(new EvolutionInstanceInfo("open", "5565999990000", "Vendedor"));
    public Task SetWebhookAsync(string instance, string webhookUrl, string webhookToken, CancellationToken ct = default) => Task.CompletedTask;
    public Task LogoutAsync(string instance, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteAsync(string instance, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> SendTextAsync(string instance, string phone, string text, CancellationToken ct = default) { Sent.Add((instance, phone, text)); return Task.FromResult<string?>("s" + Sent.Count); }
    public Task<string?> SendMediaAsync(string instance, string phone, byte[] content, string fileName, string mimeType, string? caption, CancellationToken ct = default) => Task.FromResult<string?>("x");
    public List<(string Instance, string Phone, int Bytes)> Audios { get; } = new();
    public (byte[], string?)? Media { get; set; }
    public Task<(byte[] Content, string? MimeType)?> GetMediaAsync(string instance, string messageId, CancellationToken ct = default) => Task.FromResult<(byte[], string?)?>(Media);
    public Task<string?> SendAudioAsync(string instance, string phone, byte[] audio, CancellationToken ct = default) { Audios.Add((instance, phone, audio.Length)); return Task.FromResult<string?>("a" + Audios.Count); }
    public HashSet<string> NoWhatsApp { get; } = new();
    public Task<(bool Exists, string? Number)> CheckNumberAsync(string instance, string phone, CancellationToken ct = default) => Task.FromResult((!NoWhatsApp.Contains(phone), (string?)phone));
    public Task<string?> GetProfilePictureAsync(string instance, string phone, CancellationToken ct = default) => Task.FromResult<string?>("https://pic.example/" + phone);
}

public class SellerWhatsAppTests
{
    private static string Body(string instance, string id, string text) => new JsonObject
    {
        ["event"] = "messages.upsert",
        ["instance"] = instance,
        ["data"] = new JsonObject
        {
            ["key"] = new JsonObject { ["id"] = id, ["remoteJid"] = "5511988887777@s.whatsapp.net", ["fromMe"] = false },
            ["pushName"] = "Maria",
            ["message"] = new JsonObject { ["conversation"] = text },
            ["messageTimestamp"] = 1893456000
        }
    }.ToJsonString();

    [Fact]
    public async Task Connect_CreatesInstanceWithWebhook_AndStatusFollowsEvolution()
    {
        var fake = new FakeEvolutionInstances();
        using var h = new TestHost(configure: s => s.AddSingleton<IEvolutionInstances>(fake)); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        var seller = new Seller { Name = "Felipe" }; db.Sellers.Add(seller); await db.SaveChangesAsync();

        var r = await wa.ConnectSellerAsync(seller.Id, "https://control.example.com/");
        Assert.NotNull(r.QrBase64);
        var c = Assert.Single(fake.Created);
        Assert.Equal($"samos_seller_{seller.Id}", c.Instance);
        Assert.Equal("https://control.example.com/api/webhooks/evolution", c.Url);
        Assert.False(string.IsNullOrEmpty(c.Token));
        Assert.Equal("CONECTANDO", (await db.Sellers.FindAsync(seller.Id))!.WaStatus);

        await wa.RefreshSellerStatusAsync(seller.Id);
        var s2 = await db.Sellers.AsNoTracking().SingleAsync();
        Assert.Equal("CONECTADO", s2.WaStatus);
        Assert.Equal("5565999990000", s2.WaNumber);
    }

    [Fact]
    public async Task Webhook_RoutesByInstance_AndKeepsChatsSeparate()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        var seller = new Seller { Name = "Ana", WaInstance = "samos_seller_9", WaStatus = "CONECTADO" }; db.Sellers.Add(seller); await db.SaveChangesAsync();

        Assert.Equal(200, (await wa.HandleWebhookAsync(Body("samos_seller_9", "A1", "oi vendedor"), "evo-secret")).StatusCode);
        Assert.Equal(200, (await wa.HandleWebhookAsync(Body("samos_seller_9", "A1", "oi vendedor"), "evo-secret")).StatusCode);
        Assert.Equal(200, (await wa.HandleWebhookAsync(Body("", "A1", "oi empresa"), "evo-secret")).StatusCode);

        var convs = await db.WhatsAppConversations.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, convs.Count);
        Assert.Equal(seller.Id, convs[0].SellerId);
        Assert.Null(convs[1].SellerId);
        Assert.Equal(2, await db.WhatsAppMessages.CountAsync());
        Assert.Equal("oi vendedor", convs[0].LastMessagePreview);
    }

    [Fact]
    public async Task Webhook_ConnectionUpdate_UpdatesSellerStatus_AndUnknownInstanceIsIgnored()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        await sc.ServiceProvider.GetRequiredService<SettingsService>().SetAsync(db, SettingKeys.EvolutionInstance, "samos_disparo");
        var seller = new Seller { Name = "Ana", WaInstance = "samos_seller_9", WaStatus = "CONECTANDO" }; db.Sellers.Add(seller); await db.SaveChangesAsync();

        var ev = new JsonObject { ["event"] = "connection.update", ["instance"] = "samos_seller_9", ["data"] = new JsonObject { ["state"] = "open" } }.ToJsonString();
        await wa.HandleWebhookAsync(ev, "evo-secret");
        Assert.Equal("CONECTADO", (await db.Sellers.AsNoTracking().SingleAsync()).WaStatus);

        await wa.HandleWebhookAsync(Body("outra_instancia", "Z1", "x"), "evo-secret");
        Assert.Equal(0, await db.WhatsAppMessages.CountAsync());
    }

    [Fact]
    public async Task SamePhoneWithAndWithoutNinthDigit_IsOneConversation_AndMergesExistingDuplicates()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        var a = await wa.GetOrCreateConversationAsync("5562993372731", null, 5);
        var b = await wa.GetOrCreateConversationAsync("556293372731", "Chry", 5);
        Assert.Equal(a.Id, b.Id);

        db.WhatsAppConversations.Add(new WhatsAppConversation { Phone = "556293372731", SellerId = 7, UnreadCount = 1 });
        db.WhatsAppConversations.Add(new WhatsAppConversation { Phone = "5562993372731", SellerId = 7 });
        await db.SaveChangesAsync();
        await wa.MergeDuplicateConversationsAsync();
        Assert.Equal(1, await db.WhatsAppConversations.CountAsync(c => c.SellerId == 7));
        Assert.Equal("5562993372731", (await db.WhatsAppConversations.SingleAsync(c => c.SellerId == 7)).Phone);
    }

    [Fact]
    public async Task Lead_BelongsToFirstSeller_OthersBlockedUnlessAdmin()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        var lead = new Lead { Name = "Loja X", Phone = "65999990000", PhoneKey = PhoneUtil.Key("65999990000") };
        db.Leads.Add(lead); await db.SaveChangesAsync();

        await wa.ClaimLeadAsync(lead.Id, 1, false);
        await wa.ClaimLeadAsync(lead.Id, 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wa.ClaimLeadAsync(lead.Id, 2, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wa.ClaimByPhoneAsync("65999990000", 2, false));
        await wa.ClaimLeadAsync(lead.Id, 2, true);
        Assert.Equal(2, (await db.Leads.AsNoTracking().SingleAsync()).SellerId);
    }

    [Fact]
    public async Task NewChat_RejectsNumberWithoutWhatsApp_AndFirstMessageMovesLeadToContato_AndAdminCanDelete()
    {
        var fake = new FakeEvolutionInstances(); fake.NoWhatsApp.Add("5565911112222");
        using var h = new TestHost(configure: s => s.AddSingleton<IEvolutionInstances>(fake)); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();
        var seller = new Seller { Name = "Felipe" }; db.Sellers.Add(seller); await db.SaveChangesAsync();
        await wa.ConnectSellerAsync(seller.Id, "https://control.example.com/"); await wa.RefreshSellerStatusAsync(seller.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wa.ValidateNumberAsync(seller.Id, "65911112222"));
        Assert.Equal("5565999990000", await wa.ValidateNumberAsync(seller.Id, "65999990000"));

        var lead = new Lead { Name = "Loja X", Phone = "65999990000", PhoneKey = PhoneUtil.Key("65999990000"), Status = LeadStatus.Novo };
        db.Leads.Add(lead); await db.SaveChangesAsync();
        var conv = await wa.GetOrCreateConversationAsync("5565999990000", "Loja X", seller.Id);
        conv.LeadId = lead.Id; await db.SaveChangesAsync();
        await wa.SendSellerTextAsync(seller.Id, "5565999990000", "Olá!", null);
        Assert.Equal(LeadStatus.Novo, (await db.Leads.AsNoTracking().SingleAsync()).Status);
        await wa.SendSellerTextAsync(seller.Id, "5565999990000", "Olá!", 1);
        Assert.Equal(LeadStatus.Contato, (await db.Leads.AsNoTracking().SingleAsync()).Status);

        await wa.EnsurePictureAsync(conv.Id);
        Assert.StartsWith("https://pic.example/", (await db.WhatsAppConversations.AsNoTracking().SingleAsync()).PictureUrl);

        await wa.DeleteConversationAsync(conv.Id);
        Assert.Empty(db.WhatsAppConversations.AsNoTracking());
        Assert.Empty(db.WhatsAppMessages.AsNoTracking());
    }

    [Theory]
    [InlineData("open", "DESCONECTADO", "CONECTADO")]
    [InlineData("close", "CONECTADO", "DESCONECTADO")]
    public void MapState(string state, string previous, string expected) => Assert.Equal(expected, WhatsAppService.MapState(state, previous));
}
