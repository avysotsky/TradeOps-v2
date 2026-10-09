namespace TradeOps.Domain.Entities;

public sealed class Fill
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public required string ExchangeFillId { get; set; }

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal? Fee { get; set; }

    public string? FeeCurrency { get; set; }

    public DateTimeOffset FilledAt { get; set; }
}
