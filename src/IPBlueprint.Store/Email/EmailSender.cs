using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IPBlueprint.Store.Email;

/// <summary>Bound from the "Smtp" section. If Host is empty, emails are written to the log instead of sent.</summary>
public class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, string? replyTo = null, CancellationToken ct = default);
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, string? replyTo = null, CancellationToken ct = default)
    {
        var o = options.Value;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        if (!string.IsNullOrWhiteSpace(replyTo)) message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(o.UserName))
            await client.AuthenticateAsync(o.UserName, o.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// Used in development, or until SMTP is set up: logs the email instead of sending it. Outside development only the
/// subject is logged, because the body holds personal data and private order links that don't belong in a log file.
/// </summary>
public sealed class LoggingEmailSender(IHostEnvironment environment, ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, string? replyTo = null, CancellationToken ct = default)
    {
        if (environment.IsDevelopment())
            logger.LogInformation("Email (not sent, SMTP not configured) to {To}: {Subject}\n{Body}", to, subject, htmlBody);
        else
            logger.LogWarning("Email not sent because SMTP is not configured: {Subject}", subject);
        return Task.CompletedTask;
    }
}
