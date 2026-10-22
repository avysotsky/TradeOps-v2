using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Explicitly opt-in PostgreSQL persistence for synthetic IBKR traces only.
/// Never registered in TradeOps dependency injection and has no broker HTTP
/// transport. Schema must be installed separately in a disposable test DB.
/// </summary>
public sealed partial class IbkrOfflinePostgresJournal
{
    private const int MaximumReplayRows = 10000;
    private readonly string _connectionString;

    public IbkrOfflinePostgresJournal(string connectionString)
    {
        // No connection may be opened to an arbitrary (including production)
        // PostgreSQL database. The caller must use a disposable local DB
        // and a unique isolated schema, exactly as the opt-in smoke does.
        _connectionString = IbkrOfflineJournalConnectionPolicy.Validate(connectionString);
    }

    public async Task<IbkrOfflineJournalRecovery> RegisterAsync(
        IbkrOfflineOrderIntent intent,
        CancellationToken cancellationToken = default)
    {
        IbkrOfflineJournalReplay.ValidateIntent(intent);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO ibkr_offline_intents
                (account_key, client_order_id, symbol, side, requested_quantity)
            VALUES (@account_key, @client_order_id, @symbol, @side, @quantity)
            ON CONFLICT (account_key, client_order_id) DO NOTHING
            """, connection, transaction))
        {
            AddText(command, "account_key", intent.AccountKey);
            AddText(command, "client_order_id", intent.ClientOrderId);
            AddText(command, "symbol", intent.Symbol);
            AddText(command, "side", intent.Side.ToString());
            AddDecimal(command, "quantity", intent.Quantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var loaded = await LoadCoreAsync(
            connection, transaction, intent.AccountKey, intent.ClientOrderId, cancellationToken);
        if (loaded.Snapshot.Intent != intent)
        {
            throw new InvalidOperationException("Persisted cOID already identifies a different order intent.");
        }

        await transaction.CommitAsync(cancellationToken);
        return loaded;
    }

    public async Task<IbkrOfflineJournalRecovery> LoadAsync(
        string accountKey,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        IbkrOfflineJournalReplay.ValidateKey(accountKey, nameof(accountKey));
        IbkrOfflineJournalReplay.ValidateKey(clientOrderId, nameof(clientOrderId));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var loaded = await LoadCoreAsync(
            connection, transaction, accountKey, clientOrderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return loaded;
    }

    /// <summary>
    /// Record a synthetic lifecycle event atomically before any future
    /// external action. A persisted SubmissionStarted event MUST NOT be
    /// retried after process restart unless broker status is reconciled.
    /// Direct CumulativeFill is forbidden; use AppendExecutionAsync.
    /// </summary>
    public async Task<IbkrOfflineJournalRecovery> AppendEventAsync(
        string accountKey,
        string eventKey,
        IbkrOfflineOrderEvent evt,
        CancellationToken cancellationToken = default)
    {
        IbkrOfflineJournalReplay.ValidateKey(accountKey, nameof(accountKey));
        ArgumentNullException.ThrowIfNull(evt);
        IbkrOfflineJournalReplay.ValidateKey(evt.ClientOrderId, nameof(evt.ClientOrderId));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var loaded = await LoadCoreAsync(
            connection, transaction, accountKey, evt.ClientOrderId, cancellationToken);
        IbkrOfflineJournalReplay.ValidateEvent(loaded.Snapshot.Intent, eventKey, evt);

        var found = loaded.Events.FirstOrDefault(
            item => string.Equals(item.EventKey, eventKey, StringComparison.Ordinal));
        if (found is not null)
        {
            if (found.Event != evt)
            {
                throw new InvalidOperationException("Idempotency event key conflicts with stored event.");
            }

            await transaction.CommitAsync(cancellationToken);
            return loaded;
        }

        // Reconciliation transitions must be atomically bound to complete
        // execution-ID evidence and an observation record. A direct lifecycle
        // append can otherwise create Filled/Cancelled with no supporting
        // evidence and silently destroy the durable baseline guarantee.
        if (evt.Type is IbkrOfflineOrderEventType.CumulativeFill
            or IbkrOfflineOrderEventType.ReconciledWorking
            or IbkrOfflineOrderEventType.ReconciledFilled
            or IbkrOfflineOrderEventType.ReconciledCancelled
            or IbkrOfflineOrderEventType.CancelConfirmed)
        {
            throw new InvalidOperationException(
                "Evidence-bound IBKR offline lifecycle events require atomic execution or reconciliation persistence.");
        }

        var updated = ApplyChecked(loaded, evt);
        var sequence = checked((long)loaded.Events.Count + 1);
        await InsertEventAsync(
            connection, transaction, accountKey, sequence, eventKey, evt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IbkrOfflineJournalRecovery(updated,
            loaded.Events.Append(new IbkrOfflineJournalEvent(eventKey, evt)).ToArray(),
            loaded.Executions);
    }

    /// <summary>
    /// Atomic insert of an execution ID and the corresponding cumulative-fill
    /// event. Identical repeats are harmless; conflicts and untracked baseline
    /// fills are blocked. A restarted process derives state from persisted rows.
    /// </summary>
    public async Task<IbkrOfflineJournalRecovery> AppendExecutionAsync(
        string accountKey,
        string clientOrderId,
        string executionId,
        string brokerOrderId,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        IbkrOfflineJournalReplay.ValidateKey(accountKey, nameof(accountKey));
        IbkrOfflineJournalReplay.ValidateKey(clientOrderId, nameof(clientOrderId));
        IbkrOfflineJournalReplay.ValidateKey(executionId, nameof(executionId));
        IbkrOfflineJournalReplay.ValidateKey(brokerOrderId, nameof(brokerOrderId));

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var loaded = await LoadCoreAsync(
            connection, transaction, accountKey, clientOrderId, cancellationToken);

        var priorExecution = loaded.Executions.FirstOrDefault(
            item => string.Equals(item.ExecutionId, executionId, StringComparison.Ordinal));
        if (priorExecution is not null)
        {
            if (priorExecution.BrokerOrderId != brokerOrderId
                || priorExecution.Quantity != quantity)
            {
                throw new InvalidOperationException("Execution ID reused with different broker order or quantity.");
            }

            await transaction.CommitAsync(cancellationToken);
            return loaded;
        }

        var state = loaded.Snapshot;
        if (state.State is not (IbkrOfflineOrderState.Working
            or IbkrOfflineOrderState.PartiallyFilled
            or IbkrOfflineOrderState.CancelPending
            or IbkrOfflineOrderState.Cancelled)
            || state.BrokerOrderId != brokerOrderId)
        {
            throw new InvalidOperationException(
                "Execution not correlated to a confirmed synthetic broker order.");
        }

        var totalPersisted = SumExecutions(loaded.Executions);
        if (totalPersisted != state.CumulativeFilled)
        {
            // An older reconciliation may have established filled quantity
            // without execution IDs. Never silently count it twice.
            throw new InvalidOperationException(
                "Offline journal lacks complete execution-ID baseline; reconciliation required.");
        }

        decimal newCumulative;
        try
        {
            newCumulative = checked(totalPersisted + quantity);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("Execution quantity overflow.");
        }

        var eventKey = "execution:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(executionId))).ToLowerInvariant();
        var evt = new IbkrOfflineOrderEvent(
            clientOrderId,
            IbkrOfflineOrderEventType.CumulativeFill,
            CumulativeFilled: newCumulative);

        IbkrOfflineJournalReplay.ValidateEvent(state.Intent, eventKey, evt);
        var updated = ApplyChecked(loaded, evt);

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO ibkr_offline_executions
                (account_key, client_order_id, execution_id, broker_order_id, quantity)
            VALUES (@account_key, @client_order_id, @execution_id, @broker_order_id, @quantity)
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            AddText(command, "execution_id", executionId);
            AddText(command, "broker_order_id", brokerOrderId);
            AddDecimal(command, "quantity", quantity);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var sequence = checked((long)loaded.Events.Count + 1);
        await InsertEventAsync(
            connection, transaction, accountKey, sequence, eventKey, evt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IbkrOfflineJournalRecovery(updated,
            loaded.Events.Append(new IbkrOfflineJournalEvent(eventKey, evt)).ToArray(),
            loaded.Executions.Append(
                new IbkrOfflineJournalExecution(executionId, brokerOrderId, quantity)).ToArray());
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IbkrOfflineJournalRecovery> LoadCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string accountKey,
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        IbkrOfflineOrderIntent intent;
        // This per-order row lock serializes concurrent writers across
        // PostgreSQL sessions/processes before reading and appending events.
        await using (var command = new NpgsqlCommand(
            """
            SELECT symbol, side, requested_quantity
            FROM ibkr_offline_intents
            WHERE account_key = @account_key AND client_order_id = @client_order_id
            FOR UPDATE
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException("Offline journal intent does not exist.");
            }

            var symbol = reader.GetString(0);
            if (!Enum.TryParse<OrderSide>(reader.GetString(1), false, out var side)
                || side is not (OrderSide.Buy or OrderSide.Sell))
            {
                throw new InvalidOperationException("Invalid persisted offline order side.");
            }

            intent = new IbkrOfflineOrderIntent(
                clientOrderId, accountKey, symbol, side, reader.GetDecimal(2));
        }

        var events = new List<IbkrOfflineJournalEvent>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT event_sequence, event_key, event_type, broker_order_id,
                   reply_id, cumulative_filled, http_status
            FROM ibkr_offline_events
            WHERE account_key = @account_key AND client_order_id = @client_order_id
            ORDER BY event_sequence
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            long expectedSequence = 1;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (events.Count >= MaximumReplayRows
                    || reader.GetInt64(0) != expectedSequence++
                    || !Enum.TryParse<IbkrOfflineOrderEventType>(
                        reader.GetString(2), false, out var type)
                    || !Enum.IsDefined(type))
                {
                    throw new InvalidOperationException("Corrupt offline journal event sequence or type.");
                }

                var evt = new IbkrOfflineOrderEvent(
                    clientOrderId,
                    type,
                    BrokerOrderId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReplyId: reader.IsDBNull(4) ? null : reader.GetString(4),
                    CumulativeFilled: reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    HttpStatus: reader.IsDBNull(6) ? null : reader.GetInt32(6));
                events.Add(new IbkrOfflineJournalEvent(reader.GetString(1), evt));
            }
        }

        var executions = new List<IbkrOfflineJournalExecution>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT execution_id, broker_order_id, quantity
            FROM ibkr_offline_executions
            WHERE account_key = @account_key AND client_order_id = @client_order_id
            ORDER BY execution_id
            """, connection, transaction))
        {
            AddText(command, "account_key", accountKey);
            AddText(command, "client_order_id", clientOrderId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (executions.Count >= MaximumReplayRows)
                {
                    throw new InvalidOperationException("Offline execution replay limit exceeded.");
                }

                var execution = new IbkrOfflineJournalExecution(
                    reader.GetString(0), reader.GetString(1), reader.GetDecimal(2));
                IbkrOfflineJournalReplay.ValidateKey(execution.ExecutionId, nameof(execution.ExecutionId));
                IbkrOfflineJournalReplay.ValidateKey(execution.BrokerOrderId, nameof(execution.BrokerOrderId));
                if (execution.Quantity <= 0m)
                {
                    throw new InvalidOperationException("Corrupt execution quantity.");
                }

                executions.Add(execution);
            }
        }

        var snapshot = IbkrOfflineJournalReplay.Restore(intent, events);
        if (SumExecutions(executions) > snapshot.CumulativeFilled)
        {
            throw new InvalidOperationException(
                "Persisted execution ledger exceeds recorded cumulative fill.");
        }

        return new IbkrOfflineJournalRecovery(snapshot, events.ToArray(), executions.ToArray());
    }

    private static decimal SumExecutions(
        IReadOnlyList<IbkrOfflineJournalExecution> executions)
    {
        decimal sum = 0m;
        foreach (var execution in executions)
        {
            try
            {
                sum = checked(sum + execution.Quantity);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Offline execution ledger overflow.");
            }
        }

        return sum;
    }

    private static IbkrOfflineOrderSnapshot ApplyChecked(
        IbkrOfflineJournalRecovery loaded, IbkrOfflineOrderEvent evt)
    {
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(loaded.Snapshot.Intent);
        foreach (var entry in loaded.Events)
        {
            lifecycle.Apply(entry.Event);
        }
        return lifecycle.Apply(evt);
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string accountKey,
        long sequence,
        string eventKey,
        IbkrOfflineOrderEvent evt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ibkr_offline_events
                (account_key, client_order_id, event_sequence, event_key,
                 event_type, broker_order_id, reply_id, cumulative_filled, http_status)
            VALUES
                (@account_key, @client_order_id, @sequence, @event_key,
                 @event_type, @broker_order_id, @reply_id, @cumulative, @http_status)
            """, connection, transaction);
        AddText(command, "account_key", accountKey);
        AddText(command, "client_order_id", evt.ClientOrderId);
        command.Parameters.Add("sequence", NpgsqlDbType.Bigint).Value = sequence;
        AddText(command, "event_key", eventKey);
        AddText(command, "event_type", evt.Type.ToString());
        AddNullableText(command, "broker_order_id", evt.BrokerOrderId);
        AddNullableText(command, "reply_id", evt.ReplyId);
        command.Parameters.Add("cumulative", NpgsqlDbType.Numeric).Value =
            evt.CumulativeFilled is { } filled ? filled : DBNull.Value;
        command.Parameters.Add("http_status", NpgsqlDbType.Integer).Value =
            evt.HttpStatus is { } status ? status : DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Varchar).Value =
            value is null ? DBNull.Value : value;

    private static void AddDecimal(NpgsqlCommand command, string name, decimal value) =>
        command.Parameters.Add(name, NpgsqlDbType.Numeric).Value = value;
}
