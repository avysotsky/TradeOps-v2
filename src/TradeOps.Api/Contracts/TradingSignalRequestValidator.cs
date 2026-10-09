namespace TradeOps.Api.Contracts;

public static class TradingSignalRequestValidator
{
    private const int MaxSymbolLength = 50;
    private const int MaxSourceLength = 100;
    private const int QuantityAndPriceScale = 12;
    private const int RiskPercentScale = 8;
    private const decimal MaxNumeric28WithScale12Exclusive = 10_000_000_000_000_000m;

    public static Dictionary<string, string[]> Validate(CreateTradingSignalRequest request)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            Add(errors, nameof(request.Symbol), "Symbol is required.");
        }
        else if (request.Symbol.Trim().Length > MaxSymbolLength)
        {
            Add(errors, nameof(request.Symbol), $"Symbol must not exceed {MaxSymbolLength} characters.");
        }

        if (!Enum.IsDefined(typeof(TradeOps.Domain.Enums.OrderSide), request.Side))
        {
            Add(errors, nameof(request.Side), "Side must be a supported OrderSide value.");
        }

        ValidatePositiveNumeric28Scale12(
            errors,
            nameof(request.Quantity),
            request.Quantity,
            required: true);

        if (request.RiskPercent.HasValue)
        {
            if (request.RiskPercent.Value <= 0m || request.RiskPercent.Value > 100m)
            {
                Add(errors, nameof(request.RiskPercent), "RiskPercent must be greater than 0 and at most 100.");
            }
            else if (GetDecimalScale(request.RiskPercent.Value) > RiskPercentScale)
            {
                Add(errors, nameof(request.RiskPercent), $"RiskPercent must have at most {RiskPercentScale} decimal places.");
            }
        }

        if (request.StopLoss.HasValue)
        {
            ValidatePositiveNumeric28Scale12(
                errors,
                nameof(request.StopLoss),
                request.StopLoss.Value,
                required: false);
        }

        if (request.TakeProfit.HasValue)
        {
            ValidatePositiveNumeric28Scale12(
                errors,
                nameof(request.TakeProfit),
                request.TakeProfit.Value,
                required: false);
        }

        if (request.Source is { Length: > MaxSourceLength })
        {
            Add(errors, nameof(request.Source), $"Source must not exceed {MaxSourceLength} characters.");
        }

        if (request.SignalId == Guid.Empty)
        {
            Add(errors, nameof(request.SignalId), "SignalId must not be an empty GUID.");
        }

        return errors.ToDictionary(
            item => item.Key,
            item => item.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static void ValidatePositiveNumeric28Scale12(
        IDictionary<string, List<string>> errors,
        string field,
        decimal value,
        bool required)
    {
        if (value <= 0m)
        {
            Add(
                errors,
                field,
                required
                    ? $"{field} must be greater than zero."
                    : $"{field} must be greater than zero when provided.");
            return;
        }

        if (value >= MaxNumeric28WithScale12Exclusive)
        {
            Add(errors, field, $"{field} is too large to persist safely.");
        }

        if (GetDecimalScale(value) > QuantityAndPriceScale)
        {
            Add(errors, field, $"{field} must have at most {QuantityAndPriceScale} decimal places.");
        }
    }

    private static int GetDecimalScale(decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static void Add(
        IDictionary<string, List<string>> errors,
        string field,
        string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            messages = [];
            errors[field] = messages;
        }

        messages.Add(message);
    }
}
