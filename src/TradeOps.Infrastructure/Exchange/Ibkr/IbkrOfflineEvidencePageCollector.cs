using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// A synthetic, normalized page of offline account order evidence.
/// Cursor fields are fixture-local opaque tokens; they are NOT IBKR API cursors.
/// No network, broker session or mutation is involved.
/// </summary>
public sealed record IbkrOfflineEvidencePage(
    string AccountKey,
    string CollectionKey,
    long ObservationSequence,
    int PageIndex,
    string? RequestCursor,
    string? NextCursor,
    bool IsLastPage,
    IReadOnlyList<IbkrOfflineBrokerOrderEvidence> Orders);

/// <summary>
/// Fail-closed assembly of paginated synthetic evidence. A complete account
/// snapshot can be emitted only after an uninterrupted terminal cursor chain.
/// This is not a trusted broker-source verifier: every input is caller-supplied
/// and must be authenticated/qualified separately before production use.
/// </summary>
public sealed class IbkrOfflineEvidencePageCollector
{
    private const int MaxPages = 32;
    private const int MaxOrders = 256;
    private const int MaxExecutions = 4096;
    private const int MaxKeyLength = 128;
    private const decimal MaxMagnitudeExclusive = 10_000_000_000_000_000m;

    private readonly string _accountKey;
    private readonly string _collectionKey;
    private readonly long _sequence;
    private readonly List<string> _pageDigests = [];
    private readonly List<IbkrOfflineBrokerOrderEvidence> _orders = [];
    private readonly HashSet<string> _coids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _brokerOrderIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _issuedCursors = new(StringComparer.Ordinal);
    private string? _nextCursor;
    private int _executionCount;

    public IbkrOfflineEvidencePageCollector(
        string accountKey, string collectionKey, long observationSequence)
    {
        CheckKey(accountKey);
        CheckKey(collectionKey);
        if (observationSequence <= 0)
        {
            throw Invalid();
        }

        _accountKey = accountKey;
        _collectionKey = collectionKey;
        _sequence = observationSequence;
    }

    public bool IsComplete { get; private set; }

    public int AcceptedPageCount => _pageDigests.Count;

    public void Append(IbkrOfflineEvidencePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.PageIndex < 0 || page.Orders is null
            || page.Orders.Count > MaxOrders
            || !string.Equals(page.AccountKey, _accountKey, StringComparison.Ordinal)
            || !string.Equals(page.CollectionKey, _collectionKey, StringComparison.Ordinal)
            || page.ObservationSequence != _sequence)
        {
            throw Invalid();
        }

        // Reject obviously oversized synthetic fixtures before serialization.
        // Replays are fingerprinted by value, not IReadOnlyList reference identity.
        if (page.Orders.Any(order => order is null
            || order.Executions is null
            || order.Executions.Count > MaxExecutions))
        {
            throw Invalid();
        }

        // Fingerprints include the nested normalized execution evidence.
        // Conflicting retries never mutate state.
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page))));

        if (page.PageIndex < _pageDigests.Count)
        {
            if (!string.Equals(
                    _pageDigests[page.PageIndex], digest, StringComparison.Ordinal))
            {
                throw Invalid();
            }

            return; // A byte-equivalent normalized page is idempotent.
        }

        if (IsComplete || page.PageIndex != _pageDigests.Count
            || page.PageIndex >= MaxPages
            || _orders.Count + page.Orders.Count > MaxOrders
            || (page.PageIndex == MaxPages - 1 && !page.IsLastPage)
            || (page.IsLastPage && page.NextCursor is not null)
            || (!page.IsLastPage && !SafeCursor(page.NextCursor))
            || !string.Equals(page.RequestCursor, _nextCursor, StringComparison.Ordinal)
            || (page.NextCursor is not null && _issuedCursors.Contains(page.NextCursor)))
        {
            throw Invalid();
        }

        if (page.PageIndex == 0 && page.RequestCursor is not null)
        {
            throw Invalid();
        }

        if (page.PageIndex > 0 && !SafeCursor(page.RequestCursor))
        {
            throw Invalid();
        }

        // Stage all validation and defensive copies BEFORE any state change.
        // A single invalid row cannot partially consume this cursor/page.
        var freshCoids = new HashSet<string>(StringComparer.Ordinal);
        var freshBrokerIds = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<IbkrOfflineBrokerOrderEvidence>(page.Orders.Count);
        var newExecutions = 0;

        foreach (var order in page.Orders)
        {
            if (order is null)
            {
                throw Invalid();
            }

            CheckKey(order.AccountKey);
            CheckKey(order.ClientOrderId);
            CheckKey(order.BrokerOrderId);
            CheckKey(order.Symbol);

            if (!string.Equals(order.AccountKey, _accountKey, StringComparison.Ordinal)
                || _coids.Contains(order.ClientOrderId)
                || !freshCoids.Add(order.ClientOrderId)
                || _brokerOrderIds.Contains(order.BrokerOrderId)
                || !freshBrokerIds.Add(order.BrokerOrderId)
                || !Enum.IsDefined(order.Side)
                || !Enum.IsDefined(order.Status)
                || !ValidAmount(order.RequestedQuantity, allowZero: false)
                || !ValidAmount(order.CumulativeFilled, allowZero: true)
                || order.CumulativeFilled > order.RequestedQuantity
                || order.Executions is null)
            {
                throw Invalid();
            }

            if (newExecutions + order.Executions.Count > MaxExecutions
                || _executionCount + newExecutions + order.Executions.Count > MaxExecutions)
            {
                throw Invalid();
            }

            newExecutions += order.Executions.Count;
            var seenExecutions = new HashSet<string>(StringComparer.Ordinal);
            var copied = new List<IbkrOfflineExecutionEvidence>(order.Executions.Count);
            decimal sum = 0;

            foreach (var execution in order.Executions)
            {
                if (execution is null)
                {
                    throw Invalid();
                }

                CheckKey(execution.ExecutionId);
                if (!seenExecutions.Add(execution.ExecutionId)
                    || !string.Equals(execution.AccountKey, order.AccountKey, StringComparison.Ordinal)
                    || !string.Equals(execution.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal)
                    || !string.Equals(execution.BrokerOrderId, order.BrokerOrderId, StringComparison.Ordinal)
                    || !ValidAmount(execution.Quantity, allowZero: false))
                {
                    throw Invalid();
                }

                try
                {
                    sum = checked(sum + execution.Quantity);
                }
                catch (OverflowException)
                {
                    throw Invalid();
                }

                if (sum > order.CumulativeFilled)
                {
                    throw Invalid();
                }

                copied.Add(execution with { });
            }

            if (order.ExecutionsComplete && sum != order.CumulativeFilled)
            {
                throw Invalid();
            }

            accepted.Add(order with
            {
                Executions = Array.AsReadOnly(copied.ToArray())
            });
        }

        // Publish as a single atomic mutation after all rows pass.
        _orders.AddRange(accepted);
        _coids.UnionWith(freshCoids);
        _brokerOrderIds.UnionWith(freshBrokerIds);
        _executionCount += newExecutions;
        _pageDigests.Add(digest);

        if (page.NextCursor is not null)
        {
            _issuedCursors.Add(page.NextCursor);
        }

        _nextCursor = page.NextCursor;
        IsComplete = page.IsLastPage;
    }

    /// <summary>
    /// A partial page chain MUST remain inconclusive in the existing reducer.
    /// Never label it as a full order list.
    /// </summary>
    public IbkrOfflineEvidenceSnapshot GetPartialSnapshot()
    {
        if (_pageDigests.Count == 0 || IsComplete)
        {
            throw Invalid();
        }

        return new(_sequence, _accountKey, false,
            Array.AsReadOnly(_orders.ToArray()));
    }

    public IbkrOfflineEvidenceSnapshot RequireCompleteSnapshot()
    {
        if (!IsComplete || _pageDigests.Count == 0 || _nextCursor is not null)
        {
            throw Invalid();
        }

        return new(_sequence, _accountKey, true,
            Array.AsReadOnly(_orders.ToArray()));
    }

    private static bool ValidAmount(decimal amount, bool allowZero) =>
        (allowZero ? amount >= 0m : amount > 0m)
        && amount < MaxMagnitudeExclusive
        && decimal.Round(amount, 12) == amount;

    private static bool SafeCursor(string? cursor) =>
        cursor is { Length: > 0 and <= MaxKeyLength }
        && cursor.All(c => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-' or '.' or '~');

    private static void CheckKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxKeyLength
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw Invalid();
        }
    }

    private static InvalidOperationException Invalid() =>
        new("Invalid or incomplete synthetic IBKR evidence cursor chain; no completeness proof.");
}
