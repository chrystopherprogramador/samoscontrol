using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace SamosControl.Integrations;

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}

/// <summary>SMTP via MailKit: porta 465 usa SSL direto; as demais usam STARTTLS quando o servidor oferece.</summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly ISettingsReader _s;
    public SmtpEmailSender(ISettingsReader s) => _s = s;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_s.Get(SettingKeys.SmtpHost)) && !string.IsNullOrWhiteSpace(FromAddress);

    private string? FromAddress => _s.Get(SettingKeys.SmtpFrom) ?? _s.Get(SettingKeys.SmtpUser);

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (!IsConfigured) throw new NotConfiguredException("E-mail (SMTP)");
        var host = _s.Get(SettingKeys.SmtpHost)!.Trim();
        var port = int.TryParse(_s.Get(SettingKeys.SmtpPort), out var p) && p > 0 ? p : 587;
        var user = _s.Get(SettingKeys.SmtpUser);
        var pwd = _s.Get(SettingKeys.SmtpPassword);

        var msg = new MimeMessage();
        try
        {
            msg.From.Add(MailboxAddress.Parse(FromAddress!));
            msg.To.Add(MailboxAddress.Parse(to));
        }
        catch (ParseException ex) { throw new IntegrationException("EndereÃ§o de e-mail invÃ¡lido: " + ex.Message, ex); }
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient { Timeout = 20000 };
        try
        {
            await client.ConnectAsync(host, port, port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!string.IsNullOrWhiteSpace(user)) await client.AuthenticateAsync(user, pwd ?? "", ct);
            await client.SendAsync(new MailKit.SendRequest(msg), ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is MailKit.ProtocolException or MailKit.ServiceNotConnectedException or MailKit.ServiceNotAuthenticatedException
            or AuthenticationException or System.Net.Sockets.SocketException or IOException or TimeoutException or MailKit.CommandException)
        {
            throw new IntegrationException("Falha no envio de e-mail: " + ex.Message, ex);
        }
    }
}
