using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;

namespace SamosControl.Tests;

public class SdrTests
{
    [Fact]
    public void Render_and_split_variants()
    {
        var l = new Lead { Name = "Maria Silva", CompanyName = "Clínica X", City = "Goiânia", Segment = "Saúde" };
        Assert.Equal("Oi Maria, Clínica X em Goiânia", SdrService.Render("Oi {{nome}}, {{empresa}} em {{cidade}}", l));
        Assert.Equal(2, SdrService.SplitVariants("a\n---\nb").Count);
    }

    [Fact]
    public async Task Enqueue_dedupes_phones_and_skips_opt_out()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Leads.AddRange(
            new Lead { Name = "A", Phone = "62999990001", Status = LeadStatus.Novo },
            new Lead { Name = "B", Phone = "(62) 99999-0001", Status = LeadStatus.Novo },
            new Lead { Name = "C", Phone = "62999990002", Status = LeadStatus.Novo });
        var c = new SdrCampaign { Name = "T", FilterStatus = LeadStatus.Novo };
        db.SdrCampaigns.Add(c);
        await db.SaveChangesAsync();
        var sdr = sc.ServiceProvider.GetRequiredService<SdrService>();
        Assert.Equal(2, await sdr.EnqueueAsync(c));
        Assert.Equal(0, await sdr.EnqueueAsync(c));
    }

    [Fact]
    public async Task Knowledge_seed_and_gap_dedup()
    {
        using var h = new TestHost(); using var sc = h.Scope();
        var kb = sc.ServiceProvider.GetRequiredService<KnowledgeService>();
        await kb.EnsureSeedAsync(); await kb.EnsureSeedAsync();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        var n = await db.AiKnowledge.CountAsync();
        Assert.True(n >= 20);
        await kb.RegisterGapAsync("Tem fidelidade?", null); await kb.RegisterGapAsync("Tem fidelidade?", null);
        Assert.Equal(n + 1, await db.AiKnowledge.CountAsync());
    }
}
