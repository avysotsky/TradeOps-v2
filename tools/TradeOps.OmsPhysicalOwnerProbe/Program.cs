return TradeOps.OmsPhysicalOwnerProbe.Probe.Run(args);

namespace TradeOps.OmsPhysicalOwnerProbe
{
    // Test-only OS primitive probe. No DB, credentials, RPCs or exchange access.
    public static class Probe
    {
        public static int Run(string[] args)
        {
            if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 3;
            try
            {
                using var file = new FileStream(args[0],FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                Console.WriteLine("ACQUIRED"); Console.Out.Flush();
                var input=Console.In.ReadLineAsync(); input.Wait(TimeSpan.FromSeconds(30)); return 0;
            }
            catch(IOException) { Console.WriteLine("BUSY"); return 2; }
        }
    }
}
