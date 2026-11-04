using TradeOps.Connectors.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitReadOnlyScopeTests
{
    [Theory]
    [InlineData("connection trade:read account:read name:opaque")]
    [InlineData("connection trade:read account:read")]
    [InlineData("connection trade:read account:read wallet:none block_trade:none block_rfq:none")]
    [InlineData("connection mainaccount trade:read account:read name:opaque")]
    public void NormalizedOrExplicitDeniedScopes_AreAcceptedOnlyWithRequiredReadGrants(string scope) =>
        Assert.True(DeribitPrivateReadOnlyScope.IsValidGrant(scope));

    [Theory]
    [InlineData("connection account:read name:opaque")]
    [InlineData("connection trade:read name:opaque")]
    [InlineData("session:x trade:read account:read")]
    [InlineData("connection trade:read_write account:read")]
    [InlineData("connection trade:read account:read wallet:read")]
    [InlineData("connection trade:read account:read block_trade:read")]
    [InlineData("connection trade:read account:read block_rfq:read_write")]
    [InlineData("connection trade:read account:read name:")]
    [InlineData("connection trade:read account:read name:a name:b")]
    [InlineData("connection trade:read trade:read account:read")]
    [InlineData("connection trade:read account:read unknown:read")]
    [InlineData("connection mainaccount subaccount trade:read account:read")]
    public void MissingElevatedDuplicateOrUnknownGrants_AreRejected(string scope) =>
        Assert.False(DeribitPrivateReadOnlyScope.IsValidGrant(scope));
}
