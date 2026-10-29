using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SamosControl.Domain;
using SamosControl.Infrastructure;
using SamosControl.Integrations;

namespace SamosControl.Application;

public record SignRequest(string Name, string? Document, string? Email, string? Phone, bool AcceptedTerms, string? Otp = null);
public record SignResult(bool Success, string? Error, bool ContractCompleted);
public record ContractTerms(decimal ImplementationValue, decimal Discount, int Installments, DateTime? FirstDue,
    decimal MonthlyValue, DateTime? MonthlyStart, int? MonthlyMonths, BillingType BillingType);
public record LinkSendResult(string Party, bool Sent, string Message);

public static class DocumentUtil
{
    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    public static bool IsValid(string? doc)
    {
        var d = Digits(doc);
        return d.Length switch { 11 => ValidCpf(d), 14 => ValidCnpj(d), _ => false };
    }

    private static bool ValidCpf(string d)
    {
        if (d.Distinct().Count() == 1) return false;
        for (var t = 9; t < 11; t++)
        {
            var sum = 0;
            for (var i = 0; i < t; i++) sum += (d[i] - '0') * (t + 1 - i);
            var dv = sum * 10 % 11 % 10;
            if (dv != d[t] - '0') return false;
        }
        return true;
    }

    private static bool ValidCnpj(string d)
    {
        if (d.Distinct().Count() == 1) return false;
        int[] w1 = { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 }, w2 = { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
        int Dv(int[] w) { var s = 0; for (var i = 0; i < w.Length; i++) s += (d[i] - '0') * w[i]; var r = s % 11; return r < 2 ? 0 : 11 - r; }
        return Dv(w1) == d[12] - '0' && Dv(w2) == d[13] - '0';
    }

    /// <summary>Masks a CPF/CNPJ for public display (LGPD data minimisation).</summary>
    public static string Mask(string? doc)
    {
        var d = Digits(doc);
        return d.Length == 11 ? $"***.{d[3..6]}.{d[6..9]}-**" : d.Length == 14 ? $"**.{d[2..5]}.{d[5..8]}/****-**" : "—";
    }
}

public class ContractService
{
    public const string TermsText =
        "Li e concordo com o conteúdo integral deste contrato e aceito que a assinatura eletrônica aqui realizada seja admitida como válida e " +
        "suficiente para comprovar minha autoria e a integridade do documento, nos termos do art. 10, §2º, da MP nº 2.200-2/2001 e da Lei nº 14.063/2020. " +
        "Declaro que sou a pessoa identificada, que os dados informados são verdadeiros e que autorizo o tratamento dos dados desta sessão (IP, data e hora, " +
        "dispositivo/navegador, telefone e documentos informados) como trilha de auditoria, com a finalidade de execução e comprovação deste contrato (Lei nº 13.709/2018 - LGPD).";

    public static readonly string[] Placeholders =
    {
        "CLIENTE_NOME", "CLIENTE_CPF_CNPJ", "CLIENTE_ENDERECO", "CLIENTE_EMAIL", "EMPRESA_NOME", "EMPRESA_CNPJ", "EMPRESA_ENDERECO",
        "REPRESENTANTE_NOME", "REPRESENTANTE_CPF", "REPRESENTANTE_CARGO",
        "SERVICO", "VALOR", "VENCIMENTO", "DATA_CONTRATO",
        "VALOR_IMPLANTACAO", "DESCONTO", "VALOR_IMPLANTACAO_LIQUIDO", "PARCELAS_IMPLANTACAO", "VALOR_PARCELA_IMPLANTACAO", "PRIMEIRO_VENCIMENTO_IMPLANTACAO",
        "VALOR_MENSALIDADE", "INICIO_MENSALIDADE", "DIA_VENCIMENTO", "PRAZO_MESES", "FORMA_PAGAMENTO", "CONDICOES_FINANCEIRAS", "CODIGO_VERIFICACAO",
        "VENDEDOR_NOME", "VENDEDOR_CPF_CNPJ", "VENDEDOR_EMAIL", "VENDEDOR_ENDERECO", "VENDEDOR_PIX", "COMISSAO_IMPLANTACAO", "COMISSAO_MENSALIDADE", "COMISSAO_PRAZO"
    };

    public const string RoleClient = "CONTRATANTE", RoleCompany = "CONTRATADA", RoleSeller = "PARCEIRO COMERCIAL", RoleCompanyPartnership = "EMPRESA";
    public const string KindService = "SERVICO", KindPartnership = "PARCERIA";

    private static readonly CultureInfo Br = new("pt-BR");
    private const string Brand = "#1f3a68";

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly FileStorage _storage;
    private readonly AuditService _audit;
    private readonly NotificationService _notify;
    private readonly IConfiguration _cfg;
    private readonly WhatsAppService _wa;
    private readonly ContractBillingService _billing;

    static ContractService() { QuestPDF.Settings.License = LicenseType.Community; }

    public ContractService(AppDbContext db, SettingsService settings, FileStorage storage, AuditService audit, NotificationService notify, IConfiguration cfg,
        WhatsAppService wa, ContractBillingService billing)
    { _db = db; _settings = settings; _storage = storage; _audit = audit; _notify = notify; _cfg = cfg; _wa = wa; _billing = billing; }

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Hash covers the rendered text AND the commercial terms, so neither can change after sending.</summary>
    public static string ComputeHash(Contract c)
    {
        if (c.VerificationCode is null) return Sha256(c.RenderedText); // contracts created before the financial terms existed
        var inv = CultureInfo.InvariantCulture;
        var terms = string.Join("|", c.ImplementationValue.ToString(inv), c.DiscountValue.ToString(inv), c.ImplementationInstallments,
            c.ImplementationFirstDue?.ToString("yyyy-MM-dd"), c.MonthlyValue.ToString(inv), c.MonthlyStart?.ToString("yyyy-MM-dd"), c.MonthlyMonths, c.BillingType, c.VerificationCode);
        return Sha256(c.RenderedText + "\n#TERMS#" + terms);
    }

    public static string Render(string template, IDictionary<string, string> values) =>
        Regex.Replace(template, @"\{\{\s*([A-Za-z_]+)\s*\}\}", m =>
            values.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out var v) ? v : m.Value);

    public static string Money(decimal v) =>
        "R$ " + v.ToString("N2", CultureInfo.InvariantCulture).Replace(",", "X").Replace(".", ",").Replace("X", ".");

    public static string FinancialText(Contract k)
    {
        var lines = new List<string>();
        var net = BillingPlan.NetImplementation(k);
        if (k.ImplementationValue > 0)
        {
            var parts = BillingPlan.Split(net, k.ImplementationInstallments);
            var s = $"Implantação: valor de {Money(k.ImplementationValue)}";
            if (k.DiscountValue > 0) s += $", com desconto negociado de {Money(k.DiscountValue)}, totalizando {Money(net)}";
            s += parts.Length > 1
                ? $", pago em {parts.Length} parcelas mensais de {Money(parts[0])}" + (parts.Distinct().Count() > 1 ? " (a primeira parcela absorve eventual diferença de centavos)" : "") + $", a primeira com vencimento em {k.ImplementationFirstDue:dd/MM/yyyy}."
                : $", com vencimento em {k.ImplementationFirstDue:dd/MM/yyyy}.";
            lines.Add(s);
        }
        if (k.MonthlyValue > 0)
            lines.Add($"Mensalidade: {Money(k.MonthlyValue)} por mês, com primeiro vencimento em {k.MonthlyStart:dd/MM/yyyy} e demais no dia {k.MonthlyStart?.Day} de cada mês, " +
                      (k.MonthlyMonths.HasValue ? $"por {k.MonthlyMonths} mês(es)." : "por prazo indeterminado."));
        if (lines.Count == 0) lines.Add($"Valor: {Money(k.Value)}" + (k.DueDate.HasValue ? $", com vencimento em {k.DueDate:dd/MM/yyyy}." : "."));
        lines.Add("Forma de pagamento: " + (k.BillingType == BillingType.Pix ? "PIX" : "boleto bancário") + ", por cobrança emitida pela CONTRATADA.");
        return string.Join("\n", lines);
    }

    public Dictionary<string, string> BuildValues(Customer? c, Contract k, Seller? seller = null)
    {
        var net = BillingPlan.NetImplementation(k);
        var inst = BillingPlan.Split(net, k.ImplementationInstallments);
        var owner = OwnerInfo();
        return new Dictionary<string, string>
        {
            ["EMPRESA_ENDERECO"] = _settings.Get(SettingKeys.CompanyAddress) ?? "",
            ["REPRESENTANTE_NOME"] = owner.Name, ["REPRESENTANTE_CPF"] = owner.Document ?? "", ["REPRESENTANTE_CARGO"] = _settings.Get(SettingKeys.OwnerTitle) ?? "representante legal",
            ["VENDEDOR_NOME"] = seller?.Name ?? "", ["VENDEDOR_CPF_CNPJ"] = seller?.Document ?? "", ["VENDEDOR_EMAIL"] = seller?.Email ?? "",
            ["VENDEDOR_ENDERECO"] = seller?.Address ?? "",
            ["VENDEDOR_PIX"] = string.IsNullOrWhiteSpace(seller?.PixKey) ? "a ser informada pelo PARCEIRO" : $"{seller!.PixKey} ({seller.PixKeyType ?? "chave Pix"})",
            ["COMISSAO_IMPLANTACAO"] = (seller?.ImplementationPercent ?? 0).ToString("0.##", Br) + "%",
            ["COMISSAO_MENSALIDADE"] = (seller?.MonthlyPercent ?? 0).ToString("0.##", Br) + "%",
            ["COMISSAO_PRAZO"] = seller?.MonthlyCommissionMonths is { } m ? $"pelos primeiros {m} mês(es) de cada cliente" : "enquanto o cliente permanecer ativo e adimplente",
            ["CLIENTE_NOME"] = c?.Name ?? "", ["CLIENTE_CPF_CNPJ"] = c?.Document ?? "",
            ["CLIENTE_ENDERECO"] = string.Join(", ", new[] { c?.Address, c?.City, c?.State }.Where(x => !string.IsNullOrWhiteSpace(x))),
            ["CLIENTE_EMAIL"] = c?.Email ?? "",
            ["EMPRESA_NOME"] = _settings.Get(SettingKeys.CompanyName) ?? "Samos", ["EMPRESA_CNPJ"] = _settings.Get(SettingKeys.CompanyDocument) ?? "",
            ["SERVICO"] = k.ServiceName ?? "", ["VALOR"] = Money(k.Value),
            ["VENCIMENTO"] = k.DueDate?.ToString("dd/MM/yyyy") ?? "", ["DATA_CONTRATO"] = AppTime.Now.ToString("dd/MM/yyyy"),
            ["VALOR_IMPLANTACAO"] = Money(k.ImplementationValue), ["DESCONTO"] = Money(k.DiscountValue), ["VALOR_IMPLANTACAO_LIQUIDO"] = Money(net),
            ["PARCELAS_IMPLANTACAO"] = k.ImplementationInstallments.ToString(), ["VALOR_PARCELA_IMPLANTACAO"] = Money(inst[0]),
            ["PRIMEIRO_VENCIMENTO_IMPLANTACAO"] = k.ImplementationFirstDue?.ToString("dd/MM/yyyy") ?? "",
            ["VALOR_MENSALIDADE"] = Money(k.MonthlyValue), ["INICIO_MENSALIDADE"] = k.MonthlyStart?.ToString("dd/MM/yyyy") ?? "",
            ["DIA_VENCIMENTO"] = k.MonthlyStart?.Day.ToString() ?? "", ["PRAZO_MESES"] = k.MonthlyMonths?.ToString() ?? "indeterminado",
            ["FORMA_PAGAMENTO"] = k.BillingType == BillingType.Pix ? "PIX" : "boleto bancário",
            ["CONDICOES_FINANCEIRAS"] = FinancialText(k), ["CODIGO_VERIFICACAO"] = k.VerificationCode ?? ""
        };
    }

    public static void ValidateTerms(ContractTerms t)
    {
        if (t.ImplementationValue < 0 || t.MonthlyValue < 0 || t.Discount < 0) throw new ArgumentException("Valores não podem ser negativos.");
        if (t.Discount > t.ImplementationValue) throw new ArgumentException("O desconto não pode ser maior que o valor da implantação.");
        if (t.ImplementationValue - t.Discount > 0)
        {
            if (t.Installments is < 1 or > 24) throw new ArgumentException("Parcelas da implantação: de 1 a 24.");
            if (t.FirstDue is null) throw new ArgumentException("Informe o primeiro vencimento da implantação.");
        }
        if (t.MonthlyValue > 0 && t.MonthlyStart is null) throw new ArgumentException("Informe o primeiro vencimento da mensalidade.");
        if (t.MonthlyMonths is < 1 or > 120) throw new ArgumentException("Prazo da mensalidade: de 1 a 120 meses (ou vazio para indeterminado).");
    }

    public record OwnerData(string Name, string? Document, string? Phone, string? Email, bool Configured);

    /// <summary>The company representative who co-signs every contract. Phone falls back to the first monitoring alert number.</summary>
    public OwnerData OwnerInfo()
    {
        var name = _settings.Get(SettingKeys.OwnerName)?.Trim();
        var phone = _settings.Get(SettingKeys.OwnerPhone)?.Trim();
        if (string.IsNullOrWhiteSpace(phone))
            phone = (_settings.Get(SettingKeys.AlertNumbers) ?? "").Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var configured = !string.IsNullOrWhiteSpace(name);
        return new(configured ? name! : _settings.Get(SettingKeys.CompanyName) ?? "Samos", _settings.Get(SettingKeys.OwnerDocument) ?? _settings.Get(SettingKeys.CompanyDocument), phone, _settings.Get(SettingKeys.OwnerEmail), configured);
    }

    public static bool IsCompanyParty(ContractParty p) => p.Role is RoleCompany or RoleCompanyPartnership;

    /// <summary>Adds the company representative as a signer (idempotent). Skipped when no representative was configured in Settings.</summary>
    public bool EnsureOwnerParty(Contract c)
    {
        if (c.Parties.Any(IsCompanyParty)) return true;
        var o = OwnerInfo();
        if (!o.Configured) return false;
        c.Parties.Add(new ContractParty
        {
            Role = c.Kind == KindPartnership ? RoleCompanyPartnership : RoleCompany, Name = o.Name, Document = o.Document, Email = o.Email, Phone = o.Phone, Token = NewToken()
        });
        return true;
    }

    public async Task<Contract> CreateAsync(int templateId, int customerId, string? title, string? service, decimal value, DateTime? due, ContractTerms? terms = null, int? sellerId = null)
    {
        var t = await _db.ContractTemplates.FindAsync(templateId) ?? throw new KeyNotFoundException("Modelo não encontrado.");
        var c = await _db.Customers.FindAsync(customerId) ?? throw new KeyNotFoundException("Cliente não encontrado.");
        sellerId ??= c.SellerId;
        var seller = sellerId.HasValue ? await _db.Sellers.FindAsync(sellerId.Value) ?? throw new KeyNotFoundException("Vendedor não encontrado.") : null;
        var contract = new Contract
        {
            Title = string.IsNullOrWhiteSpace(title) ? $"{t.Name} - {c.Name}" : title.Trim(), TemplateId = t.Id, CustomerId = c.Id,
            ServiceName = service, Value = value, DueDate = due, VerificationCode = NewVerificationCode(), Kind = KindService,
            SellerId = seller?.Id, ImplementationCommissionPercent = seller?.ImplementationPercent ?? 0, MonthlyCommissionPercent = seller?.MonthlyPercent ?? 0
        };
        if (seller != null && c.SellerId is null) c.SellerId = seller.Id; // the client joins the seller's carteira
        if (terms != null)
        {
            ValidateTerms(terms);
            contract.ImplementationValue = terms.ImplementationValue; contract.DiscountValue = terms.Discount;
            contract.ImplementationInstallments = Math.Max(1, terms.Installments); contract.ImplementationFirstDue = terms.FirstDue;
            contract.MonthlyValue = terms.MonthlyValue; contract.MonthlyStart = terms.MonthlyStart; contract.MonthlyMonths = terms.MonthlyMonths;
            contract.BillingType = terms.BillingType;
            if (value <= 0) contract.Value = BillingPlan.NetImplementation(contract) + (terms.MonthlyMonths.HasValue ? terms.MonthlyValue * terms.MonthlyMonths.Value : 0);
        }
        contract.RenderedText = Render(t.Body, BuildValues(c, contract, seller));
        contract.Parties.Add(new ContractParty
        {
            Role = RoleClient, Name = c.Name, Document = c.Document, Email = c.Email, Phone = c.WhatsApp ?? c.Phone, Token = NewToken()
        });
        var ownerOk = EnsureOwnerParty(contract);
        contract.Events.Add(new ContractEvent { Type = "CRIADO", Details = ownerOk ? null : "Representante da empresa não configurado: o contrato não terá a assinatura da CONTRATADA." });
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "create", "Contract", contract.Id, newData: contract.Title);
        return contract;
    }

    /// <summary>Partnership contract between the company and a seller (commission terms). It generates no charges.</summary>
    public static bool IsPartnershipTemplate(ContractTemplate t) =>
        t.Name == DbInitializer.PartnershipTemplateName ||
        (Regex.IsMatch(t.Body, @"\{\{\s*VENDEDOR_", RegexOptions.IgnoreCase) && !Regex.IsMatch(t.Body, @"\{\{\s*CLIENTE_", RegexOptions.IgnoreCase));

    public async Task<Contract> CreatePartnershipAsync(int templateId, int sellerId, string? title = null)
    {
        var t = await _db.ContractTemplates.FindAsync(templateId) ?? throw new KeyNotFoundException("Modelo não encontrado.");
        var s = await _db.Sellers.FindAsync(sellerId) ?? throw new KeyNotFoundException("Vendedor não encontrado.");
        if (string.IsNullOrWhiteSpace(s.Document) || !DocumentUtil.IsValid(s.Document)) throw new ArgumentException("Informe um CPF/CNPJ válido para o vendedor antes de gerar o contrato.");
        if (string.IsNullOrWhiteSpace(s.Phone)) throw new ArgumentException("Informe o WhatsApp do vendedor para enviar o link de assinatura.");
        var contract = new Contract
        {
            Title = string.IsNullOrWhiteSpace(title) ? $"Parceria comercial - {s.Name}" : title.Trim(), TemplateId = t.Id, ServiceName = "Parceria comercial (vendas)", VerificationCode = NewVerificationCode(), Kind = KindPartnership,
            SellerId = s.Id, ImplementationCommissionPercent = s.ImplementationPercent, MonthlyCommissionPercent = s.MonthlyPercent, BillingClosed = true
        };
        contract.RenderedText = Render(t.Body, BuildValues(null, contract, s));
        contract.Parties.Add(new ContractParty { Role = RoleSeller, Name = s.Name, Document = s.Document, Email = s.Email, Phone = s.Phone, Token = NewToken() });
        var ownerOk = EnsureOwnerParty(contract);
        contract.Events.Add(new ContractEvent { Type = "CRIADO", Details = ownerOk ? null : "Representante da empresa não configurado." });
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync();
        s.PartnerContractId = contract.Id;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "create_partnership", "Contract", contract.Id, newData: s.Name);
        return contract;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string NewVerificationCode()
    {
        var h = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        return $"{h[..4]}-{h[4..8]}-{h[8..12]}";
    }

    public static string NormalizeCode(string? code) => Regex.Replace((code ?? "").ToUpperInvariant(), "[^A-F0-9]", "") is { Length: 12 } s ? $"{s[..4]}-{s[4..8]}-{s[8..12]}" : "";

    public async Task AddPartyAsync(int contractId, string role, string name, string? doc, string? email, string? phone)
    {
        var c = await _db.Contracts.FindAsync(contractId) ?? throw new KeyNotFoundException();
        if (c.Status != ContractStatus.Rascunho) throw new InvalidOperationException("Só é possível alterar partes de contratos em rascunho.");
        _db.ContractParties.Add(new ContractParty { ContractId = contractId, Role = role, Name = name, Document = doc, Email = email, Phone = phone, Token = NewToken() });
        await _db.SaveChangesAsync();
    }

    public string SigningUrl(string token) => $"{(_cfg["APP_URL"] ?? "").TrimEnd('/')}/sign/{token}";
    public string VerifyUrl(string? code) => $"{(_cfg["APP_URL"] ?? "").TrimEnd('/')}/verify/{code}";

    /// <summary>Locks the document (hash) and sends the signing link to every signer's WhatsApp.</summary>
    public async Task<List<LinkSendResult>> SendForSignatureAsync(int id)
    {
        var c = await _db.Contracts.Include(x => x.Parties).FirstOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
        if (c.Status != ContractStatus.Rascunho) throw new InvalidOperationException("O contrato não está em rascunho.");
        EnsureOwnerParty(c); // contracts drafted before the owner was configured still get the company signature
        if (c.Parties.Count == 0) throw new InvalidOperationException("Adicione ao menos uma parte signatária.");
        c.VerificationCode ??= NewVerificationCode();
        c.DocumentHash = ComputeHash(c);
        c.Status = ContractStatus.AguardandoAssinatura;
        c.SentAt = DateTime.UtcNow;
        _db.ContractEvents.Add(new ContractEvent { ContractId = id, Type = "ENVIADO_PARA_ASSINATURA", Details = "hash=" + c.DocumentHash });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "send_for_signature", "Contract", id, newData: c.DocumentHash);

        var results = new List<LinkSendResult>();
        foreach (var p in c.Parties.OrderBy(x => x.Id)) results.Add(await SendLinkAsync(c, p));
        return results;
    }

    public async Task<LinkSendResult> ResendLinkAsync(int contractId, int partyId)
    {
        var c = await _db.Contracts.Include(x => x.Parties).FirstOrDefaultAsync(x => x.Id == contractId) ?? throw new KeyNotFoundException();
        var p = c.Parties.FirstOrDefault(x => x.Id == partyId) ?? throw new KeyNotFoundException();
        if (c.Status != ContractStatus.AguardandoAssinatura) throw new InvalidOperationException("Contrato não está aguardando assinatura.");
        if (p.Signature != null || await _db.ContractSignatures.AnyAsync(s => s.PartyId == p.Id)) throw new InvalidOperationException("Esta parte já assinou.");
        return await SendLinkAsync(c, p);
    }

    private async Task<LinkSendResult> SendLinkAsync(Contract c, ContractParty p)
    {
        LinkSendResult res;
        if (string.IsNullOrWhiteSpace(p.Phone)) res = new(p.Name, false, "sem telefone/WhatsApp cadastrado");
        else if (!_wa.IsConfigured) res = new(p.Name, false, "Evolution API não configurada");
        else
        {
            var company = _settings.Get(SettingKeys.CompanyName) ?? "Samos";
            var first = p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p.Name;
            var other = c.Parties.Where(x => x.Id != p.Id && !IsCompanyParty(x)).Select(x => x.Name).FirstOrDefault();
            var intro = IsCompanyParty(p)
                ? $"Olá, {first}! 👋\n\nO contrato *{c.Title}*{(other != null ? $" (parte: {other})" : "")} foi emitido e aguarda a *sua assinatura* como representante da {company}."
                : $"Olá, {first}! 👋\n\n{company} enviou o contrato *{c.Title}* para a sua assinatura eletrônica.";
            var msg = $"{intro}\n\n" +
                      $"🔗 Leia e assine pelo celular:\n{SigningUrl(p.Token)}\n\n" +
                      "Para sua segurança, enviaremos um código de confirmação neste mesmo WhatsApp antes de concluir a assinatura. " +
                      "Este link é pessoal e intransferível. Se você não reconhece esta solicitação, ignore esta mensagem.";
            try { await _wa.SendTextAsync(p.Phone, msg); res = new(p.Name, true, "enviado"); }
            catch (Exception ex) when (ex is IntegrationException or ArgumentException) { res = new(p.Name, false, ex.Message.Truncate(200)); }
        }
        _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, PartyId = p.Id, Type = res.Sent ? "LINK_ENVIADO_WHATSAPP" : "LINK_NAO_ENVIADO", Details = $"{p.Name}: {res.Message}" });
        await _db.SaveChangesAsync();
        return res;
    }

    public async Task CancelAsync(int id)
    {
        var c = await _db.Contracts.FindAsync(id) ?? throw new KeyNotFoundException();
        if (c.Status == ContractStatus.Assinado) throw new InvalidOperationException("Contrato assinado não pode ser cancelado.");
        c.Status = ContractStatus.Cancelado;
        _db.ContractEvents.Add(new ContractEvent { ContractId = id, Type = "CANCELADO" });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "cancel", "Contract", id);
    }

    public async Task<ContractParty?> OpenForSigningAsync(string token, string? ip, string? ua)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var p = await _db.ContractParties.Include(x => x.Contract).Include(x => x.Signature).FirstOrDefaultAsync(x => x.Token == token);
        if (p?.Contract is null) return null;
        _db.ContractEvents.Add(new ContractEvent { ContractId = p.ContractId, PartyId = p.Id, Type = "LINK_ACESSADO", Ip = ip, UserAgent = Trunc(ua, 500) });
        await _db.SaveChangesAsync();
        return p;
    }

    /// <summary>The one-time WhatsApp code is mandatory whenever WhatsApp is configured and the signer has a phone.</summary>
    public bool RequiresOtp(ContractParty p) => !string.IsNullOrWhiteSpace(p.Phone) && _wa.IsConfigured;

    private static string OtpHash(string token, string code) => Sha256(token + ":" + code);

    public async Task<(bool Ok, string Message)> RequestOtpAsync(string token, string? ip, string? ua)
    {
        var p = await _db.ContractParties.Include(x => x.Contract).Include(x => x.Signature).FirstOrDefaultAsync(x => x.Token == token);
        if (p?.Contract is null || p.Contract.Status != ContractStatus.AguardandoAssinatura || p.Signature != null) return (false, "Link inválido ou contrato indisponível.");
        if (!RequiresOtp(p)) return (false, "Confirmação por código não é necessária.");
        if (p.OtpSentAt.HasValue && DateTime.UtcNow - p.OtpSentAt < TimeSpan.FromSeconds(60)) return (false, "Aguarde 1 minuto para solicitar um novo código.");
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        try { await _wa.SendTextAsync(p.Phone!, $"Seu código de confirmação para assinar o contrato \"{p.Contract.Title}\" é: *{code}*\nVálido por 10 minutos. Não compartilhe este código."); }
        catch (Exception ex) when (ex is IntegrationException or ArgumentException)
        {
            _db.ContractEvents.Add(new ContractEvent { ContractId = p.ContractId, PartyId = p.Id, Type = "CODIGO_NAO_ENVIADO", Ip = ip, Details = ex.Message.Truncate(200) });
            await _db.SaveChangesAsync();
            return (false, "Não foi possível enviar o código por WhatsApp. Tente novamente em instantes.");
        }
        p.OtpHash = OtpHash(token, code); p.OtpExpiresAt = DateTime.UtcNow.AddMinutes(10); p.OtpAttempts = 0; p.OtpSentAt = DateTime.UtcNow;
        _db.ContractEvents.Add(new ContractEvent { ContractId = p.ContractId, PartyId = p.Id, Type = "CODIGO_ENVIADO", Ip = ip, UserAgent = Trunc(ua, 500) });
        await _db.SaveChangesAsync();
        return (true, "Código enviado para o seu WhatsApp.");
    }

    public async Task<SignResult> SignAsync(string token, SignRequest r, string? ip, string? ua)
    {
        var p = await _db.ContractParties.Include(x => x.Contract).ThenInclude(c => c!.Parties).Include(x => x.Signature)
            .FirstOrDefaultAsync(x => x.Token == token);
        if (p?.Contract is null) return new(false, "Link inválido.", false);
        var c = p.Contract;
        if (c.Status != ContractStatus.AguardandoAssinatura) return new(false, "Este contrato não está disponível para assinatura.", false);
        if (p.Signature != null) return new(false, "Você já assinou este contrato.", false);
        if (!r.AcceptedTerms) return new(false, "É necessário aceitar os termos para assinar.", false);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length < 3) return new(false, "Informe seu nome completo.", false);
        if (!DocumentUtil.IsValid(r.Document)) return new(false, "Informe um CPF ou CNPJ válido.", false);
        if (ComputeHash(c) != c.DocumentHash) return new(false, "Integridade do documento não confere. Contate o remetente.", false);

        var verified = false;
        if (RequiresOtp(p))
        {
            var code = DocumentUtil.Digits(r.Otp);
            if (p.OtpHash is null || p.OtpExpiresAt is null || p.OtpExpiresAt < DateTime.UtcNow) return new(false, "Solicite o código de confirmação por WhatsApp e informe-o para assinar.", false);
            if (p.OtpAttempts >= 5) return new(false, "Muitas tentativas. Solicite um novo código.", false);
            p.OtpAttempts++;
            var ok = code.Length == 6 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(OtpHash(token, code)), Encoding.UTF8.GetBytes(p.OtpHash));
            if (!ok)
            {
                _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, PartyId = p.Id, Type = "CODIGO_INVALIDO", Ip = ip, UserAgent = Trunc(ua, 500) });
                await _db.SaveChangesAsync();
                return new(false, "Código de confirmação inválido.", false);
            }
            p.OtpHash = null; verified = true;
        }

        var now = DateTime.UtcNow;
        var sig = new ContractSignature
        {
            ContractId = c.Id, PartyId = p.Id, SignedAt = now, Name = r.Name.Trim(), Document = Trunc(r.Document?.Trim(), 30),
            Email = Trunc(r.Email?.Trim(), 200), Phone = Trunc(verified ? p.Phone : r.Phone?.Trim(), 40), Ip = ip, UserAgent = Trunc(ua, 500),
            DocumentHash = c.DocumentHash!, AcceptedTerms = true, AcceptedTermsText = TermsText,
            VerificationMethod = verified ? "WHATSAPP_OTP" : null, VerifiedPhone = verified ? p.Phone : null, VerifiedAt = verified ? now : null
        };
        sig.SignatureHash = Sha256(string.Join("|", c.DocumentHash, p.Token, sig.Name, sig.Document, sig.Email, sig.Phone, ip, ua, now.ToString("O"), sig.VerificationMethod));
        _db.ContractSignatures.Add(sig);
        _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, PartyId = p.Id, Type = "ASSINADO", Ip = ip, UserAgent = Trunc(ua, 500), Details = $"{sig.Name}; sig={sig.SignatureHash}" + (verified ? "; otp=ok" : "") });
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException) { return new(false, "Assinatura já registrada.", false); }
        await _audit.LogAsync("contracts", "sign", "Contract", c.Id, newData: $"party={p.Id}; sig={sig.SignatureHash}", userName: sig.Name);

        var all = await _db.ContractParties.Where(x => x.ContractId == c.Id).Select(x => x.Signature != null).ToListAsync();
        if (all.All(x => x))
        {
            await FinalizeAsync(c.Id);
            return new(true, null, true);
        }
        return new(true, null, false);
    }

    private async Task FinalizeAsync(int contractId)
    {
        var c = await _db.Contracts.Include(x => x.Customer).Include(x => x.Parties).ThenInclude(p => p.Signature).Include(x => x.Events).FirstAsync(x => x.Id == contractId);
        var pdf = BuildPdf(c, includeEvidence: true);
        var path = $"contracts/{c.Id}/contrato-{c.Id}-assinado.pdf";
        await _storage.SaveAsync(path, pdf);
        c.FinalPdfPath = path;
        c.FinalPdfHash = Sha256(pdf);
        c.Status = ContractStatus.Assinado;
        c.SignedAt = DateTime.UtcNow;
        _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, Type = "DOCUMENTO_FINAL_GERADO", Details = "pdfHash=" + c.FinalPdfHash });
        await _db.SaveChangesAsync();
        await _audit.LogAsync("contracts", "completed", "Contract", c.Id, newData: c.FinalPdfHash);
        await _notify.CreateAsync(NotificationType.Success, NotificationSource.Contract, "Contrato assinado", c.Title, $"/Contracts/Details/{c.Id}");

        // Billing and copies are best-effort: the signature is already valid and recorded.
        try { if (c.Kind == KindService) await _billing.GenerateAsync(c.Id); }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _db.ContractEvents.Add(new ContractEvent { ContractId = contractId, Type = "COBRANCA_FALHOU", Details = ex.Message.Truncate(300) });
            await _db.SaveChangesAsync();
        }
        if (_wa.IsConfigured)
            foreach (var p in c.Parties.Where(x => !string.IsNullOrWhiteSpace(x.Phone)))
            {
                try
                {
                    await _wa.SendDocumentAsync(p.Phone!, pdf, $"contrato-{c.Id}-assinado.pdf", $"✅ Contrato \"{c.Title}\" assinado. Segue a cópia com o certificado de assinatura. Código de verificação: {c.VerificationCode}");
                    _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, PartyId = p.Id, Type = "COPIA_ENVIADA_WHATSAPP", Details = p.Name });
                }
                catch (Exception ex) when (ex is IntegrationException or ArgumentException)
                {
                    _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, PartyId = p.Id, Type = "COPIA_NAO_ENVIADA", Details = $"{p.Name}: {ex.Message.Truncate(200)}" });
                }
            }
        await _db.SaveChangesAsync();
    }

    /// <summary>PDF of the current document (preview) or the stored final PDF when signed.</summary>
    public async Task<byte[]> GetPdfAsync(int id)
    {
        var c = await _db.Contracts.Include(x => x.Customer).Include(x => x.Parties).ThenInclude(p => p.Signature).FirstOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
        if (c.FinalPdfPath != null && _storage.Exists(c.FinalPdfPath)) return await _storage.ReadAsync(c.FinalPdfPath);
        return BuildPdf(c, includeEvidence: false);
    }

    public async Task<(bool DocumentOk, bool? PdfOk)> VerifyIntegrityAsync(int id)
    {
        var c = await _db.Contracts.FindAsync(id) ?? throw new KeyNotFoundException();
        var docOk = c.DocumentHash == null || ComputeHash(c) == c.DocumentHash;
        bool? pdfOk = null;
        if (c.FinalPdfPath != null) pdfOk = _storage.Exists(c.FinalPdfPath) && Sha256(await _storage.ReadAsync(c.FinalPdfPath)) == c.FinalPdfHash;
        return (docOk, pdfOk);
    }

    public async Task<Contract?> FindByCodeAsync(string? code)
    {
        var n = NormalizeCode(code);
        if (n.Length == 0) return null;
        var c = await _db.Contracts.Include(x => x.Parties).ThenInclude(p => p.Signature).FirstOrDefaultAsync(x => x.VerificationCode == n && x.Status != ContractStatus.Rascunho);
        if (c != null) _db.ContractEvents.Add(new ContractEvent { ContractId = c.Id, Type = "VERIFICACAO_CONSULTADA" });
        await _db.SaveChangesAsync();
        return c;
    }

    // PDF layout lives in ContractPdf.cs
    private byte[] BuildPdf(Contract c, bool includeEvidence) => ContractPdf.Build(c, new PdfBranding(_settings.Get(SettingKeys.CompanyName) ?? "Samos", _settings.Get(SettingKeys.CompanyDocument), VerifyUrl(c.VerificationCode)), includeEvidence);

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
