namespace TradeOps.DeribitTestnetOrderQualification;

internal static class TestnetOrderEntryPoint
{
    public static Task<int> Main(string[] args)
    {
        // Offline scripted qualification/replay remains in the library; real order entry requires B5.
        Console.WriteLine("{\"Status\":\"TESTNET_ORDER_OMS_GATEWAY_REQUIRED\",\"PlacementAttempts\":0,\"CancellationAttempts\":0,\"TradingReady\":false}");
        return Task.FromResult(1);
    }
}
