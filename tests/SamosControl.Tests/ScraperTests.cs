using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SamosControl.Application;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Tests;

public class ScraperTests
{
    // Header and column order of gosom/google-maps-scraper v1.18.1 (gmaps/entry.go CsvHeaders).
    private const string Header = "input_id,link,title,category,address,open_hours,popular_times,website,phone,plus_code,review_count,review_rating,reviews_per_rating,latitude,longitude,cid,status,descriptions,reviews_link,thumbnail,timezone,price_range,data_id,street_view_url,place_id,images,reservations,order_online,menu,owner,complete_address,credit_cards_accepted,about,user_reviews,user_reviews_extended,emails";

    private static string Row(string title, string phone = "", string website = "", string placeId = "", string address = "", string category = "Loja", string complete = "", string emails = "", string descriptions = "")
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var cols = Header.Split(',');
        var vals = cols.Select(c => c switch
        {
            "title" => Q(title), "category" => Q(category), "address" => Q(address), "website" => Q(website), "phone" => Q(phone),
            "place_id" => Q(placeId), "complete_address" => Q(complete), "emails" => Q(emails), "descriptions" => Q(descriptions),
            "open_hours" => Q("{\"Monday\":[\"8 am–6 pm\"]}"), _ => ""
        });
        return string.Join(",", vals);
    }

    private sealed class Settings : ISettingsReader
    {
        public Dictionary<string, string?> V { get; } = new()
        {
            [SettingKeys.GoogleUrl] = "https://scraper.test",
            [SettingKeys.GoogleMode] = "gosom"
        };
        public string? Get(string key) => V.TryGetValue(key, out var v) ? v : null;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return Respond(request);
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public Factory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Csv(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, new UTF8Encoding(true), "text/csv") };

    private static (GoogleMapsScraperIntegration Api, Handler H, Settings S) Make()
    {
        var h = new Handler(); var s = new Settings();
        return (new GoogleMapsScraperIntegration(new Factory(h), s), h, s);
    }

    [Fact]
    public void Csv_ParsesQuotesCommasNewlinesAccentsAndEmptyFields()
    {
        var csv = Header + "\r\n" +
            Row("Padaria \"Pão Quente\", Centro", phone: "(62) 3333-4444", website: "https://pao.com.br", placeId: "ChIJabc", address: "Rua 1, 10\nSala 2, Goiânia",
                complete: "{\"city\":\"Goiânia\",\"state\":\"GO\",\"country\":\"BR\"}", emails: "a@x.com, b@y.com", descriptions: "linha1\nlinha2, \"citada\"") + "\r\n" +
            "\r\n" +
            Row("Sem Dados") + "\r\n";
        var places = GoogleScraperCsv.Parse("\uFEFF" + csv);

        Assert.Equal(2, places.Count);
        var a = places[0];
        Assert.Equal("Padaria \"Pão Quente\", Centro", a.Name);
        Assert.Equal("(62) 3333-4444", a.Phone);
        Assert.Equal("https://pao.com.br", a.Website);
        Assert.Equal("ChIJabc", a.PlaceId);
        Assert.Equal("Rua 1, 10\nSala 2, Goiânia", a.Address);
        Assert.Equal("Goiânia", a.City);
        Assert.Equal("GO", a.State);
        Assert.Equal("a@x.com", a.Email);
        Assert.Equal("Loja", a.Category);

        var b = places[1];
        Assert.Equal("Sem Dados", b.Name);
        Assert.Null(b.Phone); Assert.Null(b.Website); Assert.Null(b.PlaceId); Assert.Null(b.City); Assert.Null(b.State); Assert.Null(b.Email);
    }

    [Fact]
    public void Csv_EmptyBody_IsEmptyList_ButUnknownHeader_Throws()
    {
        Assert.Empty(GoogleScraperCsv.Parse(""));
        Assert.Throws<IntegrationException>(() => GoogleScraperCsv.Parse("foo,bar\n1,2\n"));
    }

    [Fact]
    public async Task Create_SendsEscapedJobBody_AndReadsId()
    {
        var (api, h, _) = Make();
        h.Respond = _ => Json("{\"id\":\"2a1b\"}", HttpStatusCode.Created);
        var r = await api.CreateSearchAsync("loja \"top\" é", "Goiânia, GO");

        Assert.Equal("2a1b", r.ExternalId);
        Assert.Empty(r.Items);
        Assert.Equal("https://scraper.test/api/v1/jobs", h.Requests[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        var body = System.Text.Json.Nodes.JsonNode.Parse(h.Bodies[0])!;
        Assert.Equal("loja \"top\" é Goiânia, GO", (string?)body["keywords"]![0]);
        Assert.Equal("pt", (string?)body["lang"]);
        Assert.Equal(10, (int?)body["depth"]);
        Assert.Equal(600, (int?)body["max_time"]);
    }

    [Fact]
    public async Task Status_MapsPendingWorkingOkFailed_CaseInsensitively()
    {
        var (api, h, _) = Make();
        foreach (var (raw, expected) in new[] { ("pending", ScraperJobStatus.Running), ("working", ScraperJobStatus.Running), ("ok", ScraperJobStatus.Completed), ("failed", ScraperJobStatus.Failed) })
        {
            h.Respond = _ => Json($"{{\"ID\":\"x\",\"Status\":\"{raw}\"}}");
            Assert.Equal(expected, await api.GetStatusAsync("x"));
        }
        h.Respond = _ => Json("{\"Status\":\"weird\"}");
        await Assert.ThrowsAsync<IntegrationException>(() => api.GetStatusAsync("x"));
    }

    [Fact]
    public async Task Download_ReadsCsv_AndRejectsJsonOrHtml()
    {
        var (api, h, _) = Make();
        h.Respond = _ => Csv("\uFEFF" + Header + "\n" + Row("Loja X", phone: "62999990000") + "\n");
        var items = await api.GetResultsAsync("abc");
        Assert.Single(items);
        Assert.Equal("https://scraper.test/api/v1/jobs/abc/download", h.Requests[0].RequestUri!.ToString());

        h.Respond = _ => Json("{\"items\":[]}");
        await Assert.ThrowsAsync<IntegrationException>(() => api.GetResultsAsync("abc"));
    }

    [Fact]
    public async Task BasicAuth_IsSent_AndAuthFailureDoesNotLeakCredentials()
    {
        var (api, h, s) = Make();
        s.V[SettingKeys.GoogleBasicUser] = "admin";
        s.V[SettingKeys.GoogleBasicPassword] = "S3nh@-Secreta";
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied", Encoding.UTF8, "text/html") };

        var ex = await Assert.ThrowsAsync<IntegrationException>(() => api.GetStatusAsync("x"));
        var auth = h.Requests[0].Headers.Authorization!;
        Assert.Equal("Basic", auth.Scheme);
        Assert.Equal("admin:S3nh@-Secreta", Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("S3nh@-Secreta", ex.Message);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:S3nh@-Secreta")), ex.Message);
    }

    [Fact]
    public async Task GenericJsonMode_IsUnchanged()
    {
        var (api, h, s) = Make();
        s.V[SettingKeys.GoogleMode] = null;
        s.V[SettingKeys.GoogleCreatePath] = "/search";
        s.V[SettingKeys.GoogleSearchIdPath] = "data.id";
        s.V[SettingKeys.GoogleResultsPath] = "/results/{{searchId}}";
        s.V[SettingKeys.GoogleItemsPath] = "items";
        h.Respond = r => r.Method == HttpMethod.Post ? Json("{\"data\":{\"id\":\"7\"}}") : Json("{\"items\":[{\"name\":\"Loja J\",\"phone\":\"6233334444\"}]}");

        var c = await api.CreateSearchAsync("q", "l");
        Assert.Equal("7", c.ExternalId);
        Assert.Equal(ScraperJobStatus.Unknown, await api.GetStatusAsync("7"));
        var items = await api.GetResultsAsync("7");
        Assert.Equal("Loja J", Assert.Single(items).Name);
    }

    private sealed class FakeScraper : IGoogleMapsScraperIntegration
    {
        public bool IsConfigured => true;
        public ScraperJobStatus Status { get; set; }
        public int Downloads { get; private set; }
        public List<ScrapedPlace> Items { get; set; } = new();
        public Task<ScraperSearchCreated> CreateSearchAsync(string query, string? location, CancellationToken ct = default) => Task.FromResult(new ScraperSearchCreated("job-1", new()));
        public Task<ScraperJobStatus> GetStatusAsync(string externalId, CancellationToken ct = default) => Task.FromResult(Status);
        public Task<List<ScrapedPlace>> GetResultsAsync(string externalId, CancellationToken ct = default) { Downloads++; return Task.FromResult(Items); }
    }

    private static (LeadSyncService Svc, AppDbContext Db) SyncSvc(IServiceScope sc, FakeScraper fake)
    {
        var sp = sc.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        return (new LeadSyncService(db, fake, sp.GetRequiredService<LeadService>(), sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<ILogger<LeadSyncService>>()), db);
    }

    [Fact]
    public async Task Sync_PendingDoesNotImport_CompletedImportsOnce_RepeatedSyncDoesNotDuplicate()
    {
        using var host = new TestHost(); using var sc = host.Scope();
        var fake = new FakeScraper { Status = ScraperJobStatus.Running, Items = { new ScrapedPlace("Loja A", "62999991111", null, null, "PID1", null, null, "Loja", null) } };
        var (svc, db) = SyncSvc(sc, fake);
        var search = await svc.CreateSearchAsync("loja", "Goiânia", null);

        await svc.SyncAsync();
        Assert.Equal(0, fake.Downloads);
        Assert.Equal(0, await db.Leads.CountAsync());
        Assert.Equal("ATIVA", (await db.ScraperSearches.SingleAsync()).Status);

        fake.Status = ScraperJobStatus.Completed;
        var run = await svc.SyncAsync();
        Assert.Equal(1, run.Created);
        Assert.Equal("CONCLUIDA", (await db.ScraperSearches.SingleAsync()).Status);

        await svc.SyncAsync();
        Assert.Equal(1, fake.Downloads);
        Assert.Equal(1, await db.Leads.CountAsync());

        // Same place again through a new search: deduplicated by place id/phone.
        await svc.CreateSearchAsync("loja", "Goiânia", null);
        await svc.SyncAsync();
        Assert.Equal(1, await db.Leads.CountAsync());
        Assert.Equal(search.Id, (await db.ScraperSearches.OrderBy(x => x.Id).FirstAsync()).Id);
    }

    [Fact]
    public async Task Sync_Failed_MarksSearchFailed_WithoutDownload()
    {
        using var host = new TestHost(); using var sc = host.Scope();
        var fake = new FakeScraper { Status = ScraperJobStatus.Failed };
        var (svc, db) = SyncSvc(sc, fake);
        await svc.CreateSearchAsync("loja", null, null);

        var run = await svc.SyncAsync();
        var s = await db.ScraperSearches.SingleAsync();
        Assert.Equal("FALHA", s.Status);
        Assert.NotNull(s.LastError);
        Assert.Equal(0, fake.Downloads);
        Assert.Equal(1, run.Errors);
        Assert.Equal("COM_ERROS", run.Status);
    }
}
