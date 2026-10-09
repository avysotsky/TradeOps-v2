namespace TradeOps.Infrastructure.Alerts;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }

    public string? BotToken { get; set; }

    public string? ChatId { get; set; }
}
