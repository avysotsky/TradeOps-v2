namespace TradeOps.PublicResearchDemo;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await PublicResearchDemoCli.RunAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"PUBLIC DATA DEMO: FAIL ({exception.GetType().Name}: {exception.Message})");
            return 1;
        }
    }
}
