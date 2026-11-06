using System.Text.Json;
using TradeOps.DeribitTestnetOrderQualification;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class MainnetEthEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        TestnetOrderInput? input = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            if (args.SequenceEqual(new[] { "--owner-bounded-oms" }))
                return await BoundedOmsScenario.RunFromStdinAsync(deadline.Token);
            if (args.SequenceEqual(new[] { "--assess-account-recovery" }))
                return await AccountRecoveryQualification.RunAsync(deadline.Token);
            if (args.SequenceEqual(new[] { "--read-eth-future-snapshot" }))
                return await AccountSnapshotQualification.RunAsync(deadline.Token, ethInverseFutures: true);
            if (args.SequenceEqual(new[] { "--read-account-snapshot" }))
                return await AccountSnapshotQualification.RunAsync(deadline.Token);
            if (args.SequenceEqual(new[] { "--assess-recovery" }))
                return await KnownOrderQualification.RunAsync(deadline.Token, assess: true);
            if (args.SequenceEqual(new[] { "--read-known-orders" }))
                return await KnownOrderQualification.RunAsync(deadline.Token);
            if (args.SequenceEqual(new[] { "--collect-recovery" }))
                return await RecoveryCollection.RunAsync(deadline.Token);
            // Explicit read-side modes never enter the order runner or migrating store opener.
            if (args.SequenceEqual(new[] { "--read-snapshot" }) || args.SequenceEqual(new[] { "--provision-reader" }))
                return await RecoveryStore.RunAsync(args[0], deadline.Token);
            if (args.SequenceEqual(new[] { "--store-check" }))
            {
                await using var database = await QualificationDatabase.OpenAsync(true, deadline.Token);
                Console.WriteLine("{\"Status\":\"MAINNET_STORE_READY\",\"PersistentStore\":true,\"PlacementAttempts\":0,\"CancellationAttempts\":0}");
                return 0;
            }
            if (args.Length != 0) throw new InvalidOperationException();
            // An old standalone executable must not bypass mandatory B5 risk/physical ownership.
            Console.WriteLine("{\"Status\":\"MAINNET_ETH_OMS_GATEWAY_REQUIRED\",\"PlacementAttempts\":0,\"CancellationAttempts\":0,\"TradingReady\":false}");
            return 1;
        }
        catch
        {
            Console.WriteLine("{\"Status\":\"MAINNET_ETH_FAILED_UNKNOWN\",\"PlacementAttempts\":null,\"CancellationAttempts\":null,\"ExchangeOrderResolved\":false,\"TradingReady\":false,\"JournalRetained\":null}");
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
