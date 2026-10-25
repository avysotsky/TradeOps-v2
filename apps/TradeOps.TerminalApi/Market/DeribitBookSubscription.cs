namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Exact Deribit depth-limited WebSocket subscriptions are available for
/// 1/10/20, not 25/50. Preserve the operator's precise 25/50 visible
/// levels through authenticated full-depth RAW instead of silently using
/// the insufficient .20 channel.
/// </summary>
public static class DeribitBookSubscription
{
    public static bool ValidDepth(int depth) => depth is 10 or 25 or 50;

    public static string Channel(string instrument, int depth)
    {
        if (!ValidDepth(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        if (string.IsNullOrWhiteSpace(instrument))
            throw new ArgumentException("Instrument required", nameof(instrument));

        return depth == 10
            ? $"book.{instrument}.none.10.100ms"
            : $"book.{instrument}.raw";
    }

    public static bool IsAggregated(int depth) => depth == 10;

    public static string Mode(int depth) => IsAggregated(depth)
        ? "DEPTH_10_100MS" : "RAW_FULL_BOOK";
}
