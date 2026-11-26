using System.Net.Http.Json;
using Mail = System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure.Notifications;

public record Notification(string Title, string Message, Guid? TenantId = null, Guid? JobId = null, string Severity = "warning");

public interface INotifier
{
    Task NotifyAsync(Notification n, CancellationToken ct = default);
}

/// <summary>Sends alerts to configured webhooks (generic JSON, compatible with Teams/Slack incoming webhooks) and SMTP.</summary>
public sealed class Notifier(IHttpClientFactory http, IOptions<MbrOptions> options, ILogger<Notifier> log) : INotifier
{
    public async Task NotifyAsync(Notification n, CancellationToken ct = default)
    {
        var o = options.Value;
        var link = n.JobId is { } j ? $"{o.PublicBaseUrl.TrimEnd('/')}/jobs/{j}" : o.PublicBaseUrl;
        foreach (var url in o.Notifications.Webhooks)
        {
            try
            {
                using var client = http.CreateClient("mbr-notify");
                // "text" is understood by Slack and Teams workflows; the remaining fields are for generic receivers.
                await client.PostAsJsonAsync(url, new
                {
                    text = $"**{n.Title}**\n{n.Message}\n{link}",
                    title = n.Title, message = n.Message, severity = n.Severity, tenantId = n.TenantId, jobId = n.JobId, link,
                }, ct);
            }
            catch (Exception e) { log.LogWarning(e, "Webhook notification failed"); }
        }

        if (o.Notifications.Smtp is { Host.Length: > 0 } smtp && smtp.To.Count > 0)
        {
            try
            {
                using var client = new Mail.SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseSsl };
                if (smtp.Username != null) client.Credentials = new System.Net.NetworkCredential(smtp.Username, smtp.Password);
                using var msg = new Mail.MailMessage { From = new Mail.MailAddress(smtp.From), Subject = $"[MBR] {n.Title}", Body = $"{n.Message}\n\n{link}" };
                foreach (var to in smtp.To) msg.To.Add(to);
                await client.SendMailAsync(msg, ct);
            }
            catch (Exception e) { log.LogWarning(e, "Email notification failed"); }
        }
    }
}
