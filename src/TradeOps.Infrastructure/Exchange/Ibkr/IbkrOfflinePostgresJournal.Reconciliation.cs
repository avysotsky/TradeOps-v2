using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Atomic, explicit opt-in persistence of offline normalized reconciliation
/// observations, verified execution IDs, and the matching lifecycle event.
/// The method has NO broker network transport or trading capability.
/// </summary>
public sealed partial class IbkrOfflinePostgresJournal
{
    private const int MaximumReconciliationJsonCharacters = 262_144;

    public async Task<IbkrOfflineDurableReconciliationResult> AppendReconciliationAsync(
        string accountKey,
        string clientOrderId,
        IbkrOfflineEvidenceSnapshot evidence,
        CancellationToken cancellationToken = default)
    {
        IbkrOfflineJournalReplay.ValidateKey(accountKey, nameof(accountKey));
        IbkrOfflineJournalReplay.ValidateKey(clientOrderId, nameof(clientOrderId));
        ArgumentNullException.ThrowIfNull(evidence);

        // Detach from caller-owned mutable collections; the digest, validation
        // and persisted evidence must all refer to identical immutable bytes.
        var json = JsonSerializer.Serialize(evidence);
        if (json.Length > MaximumReconciliationJsonCharacters
            || evidence.Sequence <= 0)
        {
            throw new InvalidOperationException("Oversized or invalid synthetic observation.");
        }

        var frozen = JsonSerializer.Deserialize<IbkrOfflineEvidenceSnapshot>(json)
            ?? throw new InvalidOperationException("Invalid synthetic observation.");
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // LoadCoreAsync serializes every writer for this account/cOID through
        // SELECT ... FOR UPDATE before any reconciliation audit is read.
        var previous = await LoadCoreAsync(
            connection, transaction, accountKey, clientOrderId, cancellationToken);

        (long Sequence, string Digest, string Disposition)? latest = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT observation_sequence, payload_sha256, disposition
            FROM ibkr_offline_reconciliation_observations
            WHERE account_key = @account_key AND client_order_id = @client_order_id
            ORDER BY observation_sequence DESC LIMIT 1
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                latest = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
            }
        }

        if (latest is { } last)
        {
            if (frozen.Sequence < last.Sequence
                || (frozen.Sequence == last.Sequence
                    && !string.Equals(digest, last.Digest, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Stale or conflicting durable IBKR offline reconciliation observation.");
            }

            if (frozen.Sequence == last.Sequence)
            {
                if (!Enum.TryParse<IbkrOfflineReconciliationDisposition>(
                        last.Disposition, false, out var disposition)
                    || !Enum.IsDefined(disposition))
                {
                    throw new InvalidOperationException(
                        "Corrupt durable reconciliation disposition.");
                }

                await transaction.CommitAsync(cancellationToken);
                return new(new(disposition, previous.Snapshot, frozen.Sequence),
                    IdempotentReplay: true, AddedExecutionIds: 0);
            }
        }

        if (previous.Snapshot.State != IbkrOfflineOrderState.Unknown)
        {
            throw new InvalidOperationException(
                "Only Unknown offline orders may receive a new reconciliation observation.");
        }

        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(previous.Snapshot.Intent);
        lifecycle.Replay(previous.Events.Select(item => item.Event));
        var outcome = new IbkrOfflineSnapshotReconciler(lifecycle)
            .Reconcile(clientOrderId, frozen);

        IbkrOfflineOrderEvent? resolvedEvent = null;
        var newExecutionRows = new List<IbkrOfflineExecutionEvidence>();

        if (outcome.Disposition is not
            (IbkrOfflineReconciliationDisposition.Inconclusive
             or IbkrOfflineReconciliationDisposition.NotFound))
        {
            var observedOrder = frozen.Orders.Single(
                order => string.Equals(
                    order.ClientOrderId, clientOrderId, StringComparison.Ordinal));
            if (!observedOrder.ExecutionsComplete || observedOrder.Executions is null)
            {
                throw new InvalidOperationException(
                    "Incomplete synthetic execution evidence cannot be imported.");
            }

            newExecutionRows.AddRange(IbkrOfflineExecutionBaselinePlanner.Plan(
                previous.Executions, observedOrder));

            var reconciledType = outcome.Disposition switch
            {
                IbkrOfflineReconciliationDisposition.Working
                    or IbkrOfflineReconciliationDisposition.PartiallyFilled
                    => IbkrOfflineOrderEventType.ReconciledWorking,
                IbkrOfflineReconciliationDisposition.Filled
                    => IbkrOfflineOrderEventType.ReconciledFilled,
                IbkrOfflineReconciliationDisposition.Cancelled
                    => IbkrOfflineOrderEventType.ReconciledCancelled,
                _ => throw new InvalidOperationException("Unexpected reconciliation result.")
            };

            resolvedEvent = new IbkrOfflineOrderEvent(
                clientOrderId,
                reconciledType,
                BrokerOrderId: outcome.Order.BrokerOrderId,
                CumulativeFilled: outcome.Order.CumulativeFilled);
            IbkrOfflineJournalReplay.ValidateEvent(
                previous.Snapshot.Intent, "reconciliation:" + frozen.Sequence, resolvedEvent);
        }

        // Everything below executes in the SAME transaction as the row lock,
        // validated lifecycle event and normalized evidence journal entry.
        foreach (var execution in newExecutionRows)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO ibkr_offline_executions
                    (account_key, client_order_id, execution_id, broker_order_id, quantity)
                VALUES (@account_key, @client_order_id, @execution_id, @broker_order_id, @quantity)
                """, connection, transaction);
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            AddText(command, "execution_id", execution.ExecutionId);
            AddText(command, "broker_order_id", execution.BrokerOrderId);
            AddDecimal(command, "quantity", execution.Quantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        long? eventSequence = null;
        if (resolvedEvent is not null)
        {
            eventSequence = checked((long)previous.Events.Count + 1);
            await InsertEventAsync(
                connection,
                transaction,
                accountKey,
                eventSequence.Value,
                "reconciliation:" + frozen.Sequence,
                resolvedEvent,
                cancellationToken);
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO ibkr_offline_reconciliation_observations
                (account_key, client_order_id, observation_sequence,
                 payload_sha256, evidence_json, disposition, lifecycle_event_sequence)
            VALUES (@account_key, @client_order_id, @sequence,
                    @digest, @evidence, @disposition, @event_sequence)
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            command.Parameters.Add("sequence", NpgsqlDbType.Bigint).Value = frozen.Sequence;
            AddText(command, "digest", digest);
            AddText(command, "evidence", json);
            AddText(command, "disposition", outcome.Disposition.ToString());
            command.Parameters.Add("event_sequence", NpgsqlDbType.Bigint).Value =
                eventSequence is { } value ? value : DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(outcome, IdempotentReplay: false,
            AddedExecutionIds: newExecutionRows.Count);
    }
}

public sealed record IbkrOfflineDurableReconciliationResult(
    IbkrOfflineReconciliationResult Result,
    bool IdempotentReplay,
    int AddedExecutionIds);
