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
    // Axis (Deployer): fonte única da configuração Efí. Quando preenchidos, os valores EFI_* vêm de lá.
    public const string AxisEfiUrl = "AXIS_EFI_CONFIG_URL";
    public const string AxisEfiToken = "AXIS_EFI_TOKEN";
    public const string AxisLicenseBilling = "AXIS_COBRANCA_LICENCAS";
    public const string AxisLicenseDaysBefore = "AXIS_COBRANCA_DIAS_ANTES";
    public const string AxisLicenseMaxOverdue = "AXIS_COBRANCA_MAX_ATRASO_DIAS";
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
    // Roberta 2.0: runtime OpenClaw (gateway privado) com GitHub Copilot; Ollama como contingência
    public const string RobertaProvider = "ROBERTA_PROVIDER"; // ollama (padrão) | openclaw
    public const string OpenClawUrl = "OPENCLAW_BASE_URL";
    public const string OpenClawToken = "OPENCLAW_TOKEN";
    public const string OpenClawModel = "ROBERTA_MODEL";
    public const string OpenClawTimeout = "ROBERTA_TIMEOUT_SECONDS";
    public const string RobertaFallbackOllama = "ROBERTA_FALLBACK_OLLAMA"; // true (padrão)
    // Voz (ElevenLabs): IA fala e entende áudios
    public const string ElevenLabsKey = "ELEVENLABS_API_KEY";
    public const string ElevenLabsVoiceId = "ELEVENLABS_VOICE_ID";
    public const string ElevenLabsModel = "ELEVENLABS_MODEL";
    public const string ElevenLabsSttModel = "ELEVENLABS_STT_MODEL";
    public const string ElevenLabsSttLanguage = "ELEVENLABS_STT_LANGUAGE";
    public const string ElevenLabsStability = "ELEVENLABS_STABILITY";
    public const string ElevenLabsSimilarity = "ELEVENLABS_SIMILARITY";
    public const string ElevenLabsVoiceReplies = "ELEVENLABS_VOICE_REPLIES";
    public const string ElevenLabsUrl = "ELEVENLABS_API_URL";
    // Ligações de voz da Roberta (prova de conceito; tudo desligado por padrão)
    public const string VoiceEnabled = "VOICE_CALLS_ENABLED";
    public const string VoiceKillSwitch = "VOICE_CALLS_KILL_SWITCH";
    public const string VoiceGoUrl = "VOICE_CALLS_GO_URL";
    public const string VoiceGoApiKey = "VOICE_CALLS_GO_API_KEY";
    public const string VoiceGoInstanceId = "VOICE_CALLS_GO_INSTANCE_ID";
    public const string VoiceGoStreamKey = "VOICE_CALLS_GO_STREAM_KEY";
    public const string VoiceWebhookToken = "VOICE_CALLS_WEBHOOK_TOKEN";
    public const string VoiceInboundEnabled = "VOICE_CALLS_INBOUND_ENABLED";
    public const string VoiceMaxConcurrent = "VOICE_CALLS_MAX_CONCURRENT";
    public const string VoiceMaxSeconds = "VOICE_CALLS_MAX_SECONDS";
    public const string VoiceMaxAttemptsDay = "VOICE_CALLS_MAX_ATTEMPTS_DAY";
    public const string VoiceRingSeconds = "VOICE_CALLS_RING_SECONDS";
    public const string VoiceStoreTranscript = "VOICE_CALLS_STORE_TRANSCRIPT";
    public const string VoiceSttMode = "VOICE_CALLS_STT_MODE"; // realtime (padrão) | batch
    public const string VoiceTtsModel = "VOICE_CALLS_TTS_MODEL"; // vazio = mesmo modelo configurado para áudios
    public const string VoiceCostTtsPer1kChars = "VOICE_COST_TTS_PER_1K_CHARS";
    public const string VoiceCostSttPerMinute = "VOICE_COST_STT_PER_MINUTE";
    public const string VoiceCostLlmPerCall = "VOICE_COST_LLM_PER_CALL";
    // Monitoramento
    public const string AlertNumbers = "MONITORING_ALERT_NUMBERS";
    public const string AlertCooldownMinutes = "MONITORING_ALERT_COOLDOWN_MINUTES";
    // Email (preparado: envio ainda não implementado)
    public const string SmtpHost = "SMTP_HOST";
    public const string SmtpPort = "SMTP_PORT";
    public const string SmtpUser = "SMTP_USER";
    public const string SmtpPassword = "SMTP_PASSWORD";
    public const string SmtpFrom = "SMTP_FROM";
    // Avisos automáticos de cobrança (WhatsApp e e-mail)
    public const string NoticeEnabled = "COBRANCA_AVISOS_ATIVOS";
    public const string NoticeChannels = "COBRANCA_CANAIS";
    public const string NoticeDaysBefore = "COBRANCA_DIAS_ANTES";
    public const string NoticeOnDue = "COBRANCA_AVISO_NO_VENCIMENTO";
    public const string NoticeDaysAfter = "COBRANCA_DIAS_APOS";
    public const string NoticeHour = "COBRANCA_HORA_ENVIO";
    public const string NoticeMsgBefore = "COBRANCA_MSG_ANTES";
    public const string NoticeMsgDue = "COBRANCA_MSG_VENCIMENTO";
    public const string NoticeMsgAfter = "COBRANCA_MSG_ATRASO";
    public const string NoticeEmailSubject = "COBRANCA_EMAIL_ASSUNTO";
    // Segurança
    public const string SessionHours = "SESSION_HOURS";

    public static readonly HashSet<string> Secrets = new()
    {
        GoogleApiKey, GoogleBasicPassword, AsaasKey, AsaasWebhookToken, EfiClientSecret, EfiCertBase64, EfiCertPassword, AxisEfiToken, EvolutionKey, EvolutionWebhookToken, SmtpPassword, OllamaApiKey, OpenClawToken, ElevenLabsKey, VoiceGoApiKey, VoiceGoStreamKey, VoiceWebhookToken
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
