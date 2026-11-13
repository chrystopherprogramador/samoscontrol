using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SamosControl.Application.Voice;

/// <summary>Números por extenso em português do Brasil (determinístico, sem LLM).</summary>
public static class PtNumber
{
    private static readonly string[] Units = { "zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove" };
    private static readonly string[] Tens = { "", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa" };
    private static readonly string[] Hundreds = { "", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos" };
    private static readonly string[] Months = { "", "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };

    public const long Max = 999_999_999_999;

    private static string Gender(string unit, bool fem) => !fem ? unit : unit switch { "um" => "uma", "dois" => "duas", _ => unit };

    private static string Below1000(int n, bool fem)
    {
        if (n == 100) return "cem";
        var parts = new List<string>();
        var h = n / 100; var r = n % 100;
        if (h > 0) parts.Add(fem ? Hundreds[h].Replace("entos", "entas") : Hundreds[h]);
        if (r > 0)
        {
            if (r < 20) parts.Add(Gender(Units[r], fem));
            else
            {
                var u = r % 10;
                parts.Add(u == 0 ? Tens[r / 10] : Tens[r / 10] + " e " + Gender(Units[u], fem));
            }
        }
        return string.Join(" e ", parts);
    }

    /// <summary>Cardinal por extenso. <paramref name="feminine"/> concorda "um/dois/centenas" com o substantivo feminino.</summary>
    public static string Cardinal(long n, bool feminine = false)
    {
        if (n == 0) return "zero";
        if (n < 0) return "menos " + Cardinal(-n, feminine);
        var groups = new List<(string Text, int Value)>();
        var bi = (int)(n / 1_000_000_000 % 1000); var mi = (int)(n / 1_000_000 % 1000); var th = (int)(n / 1000 % 1000); var rest = (int)(n % 1000);
        if (bi > 0) groups.Add((bi == 1 ? "um bilhão" : Below1000(bi, false) + " bilhões", bi));
        if (mi > 0) groups.Add((mi == 1 ? "um milhão" : Below1000(mi, false) + " milhões", mi));
        if (th > 0) groups.Add((th == 1 ? "mil" : Below1000(th, feminine) + " mil", th));
        if (rest > 0) groups.Add((Below1000(rest, feminine), rest));
        var sb = new StringBuilder();
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) sb.Append(i == groups.Count - 1 && (groups[i].Value < 100 || groups[i].Value % 100 == 0) ? " e " : " ");
            sb.Append(groups[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>Valor em reais por extenso, ex.: 4785.50 → "quatro mil setecentos e oitenta e cinco reais e cinquenta centavos".</summary>
    public static string Currency(long reais, int cents)
    {
        var r = reais == 0 ? "" : Cardinal(reais) + (reais >= 1_000_000 && reais % 1_000_000 == 0 ? " de " : " ") + (reais == 1 ? "real" : "reais");
        var c = cents == 0 ? "" : Cardinal(cents) + (cents == 1 ? " centavo" : " centavos");
        if (r.Length > 0 && c.Length > 0) return r + " e " + c;
        if (r.Length > 0) return r;
        return c.Length > 0 ? c : "zero reais";
    }

    public static string Month(int m) => Months[m];

    public static string Day(int d) => d == 1 ? "primeiro" : Cardinal(d);

    /// <summary>Horário falado: 9:30 → "nove e trinta"; 14:00 → "quatorze horas"; 0:00/12:00 → meia-noite/meio-dia.</summary>
    public static string Time(int h, int m)
    {
        if (m == 0 && h == 0) return "meia-noite";
        if (m == 0 && h == 12) return "meio-dia";
        if (m == 0) return Cardinal(h, true) + (h == 1 ? " hora" : " horas");
        return Cardinal(h, true) + " e " + Cardinal(m);
    }
}

/// <summary>
/// Prepara o texto para ser FALADO (ElevenLabs/ligação). Transformações determinísticas: não usa LLM e não altera valores,
/// números ou o sentido da resposta; só remove o que não se fala e escreve por extenso o que costuma ser lido errado.
/// O texto original continua sendo o que vai para o histórico e para a mensagem escrita.
/// </summary>
public static class SpeechText
{
    private static readonly Regex CodeBlock = new(@"```[\s\S]*?```", RegexOptions.Compiled);
    private static readonly Regex MdLink = new(@"\[([^\]]+)\]\((?:https?://|www\.)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Guid = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Emoji = new(@"[\u2190-\u21FF\u2300-\u23FF\u2600-\u27BF\u2B00-\u2BFF]|[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\uFE00-\uFE0F\u200D\u20E3]", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^[ \t]{0,3}#{1,6}[ \t]+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Bullet = new(@"^[ \t]*(?:[-*•▪◦]|\d{1,2}[.)])[ \t]+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Quote = new(@"^[ \t]*>+[ \t]?", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TableRule = new(@"^[ \t]*\|?[ \t]*:?-{2,}[-:| \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex InternalId = new(@"#\d+\b", RegexOptions.Compiled);
    private static readonly Regex EmptyParens = new(@"\(\s*\)|\[\s*\]", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"[*_`~]+", RegexOptions.Compiled);

    private static readonly Regex Currency = new(@"R\$\s*(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{1,2})(?!\d)|\.(\d{2})(?![\d.]))?", RegexOptions.Compiled);
    private static readonly Regex IsoDateTime = new(@"(?<![\d-])(\d{4})-(\d{2})-(\d{2})[T ](\d{1,2}):(\d{2})(?::\d{2}(?:\.\d+)?)?Z?(?![\d:])", RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"(?<![\d-])(\d{4})-(\d{2})-(\d{2})(?![\d-])", RegexOptions.Compiled);
    private static readonly Regex BrDate = new(@"(?<![\d/])(\d{1,2})/(\d{1,2})/(\d{4})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex BrDayMonth = new(@"(?<![\d/.,])(\d{1,2})/(\d{1,2})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex Clock = new(@"(?<![\d:])([01]?\d|2[0-3]):([0-5]\d)(?![\d:])", RegexOptions.Compiled);
    private static readonly Regex ClockH = new(@"(?<![\p{L}\d])([01]?\d|2[0-3])\s?h(?:([0-5]\d))?(?![\p{L}\d])", RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"(?<![\p{L}\d])(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d+))?\s?%", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"(?<![\p{L}\p{N}_/\-.,#])(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d+))?(?![\p{L}\p{N}_]|[.,/\-]\d)", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"\(?\d{2}\)?[\s.-]?9?\d{4}[\s.-]\d{4}", RegexOptions.Compiled);
    private static readonly Regex NextWord = new(@"^\s+([\p{L}]+)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.Ordinal)
    {
        ["CPF"] = "C P F", ["CNPJ"] = "C N P J", ["PDV"] = "P D V", ["API"] = "A P I", ["CRM"] = "C R M", ["ERP"] = "E R P", ["SMS"] = "S M S",
        ["PIX"] = "pix", ["NF-e"] = "nota fiscal eletrônica", ["NFe"] = "nota fiscal eletrônica", ["NFC-e"] = "nota fiscal de consumidor", ["NFCe"] = "nota fiscal de consumidor",
        ["SDR"] = "S D R", ["IA"] = "I A", ["VPS"] = "V P S", ["SSL"] = "S S L", ["DNS"] = "D N S", ["TI"] = "T I", ["OK"] = "ok"
    };
    private static readonly Regex AcronymRx = new(@"(?<![\p{L}\p{N}])(" + string.Join("|", Acronyms.Keys.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])", RegexOptions.Compiled);

    private static readonly HashSet<string> MasculineA = new(StringComparer.OrdinalIgnoreCase) { "dia", "dias", "mapa", "mapas", "problema", "problemas", "sistema", "sistemas", "programa", "programas", "tema", "temas", "plano", "planos" };
    private static readonly HashSet<string> FeminineOther = new(StringComparer.OrdinalIgnoreCase) { "vez", "vezes", "tarde", "tardes", "noite", "noites", "mulher", "mulheres", "unidade", "unidades", "cidade", "cidades", "parte", "partes" };

    /// <summary>Reconhecimento do código de confirmação do agente ("CONFIRMAR 1A2B3C"): quem precisa LER o código não pode receber só áudio.</summary>
    public static readonly Regex ConfirmCode = new(@"\bCONFIRMAR\s+[0-9A-F]{6}\b", RegexOptions.Compiled);

    public static string Normalize(string? text)
    {
        var t = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Trim().Length == 0) return "";
        t = CodeBlock.Replace(t, " ");
        t = MdLink.Replace(t, "$1");
        t = Url.Replace(t, "");
        t = Guid.Replace(t, "");
        t = Emoji.Replace(t, "");
        t = Heading.Replace(t, "");
        t = Quote.Replace(t, "");
        t = TableRule.Replace(t, "");
        t = Bullet.Replace(t, "");
        t = InternalId.Replace(t, "");
        t = t.Replace('|', ',');
        t = Emphasis.Replace(t, "");
        t = t.Replace(" — ", ", ").Replace(" – ", ", ").Replace("—", ", ").Replace("&", " e ").Replace("=>", " ").Replace("->", " ");
        t = EmptyParens.Replace(t, "");

        t = SpeakValues(t);
        t = AcronymRx.Replace(t, m => Acronyms[m.Value]);
        t = t.Replace("R$", "reais");

        // uma linha vira uma frase (listas viram falas contínuas); ninguém "lê" quebra de linha
        var lines = t.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim(' ', ',', ';', '-')).Where(l => l.Length > 0).Select(EndSentence);
        t = string.Join(" ", lines);
        t = Regex.Replace(t, @"\s+([,.;:!?])", "$1");
        t = Regex.Replace(t, @"([,;:])\s*(?=[.!?])", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return DropRepeats(t);
    }

    private static string EndSentence(string line)
    {
        var last = line[^1];
        return last is '.' or '!' or '?' or '…' ? line : (last is ':' or ',' or ';' ? line[..^1] : line) + ".";
    }

    /// <summary>Remove frase idêntica repetida em sequência (artefato de streaming/concatenação), preservando a ordem.</summary>
    private static string DropRepeats(string t)
    {
        var parts = Regex.Split(t, @"(?<=[.!?])\s+");
        var res = new List<string>();
        foreach (var p in parts)
        {
            if (res.Count > 0 && string.Equals(res[^1], p, StringComparison.OrdinalIgnoreCase) && p.Length > 3) continue;
            res.Add(p);
        }
        return string.Join(" ", res);
    }

    private static string SpeakValues(string t)
    {
        t = Currency.Replace(t, m =>
        {
            var reais = ParseInt(m.Groups[1].Value);
            if (reais is null or > PtNumber.Max) return m.Value;
            var cents = m.Groups[2].Success ? ParseCents(m.Groups[2].Value) : m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            return PtNumber.Currency(reais.Value, cents);
        });
        t = IsoDateTime.Replace(t, m => Safe(m, () =>
        {
            var d = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            var h = int.Parse(m.Groups[4].Value); var mi = int.Parse(m.Groups[5].Value);
            if (h > 23) return m.Value;
            return DateWords(d.Day, d.Month, d.Year) + " " + AtTime(h, mi);
        }));
        t = IsoDate.Replace(t, m => Safe(m, () => { var d = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); return DateWords(d.Day, d.Month, d.Year); }));
        t = BrDate.Replace(t, m => Safe(m, () => { var d = new DateTime(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value)); return DateWords(d.Day, d.Month, d.Year); }));
        t = BrDayMonth.Replace(t, m =>
        {
            int d = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value);
            return d is >= 1 and <= 31 && mo is >= 1 and <= 12 ? DateWords(d, mo, null) : m.Value;
        });
        t = Clock.Replace(t, m => PtNumber.Time(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        t = ClockH.Replace(t, m => PtNumber.Time(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0));
        t = Percent.Replace(t, m => NumberWords(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, false) is { } w ? w + " por cento" : m.Value);

        // telefones/CPF/CNPJ permanecem como estão (a ElevenLabs lê dígitos); o resto vira palavra
        var protectedSpans = Phone.Matches(t).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var src = t;
        t = Number.Replace(src, m =>
        {
            if (protectedSpans.Any(p => m.Index >= p.Index && m.Index < p.End)) return m.Value;
            var fem = IsFeminine(src, m.Index + m.Length);
            return NumberWords(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, fem) ?? m.Value;
        });
        return t;
    }

    private static string Safe(Match m, Func<string> f)
    {
        try { return f(); } catch (ArgumentOutOfRangeException) { return m.Value; } catch (FormatException) { return m.Value; }
    }

    private static string DateWords(int day, int month, int? year) =>
        $"{PtNumber.Day(day)} de {PtNumber.Month(month)}" + (year.HasValue ? " de " + PtNumber.Cardinal(year.Value < 100 ? 2000 + year.Value : year.Value) : "");

    private static string AtTime(int h, int m)
    {
        var words = PtNumber.Time(h, m);
        return h switch { 0 when m == 0 => "à meia-noite", 12 when m == 0 => "ao meio-dia", 1 => "à " + words, _ => "às " + words };
    }

    private static long? ParseInt(string s)
    {
        var digits = s.Replace(".", "");
        return digits.Length <= 12 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int ParseCents(string s) => s.Length == 1 ? int.Parse(s) * 10 : int.Parse(s);

    /// <summary>Parte inteira (com ou sem separador de milhar) e decimais → palavras. null = deixar como está (ex.: número longo demais, provável identificador).</summary>
    private static string? NumberWords(string intPart, string? dec, bool feminine)
    {
        var hasSeparators = intPart.Contains('.');
        if (!hasSeparators && intPart.Length > 7) return null;
        var v = ParseInt(intPart);
        if (v is null) return null;
        var words = PtNumber.Cardinal(v.Value, feminine && dec == null);
        if (dec == null) return words;
        string d;
        if (dec.Length <= 2 && dec[0] != '0') d = PtNumber.Cardinal(int.Parse(dec));
        else d = string.Join(" ", dec.Select(c => PtNumber.Cardinal(c - '0')));
        return words + " vírgula " + d;
    }

    private static bool IsFeminine(string text, int after)
    {
        var m = NextWord.Match(text[after..]);
        if (!m.Success) return false;
        var w = m.Groups[1].Value;
        if (MasculineA.Contains(w)) return false;
        return FeminineOther.Contains(w) || w.EndsWith("as", StringComparison.OrdinalIgnoreCase) || w.EndsWith("a", StringComparison.OrdinalIgnoreCase);
    }
}
