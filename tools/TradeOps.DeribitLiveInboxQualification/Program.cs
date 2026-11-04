using System.Text.Json;
using TradeOps.DeribitPrivateSessionProbe;

namespace TradeOps.DeribitLiveInboxQualification;

internal static class LiveInboxQualificationEntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        PrivateSessionProbeInput? input = null;
        try
        {
            if (args.Length != 0) throw new InvalidOperationException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            var stdin = Console.OpenStandardInput();
            int count;
            while ((count = await stdin.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + count > 16384) throw new InvalidOperationException();
                buffer.Write(chunk, 0, count);
            }
            input = JsonSerializer.Deserialize<PrivateSessionProbeInput>(buffer.ToArray()) ?? throw new InvalidOperationException();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var result = await LiveInboxQualification.RunAsync(input, http, deadline.Token);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return result.Status == "LIVE_INBOX_FAILED" ? 1 : 0;
        }
        catch
        {
            // Includes cleanup failures: no SQL/row details or private identity may escape.
            Console.WriteLine(JsonSerializer.Serialize(new { Status = "LIVE_INBOX_FAILED", Stage = "GUARD_TRANSPORT_OR_CLEANUP",
                DatabaseUsed = (bool?)null, PrivateEventCaptureQualified = false, TradingReady = false, MutatingCalls = 0 }));
            return 1;
        }
        finally { if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; } }
    }
}
