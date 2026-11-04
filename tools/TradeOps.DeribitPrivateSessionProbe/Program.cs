using System.Text.Json;
namespace TradeOps.DeribitPrivateSessionProbe;

internal static class PrivateSessionProbeEntryPoint
{
public static async Task<int> Main(string[] args)
{
PrivateSessionProbeInput? input = null;
try
{
    if (args.Length != 0) throw new InvalidOperationException();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    using var bytes = new MemoryStream();
    var chunk = new byte[4096];
    var stdin = Console.OpenStandardInput();
    int count;
    while ((count = await stdin.ReadAsync(chunk, deadline.Token)) != 0)
    {
        if (bytes.Length + count > 16384) throw new InvalidOperationException();
        bytes.Write(chunk, 0, count);
    }
    input = JsonSerializer.Deserialize<PrivateSessionProbeInput>(bytes.ToArray()) ?? throw new InvalidOperationException();
    input.Validate();
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    var result = await PrivateSessionProbe.RunAsync(input, http, ct: deadline.Token);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return result.Status == "LIVE_SESSION_PASS_NO_DURABLE_CAPTURE" ? 0 : 1;
}
catch
{
    Console.WriteLine(JsonSerializer.Serialize(new PrivateSessionProbeResult("LIVE_SESSION_FAIL", "INPUT_OR_PUBLIC_PREFLIGHT", null, 0, 0)));
    return 1;
}
finally
{
    if (input is not null) { input.ClientId = ""; input.ClientSecret = ""; }
}
}
}
