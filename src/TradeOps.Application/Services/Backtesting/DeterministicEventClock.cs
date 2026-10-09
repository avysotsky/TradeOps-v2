namespace TradeOps.Application.Services.Backtesting;

public sealed class DeterministicEventClock
{
    private readonly DateTimeOffset[] _timeline;
    private int _index = -1;

    public DeterministicEventClock(
        IEnumerable<DateTimeOffset> timestamps)
    {
        ArgumentNullException.ThrowIfNull(timestamps);

        _timeline =
            timestamps
                .Select(
                    timestamp =>
                    {
                        if (timestamp == default)
                        {
                            throw new ArgumentException(
                                "Clock timestamps must be non-default.",
                                nameof(timestamps));
                        }

                        return timestamp.ToUniversalTime();
                    })
                .Distinct()
                .OrderBy(timestamp => timestamp)
                .ToArray();
    }

    public DateTimeOffset? Current =>
        _index >= 0 && _index < _timeline.Length
            ? _timeline[_index]
            : null;

    public int Count => _timeline.Length;

    public bool MoveNext()
    {
        if (_index + 1 >= _timeline.Length)
        {
            return false;
        }

        _index++;
        return true;
    }
}
