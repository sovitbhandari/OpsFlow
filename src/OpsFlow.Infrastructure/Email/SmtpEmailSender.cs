using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using OpsFlow.Application;
using OpsFlow.Infrastructure.Options;

namespace OpsFlow.Infrastructure.Email;

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string toAddress, string subject, string textBody, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = textBody };

        using var client = new SmtpClient();
        var socket = settings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(settings.Host, settings.Port, socket, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        logger.LogInformation("Sent notification email to {RecipientDomain} with subject length {SubjectLength}.", MailboxAddress.Parse(toAddress).Domain, subject.Length);
    }
}
