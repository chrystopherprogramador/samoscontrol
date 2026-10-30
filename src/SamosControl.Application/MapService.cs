using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record MapState(string Uf, string Name, int Total, int Novos, int Open, int Ganhos, int Perdidos, double Conv, double Score, List<KeyValuePair<string, int>> Segments);
public record MapCity(string City, string Uf, double Lat, double Lng, int Total, int Novos, int Open, int Ganhos, int Perdidos, double Score, string? TopSegment);
public record MapData(int Total, int Unlocated, List<MapState> States, List<MapCity> Cities);
public record MapInsight(string Title, string Where, string Why, List<string> Steps, string Priority, string? Uf, string? City);
public record MapInsights(string Summary, List<MapInsight> Actions, bool Ai);

/// <summary>Aggregates leads by state/city (using a bundled IBGE city centroid table) and suggests where to focus.</summary>
public class MapService
{
    private static readonly Dictionary<string, string> UfNames = new()
    {
        ["AC"] = "Acre", ["AL"] = "Alagoas", ["AP"] = "Amapá", ["AM"] = "Amazonas", ["BA"] = "Bahia", ["CE"] = "Ceará", ["DF"] = "Distrito Federal",
        ["ES"] = "Espírito Santo", ["GO"] = "Goiás", ["MA"] = "Maranhão", ["MT"] = "Mato Grosso", ["MS"] = "Mato Grosso do Sul", ["MG"] = "Minas Gerais",
        ["PA"] = "Pará", ["PB"] = "Paraíba", ["PR"] = "Paraná", ["PE"] = "Pernambuco", ["PI"] = "Piauí", ["RJ"] = "Rio de Janeiro", ["RN"] = "Rio Grande do Norte",
        ["RS"] = "Rio Grande do Sul", ["RO"] = "Rondônia", ["RR"] = "Roraima", ["SC"] = "Santa Catarina", ["SP"] = "São Paulo", ["SE"] = "Sergipe", ["TO"] = "Tocantins"
    };
    private static readonly Lazy<Dictionary<string, Dictionary<string, double[]>>> Cities = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "cities.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(File.ReadAllText(path)) ?? new()
            : new();
    });

    private readonly AppDbContext _db;
    private readonly IAiProvider _ai;
    public MapService(AppDbContext db, IAiProvider ai) { _db = db; _ai = ai; }

    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly System.Text.RegularExpressions.Regex UfInAddress = new(@"(?:-|,|\s)\s*([A-Z]{2})\s*(?:,|-|\d{5}|$)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly Lazy<Dictionary<string, string>> UniqueCityUf = new(() =>
    {
        var map = new Dictionary<string, string>(); var dup = new HashSet<string>();
        foreach (var (uf, cs) in Cities.Value) foreach (var c in cs.Keys) { if (map.ContainsKey(c)) dup.Add(c); else map[c] = uf; }
        foreach (var d in dup) map.Remove(d);
        return map;
    });

    /// <summary>State from the field, else from the address ("... - GO, 74000-000"), else from a city name that exists in one state only.</summary>
    internal static string? InferUf(string? state, string? city, string? address)
    {
        var uf = state?.Trim().ToUpperInvariant();
        if (uf != null && UfNames.ContainsKey(uf)) return uf;
        if (!string.IsNullOrWhiteSpace(address))
            foreach (System.Text.RegularExpressions.Match m in UfInAddress.Matches(address))
                if (UfNames.ContainsKey(m.Groups[1].Value)) return m.Groups[1].Value;
        var n = Norm(city);
        return n.Length > 0 && UniqueCityUf.Value.TryGetValue(n, out var u) ? u : null;
    }

    private static bool IsOpen(LeadStatus s) => s != LeadStatus.Ganho && s != LeadStatus.Perdido;
    // Opportunity: open pipeline weighted by how well the region already converts, plus untouched leads.
    private static double Score(int open, int novos, int ganhos, int perdidos)
        => open * (0.6 + (ganhos + 1.0) / (ganhos + perdidos + 3.0)) + novos * 0.5;

    public async Task<MapData> GetAsync(CancellationToken ct = default)
    {
        // Persist inferred states so list filters ("Ver leads") match what the map shows.
        var missing = await _db.Leads.Where(l => l.State == null || l.State == "").ToListAsync(ct);
        var fixedAny = false;
        foreach (var l in missing) { var u = InferUf(null, l.City, l.Address); if (u != null) { l.State = u; fixedAny = true; } }
        if (fixedAny) await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        var rows = await _db.Leads.AsNoTracking().Select(l => new { l.State, l.City, l.Address, l.Segment, l.Status }).ToListAsync(ct);
        var cityTable = Cities.Value;
        var unlocated = 0;
        var byState = new Dictionary<string, List<(string? City, string? Seg, LeadStatus St)>>();
        foreach (var r in rows)
        {
            var uf = InferUf(r.State, r.City, r.Address);
            if (uf == null) { unlocated++; continue; }
            if (!byState.TryGetValue(uf, out var list)) byState[uf] = list = new();
            list.Add((r.City, r.Segment, r.Status));
        }

        var states = new List<MapState>();
        var cities = new List<MapCity>();
        foreach (var (uf, list) in byState)
        {
            var g = list.Count(x => x.St == LeadStatus.Ganho); var p = list.Count(x => x.St == LeadStatus.Perdido);
            var open = list.Count(x => IsOpen(x.St)); var nw = list.Count(x => x.St == LeadStatus.Novo);
            var segs = list.Where(x => !string.IsNullOrWhiteSpace(x.Seg)).GroupBy(x => x.Seg!.Trim()).OrderByDescending(x => x.Count()).Take(3)
                .Select(x => new KeyValuePair<string, int>(x.Key, x.Count())).ToList();
            states.Add(new(uf, UfNames[uf], list.Count, nw, open, g, p, g + p == 0 ? 0 : Math.Round(100.0 * g / (g + p), 1), Math.Round(Score(open, nw, g, p), 1), segs));

            cityTable.TryGetValue(uf, out var table);
            foreach (var cg in list.Where(x => !string.IsNullOrWhiteSpace(x.City)).GroupBy(x => Norm(x.City)))
            {
                if (table == null || !table.TryGetValue(cg.Key, out var ll)) { unlocated += cg.Count(); continue; }
                var cg_g = cg.Count(x => x.St == LeadStatus.Ganho); var cg_p = cg.Count(x => x.St == LeadStatus.Perdido);
                var cOpen = cg.Count(x => IsOpen(x.St)); var cNew = cg.Count(x => x.St == LeadStatus.Novo);
                var seg = cg.Where(x => !string.IsNullOrWhiteSpace(x.Seg)).GroupBy(x => x.Seg!.Trim()).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                cities.Add(new(cg.First().City!.Trim(), uf, ll[0], ll[1], cg.Count(), cNew, cOpen, cg_g, cg_p, Math.Round(Score(cOpen, cNew, cg_g, cg_p), 1), seg));
            }
        }
        return new(rows.Count, unlocated, states.OrderByDescending(s => s.Score).ToList(), cities.OrderByDescending(c => c.Score).ToList());
    }

    public async Task<MapInsights> InsightsAsync(CancellationToken ct = default)
    {
        var data = await GetAsync(ct);
        if (data.Total == 0) return new("Ainda não há leads para analisar. Importe leads pelo Google Scraper ou cadastre manualmente.", new(), false);
        var fallback = Heuristic(data);
        if (!_ai.IsConfigured) return fallback;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                totalLeads = data.Total,
                estados = data.States.Take(8).Select(s => new { s.Uf, s.Name, s.Total, s.Novos, emAberto = s.Open, ganhos = s.Ganhos, perdidos = s.Perdidos, conversaoPct = s.Conv, s.Score, segmentos = s.Segments.Select(x => x.Key + " (" + x.Value + ")") }),
                cidades = data.Cities.Take(12).Select(c => new { c.City, c.Uf, c.Total, c.Novos, emAberto = c.Open, ganhos = c.Ganhos, perdidos = c.Perdidos, c.Score, segmento = c.TopSegment })
            });
            var sys = "Você é um estrategista comercial B2B brasileiro da Samos (software/serviços digitais). Analise a distribuição geográfica de leads e proponha ações comerciais práticas e específicas. " +
                      "Use SOMENTE os números fornecidos; não invente dados. Responda APENAS JSON: {\"resumo\":\"2 frases\",\"acoes\":[{\"titulo\":\"\",\"local\":\"Cidade/UF\",\"uf\":\"SP\",\"cidade\":\"\" ,\"porque\":\"cite números\",\"passos\":[\"3 a 4 passos curtos\"],\"prioridade\":\"alta|media|baixa\"}]}. Máximo 5 ações, em português.";
            var raw = await _ai.ChatAsync(new[] { new AiChatMessage("system", sys), new AiChatMessage("user", payload) }, true, ct);
            var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return fallback;
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var actions = new List<MapInsight>();
            if (root.TryGetProperty("acoes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray().Take(6))
                {
                    string S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var steps = a.TryGetProperty("passos", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0).Take(5).ToList() : new();
                    var uf = S("uf").ToUpperInvariant();
                    if (S("titulo").Length == 0) continue;
                    actions.Add(new(S("titulo"), S("local"), S("porque"), steps, S("prioridade").ToLowerInvariant() is "alta" or "media" or "baixa" ? S("prioridade").ToLowerInvariant() : "media", UfNames.ContainsKey(uf) ? uf : null, S("cidade").Length > 0 ? S("cidade") : null));
                }
            var summary = root.TryGetProperty("resumo", out var rs) ? rs.GetString() ?? "" : "";
            return actions.Count == 0 ? fallback : new(summary, actions, true);
        }
        catch { return fallback; }
    }

    private static MapInsights Heuristic(MapData d)
    {
        var acts = new List<MapInsight>();
        var top = d.States.FirstOrDefault();
        foreach (var c in d.Cities.Where(c => c.Novos > 0).Take(2))
            acts.Add(new($"Abordar leads novos em {c.City}/{c.Uf}", $"{c.City}/{c.Uf}", $"{c.Novos} leads novos sem contato e {c.Open} em aberto" + (c.TopSegment != null ? $", concentrados em {c.TopSegment}." : "."),
                new() { "Priorizar o WhatsApp com mensagem de abertura personalizada ao segmento.", "Agendar follow-up de 48h para quem não responder.", "Mover quem responder para Contato/Qualificado." }, "alta", c.Uf, c.City));
        if (top != null)
            acts.Add(new($"Campanha regional em {top.Name}", top.Name, $"{top.Open} leads em aberto e score {top.Score}; conversão atual de {top.Conv}%.",
                new() { "Definir um responsável por região.", "Rodar uma sequência de mensagens para o funil em aberto.", "Revisar propostas paradas há mais de 7 dias." }, "alta", top.Uf, null));
        var weak = d.States.Where(s => s.Perdidos > s.Ganhos && s.Perdidos >= 3).FirstOrDefault();
        if (weak != null)
            acts.Add(new($"Revisar abordagem em {weak.Name}", weak.Name, $"{weak.Perdidos} perdidos contra {weak.Ganhos} ganhos.", new() { "Levantar os motivos de perda.", "Testar nova oferta/preço na região." }, "media", weak.Uf, null));
        var gap = d.States.Where(s => s.Total >= 1 && s.Total <= 3).OrderBy(s => s.Total).FirstOrDefault();
        if (gap != null && d.States.Count > 3)
            acts.Add(new($"Explorar mercado pouco coberto: {gap.Name}", gap.Name, $"Apenas {gap.Total} lead(s) captados; espaço para expansão.", new() { "Criar pesquisa no Google Scraper para o segmento principal na região." }, "baixa", gap.Uf, null));
        var sum = top == null ? "" : $"{top.Name} lidera em oportunidade ({top.Open} leads em aberto). {d.Cities.Count(c => c.Novos > 0)} cidades têm leads novos aguardando abordagem.";
        return new(sum, acts, false);
    }
}
