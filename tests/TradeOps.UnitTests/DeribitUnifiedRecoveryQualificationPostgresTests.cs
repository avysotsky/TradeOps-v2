using TradeOps.Application.Oms;
using TradeOps.DeribitMainnetEthQualification;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitUnifiedRecoveryQualificationPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnifiedHostReadsRealRetainedJournalWithoutWritesOrExpiredOrderLookup(bool incident)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var capture = new OmsPostgresPrivateEventCapture(db.Source);
        var scope = RecoveryStore.Scope(123, "main"); var seed = Report("cancelled") with {
            Scope = scope, Facts = Report("cancelled").Facts with { VenueTimestampUtc = DateTimeOffset.UtcNow.AddHours(-1) },
            Observation = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-50), OmsPrivateCaptureSource.WebSocket, "orders") };
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(seed));
        if (incident) Assert.Equal(OmsPrivateCaptureResult.Collision, await capture.CaptureAsync(seed with {
            Facts = seed.Facts with { NativeAmount = seed.Facts.NativeAmount+1 } }));
        async Task<string?> Audit()
        {
            await using var command = db.Source.CreateCommand("SELECT json_build_array((SELECT count(*) FROM oms_inbox),(SELECT count(*) FROM oms_private_capture_observations),(SELECT count(*) FROM oms_integrity_incidents))::text");
            return await command.ExecuteScalarAsync() as string;
        }
        var before = await Audit(); using var stdin = DeribitUnifiedRecoveryQualificationTests.Stdin();
        using var venue = new DeribitUnifiedRecoveryQualificationTests.Venue(); using var http = new HttpClient(venue);
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, (actual, _) => {
            Assert.Equal(scope, actual); return Task.FromResult(new AccountRecoveryQualification.ReaderLease(new OmsPostgresPrivateRecoveryReader(db.Source)));
        }, CancellationToken.None);
        Assert.Equal(before, await Audit()); Assert.True(result.AssessmentCompleted); Assert.True(result.SelectOnly);
        Assert.Equal(0, venue.KnownLookups); Assert.Equal(0, result.AddressedOrders);
        if (incident)
        {
            Assert.False(result.ScopedAccountObservationsValidated); Assert.Contains("SnapshotUnusable", result.Blockers);
            Assert.Single(venue.Methods); Assert.Contains("ReadSkipped", result.AccountProblems);
        }
        else
        {
            Assert.Equal(1, result.RetainedFacts); Assert.Equal(1, result.KnownOrders); Assert.Equal(1, result.RetentionExpiredOrders);
            Assert.True(result.ScopedAccountObservationsValidated); Assert.Equal(1, result.AcceptedPositionRows);
            Assert.Equal(1, result.RetainedTerminalOrdersValidated);
            Assert.DoesNotContain("KnownOrderRetentionExpired", result.Blockers); Assert.Equal(13, venue.Methods.Count);
        }
        DeribitUnifiedRecoveryQualificationTests.AssertSafe(result);
    }
}
