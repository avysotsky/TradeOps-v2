using Microsoft.EntityFrameworkCore;
using TradeOps.Infrastructure.Exchange.Deribit;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRealReadOnlyEvidencePgStoreTests
{
    private static DeribitPositionSnapshotCapture Valid() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DeribitRealReadOnlyEvidencePgStore.FingerprintCredentialId(
                "test-only-credential-id"),
            new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 23, 12, 0, 1, TimeSpan.Zero),
            new[]
            {
                new DeribitPositionEvidence(
                    "BTC-PERPETUAL", "future", "sell", -100m, 0.002m),
                new DeribitPositionEvidence(
                    "ETH-PERPETUAL", "future", "buy", 50m, null)
            });

    [Fact]
    public void ValidRealObservations_PreserveSignedNativeUnitsAndEmptySnapshots()
    {
        var capture = Valid();
        DeribitRealReadOnlyEvidencePgStore.Validate(capture);
        Assert.Equal(-100m, capture.Positions[0].NativeSize);
        Assert.Equal(0.002m, capture.Positions[0].BaseCurrencySize);
        DeribitRealReadOnlyEvidencePgStore.Validate(
            capture with { Positions = Array.Empty<DeribitPositionEvidence>() });
    }

    [Fact]
    public void CredentialFingerprint_IsStableAndNeverStoresRawClientId()
    {
        var hashed = DeribitRealReadOnlyEvidencePgStore
            .FingerprintCredentialId("test-only-credential-id");
        Assert.Equal(64, hashed.Length);
        Assert.NotEqual("test-only-credential-id", hashed);
        Assert.Equal(hashed, DeribitRealReadOnlyEvidencePgStore
            .FingerprintCredentialId("test-only-credential-id"));
        Assert.NotEqual(hashed, DeribitRealReadOnlyEvidencePgStore
            .FingerprintCredentialId("another-test-only-credential-id"));
        Assert.Throws<InvalidOperationException>(() =>
            DeribitRealReadOnlyEvidencePgStore.FingerprintCredentialId(" "));
    }

    [Fact]
    public void RebindAndMalformedObservations_FailBeforeOpeningDatabase()
    {
        var good = Valid();
        var broken = new[]
        {
            good with { BindingId = Guid.Empty },
            good with { SnapshotId = Guid.Empty },
            good with { CredentialIdSha256 = new string('z', 64) },
            good with { CredentialIdSha256 = "plaintext-id" },
            good with { RequestStartedAt = good.ResponseReceivedAt.AddMinutes(1) },
            good with { Positions = new[]
            {
                new DeribitPositionEvidence("BTC-PERPETUAL", "future", "zero", 1m, null)
            }},
            good with { Positions = new[]
            {
                new DeribitPositionEvidence("BTC-PERPETUAL", "future", "buy", 1m, null),
                new DeribitPositionEvidence("BTC-PERPETUAL", "future", "sell", -1m, null)
            }},
            good with { Positions = new[]
            {
                new DeribitPositionEvidence("ETH-CALL", "option", "buy", 1m, 1m)
            }}
        };
        foreach (var item in broken)
            Assert.Throws<InvalidOperationException>(
                () => DeribitRealReadOnlyEvidencePgStore.Validate(item));
    }

    [Theory]
    [InlineData("Host=unexpected;Port=5432;Database=tradeops;Username=writer")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=tradeops;Username=postgres")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=production;Username=writer")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=tradeops;Username=writer;Search Path=public")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=tradeops;Username=writer;Options=-c statement_timeout=500")]
    public void MismatchedApprovedTarget_RejectsWithoutNetwork(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new DeribitRealReadOnlyEvidencePgStore(
                connectionString, "127.0.0.1", 5432, "tradeops", "writer"));
    }

    [Fact]
    public void MatchingApprovedTarget_CreatesNoNetworkSessionOrMigration()
    {
        var store = new DeribitRealReadOnlyEvidencePgStore(
            "Host=127.0.0.1;Port=5432;Database=tradeops;Username=writer",
            "127.0.0.1", 5432, "tradeops", "writer");
        Assert.NotNull(store);
    }

    [Fact]
    public void LegacyFillsAndTradingModel_AreNotReconfigured()
    {
        using var db = new TradeOpsDbContext(
            new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=x")
                .Options);
        Assert.Null(db.Model.FindEntityType(typeof(DeribitPositionSnapshotCapture)));
        Assert.Null(db.Model.FindEntityType(typeof(DeribitPositionEvidence)));
        var fill = db.Model.FindEntityType(typeof(TradeOps.Domain.Entities.Fill));
        Assert.NotNull(fill);
        Assert.Contains(fill!.GetIndexes(),
            i => i.IsUnique && i.Properties.Single().Name == "ExchangeFillId");
    }
}
