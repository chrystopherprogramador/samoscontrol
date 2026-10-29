using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamosControl.Integrations;

/// <summary>Reads configuration (database settings first, environment variables as fallback).</summary>
public interface ISettingsReader
{
    string? Get(string key);
}

public static class SettingKeys
{
    // Empresa
    public const string CompanyName = "COMPANY_NAME";
    public const string CompanyDocument = "COMPANY_DOCUMENT";
    public const string CompanyAddress = "COMPANY_ADDRESS";
    // Dono/representante legal da empresa: assina todos os contratos como CONTRATADA
    public const string OwnerName = "OWNER_NAME";
    public const string OwnerDocument = "OWNER_DOCUMENT";
    public const string OwnerPhone = "OWNER_PHONE";
    public const string OwnerEmail = "OWNER_EMAIL";
    public const string OwnerTitle = "OWNER_TITLE";
    // Repasse de comissões: "manual" (padrão) ou "efi" (Pix automático pela Efí)
    public const string PayoutMode = "PAYOUT_MODE";
    // Google scraper (endpoints are configurable: this project does not assume a specific scraper API)
    public const string GoogleUrl = "GOOGLE_SCRAPER_URL";
    public const string GoogleApiKey = "GOOGLE_SCRAPER_API_KEY";
    public const string GoogleApiKeyHeader = "GOOGLE_SCRAPER_API_KEY_HEADER";
    public const string GoogleMode = "GOOGLE_SCRAPER_MODE"; // "gosom" = gosom/google-maps-scraper (jobs + CSV); empty = generic JSON API
    public const string GoogleBasicUser = "GOOGLE_SCRAPER_BASIC_USER";
    public const string GoogleBasicPassword = "GOOGLE_SCRAPER_BASIC_PASSWORD";
    public const string GoogleCreatePath = "GOOGLE_SCRAPER_CREATE_PATH";
    public const string GoogleCreateBody = "GOOGLE_SCRAPER_CREATE_BODY";
    public const string GoogleSearchIdPath = "GOOGLE_SCRAPER_SEARCH_ID_PATH";
    public const string GoogleResultsPath = "GOOGLE_SCRAPER_RESULTS_PATH";
    public const string GoogleItemsPath = "GOOGLE_SCRAPER_ITEMS_PATH";
    public const string GoogleMapPrefix = "GOOGLE_SCRAPER_MAP_"; // + NAME, PHONE, ADDRESS, WEBSITE, PLACE_ID, CITY, STATE, CATEGORY, EMAIL
    public const string GoogleSyncMinutes = "GOOGLE_SCRAPER_SYNC_MINUTES";
    // Asaas
    public const string AsaasUrl = "ASAAS_API_URL";
    public const string AsaasKey = "ASAAS_API_KEY";
    public const string AsaasWebhookToken = "ASAAS_WEBHOOK_TOKEN";
    // Provedor de cobrança ativo: "asaas" (padrão) ou "efi"
    public const string BillingProvider = "BILLING_PROVIDER";
    // Efí Bank
    public const string EfiClientId = "EFI_CLIENT_ID";
    public const string EfiClientSecret = "EFI_CLIENT_SECRET";
    public const string EfiSandbox = "EFI_SANDBOX";
    public const string EfiPixKey = "EFI_PIX_KEY";
    public const string EfiCertPath = "EFI_CERT_PATH";
    public const string EfiCertBase64 = "EFI_CERT_BASE64";
    public const string EfiCertPassword = "EFI_CERT_PASSWORD";
    // Evolution
    public const string EvolutionUrl = "EVOLUTION_API_URL";
    public const string EvolutionKey = "EVOLUTION_API_KEY";
    public const string EvolutionInstance = "EVOLUTION_INSTANCE";
    public const string EvolutionWebhookToken = "EVOLUTION_WEBHOOK_TOKEN";
    // IA
    public const string OllamaUrl = "OLLAMA_BASE_URL";
    public const string OllamaModel = "OLLAMA_MODEL";
    public const string OllamaApiKey = "OLLAMA_API_KEY";
    public const string OllamaFallbackModel = "OLLAMA_FALLBACK_MODEL";
    // Monitoramento
    public const string AlertNumbers = "MONITORING_ALERT_NUMBERS";
    public const string AlertCooldownMinutes = "MONITORING_ALERT_COOLDOWN_MINUTES";
    // Email (preparado: envio ainda não implementado)
    public const string SmtpHost = "SMTP_HOST";
    public const string SmtpPort = "SMTP_PORT";
    public const string SmtpUser = "SMTP_USER";
    public const string SmtpPassword = "SMTP_PASSWORD";
    public const string SmtpFrom = "SMTP_FROM";
    // Segurança
    public const string SessionHours = "SESSION_HOURS";

    public static readonly HashSet<string> Secrets = new()
    {
        GoogleApiKey, GoogleBasicPassword, AsaasKey, AsaasWebhookToken, EfiClientSecret, EfiCertBase64, EfiCertPassword, EvolutionKey, EvolutionWebhookToken, SmtpPassword, OllamaApiKey
    };
}

public class IntegrationException : Exception
{
    public IntegrationException(string message, Exception? inner = null) : base(message, inner) { }
}

public class NotConfiguredException : IntegrationException
{
    public NotConfiguredException(string integration) : base($"Integração '{integration}' não configurada.") { }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves a dotted path (a.b.c) in a JSON node. Empty path returns the node itself.</summary>
    public static JsonNode? At(JsonNode? node, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return node;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject o) node = o[part];
            else if (node is JsonArray a && int.TryParse(part, out var i) && i < a.Count) node = a[i];
            else return null;
        }
        return node;
    }

    /// <summary>Top-level property lookup ignoring case (Go serializes untagged fields as "Status", "ID").</summary>
    public static JsonNode? AtIgnoreCase(JsonNode? node, string name) =>
        node is JsonObject o ? o.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;

    public static string? Str(JsonNode? n)
    {
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString().Trim('"');
        }
        return null;
    }
}

public static class PhoneUtil
{
    /// <summary>Digits only; adds Brazil country code (55) when it looks like a national number.</summary>
    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.Length < 8) return null;
        if (d.Length is 10 or 11) d = "55" + d;
        return d;
    }

    /// <summary>Comparison key: area code + last 8 digits (ignores country code and the optional 9th digit).</summary>
    public static string? Key(string? phone)
    {
        var n = Normalize(phone);
        if (n is null) return null;
        return n.StartsWith("55") && n.Length >= 12 ? n[2..4] + n[^8..] : n[^8..];
    }
}
