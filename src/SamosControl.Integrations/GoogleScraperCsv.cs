using System.Text;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

/// <summary>
/// Reads the CSV produced by gosom/google-maps-scraper v1.18.1 (gmaps/entry.go CsvHeaders).
/// Columns used: title, category, address, website, phone, place_id, emails, complete_address (JSON with city/state).
/// </summary>
public static class GoogleScraperCsv
{
    public static List<ScrapedPlace> Parse(string csv)
    {
        var rows = ReadRows(csv);
        var list = new List<ScrapedPlace>();
        if (rows.Count == 0) return list;

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var title = Col("title");
        if (title < 0) throw new IntegrationException("CSV do scraper sem a coluna 'title'. Formato inesperado.");
        int category = Col("category"), address = Col("address"), website = Col("website"), phone = Col("phone"),
            placeId = Col("place_id"), emails = Col("emails"), complete = Col("complete_address");

        string? Get(List<string> r, int i) => i >= 0 && i < r.Count && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;

        foreach (var r in rows.Skip(1))
        {
            var (city, state) = CityState(Get(r, complete));
            var email = Get(r, emails)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            list.Add(new ScrapedPlace(Get(r, title), Get(r, phone), Get(r, address), Get(r, website), Get(r, placeId),
                city, state, Get(r, category), email));
        }
        return list;
    }

    private static (string? City, string? State) CityState(string? json)
    {
        if (json is null) return (null, null);
        try
        {
            var o = JsonNode.Parse(json) as JsonObject;
            string? V(string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
            return (V("city"), V("state"));
        }
        catch (System.Text.Json.JsonException) { return (null, null); }
    }

    /// <summary>RFC 4180 reader: quoted fields, "" escapes, commas and line breaks inside quotes, BOM, blank lines skipped.</summary>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return rows;
        if (text[0] == '\uFEFF') text = text[1..];
        var row = new List<string>();
        var f = new StringBuilder();
        bool inQuotes = false, fieldStarted = false;

        void EndField() { row.Add(f.ToString()); f.Clear(); fieldStarted = false; }
        void EndRow()
        {
            EndField();
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            row = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch != '"') f.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"') { f.Append('"'); i++; }
                else inQuotes = false;
                continue;
            }
            switch (ch)
            {
                case '"' when !fieldStarted: inQuotes = true; fieldStarted = true; break;
                case ',': EndField(); break;
                case '\r': if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); break;
                case '\n': EndRow(); break;
                default: f.Append(ch); fieldStarted = true; break;
            }
        }
        if (fieldStarted || f.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }
}
