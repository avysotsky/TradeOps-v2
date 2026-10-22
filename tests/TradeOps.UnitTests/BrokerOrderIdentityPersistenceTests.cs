using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BrokerOrderIdentityPersistenceTests
{
    private static TradeOpsDbContext ModelOnlyContext() =>
        new(new DbContextOptionsBuilder<TradeOpsDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=model_only;Username=no;Password=no;Timeout=1")
            .Options);

    [Fact]
    public void Model_AddsScopedSyntheticTable_WithoutChangingLegacyGlobalIndexes()
    {
        using var db = ModelOnlyContext();
        var model = db.Model;

        var scope = model.FindEntityType(typeof(BrokerOrderIdentity));
        Assert.NotNull(scope);
        Assert.Equal("BrokerOrderIdentities", scope!.GetTableName());

        var properties = scope.GetProperties().ToDictionary(x => x.Name);
        Assert.Equal(64, properties[nameof(BrokerOrderIdentity.ProviderKey)].GetMaxLength());
        Assert.Equal(128, properties[nameof(BrokerOrderIdentity.AccountKey)].GetMaxLength());
        Assert.Equal(192, properties[nameof(BrokerOrderIdentity.InstrumentKey)].GetMaxLength());
        Assert.Equal(100, properties[nameof(BrokerOrderIdentity.ClientOrderId)].GetMaxLength());
        Assert.Equal(100, properties[nameof(BrokerOrderIdentity.BrokerOrderId)].GetMaxLength());

        Assert.Equal(nameof(BrokerOrderIdentity.OrderId),
            Assert.Single(scope.FindPrimaryKey()!.Properties).Name);
        Assert.Contains(scope.GetForeignKeys(), key =>
            key.PrincipalEntityType.ClrType == typeof(TradeOps.Domain.Entities.Order)
            && key.DeleteBehavior == DeleteBehavior.Restrict
            && key.IsUnique);

        var indexes = scope.GetIndexes().ToArray();
        Assert.Contains(indexes, index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(BrokerOrderIdentity.ProviderKey),
                    nameof(BrokerOrderIdentity.AccountKey),
                    nameof(BrokerOrderIdentity.ClientOrderId)
                }));
        Assert.Contains(indexes, index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(BrokerOrderIdentity.ProviderKey),
                    nameof(BrokerOrderIdentity.AccountKey),
                    nameof(BrokerOrderIdentity.BrokerOrderId)
                })
            && index.GetFilter() == "\"BrokerOrderId\" IS NOT NULL");

        var legacyOrder = model.FindEntityType(typeof(TradeOps.Domain.Entities.Order));
        var legacyFill = model.FindEntityType(typeof(TradeOps.Domain.Entities.Fill));
        Assert.NotNull(legacyOrder);
        Assert.NotNull(legacyFill);
        Assert.Null(legacyOrder!.FindProperty("ProviderKey"));
        Assert.Null(legacyOrder.FindProperty("AccountKey"));
        Assert.Null(legacyOrder.FindProperty("InstrumentKey"));
        Assert.Contains(legacyOrder.GetIndexes(), index => index.IsUnique
            && index.Properties.SingleOrDefault()?.Name == "ClientOrderId");
        Assert.Contains(legacyFill!.GetIndexes(), index => index.IsUnique
            && index.Properties.SingleOrDefault()?.Name == "ExchangeFillId");
    }

    [Fact]
    public void Migration_IsAdditiveAndDiscoverable_WithoutLegacyBackfill()
    {
        using var db = ModelOnlyContext();
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.Contains("20261022235900_AddSyntheticBrokerOrderIdentities",
            migrations);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "TradeOps.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var migrationPath = Path.Combine(dir!.FullName,
            "src", "TradeOps.Infrastructure", "Persistence", "Migrations",
            "20261022235900_AddSyntheticBrokerOrderIdentities.cs");
        var source = File.ReadAllText(migrationPath);

        Assert.Contains("CreateTable(", source, StringComparison.Ordinal);
        Assert.Contains("BrokerOrderIdentities", source, StringComparison.Ordinal);
        Assert.Contains("ReferentialAction.Restrict", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE \"Orders\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE \"Fills\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DropIndex(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AlterColumn(", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_FailsClosedOnInvalidProviderOrAccount_NoDatabaseUsed()
    {
        using var db = ModelOnlyContext();
        var reader = new EfSyntheticScopedOrderReader(db);
        foreach (var provider in new[]
        {
            "IbkrPaper", "Binance", "mock", "", "Mock "
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.GetReconciliationCandidatesAsync(provider, "synthetic-account"));
        }

        foreach (var account in new[]
        {
            "", " ", " leading", "trailing ", "bad\naccount",
            new string('x', 129)
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.GetReconciliationCandidatesAsync("Mock", account));
        }
    }

    [Fact]
    public void SyntheticOnlyClassification_IsExplicit_NoTrustUpgradeInEntity()
    {
        var record = new BrokerOrderIdentity
        {
            OrderId = Guid.NewGuid(),
            ProviderKey = "Mock",
            AccountKey = "synthetic-account",
            ClientOrderId = "synthetic-coid",
            InstrumentKey = "MOCK:BTCUSDT",
            RecordedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("SyntheticOnly", record.Classification);
        Assert.DoesNotContain(
            typeof(BrokerOrderIdentity).GetProperties(),
            prop => prop.Name is "IsAuthenticated" or "BrokerVerified");
    }
}
