using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SyntheticScopedReservationJournalTests
{
    private static ScopedReservationsDbContext ModelOnly() =>
        new(new DbContextOptionsBuilder<ScopedReservationsDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=model_only;Username=no;Password=no;Timeout=1")
            .Options);

    [Fact]
    public void Model_KeysAndExactFivePartExecutionFk_AreBrokerAndAccountScoped()
    {
        using var db = ModelOnly();
        var order = db.Model.FindEntityType(typeof(SyntheticScopedOrderReservation));
        var execution = db.Model.FindEntityType(typeof(SyntheticScopedExecutionReservation));
        Assert.NotNull(order);
        Assert.NotNull(execution);

        Assert.Equal("SyntheticScopedOrderReservations", order!.GetTableName());
        Assert.Equal("SyntheticScopedExecutionReservations", execution!.GetTableName());

        Assert.Equal(new[]
        {
            "ProviderKey", "AccountKey", "ClientOrderId"
        }, order.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());

        Assert.Equal(new[]
        {
            "ProviderKey", "AccountKey", "ExecutionId"
        }, execution.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());

        var fk = Assert.Single(execution.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal(new[]
        {
            "ProviderKey", "AccountKey", "ClientOrderId",
            "LocalOrderId", "BrokerOrderId"
        }, fk.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(fk.Properties.Select(p => p.Name),
            fk.PrincipalKey.Properties.Select(p => p.Name));

        Assert.Contains(order.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "LocalOrderId" }));
        Assert.Contains(order.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(p => p.Name).SequenceEqual(
                new[] { "ProviderKey", "AccountKey", "BrokerOrderId" }));

        // Original global uniqueness is still in force until a separate
        // carefully staged multi-provider runtime migration.
        // The production model remains genuinely isolated.
        Assert.Null(db.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Order)));
        Assert.Null(db.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Fill)));
        using var legacyDb = new TradeOpsDbContext(
            new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=model_only;Username=no;Password=no;Timeout=1")
                .Options);
        Assert.Null(legacyDb.Model.FindEntityType(typeof(SyntheticScopedOrderReservation)));
        Assert.Null(legacyDb.Model.FindEntityType(typeof(SyntheticScopedExecutionReservation)));
        Assert.DoesNotContain(
            "20261023120000_AddSyntheticScopedIdempotencyReservations",
            legacyDb.Database.GetMigrations());

        var oldOrders = legacyDb.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Order));
        var oldFills = legacyDb.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Fill));
        Assert.Contains(oldOrders!.GetIndexes(), index => index.IsUnique
            && index.Properties.Count == 1
            && index.Properties[0].Name == "ClientOrderId");
        Assert.Contains(oldFills!.GetIndexes(), index => index.IsUnique
            && index.Properties.Count == 1
            && index.Properties[0].Name == "ExchangeFillId");
    }

    [Fact]
    public void Migration_RegistersOnlyNewReservations_WithoutAlterOfExistingTables()
    {
        using var db = ModelOnly();
        Assert.Contains(
            "20261023120000_AddSyntheticScopedIdempotencyReservations",
            db.Database.GetMigrations());

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "TradeOps.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(
            root!.FullName, "src", "TradeOps.Infrastructure",
            "Persistence", "ScopedReservationMigrations",
            "20261023120000_AddSyntheticScopedIdempotencyReservations.cs"));

        Assert.Contains("SyntheticScopedOrderReservations", source);
        Assert.Contains("SyntheticScopedExecutionReservations", source);
        Assert.Contains("FK_ScopedExecutions_ExactOrderBinding", source);
        Assert.Contains("ReferentialAction.Restrict", source);
        Assert.DoesNotContain("AlterColumn(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DropIndex(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE \"Orders\"", source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE \"Fills\"", source,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidProviderAndAmounts_FailBeforeConnectingToDatabase()
    {
        await using var db = ModelOnly();
        var journal = new EfSyntheticScopedReservationJournal(db);
        var syntheticOrder = new ScopedOrderFingerprint(
            "Mock", "synthetic-account", Guid.NewGuid(),
            "synthetic-client", "broker-123", "MOCK:BTCUSDT",
            OrderSide.Buy, OrderType.Limit, 5m, 100m);
        foreach (var invalid in new[]
        {
            syntheticOrder with { ProviderKey = "IbkrPaper" },
            syntheticOrder with { AccountKey = " " },
            syntheticOrder with { RequestedQuantity = -1m },
            syntheticOrder with { InstrumentKey = "MOCK:" }
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.ReserveOrderAsync(invalid));
        }

        var unavailable = await journal.ReserveOrderAsync(
            syntheticOrder with { BrokerOrderId = null });
        Assert.Equal(SyntheticReservationDisposition.RequiresProof,
            unavailable.Disposition);

        var execution = new ScopedExecutionFingerprint(
            "Mock", "synthetic-account", syntheticOrder.LocalOrderId,
            "synthetic-client", "broker-123", "execution-1",
            2m, 100m, 0.01m, "USD");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.ReserveExecutionAsync(execution with
            {
                ProviderKey = "IbkrPaper"
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.ReserveExecutionAsync(execution with { Quantity = -3m }));
    }

    [Fact]
    public void ActualSmokeExecutable_RejectsNonDisposableAndRequiresConfirmation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "TradeOps.sln")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root);

        var file = Path.Combine(root!.FullName, "tools",
            "TradeOps.ScopedReservationsPostgresSmoke", "Program.cs");
        var source = File.ReadAllText(file);
        Assert.Contains("DISPOSABLE_SCOPED_RESERVATION_PG_ONLY", source);
        Assert.Contains("tradeops_api_smoke_", source);
        Assert.Contains("127.0.0.1", source);
        Assert.Contains("ReserveOrder", source);
        Assert.Contains("ReserveExecution", source);
        Assert.Contains("Task.WhenAll", source);
        Assert.Contains("legacy_order_changed", source);
        Assert.Contains("legacy_fills_changed", source);
        Assert.DoesNotContain("PlaceOrderAsync", source);
        Assert.DoesNotContain("CancelOrderAsync", source);
    }
}
