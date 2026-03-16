namespace Crawler_project.Services;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string html, CancellationToken ct);
}