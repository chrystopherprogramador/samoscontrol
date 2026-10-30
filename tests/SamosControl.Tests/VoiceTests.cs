using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;
using Xunit;

namespace SamosControl.Tests;

public class FakeVoice : IElevenLabs
{
    public bool IsConfigured { get; set; } = true;
    public bool VoiceRepliesEnabled { get; set; } = true;
    public bool FailSynth { get; set; }
    public Task<byte[]> SynthesizeAsync(string text, CancellationToken ct = default) =>
        FailSynth ? throw new IntegrationException("falhou") : Task.FromResult(new byte[] { 1, 2, 3 });
    public Task<string?> TranscribeAsync(byte[] audio, string fileName, string? mimeType, CancellationToken ct = default) => Task.FromResult<string?>("quero saber o preço");
}

public class VoiceTests
{
    private static string AudioBody(string id) => new JsonObject
    {
        ["event"] = "messages.upsert",
        ["instance"] = "samos_disparo",
        ["data"] = new JsonObject
        {
            ["key"] = new JsonObject { ["id"] = id, ["remoteJid"] = "5511988887777@s.whatsapp.net", ["fromMe"] = false },
            ["pushName"] = "Maria",
            ["messageType"] = "audioMessage",
            ["message"] = new JsonObject { ["audioMessage"] = new JsonObject { ["seconds"] = 4 } },
            ["messageTimestamp"] = 1893456000
        }
    }.ToJsonString();

    [Fact]
    public async Task IncomingAudio_IsTranscribedAndStoredAsText()
    {
        var inst = new FakeEvolutionInstances { Media = (new byte[] { 9 }, "audio/ogg") };
        using var h = new TestHost(configure: s => { s.AddSingleton<IEvolutionInstances>(inst); s.AddSingleton<IElevenLabs>(new FakeVoice()); });
        using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        await sc.ServiceProvider.GetRequiredService<SettingsService>().SetAsync(db, SettingKeys.EvolutionInstance, "samos_disparo");
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();

        await wa.HandleWebhookAsync(AudioBody("AU1"), "evo-secret");
        var m = await db.WhatsAppMessages.SingleAsync();
        Assert.Equal("🎤 quero saber o preço", m.Body);
        Assert.Equal("audioMessage", m.Type);
    }

    [Fact]
    public async Task IncomingAudio_WithoutVoiceConfigured_StaysPlaceholder()
    {
        using var h = new TestHost(configure: s => s.AddSingleton<IElevenLabs>(new FakeVoice { IsConfigured = false }));
        using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();

        await wa.HandleWebhookAsync(AudioBody("AU2"), "evo-secret");
        Assert.Equal("[audioMessage]", (await db.WhatsAppMessages.SingleAsync()).Body);
    }

    [Fact]
    public async Task Reply_GoesAsAudio_AndFallsBackToText()
    {
        var inst = new FakeEvolutionInstances();
        var voice = new FakeVoice();
        using var h = new TestHost(configure: s => { s.AddSingleton<IEvolutionInstances>(inst); s.AddSingleton<IElevenLabs>(voice); s.AddSingleton<IEvolutionApiIntegration>(new FakeEvolution()); });
        using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        await sc.ServiceProvider.GetRequiredService<SettingsService>().SetAsync(db, SettingKeys.EvolutionInstance, "samos_disparo");
        var wa = sc.ServiceProvider.GetRequiredService<WhatsAppService>();

        var a = await wa.SendReplyAsync(null, "5511988887777", "Olá!", true);
        Assert.Equal("audio", a.Type);
        Assert.Equal("Olá!", a.Body);
        Assert.Single(inst.Audios);

        voice.FailSynth = true;
        var t = await wa.SendReplyAsync(null, "5511988887777", "Olá de novo", true);
        Assert.Equal("text", t.Type);
        Assert.Single(inst.Audios);
    }
}
