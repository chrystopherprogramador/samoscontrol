using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SamosControl.Domain;

namespace SamosControl.Application;

public record PdfBranding(string Company, string? CompanyDocument, string VerifyUrl);

/// <summary>Contract PDF: cover/clauses, "Assinaturas das partes" seals and (when signed) "Comprovantes de assinatura".</summary>
public static class ContractPdf
{
    private const string Brand = "#12305a", Accent = "#c8962e", Ok = "#1f8a5b", Muted = "#5b6675", Soft = "#f3f5f9", Line = "#d5dbe5";

    public static string StatusLabel(Contract c) => c.Status switch
    {
        ContractStatus.Assinado => "ASSINADO",
        ContractStatus.AguardandoAssinatura => "AGUARDANDO ASSINATURA",
        ContractStatus.Cancelado => "CANCELADO",
        _ => "RASCUNHO"
    };

    private static string StatusColor(Contract c) => c.Status switch { ContractStatus.Assinado => Ok, ContractStatus.Cancelado => "#b3261e", ContractStatus.AguardandoAssinatura => Accent, _ => Muted };

    private static string Stamp(DateTime utc) => AppTime.ToLocal(utc).ToString("dd/MM/yyyy, HH:mm:ss") + " • Brasília (UTC−3)";

    private static string RoleLabel(string role) => role.ToUpperInvariant();

    private static string AuthLabel(ContractSignature s) =>
        s.VerificationMethod == "WHATSAPP_OTP" ? "Código único via WhatsApp + aceite expresso" : "Link pessoal + aceite expresso";

    private static void Heading(IContainer c, string text) =>
        c.PaddingTop(10).Column(col =>
        {
            col.Item().Text(text.ToUpperInvariant()).FontSize(9).Bold().FontColor(Brand).LetterSpacing(0.12f);
            col.Item().PaddingTop(3).LineHorizontal(0.8f).LineColor(Line);
        });

    private static IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor(Line).PaddingVertical(4).PaddingHorizontal(6);
    private static IContainer HeadCell(IContainer c) => c.Background(Brand).PaddingVertical(4).PaddingHorizontal(6);

    private static void Logo(IContainer c, float size) =>
        c.Width(size).Height(size).Background(Brand).AlignCenter().AlignMiddle().Text("S").FontColor(Colors.White).Bold().FontSize(size * 0.6f);

    public static byte[] Build(Contract c, PdfBranding b, bool includeEvidence)
    {
        var partnership = c.Kind == ContractService.KindPartnership;
        var net = BillingPlan.NetImplementation(c);
        var schedule = partnership ? new List<PlanItem>() : BillingPlan.Build(c, c.MonthlyMonths.HasValue ? DateTime.MaxValue.Date : c.MonthlyStart?.Date.AddMonths(11));
        var summary = new List<(string, string)>();
        if (partnership)
        {
            var p = c.Parties.FirstOrDefault(x => !ContractService.IsCompanyParty(x));
            summary.Add(("Parceiro comercial", p?.Name ?? "-"));
            summary.Add(("CPF/CNPJ", p?.Document ?? "-"));
            summary.Add(("Comissão sobre implantação", $"{c.ImplementationCommissionPercent:0.##}% do valor efetivamente recebido"));
            summary.Add(("Comissão sobre mensalidade", $"{c.MonthlyCommissionPercent:0.##}% de cada mensalidade efetivamente recebida"));
        }
        else
        {
            summary.Add(("Contratante", c.Customer?.Name ?? c.Parties.FirstOrDefault()?.Name ?? "-"));
            summary.Add(("CPF/CNPJ", c.Customer?.Document ?? "-"));
            summary.Add(("Serviço / sistema", c.ServiceName ?? "-"));
            if (c.ImplementationValue > 0)
            {
                summary.Add(("Implantação", ContractService.Money(c.ImplementationValue) + (c.DiscountValue > 0 ? $"  (desconto de {ContractService.Money(c.DiscountValue)}; líquido {ContractService.Money(net)})" : "")));
                summary.Add(("Parcelamento", c.ImplementationInstallments > 1 ? $"{c.ImplementationInstallments}x" : "À vista (1 parcela)"));
            }
            if (c.MonthlyValue > 0)
            {
                summary.Add(("Mensalidade", ContractService.Money(c.MonthlyValue)));
                summary.Add(("Prazo da mensalidade", c.MonthlyMonths.HasValue ? $"{c.MonthlyMonths} meses" : "Indeterminado"));
            }
            if (c.ImplementationValue <= 0 && c.MonthlyValue <= 0) summary.Add(("Valor", ContractService.Money(c.Value)));
            summary.Add(("Forma de pagamento", c.BillingType == BillingType.Pix ? "PIX" : "Boleto bancário"));
        }
        var parties = c.Parties.OrderBy(x => x.Id).ToList();
        var signedCount = parties.Count(p => p.Signature != null);
        var docHash = c.DocumentHash ?? "(rascunho — o hash é gerado ao enviar para assinatura)";

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(30); page.MarginBottom(34); page.MarginHorizontal(44);
                page.DefaultTextStyle(t => t.FontSize(10).LineHeight(1.4f).FontColor("#1c2430"));
                page.Background().AlignCenter().AlignMiddle().Text("S").FontSize(420).Bold().FontColor("#f4f6fa");

                page.Header().Column(h =>
                {
                    h.Item().Row(r => { r.RelativeItem().Height(4).Background(Brand); r.ConstantItem(70).Height(4).Background(Accent); });
                    h.Item().PaddingTop(10).Row(r =>
                    {
                        r.ConstantItem(34).Element(e => Logo(e, 34));
                        r.RelativeItem().PaddingLeft(8).Column(l =>
                        {
                            l.Item().Text(b.Company).FontSize(13).Bold().FontColor(Brand);
                            if (!string.IsNullOrWhiteSpace(b.CompanyDocument)) l.Item().Text("CNPJ " + b.CompanyDocument).FontSize(8).FontColor(Muted);
                        });
                        r.ConstantItem(150).AlignRight().Column(l =>
                        {
                            l.Item().AlignRight().Text("DOCUMENTO ELETRÔNICO").FontSize(7.5f).Bold().FontColor(Muted).LetterSpacing(0.1f);
                            l.Item().AlignRight().Text($"REF. {c.Id:D5}").FontSize(9).Bold().FontColor(Brand);
                        });
                    });
                    h.Item().PaddingTop(8).LineHorizontal(0.5f).LineColor(Line);
                });

                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(5);
                    col.Item().Text(partnership ? "PARCERIA COMERCIAL" : "FORMALIZAÇÃO DE SERVIÇOS").FontSize(8.5f).Bold().FontColor(Accent).LetterSpacing(0.2f);
                    col.Item().Text(c.Title).FontSize(21).Bold().FontColor(Brand).LineHeight(1.15f);
                    col.Item().PaddingBottom(4).Row(r =>
                    {
                        r.RelativeItem().AlignMiddle().Text($"Emitido em {Stamp(c.SentAt ?? c.CreatedAt)}").FontSize(8.5f).FontColor(Muted);
                        r.ConstantItem(150).AlignRight().Element(e => e.Background(StatusColor(c)).PaddingVertical(3).PaddingHorizontal(9)
                            .Text(StatusLabel(c)).FontSize(7.5f).Bold().FontColor(Colors.White).LetterSpacing(0.08f));
                    });

                    col.Item().Element(e => Heading(e, "Resumo da contratação"));
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(150); cd.RelativeColumn(); });
                        foreach (var (k, v) in summary)
                        {
                            t.Cell().Element(Cell).Text(k).Bold().FontColor(Muted);
                            t.Cell().Element(Cell).Text(v);
                        }
                    });
                    if (schedule.Count > 0)
                    {
                        col.Item().Element(e => Heading(e, "Cronograma de pagamento"));
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.ConstantColumn(60); cd.ConstantColumn(80); cd.ConstantColumn(85); });
                            t.Header(h =>
                            {
                                foreach (var s in new[] { "Descrição", "Parcela", "Vencimento", "Valor" }) h.Cell().Element(HeadCell).Text(s).FontColor(Colors.White).Bold().FontSize(9);
                            });
                            foreach (var it in schedule)
                            {
                                t.Cell().Element(Cell).Text(it.Kind == BillingPlan.Implementation ? "Implantação" : "Mensalidade");
                                t.Cell().Element(Cell).Text(it.Kind == BillingPlan.Implementation ? $"{it.Sequence}/{c.ImplementationInstallments}" : it.Sequence.ToString());
                                t.Cell().Element(Cell).Text(it.Due.ToString("dd/MM/yyyy"));
                                t.Cell().Element(Cell).AlignRight().Text(ContractService.Money(it.Amount));
                            }
                        });
                        if (c.MonthlyValue > 0 && !c.MonthlyMonths.HasValue)
                            col.Item().Text("As mensalidades seguintes vencem no mesmo dia de cada mês, enquanto vigorar o contrato.").FontSize(8.5f).Italic().FontColor(Muted);
                    }

                    col.Item().Element(e => Heading(e, "Cláusulas e condições"));
                    foreach (var line in c.RenderedText.Replace("\r", "").Split('\n'))
                    {
                        if (line.StartsWith("# ")) col.Item().PaddingTop(8).Text(line[2..].ToUpperInvariant()).Bold().FontSize(10).FontColor(Brand).LetterSpacing(0.06f);
                        else if (string.IsNullOrWhiteSpace(line)) col.Item().Height(3);
                        else col.Item().Text(line).Justify();
                    }

                    // ---------- Assinaturas das partes
                    col.Item().PageBreak();
                    col.Item().Text("FORMALIZAÇÃO ELETRÔNICA").FontSize(8.5f).Bold().FontColor(Accent).LetterSpacing(0.2f);
                    col.Item().Text("Assinaturas das partes").FontSize(19).Bold().FontColor(Brand);
                    col.Item().PaddingBottom(6).Text("Os selos abaixo refletem os registros de aceite deste documento.").FontSize(9).FontColor(Muted);
                    foreach (var rowParties in parties.Chunk(2))
                    {
                        col.Item().PaddingBottom(8).Row(r =>
                        {
                            r.Spacing(10);
                            foreach (var p in rowParties) r.RelativeItem().Element(e => Seal(e, p, c));
                            if (rowParties.Length == 1) r.RelativeItem();
                        });
                    }
                    col.Item().PaddingTop(4).Element(e => e.Background(Soft).Border(0.6f).BorderColor(Line).Padding(10).Column(i =>
                    {
                        i.Spacing(3);
                        i.Item().Text("IDENTIFICAÇÃO DE INTEGRIDADE").FontSize(8).Bold().FontColor(Brand).LetterSpacing(0.12f);
                        i.Item().Text("SHA-256 do conteúdo original (texto + condições comerciais)").FontSize(8).FontColor(Muted);
                        i.Item().Text(docHash).FontSize(8.5f).FontFamily(Fonts.Courier).Bold();
                        i.Item().Text($"Verificação pública: {b.VerifyUrl}   •   Código {c.VerificationCode}").FontSize(8).FontColor(Muted);
                        i.Item().PaddingTop(3).Text($"Assinatura eletrônica registrada no sistema {b.Company}. Os selos são representações visuais dos registros de aceite e não constituem certificação gov.br ou ICP-Brasil. " +
                            "Qualquer alteração no documento invalida o hash acima.").FontSize(7.5f).Italic().FontColor(Muted);
                    }));
                    col.Item().PaddingTop(8).Element(e => e.Column(i =>
                    {
                        i.Item().Text("ACEITE DA ASSINATURA ELETRÔNICA").FontSize(8).Bold().FontColor(Brand).LetterSpacing(0.12f);
                        i.Item().PaddingTop(2).Text(ContractService.TermsText).FontSize(8).Italic().FontColor(Muted).Justify();
                    }));

                    // ---------- Comprovantes
                    if (includeEvidence)
                    {
                        col.Item().PageBreak();
                        col.Item().Text("TRILHA DE AUDITORIA").FontSize(8.5f).Bold().FontColor(Accent).LetterSpacing(0.2f);
                        col.Item().Text("Comprovantes de assinatura").FontSize(19).Bold().FontColor(Brand);
                        var n = 0;
                        foreach (var p in parties.Where(x => x.Signature != null))
                        {
                            var s = p.Signature!; n++;
                            col.Item().PaddingTop(8).ShowEntire().Element(e => e.Border(0.6f).BorderColor(Line).Padding(10).Column(b2 =>
                            {
                                b2.Spacing(2);
                                b2.Item().Row(r =>
                                {
                                    r.ConstantItem(26).Text($"{n:D2}").FontSize(13).Bold().FontColor(Accent);
                                    r.RelativeItem().Text($"{s.Name}  ·  {RoleLabel(p.Role)}").FontSize(11).Bold().FontColor(Brand);
                                });
                                void Row(string k, string? v) => b2.Item().Row(r => { r.ConstantItem(120).Text(k).FontSize(8.5f).FontColor(Muted); r.RelativeItem().Text(v ?? "-").FontSize(8.5f); });
                                Row("Parte", $"{s.Name} ({RoleLabel(p.Role)})");
                                Row("Data e hora", Stamp(s.SignedAt) + $"  /  {s.SignedAt:yyyy-MM-ddTHH:mm:ss}Z");
                                Row("Autenticação", s.VerificationMethod == "WHATSAPP_OTP" ? $"Código único (OTP) enviado ao WhatsApp {s.VerifiedPhone}, confirmado às {AppTime.ToLocal(s.VerifiedAt ?? s.SignedAt):HH:mm:ss}" : AuthLabel(s));
                                Row("CPF/CNPJ", s.Document); Row("E-mail", s.Email); Row("Telefone", s.Phone);
                                Row("Endereço IP", s.Ip); Row("Navegador informado", s.UserAgent);
                                Row("Assinatura (SHA-256)", s.SignatureHash);
                                Row("Identificador do registro", $"{c.Id:D5}-{p.Id}-{s.Id}");
                                var role = ContractService.IsCompanyParty(p) ? $"representante com poderes para vincular {b.Company}" : "parte identificada neste documento";
                                b2.Item().PaddingTop(4).Text($"Declaro ser {s.Name}, {role}, e que li e concordo com o conteúdo integral do documento, reconhecendo a validade desta assinatura eletrônica (MP 2.200-2/2001, art. 10, §2º, e Lei 14.063/2020).")
                                    .FontSize(8).Italic().FontColor(Muted).Justify();
                            }));
                        }
                        col.Item().PaddingTop(10).Text("Trilha de eventos").Bold().FontSize(9).FontColor(Brand);
                        foreach (var e in c.Events.OrderBy(x => x.At).Where(x => x.Type is not ("VERIFICACAO_CONSULTADA" or "DOCUMENTO_FINAL_GERADO" or "COBRANCAS_GERADAS" or "COBRANCA_FALHOU")))
                            col.Item().Text($"{AppTime.ToLocal(e.At):dd/MM/yyyy HH:mm:ss}   {e.Type}   {e.Ip}").FontSize(7.5f).FontColor(Muted);
                    }
                });

                page.Footer().Column(f =>
                {
                    f.Item().LineHorizontal(0.5f).LineColor(Line);
                    f.Item().PaddingTop(3).Row(r =>
                    {
                        r.RelativeItem().Text($"{b.Company} • Documento eletrônico • Verificação {c.VerificationCode} • {(signedCount == parties.Count && parties.Count > 0 ? "assinado por todas as partes" : $"{signedCount}/{parties.Count} assinaturas")}")
                            .FontSize(7).FontColor(Muted);
                        r.ConstantItem(70).AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(s => s.FontSize(7).FontColor(Muted));
                            t.Span("Página "); t.CurrentPageNumber(); t.Span(" / "); t.TotalPages();
                        });
                    });
                });
            });
        }).GeneratePdf();
    }

    private static void Seal(IContainer e, ContractParty p, Contract c)
    {
        var s = p.Signature;
        if (s != null)
        {
            e.Background(Soft).BorderLeft(3).BorderColor(Ok).Border(0.5f).BorderColor(Line).Padding(10).Column(k =>
            {
                k.Spacing(2);
                k.Item().Row(r =>
                {
                    r.RelativeItem().Text(RoleLabel(p.Role)).FontSize(7.5f).Bold().FontColor(Muted).LetterSpacing(0.1f);
                    r.AutoItem().Text("ASSINADO ELETRONICAMENTE").FontSize(7).Bold().FontColor(Ok).LetterSpacing(0.05f);
                });
                k.Item().PaddingTop(2).Text(s.Name).FontSize(13).Bold().FontColor(Brand);
                k.Item().Text(Stamp(s.SignedAt)).FontSize(8).FontColor(Muted);
                k.Item().PaddingTop(3).Text(AuthLabel(s)).FontSize(8);
                k.Item().Text($"Vinculado ao SHA-256 do documento {c.DocumentHash?[..Math.Min(16, c.DocumentHash.Length)]}…").FontSize(7.5f).FontFamily(Fonts.Courier).FontColor(Muted);
            });
        }
        else
        {
            e.Border(0.8f).BorderColor("#9aa4b2").Padding(10).Column(k =>
            {
                k.Spacing(2);
                k.Item().Row(r =>
                {
                    r.RelativeItem().Text(RoleLabel(p.Role)).FontSize(7.5f).Bold().FontColor(Muted).LetterSpacing(0.1f);
                    r.AutoItem().Text("ASSINATURA PENDENTE").FontSize(7).Bold().FontColor(Accent).LetterSpacing(0.05f);
                });
                k.Item().PaddingTop(2).Text(p.Name).FontSize(13).Bold().FontColor("#8a94a3");
                k.Item().Text($"AGUARDANDO ACEITE DO {RoleLabel(p.Role)}").FontSize(8).Bold().FontColor(Muted);
                k.Item().PaddingTop(3).Text("O selo de assinatura será emitido somente após a confirmação do signatário.").FontSize(7.5f).Italic().FontColor(Muted);
            });
        }
    }
}
