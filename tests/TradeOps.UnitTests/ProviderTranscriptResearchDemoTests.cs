using TradeOps.ProviderTranscriptResearchDemo;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ProviderTranscriptResearchDemoTests
{
    [Fact]
    public void Options_require_explicit_model_and_have_no_model_default()
    {
        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--docflow-root",
                            "docflow"
                        }));
    }

    [Fact]
    public void Options_default_python_and_default_work_directory_are_exact()
    {
        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--docflow-root",
                        "docflow",
                        "--model",
                        "explicit-model"
                    });

        Assert.Equal(
            "python",
            options.PythonExecutable);
        Assert.Null(
            options.WorkDirectory);
        Assert.Equal(
            ProviderTranscriptResearchProvider.OpenAi,
            options.Provider);
        Assert.Null(
            options.DotNetExecutable);
        Assert.Null(
            options.RebalanceInputPath);

        var root =
            Path.GetFullPath(
                Path.Combine(
                    Path.GetTempPath(),
                    "tradeops-root"));

        Assert.Equal(
            Path.Combine(
                root,
                ".tradeops",
                "provider-transcript-demo"),
            options.ResolveWorkDirectory(
                root));
    }

    [Fact]
    public void Options_honor_explicit_python_model_and_work_directory()
    {
        using var fixture =
            new Fixture();
        var explicitWork =
            Path.Combine(
                fixture.Root,
                "explicit-work");

        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--docflow-root",
                        fixture.DocFlowRoot,
                        "--model",
                        "model-verbatim",
                        "--python",
                        "python-custom",
                        "--work-dir",
                        explicitWork
                    });

        Assert.Equal(
            "model-verbatim",
            options.Model);
        Assert.Equal(
            "python-custom",
            options.PythonExecutable);
        Assert.Equal(
            Path.GetFullPath(
                explicitWork),
            options.ResolveWorkDirectory(
                fixture.TradeOpsRoot));
    }

    [Fact]
    public void Options_parse_explicit_dotnet_and_missing_value_fails_closed()
    {
        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--docflow-root",
                        "docflow",
                        "--model",
                        "explicit-model",
                        "--dotnet",
                        "custom-dotnet"
                    });

        Assert.Equal(
            "custom-dotnet",
            options.DotNetExecutable);

        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--docflow-root",
                            "docflow",
                            "--model",
                            "explicit-model",
                            "--dotnet"
                        }));
    }

    [Fact]
    public void Options_parse_rebalance_input_and_missing_value_fails_closed()
    {
        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--docflow-root",
                        "docflow",
                        "--model",
                        "explicit-model",
                        "--rebalance-input",
                        "samples/rebalance.json"
                    });

        Assert.Equal(
            "samples/rebalance.json",
            options.RebalanceInputPath);

        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--docflow-root",
                            "docflow",
                            "--model",
                            "explicit-model",
                            "--rebalance-input"
                        }));
    }

    [Fact]
    public void Explicit_dotnet_host_wins_over_environment_hints()
    {
        using var fixture =
            new Fixture();
        var explicitHost =
            fixture.CreateDotNetHost(
                "explicit-host");
        var environmentHost =
            fixture.CreateDotNetHost(
                "environment-host");
        var rootHost =
            fixture.CreateDotNetHost(
                "environment-root");
        var environment =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>
                {
                    [DotNetHostResolver
                        .DotNetHostPathEnvironmentVariable] =
                        environmentHost,
                    [DotNetHostResolver
                        .DotNetRootEnvironmentVariable] =
                        Path.GetDirectoryName(
                            rootHost)
                });

        var selection =
            DotNetHostResolver.Resolve(
                explicitHost,
                environment);

        Assert.Equal(
            explicitHost,
            selection.Executable);
        Assert.Equal(
            "explicit --dotnet",
            selection.Source);
        Assert.Empty(
            environment.Requests);
    }

    [Fact]
    public void Environment_dotnet_hint_precedence_is_deterministic()
    {
        using var fixture =
            new Fixture();
        var hostPath =
            fixture.CreateDotNetHost(
                "host-path");
        var rootHost =
            fixture.CreateDotNetHost(
                "root-host");
        var resolverHost =
            fixture.CreateDotNetHost(
                "resolver-host");

        var allHints =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>
                {
                    [DotNetHostResolver
                        .DotNetHostPathEnvironmentVariable] =
                        hostPath,
                    [DotNetHostResolver
                        .DotNetRootEnvironmentVariable] =
                        Path.GetDirectoryName(
                            rootHost),
                    [DotNetHostResolver
                        .DotNetMsBuildSdkResolverCliDirEnvironmentVariable] =
                        Path.GetDirectoryName(
                            resolverHost)
                });

        Assert.Equal(
            hostPath,
            DotNetHostResolver
                .Resolve(
                    null,
                    allHints)
                .Executable);

        var rootHints =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>
                {
                    [DotNetHostResolver
                        .DotNetRootEnvironmentVariable] =
                        Path.GetDirectoryName(
                            rootHost),
                    [DotNetHostResolver
                        .DotNetMsBuildSdkResolverCliDirEnvironmentVariable] =
                        Path.GetDirectoryName(
                            resolverHost)
                });

        Assert.Equal(
            rootHost,
            DotNetHostResolver
                .Resolve(
                    null,
                    rootHints)
                .Executable);

        var resolverHints =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>
                {
                    [DotNetHostResolver
                        .DotNetMsBuildSdkResolverCliDirEnvironmentVariable] =
                        Path.GetDirectoryName(
                            resolverHost)
                });

        Assert.Equal(
            resolverHost,
            DotNetHostResolver
                .Resolve(
                    null,
                    resolverHints)
                .Executable);

        var fallback =
            DotNetHostResolver.Resolve(
                null,
                new RecordingEnvironmentReader(
                    new Dictionary<string, string?>()));

        Assert.Equal(
            DotNetHostResolver.PlatformExecutableName,
            fallback.Executable);
        Assert.Equal(
            "PATH fallback",
            fallback.Source);
    }

    [Fact]
    public void Invalid_explicit_dotnet_host_fails_before_any_child_process()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();

        var result =
            fixture.Run(
                fixture.Options(
                    dotNetExecutable:
                        Path.Combine(
                            fixture.Root,
                            "missing",
                            DotNetHostResolver
                                .PlatformExecutableName)),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.PreflightInvocations);
        Assert.Empty(
            runner.Invocations);
        Assert.Contains(
            "explicit --dotnet",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Global_json_latest_patch_policy_accepts_compatible_dotnet_8_sdk()
    {
        using var fixture =
            new Fixture();
        var policy =
            DotNetSdkPolicy.Load(
                fixture.TradeOpsRoot);

        Assert.Equal(
            "8.0.400",
            policy.RequestedVersion);
        Assert.Equal(
            "latestPatch",
            policy.RollForward);
        Assert.False(
            policy.AllowPrerelease);

        policy.ValidateResolvedVersion(
            "8.0.421");
    }

    [Theory]
    [InlineData("7.0.410")]
    [InlineData("8.1.400")]
    [InlineData("8.0.399")]
    [InlineData("8.0.500")]
    [InlineData("8.0.421-preview.1")]
    public void Global_json_policy_rejects_incompatible_or_prerelease_sdk(
        string version)
    {
        using var fixture =
            new Fixture();
        var policy =
            DotNetSdkPolicy.Load(
                fixture.TradeOpsRoot);

        Assert.Throws<InvalidOperationException>(
            () =>
                policy.ValidateResolvedVersion(
                    version));
    }

    [Fact]
    public void Dotnet_preflight_failure_stops_before_provider_and_reports_safe_host_diagnostics()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                preflightResult:
                    new ChildProcessResult(
                        131,
                        "untrusted stdout payload",
                        "A fatal error occurred. The .NET SDK could not be resolved from global.json."));

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Single(
            runner.PreflightInvocations);
        Assert.Empty(
            runner.Invocations);
        Assert.Contains(
            "dotnet preflight failed",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "exit code 131",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "SDK",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "untrusted stdout payload",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Incompatible_preflight_sdk_stops_before_provider()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                preflightResult:
                    new ChildProcessResult(
                        0,
                        "9.0.100\n",
                        string.Empty));

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Single(
            runner.PreflightInvocations);
        Assert.Empty(
            runner.Invocations);
        Assert.Contains(
            "incompatible with global.json",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_valid_host_is_preflighted_and_used_for_tradeops_consumer()
    {
        using var fixture =
            new Fixture();
        var host =
            fixture.CreateDotNetHost(
                "operator-host");
        var runner =
            fixture.SuccessfulRunner();

        var result =
            fixture.Run(
                fixture.Options(
                    dotNetExecutable:
                        host),
                runner);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.Single(
            runner.PreflightInvocations);
        Assert.Equal(
            host,
            runner.PreflightInvocations[0]
                .FileName);
        Assert.Equal(
            new[]
            {
                "--version"
            },
            runner.PreflightInvocations[0]
                .Arguments);
        Assert.Equal(
            host,
            runner.Invocations[2]
                .FileName);
        Assert.Contains(
            "Dotnet host: " +
            host,
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "Dotnet SDK: 8.0.421",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_docflow_root_stops_before_process_execution()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();
        var options =
            fixture.Options(
                docFlowRoot:
                    Path.Combine(
                        fixture.Root,
                        "missing-docflow"));

        var result =
            fixture.Run(
                options,
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.Invocations);
    }

    [Fact]
    public void Missing_df06_entry_point_stops_before_process_execution()
    {
        using var fixture =
            new Fixture();
        File.Delete(
            fixture.DocFlowEntryPoint);
        var runner =
            new RecordingRunner();

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.Invocations);
    }

    [Fact]
    public void Missing_rebalance_input_stops_before_dotnet_preflight_and_provider_work()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();

        var result =
            fixture.Run(
                fixture.Options(
                    rebalanceInputPath:
                        Path.Combine(
                            "samples",
                            "research",
                            "provider-transcript-demo",
                            "missing-rebalance-input.json")),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.PreflightInvocations);
        Assert.Empty(
            runner.Invocations);
        Assert.Contains(
            "Rebalance input file was not found",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_api_key_stops_before_process_execution()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();

        var result =
            fixture.Run(
                fixture.Options(),
                runner,
                apiKey:
                    null);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.Invocations);
        Assert.Contains(
            "OPENAI_API_KEY is required",
            result.Error);
    }

    [Fact]
    public void Prior_and_current_df06_calls_use_exact_argument_lists_model_and_working_directory()
    {
        using var fixture =
            new Fixture();
        var runner =
            fixture.SuccessfulRunner();

        var result =
            fixture.Run(
                fixture.Options(
                    model:
                        "provider-model",
                    python:
                        "python-x"),
                runner);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.Equal(
            3,
            runner.Invocations.Count);

        var expectedWorkingDirectory =
            Path.Combine(
                fixture.DocFlowRoot,
                "src",
                "DocFlow.Extraction.Worker");

        Assert.Equal(
            expectedWorkingDirectory,
            runner.Invocations[0]
                .WorkingDirectory);
        Assert.Equal(
            expectedWorkingDirectory,
            runner.Invocations[1]
                .WorkingDirectory);

        Assert.Equal(
            "python-x",
            runner.Invocations[0]
                .FileName);
        Assert.Equal(
            "python-x",
            runner.Invocations[1]
                .FileName);

        Assert.Equal(
            new[]
            {
                "text_artifact_main.py",
                "--provider",
                "openai",
                "--input-raw-json",
                fixture.PriorRaw,
                "--schema-request",
                fixture.SchemaRequest,
                "--model",
                "provider-model",
                "--output-normalized-json",
                Path.Combine(
                    fixture.WorkDirectory,
                    "prior-normalized.json"),
                "--output-structured-json",
                Path.Combine(
                    fixture.WorkDirectory,
                    "prior-structured.json"),
                "--document-name",
                ProviderTranscriptResearchDemoHarness
                    .PriorDocumentName
            },
            runner.Invocations[0]
                .Arguments);

        Assert.Equal(
            new[]
            {
                "text_artifact_main.py",
                "--provider",
                "openai",
                "--input-raw-json",
                fixture.CurrentRaw,
                "--schema-request",
                fixture.SchemaRequest,
                "--model",
                "provider-model",
                "--output-normalized-json",
                Path.Combine(
                    fixture.WorkDirectory,
                    "current-normalized.json"),
                "--output-structured-json",
                Path.Combine(
                    fixture.WorkDirectory,
                    "current-structured.json"),
                "--document-name",
                ProviderTranscriptResearchDemoHarness
                    .CurrentDocumentName
            },
            runner.Invocations[1]
                .Arguments);
    }

    [Fact]
    public void Api_key_is_never_added_to_child_arguments_or_console()
    {
        using var fixture =
            new Fixture();
        var runner =
            fixture.SuccessfulRunner();
        const string secret =
            "unit-test-secret-value";

        var result =
            fixture.Run(
                fixture.Options(),
                runner,
                apiKey:
                    secret);

        Assert.Equal(
            0,
            result.ExitCode);

        foreach (var invocation in
                 runner.Invocations)
        {
            Assert.DoesNotContain(
                invocation.Arguments,
                argument =>
                    argument.Contains(
                        secret,
                        StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            secret,
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "100,000,000",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "100,000,000",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Process_start_info_uses_argument_list_and_no_shell()
    {
        var invocation =
            new ChildProcessInvocation(
                "python",
                new[]
                {
                    "text_artifact_main.py",
                    "--model",
                    "m"
                },
                Path.GetTempPath());

        var startInfo =
            SystemChildProcessRunner
                .CreateStartInfo(
                    invocation);

        Assert.False(
            startInfo.UseShellExecute);
        Assert.True(
            startInfo.RedirectStandardOutput);
        Assert.True(
            startInfo.RedirectStandardError);
        Assert.Equal(
            "python",
            startInfo.FileName);
        Assert.Equal(
            invocation.Arguments,
            startInfo.ArgumentList
                .ToArray());
        Assert.DoesNotContain(
            startInfo.FileName,
            new[]
            {
                "cmd.exe",
                "powershell",
                "bash",
                "sh"
            });
    }

    [Fact]
    public void Bounded_diagnostics_are_deterministic_and_never_exceed_limit()
    {
        var input =
            new string(
                'x',
                SystemChildProcessRunner
                    .MaxCapturedCharactersPerStream +
                512);

        var first =
            SystemChildProcessRunner
                .BoundForDiagnostics(
                    input);
        var second =
            SystemChildProcessRunner
                .BoundForDiagnostics(
                    input);

        Assert.Equal(
            SystemChildProcessRunner
                .MaxCapturedCharactersPerStream,
            first.Length);
        Assert.Equal(
            first,
            second);
        Assert.EndsWith(
            SystemChildProcessRunner
                .TruncationMarker,
            first,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_failure_does_not_forward_provider_stdout_or_stderr()
    {
        using var fixture =
            new Fixture();
        const string secret =
            "provider-secret-marker";
        var runner =
            new RecordingRunner(
                (_, index) =>
                index == 0
                    ? new ChildProcessResult(
                        2,
                        "RAW_PROVIDER_TRANSCRIPT_MARKER " +
                        secret,
                        "Authorization: Bearer " +
                        secret)
                    : 0);

        var result =
            fixture.Run(
                fixture.Options(),
                runner,
                apiKey:
                    secret);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.DoesNotContain(
            "RAW_PROVIDER_TRANSCRIPT_MARKER",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RAW_PROVIDER_TRANSCRIPT_MARKER",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "Provider child output is suppressed",
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Tradeops_failure_reports_only_safe_redacted_dotnet_diagnostics()
    {
        using var fixture =
            new Fixture();
        const string secret =
            "tradeops-secret-marker";
        var runner =
            new RecordingRunner(
                (invocation, index) =>
                {
                    if (index < 2)
                    {
                        WriteProviderArtifacts(
                            invocation);
                        return 0;
                    }

                    return new ChildProcessResult(
                        5,
                        "RAW_TRANSCRIPT_BODY_MARKER " +
                        secret,
                        "The .NET SDK failed. Authorization: Bearer " +
                        secret);
                });

        var result =
            fixture.Run(
                fixture.Options(),
                runner,
                apiKey:
                    secret);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Contains(
            "Safe dotnet diagnostics:",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            ".NET SDK",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "failed. Authorization",
            result.Error,
            StringComparison.Ordinal);
        Assert.Contains(
            "exit code 5",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            secret,
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RAW_TRANSCRIPT_BODY_MARKER",
            result.Error,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RAW_TRANSCRIPT_BODY_MARKER",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Prior_failure_stops_current_and_vs08()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                (_, _) =>
                    1);

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Single(
            runner.Invocations);
    }

    [Fact]
    public void Df06_exit_two_is_failure_and_partial_artifact_is_preserved()
    {
        using var fixture =
            new Fixture();
        var partial =
            Path.Combine(
                fixture.WorkDirectory,
                "prior-structured.json");
        var runner =
            new RecordingRunner(
                (_, index) =>
                {
                    if (index == 0)
                    {
                        Directory.CreateDirectory(
                            fixture.WorkDirectory);
                        File.WriteAllText(
                            partial,
                            "{}");
                        return 2;
                    }

                    return 0;
                });

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Single(
            runner.Invocations);
        Assert.True(
            File.Exists(
                partial));
    }

    [Fact]
    public void Current_failure_stops_before_vs08()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                (invocation, index) =>
                {
                    if (index == 0)
                    {
                        WriteProviderArtifacts(
                            invocation);
                        return 0;
                    }

                    return 9;
                });

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Equal(
            2,
            runner.Invocations.Count);
        Assert.DoesNotContain(
            runner.Invocations,
            item =>
                string.Equals(
                    item.FileName,
                    "dotnet",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Consumer_manifest_is_copied_unchanged_and_vs08_arguments_are_exact()
    {
        using var fixture =
            new Fixture();
        var sourceBytes =
            File.ReadAllBytes(
                fixture.ConsumerManifest);
        var runner =
            fixture.SuccessfulRunner();

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            0,
            result.ExitCode);

        var runtimeManifest =
            Path.Combine(
                fixture.WorkDirectory,
                "manifest.json");

        Assert.Equal(
            sourceBytes,
            File.ReadAllBytes(
                runtimeManifest));

        var consumer =
            runner.Invocations[2];

        Assert.Equal(
            DotNetHostResolver.PlatformExecutableName,
            consumer.FileName);
        Assert.Equal(
            fixture.TradeOpsRoot,
            consumer.WorkingDirectory);
        Assert.Equal(
            new[]
            {
                "run",
                "--project",
                "tools/TradeOps.TranscriptResearchDemo",
                "--",
                "--manifest",
                runtimeManifest,
                "--policy",
                fixture.Policy,
                "--json",
                Path.Combine(
                    fixture.WorkDirectory,
                    "transcript-research-result.json")
            },
            consumer.Arguments);
    }

    [Fact]
    public void Rebalance_mode_preserves_docflow_stages_and_invokes_vs13_once_with_exact_arguments()
    {
        using var fixture =
            new Fixture();
        var runner =
            fixture.SuccessfulRunner();
        var relativeRebalanceInput =
            Path.Combine(
                "samples",
                "research",
                "provider-transcript-demo",
                "rebalance-input.json");

        var result =
            fixture.Run(
                fixture.Options(
                    rebalanceInputPath:
                        relativeRebalanceInput),
                runner);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.Equal(
            3,
            runner.Invocations.Count);

        var prior =
            runner.Invocations[0];
        var current =
            runner.Invocations[1];

        Assert.Equal(
            "python",
            prior.FileName);
        Assert.Equal(
            "python",
            current.FileName);
        Assert.Equal(
            fixture.PriorRaw,
            prior.Arguments[
                prior.Arguments
                    .ToList()
                    .IndexOf(
                        "--input-raw-json") +
                1]);
        Assert.Equal(
            fixture.CurrentRaw,
            current.Arguments[
                current.Arguments
                    .ToList()
                    .IndexOf(
                        "--input-raw-json") +
                1]);

        var runtimeManifest =
            Path.Combine(
                fixture.WorkDirectory,
                "manifest.json");
        var rebalanceResult =
            Path.Combine(
                fixture.WorkDirectory,
                "transcript-research-rebalance-result.json");
        var consumer =
            runner.Invocations[2];

        Assert.Equal(
            new[]
            {
                "run",
                "--project",
                "tools/TradeOps.TranscriptResearchDemo",
                "--",
                "--manifest",
                runtimeManifest,
                "--policy",
                fixture.Policy,
                "--rebalance-input",
                fixture.RebalanceInput,
                "--json",
                rebalanceResult
            },
            consumer.Arguments);
        Assert.True(
            File.Exists(
                rebalanceResult));
        Assert.True(
            new FileInfo(
                    rebalanceResult)
                .Length >
            0);
        Assert.False(
            File.Exists(
                Path.Combine(
                    fixture.WorkDirectory,
                    "transcript-research-result.json")));
        Assert.Contains(
            "PROVIDER TRANSCRIPT RESEARCH DEMO: PASS",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "Result JSON: " +
            rebalanceResult,
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Vs08_failure_propagates_nonzero()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                (invocation, index) =>
                {
                    if (index < 2)
                    {
                        WriteProviderArtifacts(
                            invocation);
                        return 0;
                    }

                    return 5;
                });

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Equal(
            3,
            runner.Invocations.Count);
    }

    [Fact]
    public void Success_requires_all_four_provider_artifacts()
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner(
                (invocation, index) =>
                {
                    if (index == 0)
                    {
                        WriteProviderArtifacts(
                            invocation);
                    }
                    else if (index == 1)
                    {
                        WriteArgumentFile(
                            invocation,
                            "--output-normalized-json",
                            "{}");
                    }

                    return 0;
                });

        var result =
            fixture.Run(
                fixture.Options(),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Equal(
            2,
            runner.Invocations.Count);
    }

    [Fact]
    public void Success_requires_nonempty_final_json_and_success_returns_zero()
    {
        using var fixture =
            new Fixture();

        var emptyRunner =
            new RecordingRunner(
                (invocation, index) =>
                {
                    if (index < 2)
                    {
                        WriteProviderArtifacts(
                            invocation);
                    }
                    else
                    {
                        WriteArgumentFile(
                            invocation,
                            "--json",
                            string.Empty);
                    }

                    return 0;
                });

        var emptyResult =
            fixture.Run(
                fixture.Options(),
                emptyRunner);

        Assert.Equal(
            1,
            emptyResult.ExitCode);

        using var successFixture =
            new Fixture();
        var successRunner =
            successFixture.SuccessfulRunner();
        var success =
            successFixture.Run(
                successFixture.Options(),
                successRunner);

        Assert.Equal(
            0,
            success.ExitCode);
        Assert.True(
            new FileInfo(
                    Path.Combine(
                        successFixture.WorkDirectory,
                        "transcript-research-result.json"))
                .Length >
            0);
    }

    [Theory]
    [InlineData(
        "samples")]
    [InlineData(
        "schemas")]
    public void Work_directory_must_not_write_generated_output_into_committed_input_directories(
        string committedDirectory)
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();
        var blocked =
            Path.Combine(
                fixture.TradeOpsRoot,
                committedDirectory,
                "generated");

        var result =
            fixture.Run(
                fixture.Options(
                    workDirectory:
                        blocked),
                runner);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.Invocations);
        Assert.False(
            Directory.Exists(
                blocked));
    }

    [Fact]
    public void New_tool_has_no_sdk_http_docflow_dependency_or_duplicate_decision_logic()
    {
        var root =
            FindRepositoryRoot();
        var project =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.ProviderTranscriptResearchDemo",
                    "TradeOps.ProviderTranscriptResearchDemo.csproj"));
        var source =
            File.ReadAllText(
                Path.Combine(
                    root,
                    "tools",
                    "TradeOps.ProviderTranscriptResearchDemo",
                    "ProviderTranscriptResearchDemo.cs"));

        Assert.DoesNotContain(
            "<ProjectReference",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<PackageReference",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpClient",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TranscriptResearchDecisionDemoService",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TranscriptResearchToRebalanceDemoService",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EventDrivenBacktester",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PortfolioRebalancePlanner",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DeterministicEarningsDecisionRule",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResearchDecisionAction",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "api.groq.com",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "api.openai.com",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProcessStartInfo.Environment",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"D:DotNet",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @"C:Program Filesdotnet",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ReadToEnd",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureBoundedAsync",
            source,
            StringComparison.Ordinal);
    }


    [Theory]
    [InlineData(
        "openai",
        ProviderTranscriptResearchProvider.OpenAi)]
    [InlineData(
        "groq",
        ProviderTranscriptResearchProvider.Groq)]
    public void Options_parse_explicit_provider(
        string providerName,
        ProviderTranscriptResearchProvider expectedProvider)
    {
        var options =
            ProviderTranscriptResearchDemoOptions
                .Parse(
                    new[]
                    {
                        "--provider",
                        providerName,
                        "--docflow-root",
                        "docflow",
                        "--model",
                        "explicit-model"
                    });

        Assert.Equal(
            expectedProvider,
            options.Provider);
    }

    [Fact]
    public void Unknown_provider_is_rejected_during_cli_parse()
    {
        var exception =
            Assert.Throws<ArgumentException>(
                () =>
                    ProviderTranscriptResearchDemoOptions
                        .Parse(
                            new[]
                            {
                                "--provider",
                                "unknown",
                                "--docflow-root",
                                "docflow",
                                "--model",
                                "explicit-model"
                            }));

        Assert.Contains(
            "openai, groq",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Api_key_cli_option_is_not_supported()
    {
        Assert.Throws<ArgumentException>(
            () =>
                ProviderTranscriptResearchDemoOptions
                    .Parse(
                        new[]
                        {
                            "--api-key",
                            "must-not-be-accepted",
                            "--docflow-root",
                            "docflow",
                            "--model",
                            "explicit-model"
                        }));
    }

    [Theory]
    [InlineData(
        ProviderTranscriptResearchProvider.OpenAi,
        "openai",
        ProviderTranscriptResearchDemoHarness.OpenAiApiKeyEnvironmentVariable)]
    [InlineData(
        ProviderTranscriptResearchProvider.Groq,
        "groq",
        ProviderTranscriptResearchDemoHarness.GroqApiKeyEnvironmentVariable)]
    public void Selected_provider_reads_only_selected_credential_and_forwards_exact_provider_and_model(
        ProviderTranscriptResearchProvider provider,
        string providerName,
        string selectedEnvironmentVariable)
    {
        using var fixture =
            new Fixture();
        var runner =
            fixture.SuccessfulRunner();
        var environment =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>
                {
                    [selectedEnvironmentVariable] =
                        "selected-provider-secret"
                });

        var result =
            fixture.Run(
                fixture.Options(
                    model:
                        "model-verbatim",
                    provider:
                        provider),
                runner,
                environmentReader:
                    environment);

        Assert.Equal(
            0,
            result.ExitCode);
        Assert.NotEmpty(
            environment.Requests);
        Assert.Equal(
            selectedEnvironmentVariable,
            environment.Requests[0]);
        Assert.DoesNotContain(
            environment.Requests,
            name =>
                string.Equals(
                    name,
                    provider ==
                    ProviderTranscriptResearchProvider.OpenAi
                        ? ProviderTranscriptResearchDemoHarness
                            .GroqApiKeyEnvironmentVariable
                        : ProviderTranscriptResearchDemoHarness
                            .OpenAiApiKeyEnvironmentVariable,
                    StringComparison.Ordinal));

        Assert.Equal(
            3,
            runner.Invocations.Count);

        foreach (var invocation in
                 runner.Invocations.Take(
                     2))
        {
            var providerIndex =
                invocation.Arguments
                    .ToList()
                    .IndexOf(
                        "--provider");
            var modelIndex =
                invocation.Arguments
                    .ToList()
                    .IndexOf(
                        "--model");

            Assert.True(
                providerIndex >= 0);
            Assert.Equal(
                providerName,
                invocation.Arguments[
                    providerIndex + 1]);

            Assert.True(
                modelIndex >= 0);
            Assert.Equal(
                "model-verbatim",
                invocation.Arguments[
                    modelIndex + 1]);

            Assert.DoesNotContain(
                invocation.Arguments,
                argument =>
                    argument.Contains(
                        "selected-provider-secret",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                "--api-key",
                invocation.Arguments);
        }

        Assert.DoesNotContain(
            "selected-provider-secret",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "selected-provider-secret",
            result.Error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        ProviderTranscriptResearchProvider.OpenAi,
        ProviderTranscriptResearchDemoHarness.OpenAiApiKeyEnvironmentVariable)]
    [InlineData(
        ProviderTranscriptResearchProvider.Groq,
        ProviderTranscriptResearchDemoHarness.GroqApiKeyEnvironmentVariable)]
    public void Missing_selected_credential_stops_before_process_and_names_only_selected_variable(
        ProviderTranscriptResearchProvider provider,
        string selectedEnvironmentVariable)
    {
        using var fixture =
            new Fixture();
        var runner =
            new RecordingRunner();
        var environment =
            new RecordingEnvironmentReader(
                new Dictionary<string, string?>());

        var result =
            fixture.Run(
                fixture.Options(
                    provider:
                        provider),
                runner,
                environmentReader:
                    environment);

        Assert.Equal(
            1,
            result.ExitCode);
        Assert.Empty(
            runner.Invocations);
        Assert.Equal(
            new[]
            {
                selectedEnvironmentVariable
            },
            environment.Requests);
        Assert.Contains(
            selectedEnvironmentVariable +
            " is required",
            result.Error,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
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
            "TradeOps repository root not found.");
    }

    private static void WriteProviderArtifacts(
        ChildProcessInvocation invocation)
    {
        WriteArgumentFile(
            invocation,
            "--output-normalized-json",
            "{}");
        WriteArgumentFile(
            invocation,
            "--output-structured-json",
            "{}");
    }

    private static void WriteArgumentFile(
        ChildProcessInvocation invocation,
        string option,
        string content)
    {
        var index =
            invocation.Arguments
                .ToList()
                .IndexOf(
                    option);

        Assert.True(
            index >= 0
            && index + 1 <
            invocation.Arguments.Count);

        var path =
            invocation.Arguments[index + 1];
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        File.WriteAllText(
            path,
            content);
    }

    private sealed class RecordingRunner :
        IChildProcessRunner
    {
        private readonly Func<ChildProcessInvocation, int, ChildProcessResult> _handler;
        private readonly ChildProcessResult _preflightResult;

        public RecordingRunner(
            Func<ChildProcessInvocation, int, ChildProcessResult>? handler = null,
            ChildProcessResult? preflightResult = null)
        {
            _handler =
                handler
                ?? ((_, _) =>
                    0);
            _preflightResult =
                preflightResult
                ?? new ChildProcessResult(
                    0,
                    "8.0.421\n",
                    string.Empty);
        }

        public List<ChildProcessInvocation> PreflightInvocations
        {
            get;
        } =
            new();

        public List<ChildProcessInvocation> Invocations
        {
            get;
        } =
            new();

        public ChildProcessResult Run(
            ChildProcessInvocation invocation)
        {
            if (invocation.Arguments.Count == 1
                && string.Equals(
                    invocation.Arguments[0],
                    "--version",
                    StringComparison.Ordinal))
            {
                PreflightInvocations.Add(
                    invocation);

                return _preflightResult;
            }

            var index =
                Invocations.Count;
            Invocations.Add(
                invocation);

            return _handler(
                invocation,
                index);
        }
    }

    private sealed class RecordingEnvironmentReader :
        IEnvironmentReader
    {
        private readonly IReadOnlyDictionary<string, string?> _values;

        public RecordingEnvironmentReader(
            IReadOnlyDictionary<string, string?> values)
        {
            _values =
                values;
        }

        public List<string> Requests
        {
            get;
        } =
            new();

        public string? Get(
            string name)
        {
            Requests.Add(
                name);

            return _values.TryGetValue(
                name,
                out var value)
                ? value
                : null;
        }
    }

    private sealed class EnvironmentReader :
        IEnvironmentReader
    {
        private readonly string? _apiKey;

        public EnvironmentReader(
            string? apiKey)
        {
            _apiKey =
                apiKey;
        }

        public string? Get(
            string name) =>
            string.Equals(
                name,
                ProviderTranscriptResearchDemoHarness
                    .ApiKeyEnvironmentVariable,
                StringComparison.Ordinal)
                ? _apiKey
                : null;
    }

    private sealed class Fixture :
        IDisposable
    {
        public Fixture()
        {
            Root =
                Path.Combine(
                    Path.GetTempPath(),
                    "tradeops-vs10-" +
                    Guid.NewGuid()
                        .ToString(
                            "N"));
            TradeOpsRoot =
                Path.Combine(
                    Root,
                    "TradeOps");
            DocFlowRoot =
                Path.Combine(
                    Root,
                    "DocFlow");
            WorkDirectory =
                Path.Combine(
                    Root,
                    "work");

            Directory.CreateDirectory(
                TradeOpsRoot);
            File.WriteAllText(
                Path.Combine(
                    TradeOpsRoot,
                    "TradeOps.sln"),
                string.Empty);
            File.WriteAllText(
                Path.Combine(
                    TradeOpsRoot,
                    "global.json"),
                """
                {
                  "sdk": {
                    "version": "8.0.400",
                    "rollForward": "latestPatch",
                    "allowPrerelease": false
                  }
                }
                """);

            PriorRaw =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "prior-raw.json");
            CurrentRaw =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "current-raw.json");
            ConsumerManifest =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "consumer-manifest.json",
                    "{\n  \"schemaVersion\": 1\n}\n");
            RebalanceInput =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "provider-transcript-demo",
                    "rebalance-input.json");
            SchemaRequest =
                CreateTradeOpsFile(
                    "schemas",
                    "research",
                    "earnings-transcript-facts-v1.schema-request.json");
            Policy =
                CreateTradeOpsFile(
                    "samples",
                    "research",
                    "transcript-research",
                    "policy.json");

            DocFlowEntryPoint =
                Path.Combine(
                    DocFlowRoot,
                    "src",
                    "DocFlow.Extraction.Worker",
                    "text_artifact_main.py");
            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    DocFlowEntryPoint)!);
            File.WriteAllText(
                DocFlowEntryPoint,
                string.Empty);
        }

        public string Root
        {
            get;
        }

        public string TradeOpsRoot
        {
            get;
        }

        public string DocFlowRoot
        {
            get;
        }

        public string DocFlowEntryPoint
        {
            get;
        }

        public string WorkDirectory
        {
            get;
        }

        public string PriorRaw
        {
            get;
        }

        public string CurrentRaw
        {
            get;
        }

        public string ConsumerManifest
        {
            get;
        }

        public string RebalanceInput
        {
            get;
        }

        public string SchemaRequest
        {
            get;
        }

        public string Policy
        {
            get;
        }

        public ProviderTranscriptResearchDemoOptions Options(
            string? docFlowRoot = null,
            string model = "test-model",
            string python = "python",
            string? workDirectory = null,
            ProviderTranscriptResearchProvider provider =
                ProviderTranscriptResearchProvider.OpenAi,
            string? dotNetExecutable = null,
            string? rebalanceInputPath = null) =>
            new(
                docFlowRoot
                ?? DocFlowRoot,
                model,
                python,
                workDirectory
                ?? WorkDirectory,
                provider,
                dotNetExecutable,
                rebalanceInputPath);

        public string CreateDotNetHost(
            string directoryName)
        {
            var directory =
                Path.Combine(
                    Root,
                    directoryName);
            Directory.CreateDirectory(
                directory);

            var path =
                Path.Combine(
                    directory,
                    DotNetHostResolver
                        .PlatformExecutableName);
            File.WriteAllText(
                path,
                string.Empty);

            return Path.GetFullPath(
                path);
        }

        public RecordingRunner SuccessfulRunner() =>
            new(
                (invocation, index) =>
                {
                    if (index < 2)
                    {
                        WriteProviderArtifacts(
                            invocation);
                    }
                    else
                    {
                        WriteArgumentFile(
                            invocation,
                            "--json",
                            "{}");
                    }

                    return 0;
                });

        public RunResult Run(
            ProviderTranscriptResearchDemoOptions options,
            RecordingRunner runner,
            string? apiKey = "test-key",
            IEnvironmentReader? environmentReader = null)
        {
            using var output =
                new StringWriter();
            using var error =
                new StringWriter();

            var exitCode =
                new ProviderTranscriptResearchDemoHarness(
                        runner,
                        environmentReader
                        ?? new EnvironmentReader(
                            apiKey))
                    .Run(
                        options,
                        TradeOpsRoot,
                        output,
                        error);

            return new RunResult(
                exitCode,
                output.ToString(),
                error.ToString());
        }

        public void Dispose()
        {
            if (Directory.Exists(
                    Root))
            {
                Directory.Delete(
                    Root,
                    recursive:
                        true);
            }
        }

        private string CreateTradeOpsFile(
            params string[] segments) =>
            CreateTradeOpsFile(
                segments,
                "{}");

        private string CreateTradeOpsFile(
            string first,
            string second,
            string third,
            string fourth,
            string content) =>
            CreateTradeOpsFile(
                new[]
                {
                    first,
                    second,
                    third,
                    fourth
                },
                content);

        private string CreateTradeOpsFile(
            string[] segments,
            string content)
        {
            var path =
                segments.Aggregate(
                    TradeOpsRoot,
                    Path.Combine);
            var directory =
                Path.GetDirectoryName(
                    path)!;
            Directory.CreateDirectory(
                directory);
            File.WriteAllText(
                path,
                content);

            return Path.GetFullPath(
                path);
        }
    }

    private sealed record RunResult(
        int ExitCode,
        string Output,
        string Error);
}
