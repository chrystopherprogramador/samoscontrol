using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SamosControl.Domain;

namespace SamosControl.Application;

/// <summary>Comprovante de repasse de comissão, gerado sob demanda a partir da comissão paga (sem armazenar arquivo).</summary>
public static class CommissionReceiptPdf
{
    private const string Brand = "#12305a", Ok = "#1f8a5b", Muted = "#5b6675", Line = "#d5dbe5";

    static CommissionReceiptPdf() { QuestPDF.Settings.License = LicenseType.Community; }

    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "—";
        var k = key.Trim();
        return k.Length <= 6 ? k : k[..3] + new string('•', Math.Min(k.Length - 6, 12)) + k[^3..];
    }

    public static byte[] Build(Commission c, string company, string? companyDocument)
    {
        var paidAt = AppTime.ToLocal(c.PaidAt ?? DateTime.UtcNow);
        var rows = new List<(string, string)>
        {
            ("Beneficiário", c.Seller?.Name ?? "-"),
            ("CPF/CNPJ", c.Seller?.Document ?? "-"),
            ("Chave Pix", MaskKey(c.PixKey ?? c.Seller?.PixKey)),
            ("Cliente de origem", c.Customer?.Name ?? "-"),
            ("Referência", $"{c.Kind} {c.Sequence} • {c.Percent:0.##}% de {ContractService.Money(c.BaseAmount)}"),
            ("Data e hora do pagamento", paidAt.ToString("dd/MM/yyyy HH:mm:ss") + " (Brasília)"),
            ("Identificação da transação (E2E)", string.IsNullOrWhiteSpace(c.PayoutReference) ? "Pagamento manual" : c.PayoutReference!),
            ("Nº do repasse", "#" + c.Id)
        };

        return Document.Create(d => d.Page(p =>
        {
            p.Size(PageSizes.A4); p.Margin(36); p.DefaultTextStyle(t => t.FontSize(10).FontFamily("DejaVu Sans"));
            p.Header().Column(h =>
            {
                h.Item().Text(company).FontSize(14).Bold().FontColor(Brand);
                if (!string.IsNullOrWhiteSpace(companyDocument)) h.Item().Text("CNPJ " + companyDocument).FontSize(8).FontColor(Muted);
                h.Item().PaddingTop(8).LineHorizontal(0.8f).LineColor(Line);
            });
            p.Content().PaddingVertical(16).Column(col =>
            {
                col.Item().Text("COMPROVANTE DE REPASSE DE COMISSÃO").FontSize(13).Bold().FontColor(Brand);
                col.Item().PaddingTop(4).Text("PAGO").FontSize(10).Bold().FontColor(Ok);
                col.Item().PaddingTop(12).Text(ContractService.Money(c.Amount)).FontSize(26).Bold();
                col.Item().PaddingTop(14).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(170); cd.RelativeColumn(); });
                    foreach (var (k, v) in rows)
                    {
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(5).Text(k).FontColor(Muted);
                        t.Cell().BorderBottom(0.5f).BorderColor(Line).PaddingVertical(5).Text(v);
                    }
                });
            });
            p.Footer().AlignCenter().Text("Documento gerado automaticamente pelo Samos Control.").FontSize(8).FontColor(Muted);
        })).GeneratePdf();
    }
}
