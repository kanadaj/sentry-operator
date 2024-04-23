namespace SentryOperator.Entities;

public class MailConfig
{
    public string Host { get; set; } = "smtp";
    public int Port { get; set; } = 25;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTLS { get; set; } = false;
    public bool UseSSL { get; set; } = false;
    public bool EnableReplies = false;
    public string? From { get; set; }
    public string? MailgunApiKey { get; set; }
}