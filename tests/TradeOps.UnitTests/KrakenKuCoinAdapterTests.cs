using TradeOps.Infrastructure.Exchange.Kraken;
using TradeOps.Infrastructure.Exchange.KuCoin;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class KrakenKuCoinAdapterTests
{
    [Fact]
    public void KrakenSigner_MatchesFixedVector()
    {
        var signature = KrakenFuturesSigner.Sign(
            "currency=USD",
            "1700000000000",
            "/api/v3/accounts",
            "dGVzdC1zZWNyZXQ=");

        Assert.Equal(
            "0t4kV+POvX/BtrpFXBXCGgHrahURwsZcwlJAKUcwVT7gpbo4GcoBv9FdlqctDqPmavkWiBUZoxwVrB+p0tSJHA==",
            signature);
    }

    [Fact]
    public void KrakenSigner_RejectsMissingSecret()
    {
        Assert.Throws<InvalidOperationException>(
            () => KrakenFuturesSigner.Sign(
                string.Empty,
                "1700000000000",
                "/api/v3/accounts",
                string.Empty));
    }

    [Fact]
    public void KuCoinSigner_MatchesFixedVector()
    {
        var signature = KuCoinSigner.Sign(
            "1700000000000",
            "GET",
            "/api/v1/account-overview?currency=USDT",
            string.Empty,
            "test-secret");

        Assert.Equal(
            "8ijguyY0rL91qVc85xTimU/RXruHpmlzm1HAQl216r8=",
            signature);
    }

    [Fact]
    public void KuCoinPassphrase_MatchesFixedVector()
    {
        var encrypted = KuCoinSigner.EncryptPassphrase(
            "test-passphrase",
            "test-secret");

        Assert.Equal(
            "UbgWiL7WdjQOVBl1OLuMgUbTl9VlKFsjFbLedtCDPrY=",
            encrypted);
    }
}
