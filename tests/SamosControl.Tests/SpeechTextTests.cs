using SamosControl.Application.Voice;
using Xunit;

namespace SamosControl.Tests;

public class SpeechTextTests
{
    [Fact]
    public void Money_IsSpokenExactly() =>
        Assert.Equal("Você tem doze cobranças pendentes, somando quatro mil setecentos e oitenta e cinco reais e cinquenta centavos.",
            SpeechText.Normalize("Você tem 12 cobranças pendentes, somando R$ 4.785,50."));

    [Theory]
    [InlineData("R$ 1.000.000,00", "um milhão de reais.")]
    [InlineData("R$ 1,00", "um real.")]
    [InlineData("R$ 0,50", "cinquenta centavos.")]
    [InlineData("R$ 200", "duzentos reais.")]
    public void Money_Variants(string input, string expected) => Assert.Equal(expected, SpeechText.Normalize(input));

    [Fact]
    public void Dates_AndTimes()
    {
        Assert.Equal("Vence em primeiro de março de dois mil e vinte e seis.", SpeechText.Normalize("Vence em 01/03/2026"));
        Assert.Equal("Reunião às quatorze e trinta.", SpeechText.Normalize("Reunião às 14:30"));
        Assert.Equal("Reunião ao meio-dia.", SpeechText.Normalize("Reunião ao 12:00").Replace("ao meio-dia", "ao meio-dia"));
    }

    [Fact]
    public void Percent_AndDecimal()
    {
        Assert.Equal("Margem de dez por cento.", SpeechText.Normalize("Margem de 10%"));
        Assert.Equal("Taxa de dois vírgula cinco por cento.", SpeechText.Normalize("Taxa de 2,5%"));
    }

    [Fact]
    public void RemovesMarkdown_Urls_Ids_AndCodeBlocks()
    {
        var r = SpeechText.Normalize("## Título\n- **Item** #123 veja https://x.com/a\n```json\n{\"a\":1}\n```");
        Assert.DoesNotContain("#", r); Assert.DoesNotContain("http", r); Assert.DoesNotContain("{", r); Assert.DoesNotContain("*", r);
        Assert.Contains("Item", r);
    }

    [Fact]
    public void KeepsPhoneNumbersUntouched() => Assert.Contains("11 98765-4321", SpeechText.Normalize("Ligue para 11 98765-4321"));

    [Fact]
    public void DropsConsecutiveDuplicateSentences() =>
        Assert.Equal("Tudo certo.", SpeechText.Normalize("Tudo certo.\nTudo certo."));

    [Fact]
    public void ConfirmCode_IsDetected()
    {
        Assert.Matches(SpeechText.ConfirmCode, "Responda CONFIRMAR 1A2B3C para executar");
        Assert.DoesNotMatch(SpeechText.ConfirmCode, "confirmar depois");
    }

    [Fact]
    public void Empty_ReturnsEmpty() => Assert.Equal("", SpeechText.Normalize("  \n "));
}
