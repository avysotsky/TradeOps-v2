namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Deribit grouped book channels support only 1, 10 or 20 levels per side.
/// Zero is an internal sentinel for the full tick-by-tick RAW channel;
/// it is exposed externally as the explicit mode "raw", never as a depth.
/// </summary>
public static class DeribitBookSubscription
{
    public const int RawMode = 0;

    public static bool ValidDepth(int depth) => depth is RawMode or 1 or 10 or 20;

    public static bool TryParseMode(string value, out int depth)
    {
        if (value == "raw")
        {
            depth = RawMode;
            return true;
        }
        if (value is "1" or "10" or "20")
        {
            depth = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        depth = -1;
        return false;
    }

    public static string Channel(string instrument, int depth)
    {
        if (!ValidDepth(depth)) throw new ArgumentOutOfRangeException(nameof(depth));
        if (string.IsNullOrWhiteSpace(instrument))
            throw new ArgumentException("Instrument required", nameof(instrument));
        return depth == RawMode
            ? $"book.{instrument}.raw"
            : $"book.{instrument}.none.{depth}.100ms";
    }

    public static bool IsAggregated(int depth) => depth is 1 or 10 or 20;

    public static string Mode(int depth) => IsAggregated(depth)
        ? $"DEPTH_{depth}_100MS" : "RAW_FULL_BOOK";

    public static string PublicMode(int depth) => depth == RawMode
        ? "raw" : depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
