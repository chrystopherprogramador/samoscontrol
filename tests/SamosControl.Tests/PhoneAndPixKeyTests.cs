using SamosControl.Integrations;

namespace SamosControl.Tests;

public class PhoneAndPixKeyTests
{
    [Theory]
    [InlineData("+351 924 698 607", "351924698607")]
    [InlineData("00351924698607", "351924698607")]
    [InlineData("(65) 99999-9999", "5565999999999")]
    [InlineData("+1 415 555 2671", "14155552671")]
    public void Normalize_KeepsInternationalNumbers(string raw, string expected) => Assert.Equal(expected, PhoneUtil.Normalize(raw));

    [Fact]
    public void IsBrazilian_RejectsForeignNumbers()
    {
        Assert.True(PhoneUtil.IsBrazilian("(65) 99999-9999"));
        Assert.False(PhoneUtil.IsBrazilian("+351 924 698 607"));
    }

    [Theory]
    [InlineData("CPF", "123.456.789-09", "12345678909")]
    [InlineData("CNPJ", "12.345.678/0001-95", "12345678000195")]
    [InlineData("PHONE", "(65) 99999-9999", "+5565999999999")]
    [InlineData("EMAIL", " Fulano@Mail.com ", "fulano@mail.com")]
    [InlineData(null, "123.456.789-09", "12345678909")]
    public void PixKey_IsFormattedForTheBank(string? type, string key, string expected) => Assert.Equal(expected, PixKeyUtil.Normalize(type, key));
}
