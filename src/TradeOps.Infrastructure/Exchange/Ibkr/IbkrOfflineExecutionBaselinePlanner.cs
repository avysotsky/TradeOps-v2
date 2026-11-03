namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Pure fail-closed planner for importing a COMPLETE normalized execution-ID
/// baseline into the isolated synthetic PostgreSQL journal. Does not write.
/// </summary>
public static class IbkrOfflineExecutionBaselinePlanner
{
    public static IReadOnlyList<IbkrOfflineExecutionEvidence> Plan(
        IReadOnlyList<IbkrOfflineJournalExecution> persisted,
        IbkrOfflineBrokerOrderEvidence observed)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(observed);

        if (!observed.ExecutionsComplete
            || observed.Executions is null
            || observed.Executions.Count > 4096
            || observed.RequestedQuantity <= 0m
            || observed.CumulativeFilled < 0m
            || observed.CumulativeFilled > observed.RequestedQuantity)
        {
            throw Invalid();
        }

        IbkrOfflineJournalReplay.ValidateKey(
            observed.ClientOrderId, nameof(observed.ClientOrderId));
        IbkrOfflineJournalReplay.ValidateKey(
            observed.AccountKey, nameof(observed.AccountKey));
        IbkrOfflineJournalReplay.ValidateKey(
            observed.BrokerOrderId, nameof(observed.BrokerOrderId));

        var unique = new Dictionary<string, IbkrOfflineExecutionEvidence>(
            StringComparer.Ordinal);
        decimal total = 0m;

        foreach (var execution in observed.Executions)
        {
            if (execution is null
                || !IsValidAmount(execution.Quantity)
                || !string.Equals(execution.AccountKey, observed.AccountKey, StringComparison.Ordinal)
                || !string.Equals(execution.ClientOrderId, observed.ClientOrderId, StringComparison.Ordinal)
                || !string.Equals(execution.BrokerOrderId, observed.BrokerOrderId, StringComparison.Ordinal))
            {
                throw Invalid();
            }

            IbkrOfflineJournalReplay.ValidateKey(
                execution.ExecutionId, nameof(execution.ExecutionId));
            IbkrOfflineJournalReplay.ValidateKey(
                execution.BrokerOrderId, nameof(execution.BrokerOrderId));

            if (unique.TryGetValue(execution.ExecutionId, out var existing))
            {
                if (existing != execution)
                {
                    throw Invalid();
                }
                continue;
            }

            unique.Add(execution.ExecutionId, execution);
            try
            {
                total = checked(total + execution.Quantity);
            }
            catch (OverflowException)
            {
                throw Invalid();
            }
        }

        if (total != observed.CumulativeFilled)
        {
            throw Invalid();
        }

        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in persisted)
        {
            if (old is null
                || !knownIds.Add(old.ExecutionId)
                || !unique.TryGetValue(old.ExecutionId, out var matching)
                || matching.BrokerOrderId != old.BrokerOrderId
                || matching.Quantity != old.Quantity)
            {
                throw Invalid();
            }
        }

        return unique.Values.Where(x => !knownIds.Contains(x.ExecutionId)).ToArray();
    }

    private static bool IsValidAmount(decimal value) =>
        value > 0m
        && value < 10_000_000_000_000_000m
        && decimal.Round(value, 12) == value;

    private static InvalidOperationException Invalid() =>
        new("Incomplete, conflicting or nonrepresentable offline execution-ID baseline.");
}
