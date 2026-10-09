using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeOps.ProviderTranscriptResearchDemo;

public enum ProviderTranscriptResearchProvider
{
    OpenAi,
    Groq
}

public sealed record ProviderTranscriptResearchDemoOptions(
    string DocFlowRoot,
    string Model,
    string PythonExecutable,
    string? WorkDirectory,
    ProviderTranscriptResearchProvider Provider =
        ProviderTranscriptResearchProvider.OpenAi,
    string? DotNetExecutable = null,
    string? RebalanceInputPath = null,
    string? RiskPreviewInputPath = null,
    bool ExecutionDryRun = false)
{
    public const string DefaultPythonExecutable =
        "python";

    public static ProviderTranscriptResearchDemoOptions Parse(
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(
            args);

        string? docFlowRoot =
            null;
        string? model =
            null;
        string python =
            DefaultPythonExecutable;
        string? workDirectory =
            null;
        string? dotNetExecutable =
            null;
        string? rebalanceInputPath =
            null;
        string? riskPreviewInputPath =
            null;
        var executionDryRun =
            false;
        var provider =
            ProviderTranscriptResearchProvider
                .OpenAi;

        for (var index = 0;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--docflow-root":
                    docFlowRoot =
                        RequireValue(
                            args,
                            ref index,
                            "--docflow-root");
                    break;

                case "--provider":
                    provider =
                        ParseProvider(
                            RequireValue(
                                args,
                                ref index,
                                "--provider"));
                    break;

                case "--model":
                    model =
                        RequireValue(
                            args,
                            ref index,
                            "--model");
                    break;

                case "--python":
                    python =
                        RequireValue(
                            args,
                            ref index,
                            "--python");
                    break;

                case "--work-dir":
                    workDirectory =
                        RequireValue(
                            args,
                            ref index,
                            "--work-dir");
                    break;

                case "--dotnet":
                    dotNetExecutable =
                        RequireValue(
                            args,
                            ref index,
                            "--dotnet");
                    break;

                case "--rebalance-input":
                    rebalanceInputPath =
                        RequireValue(
                            args,
                            ref index,
                            "--rebalance-input");
                    break;

                case "--risk-preview-input":
                    riskPreviewInputPath =
                        RequireValue(
                            args,
                            ref index,
                            "--risk-preview-input");
                    break;

                case "--execution-dry-run":
                    executionDryRun =
                        true;
                    break;

                default:
                    throw new ArgumentException(
                        "Unknown argument '" + args[index] + "'. Supported: --provider openai|groq, --docflow-root <path>, --model <model>, --python <executable>, --work-dir <path>, --dotnet <path-to-dotnet-host>, --rebalance-input <path>, --risk-preview-input <path>, --execution-dry-run.");
            }
        }

        if (string.IsNullOrWhiteSpace(
                docFlowRoot))
        {
            throw new ArgumentException(
                "--docflow-root <path> is required.");
        }

        if (string.IsNullOrWhiteSpace(
                model))
        {
            throw new ArgumentException(
                "--model <explicit-model> is required; no default model is configured.");
        }

        if (executionDryRun
            && string.IsNullOrWhiteSpace(
                riskPreviewInputPath))
        {
            throw new ArgumentException(
                "--execution-dry-run requires --risk-preview-input.");
        }

        if (!string.IsNullOrWhiteSpace(
                riskPreviewInputPath)
            && string.IsNullOrWhiteSpace(
                rebalanceInputPath))
        {
            throw new ArgumentException(
                "--risk-preview-input requires --rebalance-input.");
        }

        return new ProviderTranscriptResearchDemoOptions(
            docFlowRoot,
            model,
            python,
            workDirectory,
            provider,
            dotNetExecutable,
            rebalanceInputPath,
            riskPreviewInputPath,
            executionDryRun);
    }

    private static ProviderTranscriptResearchProvider ParseProvider(
        string value) =>
        value switch
        {
            "openai" =>
                ProviderTranscriptResearchProvider.OpenAi,
            "groq" =>
                ProviderTranscriptResearchProvider.Groq,
            _ =>
                throw new ArgumentException(
                    "--provider must be one of: openai, groq.")
        };

    public string ResolveWorkDirectory(
        string tradeOpsRoot)
    {
        if (!string.IsNullOrWhiteSpace(
                WorkDirectory))
        {
            return Path.GetFullPath(
                WorkDirectory);
        }

        return Path.Combine(
            Path.GetFullPath(
                tradeOpsRoot),
            ".tradeops",
            "provider-transcript-demo");
    }

    private static string RequireValue(
        string[] args,
        ref int index,
        string option)
    {
        if (index + 1 >=
            args.Length
            || string.IsNullOrWhiteSpace(
                args[index + 1]))
        {
            throw new ArgumentException(
                option + " requires a value.");
        }

        index++;
        return args[index];
    }
}

public sealed record ChildProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record ChildProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public static implicit operator ChildProcessResult(
        int exitCode) =>
        new(
            exitCode,
            string.Empty,
            string.Empty);
}

public interface IChildProcessRunner
{
    ChildProcessResult Run(
        ChildProcessInvocation invocation);
}

public sealed class SystemChildProcessRunner :
    IChildProcessRunner
{
    public const int MaxCapturedCharactersPerStream =
        4096;

    public const string TruncationMarker =
        "\n...[truncated]";

    public ChildProcessResult Run(
        ChildProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(
            invocation);

        using var process =
            new Process
            {
                StartInfo =
                    CreateStartInfo(
                        invocation)
            };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Child process could not be started.");
        }

        var standardOutputTask =
            CaptureBoundedAsync(
                process.StandardOutput);
        var standardErrorTask =
            CaptureBoundedAsync(
                process.StandardError);
        var exitTask =
            process.WaitForExitAsync();

        Task.WhenAll(
                standardOutputTask,
                standardErrorTask,
                exitTask)
            .GetAwaiter()
            .GetResult();

        return new ChildProcessResult(
            process.ExitCode,
            standardOutputTask.Result,
            standardErrorTask.Result);
    }

    public static ProcessStartInfo CreateStartInfo(
        ChildProcessInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(
            invocation);

        if (string.IsNullOrWhiteSpace(
                invocation.FileName))
        {
            throw new ArgumentException(
                "Process file name is required.",
                nameof(invocation));
        }

        if (string.IsNullOrWhiteSpace(
                invocation.WorkingDirectory))
        {
            throw new ArgumentException(
                "Process working directory is required.",
                nameof(invocation));
        }

        var startInfo =
            new ProcessStartInfo
            {
                FileName =
                    invocation.FileName,
                WorkingDirectory =
                    invocation.WorkingDirectory,
                UseShellExecute =
                    false,
                RedirectStandardOutput =
                    true,
                RedirectStandardError =
                    true,
                CreateNoWindow =
                    true
            };

        foreach (var argument in
                 invocation.Arguments)
        {
            startInfo.ArgumentList.Add(
                argument);
        }

        return startInfo;
    }

    public static string BoundForDiagnostics(
        string value)
    {
        ArgumentNullException.ThrowIfNull(
            value);

        if (value.Length <=
            MaxCapturedCharactersPerStream)
        {
            return value;
        }

        var prefixLength =
            MaxCapturedCharactersPerStream -
            TruncationMarker.Length;

        return value[..prefixLength] +
               TruncationMarker;
    }

    private static async Task<string> CaptureBoundedAsync(
        TextReader reader)
    {
        var buffer =
            new char[1024];
        var captured =
            new StringBuilder(
                MaxCapturedCharactersPerStream);
        var truncated =
            false;

        while (true)
        {
            var read =
                await reader.ReadAsync(
                    buffer.AsMemory(
                        0,
                        buffer.Length));

            if (read == 0)
            {
                break;
            }

            var remaining =
                MaxCapturedCharactersPerStream -
                captured.Length;

            if (remaining > 0)
            {
                captured.Append(
                    buffer,
                    0,
                    Math.Min(
                        read,
                        remaining));
            }

            if (read > remaining)
            {
                truncated =
                    true;
            }
        }

        if (!truncated)
        {
            return captured.ToString();
        }

        return BoundForDiagnostics(
            captured.ToString() +
            TruncationMarker);
    }
}

public interface IEnvironmentReader
{
    string? Get(
        string name);
}

public sealed class SystemEnvironmentReader :
    IEnvironmentReader
{
    public string? Get(
        string name) =>
        Environment.GetEnvironmentVariable(
            name);
}

public sealed record DotNetHostSelection(
    string Executable,
    string Source);

public static class DotNetHostResolver
{
    public const string DotNetHostPathEnvironmentVariable =
        "DOTNET_HOST_PATH";

    public const string DotNetRootEnvironmentVariable =
        "DOTNET_ROOT";

    public const string DotNetMsBuildSdkResolverCliDirEnvironmentVariable =
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR";

    public static string PlatformExecutableName =>
        OperatingSystem.IsWindows()
            ? "dotnet.exe"
            : "dotnet";

    public static DotNetHostSelection Resolve(
        string? explicitHost,
        IEnvironmentReader environmentReader)
    {
        ArgumentNullException.ThrowIfNull(
            environmentReader);

        if (!string.IsNullOrWhiteSpace(
                explicitHost))
        {
            return new DotNetHostSelection(
                RequireExistingHost(
                    explicitHost,
                    "explicit --dotnet"),
                "explicit --dotnet");
        }

        var hostPath =
            TryExistingHost(
                environmentReader.Get(
                    DotNetHostPathEnvironmentVariable));

        if (hostPath is not null)
        {
            return new DotNetHostSelection(
                hostPath,
                DotNetHostPathEnvironmentVariable);
        }

        var dotNetRoot =
            environmentReader.Get(
                DotNetRootEnvironmentVariable);
        var rootHost =
            string.IsNullOrWhiteSpace(
                dotNetRoot)
                ? null
                : TryExistingHost(
                    Path.Combine(
                        dotNetRoot,
                        PlatformExecutableName));

        if (rootHost is not null)
        {
            return new DotNetHostSelection(
                rootHost,
                DotNetRootEnvironmentVariable);
        }

        var resolverCliDirectory =
            environmentReader.Get(
                DotNetMsBuildSdkResolverCliDirEnvironmentVariable);
        var resolverHost =
            string.IsNullOrWhiteSpace(
                resolverCliDirectory)
                ? null
                : TryExistingHost(
                    Path.Combine(
                        resolverCliDirectory,
                        PlatformExecutableName));

        if (resolverHost is not null)
        {
            return new DotNetHostSelection(
                resolverHost,
                DotNetMsBuildSdkResolverCliDirEnvironmentVariable);
        }

        return new DotNetHostSelection(
            PlatformExecutableName,
            "PATH fallback");
    }

    private static string RequireExistingHost(
        string path,
        string source)
    {
        var resolved =
            TryExistingHost(
                path);

        if (resolved is null)
        {
            throw new FileNotFoundException(
                "The " +
                source +
                " dotnet host was not found.",
                path);
        }

        return resolved;
    }

    private static string? TryExistingHost(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            return null;
        }

        try
        {
            var fullPath =
                Path.GetFullPath(
                    path);

            return File.Exists(
                    fullPath)
                ? fullPath
                : null;
        }
        catch (Exception exception)
            when (exception is
                  ArgumentException or
                  NotSupportedException or
                  PathTooLongException)
        {
            return null;
        }
    }
}

public sealed record DotNetSdkPolicy(
    string RequestedVersion,
    string RollForward,
    bool AllowPrerelease)
{
    public static DotNetSdkPolicy Load(
        string tradeOpsRoot)
    {
        var globalJsonPath =
            Path.Combine(
                Path.GetFullPath(
                    tradeOpsRoot),
                "global.json");

        if (!File.Exists(
                globalJsonPath))
        {
            throw new FileNotFoundException(
                "TradeOps global.json was not found.",
                globalJsonPath);
        }

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(
                    globalJsonPath));

        if (!document.RootElement.TryGetProperty(
                "sdk",
                out var sdk)
            || !sdk.TryGetProperty(
                "version",
                out var versionElement)
            || versionElement.ValueKind !=
            JsonValueKind.String
            || string.IsNullOrWhiteSpace(
                versionElement.GetString()))
        {
            throw new InvalidDataException(
                "TradeOps global.json must define sdk.version.");
        }

        var version =
            versionElement.GetString()!;
        var rollForward =
            sdk.TryGetProperty(
                    "rollForward",
                    out var rollForwardElement)
                && rollForwardElement.ValueKind ==
                JsonValueKind.String
                ? rollForwardElement.GetString()
                : null;
        var allowPrerelease =
            sdk.TryGetProperty(
                    "allowPrerelease",
                    out var allowPrereleaseElement)
                && allowPrereleaseElement.ValueKind is
                    JsonValueKind.True or
                    JsonValueKind.False
                ? allowPrereleaseElement.GetBoolean()
                : true;

        if (rollForward is not
            "latestPatch")
        {
            throw new InvalidDataException(
                "TradeOps global.json sdk.rollForward must be latestPatch for this narrow preflight.");
        }

        if (!Version.TryParse(
                version,
                out var parsedVersion)
            || parsedVersion.Build < 0)
        {
            throw new InvalidDataException(
                "TradeOps global.json sdk.version is not a supported three-part SDK version.");
        }

        return new DotNetSdkPolicy(
            version,
            rollForward,
            allowPrerelease);
    }

    public void ValidateResolvedVersion(
        string resolvedVersion)
    {
        if (string.IsNullOrWhiteSpace(
                resolvedVersion))
        {
            throw new InvalidOperationException(
                "dotnet --version returned no SDK version.");
        }

        var trimmed =
            resolvedVersion.Trim();
        var prereleaseSeparator =
            trimmed.IndexOf(
                "-",
                StringComparison.Ordinal);
        var numericVersion =
            prereleaseSeparator >= 0
                ? trimmed[..prereleaseSeparator]
                : trimmed;

        if (prereleaseSeparator >= 0
            && !AllowPrerelease)
        {
            throw new InvalidOperationException(
                "Selected dotnet host resolved prerelease SDK " +
                trimmed +
                ", but global.json disallows prerelease SDKs.");
        }

        if (!Version.TryParse(
                RequestedVersion,
                out var requested)
            || !Version.TryParse(
                numericVersion,
                out var resolved)
            || requested.Build < 0
            || resolved.Build < 0)
        {
            throw new InvalidOperationException(
                "Selected dotnet host returned an unrecognized SDK version.");
        }

        var requestedFeatureBand =
            requested.Build /
            100;
        var resolvedFeatureBand =
            resolved.Build /
            100;

        if (resolved.Major !=
            requested.Major
            || resolved.Minor !=
            requested.Minor
            || resolvedFeatureBand !=
            requestedFeatureBand
            || resolved.Build <
            requested.Build)
        {
            throw new InvalidOperationException(
                "Selected dotnet SDK " +
                trimmed +
                " is incompatible with global.json " +
                RequestedVersion +
                " + " +
                RollForward +
                ".");
        }
    }
}

public static class ChildProcessDiagnostics
{
    private static readonly (string Marker, string Label)[]
        SafeDotNetMarkers =
        {
            (".NET SDK", ".NET SDK"),
            ("global.json", "global.json"),
            ("hostfxr", "hostfxr"),
            ("hostpolicy", "hostpolicy"),
            ("framework", ".NET framework/runtime"),
            ("MSBuild", "MSBuild"),
            ("architecture", "architecture"),
            ("The command could not be loaded", "command-load failure"),
            ("Failed to resolve", "resolution failure")
        };

    public static string Redact(
        string value,
        string? selectedSecret)
    {
        var redacted =
            value;

        if (!string.IsNullOrEmpty(
                selectedSecret))
        {
            redacted =
                redacted.Replace(
                    selectedSecret,
                    "[REDACTED]",
                    StringComparison.Ordinal);
        }

        redacted =
            Regex.Replace(
                redacted,
                @"(?i)\b(OPENAI_API_KEY|GROQ_API_KEY)\s*=\s*[^\s\r\n]+",
                "$1=[REDACTED]");
        redacted =
            Regex.Replace(
                redacted,
                @"(?i)\bAuthorization\s*:\s*Bearer\s+[^\s\r\n]+",
                "Authorization: Bearer [REDACTED]");
        redacted =
            Regex.Replace(
                redacted,
                @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
                "Bearer [REDACTED]");

        return SystemChildProcessRunner
            .BoundForDiagnostics(
                redacted);
    }

    public static string ExtractSafeDotNetDiagnostics(
        ChildProcessResult result,
        string? selectedSecret)
    {
        ArgumentNullException.ThrowIfNull(
            result);

        var combined =
            result.StandardError +
            "\n" +
            result.StandardOutput;

        var labels =
            SafeDotNetMarkers
                .Where(
                    item =>
                        combined.Contains(
                            item.Marker,
                            StringComparison.OrdinalIgnoreCase))
                .Select(
                    item =>
                        item.Label)
                .Distinct(
                    StringComparer.Ordinal)
                .Take(
                    12)
                .ToArray();

        if (labels.Length == 0)
        {
            return string.Empty;
        }

        // Never quote child output here. Fixed labels make diagnostics useful
        // without allowing transcript/provider payload text to cross the boundary.
        return string.Join(
            ", ",
            labels);
    }
}

public static class TradeOpsRepositoryLocator
{
    public static string Resolve()
    {
        var directory =
            new DirectoryInfo(
                AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "TradeOps.sln")))
            {
                return directory.FullName;
            }

            directory =
                directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "TradeOps repository root could not be resolved from the executable location.");
    }
}

public sealed record ResolvedDotNetHost(
    string Executable,
    string Version,
    string Source);

public sealed class ProviderTranscriptResearchDemoHarness
{
    public const string OpenAiApiKeyEnvironmentVariable =
        "OPENAI_API_KEY";

    public const string GroqApiKeyEnvironmentVariable =
        "GROQ_API_KEY";

    public const string ApiKeyEnvironmentVariable =
        OpenAiApiKeyEnvironmentVariable;

    public const string PriorDocumentName =
        "Synthetic Example Company Q1 2026 earnings call";

    public const string CurrentDocumentName =
        "Synthetic Example Company Q2 2026 earnings call";

    private readonly IChildProcessRunner _processRunner;
    private readonly IEnvironmentReader _environmentReader;

    public ProviderTranscriptResearchDemoHarness(
        IChildProcessRunner processRunner,
        IEnvironmentReader environmentReader)
    {
        _processRunner =
            processRunner
            ?? throw new ArgumentNullException(
                nameof(processRunner));
        _environmentReader =
            environmentReader
            ?? throw new ArgumentNullException(
                nameof(environmentReader));
    }

    public int Run(
        ProviderTranscriptResearchDemoOptions options,
        string tradeOpsRoot,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(
            options);

        output ??=
            Console.Out;
        error ??=
            Console.Error;

        string? selectedCredential =
            null;

        try
        {
            var apiKeyEnvironmentVariable =
                GetApiKeyEnvironmentVariable(
                    options.Provider);
            selectedCredential =
                _environmentReader.Get(
                    apiKeyEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(
                    selectedCredential))
            {
                throw new InvalidOperationException(
                    apiKeyEnvironmentVariable +
                    " is required in the environment.");
            }

            return RunCore(
                options,
                tradeOpsRoot,
                selectedCredential,
                output,
                error);
        }
        catch (Exception exception)
        {
            error.WriteLine(
                "PROVIDER TRANSCRIPT RESEARCH DEMO: FAIL (" +
                exception.GetType().Name +
                ": " +
                ChildProcessDiagnostics.Redact(
                    exception.Message,
                    selectedCredential) +
                ")");

            return 1;
        }
    }

    private int RunCore(
        ProviderTranscriptResearchDemoOptions options,
        string tradeOpsRoot,
        string selectedCredential,
        TextWriter output,
        TextWriter error)
    {
        var resolvedTradeOpsRoot =
            Path.GetFullPath(
                tradeOpsRoot);
        var docFlowRoot =
            Path.GetFullPath(
                options.DocFlowRoot);
        var docFlowWorkerDirectory =
            Path.Combine(
                docFlowRoot,
                "src",
                "DocFlow.Extraction.Worker");
        var docFlowEntryPoint =
            Path.Combine(
                docFlowWorkerDirectory,
                "text_artifact_main.py");
        var workDirectory =
            options.ResolveWorkDirectory(
                resolvedTradeOpsRoot);
        var providerName =
            GetProviderName(
                options.Provider);
        var rebalanceInput =
            ResolveRebalanceInput(
                options.RebalanceInputPath,
                resolvedTradeOpsRoot);
        var riskPreviewInput =
            ResolveRiskPreviewInput(
                options.RiskPreviewInputPath,
                resolvedTradeOpsRoot);

        if (options.ExecutionDryRun
            && riskPreviewInput is null)
        {
            throw new InvalidOperationException(
                "--execution-dry-run requires --risk-preview-input.");
        }

        ValidateDocFlow(
            docFlowRoot,
            docFlowEntryPoint);
        ValidateWorkDirectory(
            resolvedTradeOpsRoot,
            workDirectory);

        var priorRaw =
            RequireFile(
                Path.Combine(
                    resolvedTradeOpsRoot,
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "prior-raw.json"),
                "Prior raw transcript");
        var currentRaw =
            RequireFile(
                Path.Combine(
                    resolvedTradeOpsRoot,
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "current-raw.json"),
                "Current raw transcript");
        var schemaRequest =
            RequireFile(
                Path.Combine(
                    resolvedTradeOpsRoot,
                    "schemas",
                    "research",
                    "earnings-transcript-facts-v1.schema-request.json"),
                "Earnings extraction schema request");
        var policy =
            RequireFile(
                Path.Combine(
                    resolvedTradeOpsRoot,
                    "samples",
                    "research",
                    "transcript-research",
                    "policy.json"),
                "Transcript research policy");
        var consumerManifest =
            RequireFile(
                Path.Combine(
                    resolvedTradeOpsRoot,
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "consumer-manifest.json"),
                "Provider demo consumer manifest");

        var dotNetHost =
            PreflightDotNetHost(
                options.DotNetExecutable,
                resolvedTradeOpsRoot,
                selectedCredential);

        Directory.CreateDirectory(
            workDirectory);

        var priorNormalized =
            Path.Combine(
                workDirectory,
                "prior-normalized.json");
        var priorStructured =
            Path.Combine(
                workDirectory,
                "prior-structured.json");
        var currentNormalized =
            Path.Combine(
                workDirectory,
                "current-normalized.json");
        var currentStructured =
            Path.Combine(
                workDirectory,
                "current-structured.json");
        var runtimeManifest =
            Path.Combine(
                workDirectory,
                "manifest.json");
        var resultJson =
            Path.Combine(
                workDirectory,
                options.ExecutionDryRun
                    ? "transcript-research-execution-dry-run-result.json"
                    : riskPreviewInput is not null
                        ? "transcript-research-risk-preview-result.json"
                        : rebalanceInput is null
                            ? "transcript-research-result.json"
                            : "transcript-research-rebalance-result.json");

        output.WriteLine(
            "Stage: initialize");
        output.WriteLine(
            "Provider: " +
            providerName);
        output.WriteLine(
            "Model: " +
            options.Model);
        output.WriteLine(
            "DocFlow root: " +
            docFlowRoot);
        output.WriteLine(
            "Work directory: " +
            workDirectory);
        output.WriteLine(
            "Dotnet host: " +
            dotNetHost.Executable);
        output.WriteLine(
            "Dotnet SDK: " +
            dotNetHost.Version);

        var priorResult =
            RunStage(
                output,
                "DocFlow prior",
                BuildDocFlowInvocation(
                    options.PythonExecutable,
                    docFlowWorkerDirectory,
                    providerName,
                    priorRaw,
                    schemaRequest,
                    options.Model,
                    priorNormalized,
                    priorStructured,
                    PriorDocumentName));

        if (priorResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Prior DocFlow stage failed with exit code " +
                priorResult.ExitCode +
                ". Provider child output is suppressed.");
        }

        var currentResult =
            RunStage(
                output,
                "DocFlow current",
                BuildDocFlowInvocation(
                    options.PythonExecutable,
                    docFlowWorkerDirectory,
                    providerName,
                    currentRaw,
                    schemaRequest,
                    options.Model,
                    currentNormalized,
                    currentStructured,
                    CurrentDocumentName));

        if (currentResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Current DocFlow stage failed with exit code " +
                currentResult.ExitCode +
                ". Provider child output is suppressed.");
        }

        RequireGeneratedFile(
            priorNormalized,
            "Prior normalized artifact");
        RequireGeneratedFile(
            priorStructured,
            "Prior structured artifact");
        RequireGeneratedFile(
            currentNormalized,
            "Current normalized artifact");
        RequireGeneratedFile(
            currentStructured,
            "Current structured artifact");

        File.Copy(
            consumerManifest,
            runtimeManifest,
            overwrite:
                true);

        RequireGeneratedFile(
            runtimeManifest,
            "Runtime consumer manifest");

        var consumerArguments =
            new List<string>
            {
                "run",
                "--project",
                "tools/TradeOps.TranscriptResearchDemo",
                "--",
                "--manifest",
                runtimeManifest,
                "--policy",
                policy
            };

        if (rebalanceInput is not null)
        {
            consumerArguments.Add(
                "--rebalance-input");
            consumerArguments.Add(
                rebalanceInput);
        }

        if (riskPreviewInput is not null)
        {
            consumerArguments.Add(
                "--risk-preview-input");
            consumerArguments.Add(
                riskPreviewInput);
        }

        if (options.ExecutionDryRun)
        {
            consumerArguments.Add(
                "--execution-dry-run");
        }

        consumerArguments.Add(
            "--json");
        consumerArguments.Add(
            resultJson);

        var consumerStage =
            options.ExecutionDryRun
                ? "TradeOps VS-17"
                : riskPreviewInput is not null
                    ? "TradeOps VS-16"
                    : rebalanceInput is null
                        ? "TradeOps VS-08"
                        : "TradeOps VS-13";
        var consumerResult =
            RunStage(
                output,
                consumerStage,
                new ChildProcessInvocation(
                    dotNetHost.Executable,
                    consumerArguments,
                    resolvedTradeOpsRoot));

        if (consumerResult.ExitCode != 0)
        {
            WriteSafeDotNetDiagnostics(
                error,
                consumerResult,
                selectedCredential);

            throw new InvalidOperationException(
                consumerStage +
                " stage failed with exit code " +
                consumerResult.ExitCode +
                " using dotnet host '" +
                dotNetHost.Executable +
                "' (SDK " +
                dotNetHost.Version +
                ").");
        }

        if (!File.Exists(
                resultJson)
            || new FileInfo(
                    resultJson).Length == 0)
        {
            throw new InvalidDataException(
                consumerStage +
                " result artifact is missing or empty.");
        }

        output.WriteLine(
            "PROVIDER TRANSCRIPT RESEARCH DEMO: PASS");
        output.WriteLine(
            "Result JSON: " +
            resultJson);

        return 0;
    }

    private ResolvedDotNetHost PreflightDotNetHost(
        string? explicitHost,
        string tradeOpsRoot,
        string selectedCredential)
    {
        var selection =
            DotNetHostResolver.Resolve(
                explicitHost,
                _environmentReader);
        var policy =
            DotNetSdkPolicy.Load(
                tradeOpsRoot);
        ChildProcessResult result;

        try
        {
            result =
                _processRunner.Run(
                    new ChildProcessInvocation(
                        selection.Executable,
                        new[]
                        {
                            "--version"
                        },
                        tradeOpsRoot));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "dotnet preflight could not start host '" +
                selection.Executable +
                "' selected from " +
                selection.Source +
                ": " +
                ChildProcessDiagnostics.Redact(
                    exception.Message,
                    selectedCredential),
                exception);
        }

        if (result.ExitCode != 0)
        {
            var safeDiagnostics =
                ChildProcessDiagnostics
                    .ExtractSafeDotNetDiagnostics(
                        result,
                        selectedCredential);

            throw new InvalidOperationException(
                "dotnet preflight failed for host '" +
                selection.Executable +
                "' selected from " +
                selection.Source +
                " with exit code " +
                result.ExitCode +
                (string.IsNullOrWhiteSpace(
                    safeDiagnostics)
                    ? "."
                    : ". Safe diagnostics: " +
                      safeDiagnostics));
        }

        var version =
            ParseDotNetVersion(
                result.StandardOutput);
        policy.ValidateResolvedVersion(
            version);

        return new ResolvedDotNetHost(
            selection.Executable,
            version,
            selection.Source);
    }

    private static string ParseDotNetVersion(
        string standardOutput)
    {
        var lines =
            standardOutput
                .Split(
                    new[]
                    {
                        '\r',
                        '\n'
                    },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(
                    line =>
                        line.Trim())
                .Where(
                    line =>
                        line.Length > 0)
                .ToArray();

        if (lines.Length != 1
            || !Regex.IsMatch(
                lines[0],
                @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$"))
        {
            throw new InvalidOperationException(
                "dotnet --version did not return one recognizable SDK version line.");
        }

        return lines[0];
    }

    private static void WriteSafeDotNetDiagnostics(
        TextWriter error,
        ChildProcessResult result,
        string selectedCredential)
    {
        var diagnostics =
            ChildProcessDiagnostics
                .ExtractSafeDotNetDiagnostics(
                    result,
                    selectedCredential);

        if (!string.IsNullOrWhiteSpace(
                diagnostics))
        {
            error.WriteLine(
                "Safe dotnet diagnostics: " +
                diagnostics);
        }
    }

    private ChildProcessResult RunStage(
        TextWriter output,
        string stage,
        ChildProcessInvocation invocation)
    {
        output.WriteLine(
            "Stage: " +
            stage);

        var result =
            _processRunner.Run(
                invocation);

        output.WriteLine(
            "Exit status: " +
            result.ExitCode);

        return result;
    }

    private static ChildProcessInvocation BuildDocFlowInvocation(
        string pythonExecutable,
        string docFlowWorkerDirectory,
        string provider,
        string inputRawJson,
        string schemaRequest,
        string model,
        string outputNormalizedJson,
        string outputStructuredJson,
        string documentName) =>
        new(
            pythonExecutable,
            new[]
            {
                "text_artifact_main.py",
                "--provider",
                provider,
                "--input-raw-json",
                inputRawJson,
                "--schema-request",
                schemaRequest,
                "--model",
                model,
                "--output-normalized-json",
                outputNormalizedJson,
                "--output-structured-json",
                outputStructuredJson,
                "--document-name",
                documentName
            },
            docFlowWorkerDirectory);

    private static string GetProviderName(
        ProviderTranscriptResearchProvider provider) =>
        provider switch
        {
            ProviderTranscriptResearchProvider.OpenAi =>
                "openai",
            ProviderTranscriptResearchProvider.Groq =>
                "groq",
            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "Unsupported provider.")
        };

    private static string GetApiKeyEnvironmentVariable(
        ProviderTranscriptResearchProvider provider) =>
        provider switch
        {
            ProviderTranscriptResearchProvider.OpenAi =>
                OpenAiApiKeyEnvironmentVariable,
            ProviderTranscriptResearchProvider.Groq =>
                GroqApiKeyEnvironmentVariable,
            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(provider),
                    provider,
                    "Unsupported provider.")
        };

    private static void ValidateDocFlow(
        string docFlowRoot,
        string entryPoint)
    {
        if (!Directory.Exists(
                docFlowRoot))
        {
            throw new DirectoryNotFoundException(
                "DocFlow root directory was not found.");
        }

        if (!File.Exists(
                entryPoint))
        {
            throw new FileNotFoundException(
                "DocFlow DF-06 entry point was not found.",
                entryPoint);
        }
    }

    private static void ValidateWorkDirectory(
        string tradeOpsRoot,
        string workDirectory)
    {
        var samplesRoot =
            Path.Combine(
                tradeOpsRoot,
                "samples");
        var schemasRoot =
            Path.Combine(
                tradeOpsRoot,
                "schemas");

        if (IsWithin(
                workDirectory,
                samplesRoot)
            || IsWithin(
                workDirectory,
                schemasRoot))
        {
            throw new InvalidOperationException(
                "Work directory must not be inside committed sample/schema directories.");
        }
    }

    private static bool IsWithin(
        string candidate,
        string parent)
    {
        var relative =
            Path.GetRelativePath(
                Path.GetFullPath(
                    parent),
                Path.GetFullPath(
                    candidate));

        if (string.Equals(
                relative,
                ".",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (Path.IsPathRooted(
                relative)
            || string.Equals(
                relative,
                "..",
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." +
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." +
                Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string? ResolveRebalanceInput(
        string? rebalanceInputPath,
        string tradeOpsRoot)
    {
        if (string.IsNullOrWhiteSpace(
                rebalanceInputPath))
        {
            return null;
        }

        var fullPath =
            Path.IsPathRooted(
                rebalanceInputPath)
                ? Path.GetFullPath(
                    rebalanceInputPath)
                : Path.GetFullPath(
                    Path.Combine(
                        tradeOpsRoot,
                        rebalanceInputPath));

        if (!File.Exists(
                fullPath))
        {
            throw new FileNotFoundException(
                "Rebalance input file was not found.",
                fullPath);
        }

        return fullPath;
    }

    private static string? ResolveRiskPreviewInput(
        string? riskPreviewInputPath,
        string tradeOpsRoot)
    {
        if (string.IsNullOrWhiteSpace(
                riskPreviewInputPath))
        {
            return null;
        }

        var fullPath =
            Path.IsPathRooted(
                riskPreviewInputPath)
                ? Path.GetFullPath(
                    riskPreviewInputPath)
                : Path.GetFullPath(
                    Path.Combine(
                        tradeOpsRoot,
                        riskPreviewInputPath));

        if (!File.Exists(
                fullPath))
        {
            throw new FileNotFoundException(
                "Risk preview input file was not found.",
                fullPath);
        }

        return fullPath;
    }

    private static string RequireFile(
        string path,
        string description)
    {
        var fullPath =
            Path.GetFullPath(
                path);

        if (!File.Exists(
                fullPath))
        {
            throw new FileNotFoundException(
                description +
                " file was not found.",
                fullPath);
        }

        return fullPath;
    }

    private static void RequireGeneratedFile(
        string path,
        string description)
    {
        if (!File.Exists(
                path))
        {
            throw new FileNotFoundException(
                description +
                " was not produced.",
                path);
        }
    }
}

public static class ProviderTranscriptResearchDemoCli
{
    public static int Run(
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null,
        IChildProcessRunner? processRunner = null,
        IEnvironmentReader? environmentReader = null,
        string? tradeOpsRoot = null)
    {
        output ??=
            Console.Out;
        error ??=
            Console.Error;

        try
        {
            var options =
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        args);
            var root =
                string.IsNullOrWhiteSpace(
                    tradeOpsRoot)
                ? TradeOpsRepositoryLocator
                    .Resolve()
                : Path.GetFullPath(
                    tradeOpsRoot);

            return new ProviderTranscriptResearchDemoHarness(
                    processRunner
                    ?? new SystemChildProcessRunner(),
                    environmentReader
                    ?? new SystemEnvironmentReader())
                .Run(
                    options,
                    root,
                    output,
                    error);
        }
        catch (Exception exception)
        {
            error.WriteLine(
                "PROVIDER TRANSCRIPT RESEARCH DEMO: FAIL (" +
                exception.GetType().Name +
                ": " +
                ChildProcessDiagnostics.Redact(
                    exception.Message,
                    null) +
                ")");

            return 1;
        }
    }
}
