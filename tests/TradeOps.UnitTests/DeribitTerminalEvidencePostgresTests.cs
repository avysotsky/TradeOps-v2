using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitTerminalEvidencePostgresTests
{
    [Theory]
    [InlineData(0, true)] [InlineData(4, true)] [InlineData(4, false)]
    public async Task RealReaderDerivesQuantityProofWithoutMutatingJournal(decimal filled, bool includeFill)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var capture = new OmsPostgresPrivateEventCapture(db.Source);
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(Report(filled: filled)));
        if (filled > 0 && includeFill) Assert.Equal(OmsPrivateCaptureResult.Inserted, await capture.CaptureAsync(Fill(filled)));
        var reader = new OmsPostgresPrivateRecoveryReader(db.Source); var before = await reader.ReadAsync(Account);
        var p = new DeribitTerminalEvidenceGateTests.Ports { LiveClock = true }; var result = await p.Gate(reader).AssessAsync(p.Request());
        Assert.Equal(includeFill ? 1 : 0, result.RetainedTerminalOrdersValidated); Assert.Equal(0, p.Lookups);
        var after = await reader.ReadAsync(Account); Assert.True(after.IsUsable);
        Assert.Equal(before.AcceptedFactCount, after.AcceptedFactCount); Assert.Equal(before.Observations.Count, after.Observations.Count);
        DeribitTerminalEvidenceGateTests.GlobalGates(result);
    }
}
