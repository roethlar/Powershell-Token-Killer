using PtkMcpServer.GuardianHost;

namespace PtkMcpServer.Tests;

public sealed class PrivateHostProcessEntryTests
{
    [Fact]
    public void Program_classifies_the_exact_private_role_before_any_host_boundary()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "server",
            "PtkMcpServer",
            "Program.cs"));

        var classification = RequiredIndex(
            source,
            "PrivateHostProcessEntry.Classify(args)");
        var dispatch = RequiredIndex(
            source,
            "PrivateHostProcessEntry.RunClassifiedProductionAsync(privateRole)");
        var roleReturn = source.IndexOf("return;", dispatch, StringComparison.Ordinal);
        Assert.True(roleReturn > dispatch, "private role must return after execution");
        Assert.True(classification < dispatch);

        foreach (var publicBoundary in new[]
        {
            "Host.CreateApplicationBuilder(args)",
            "DefaultSessionRuntimeFactory.ReadCallTimeout()",
            "JobPwshExecutable.ResolveFromPath()",
            "AuditStartupConfiguration.LoadFromEnvironment()",
            "new OutputStore(",
            ".AddMcpServer(",
            "ChildStdinGuard.DetachChildStdin()",
        })
        {
            Assert.True(
                roleReturn < RequiredIndex(source, publicBoundary),
                $"private role return must precede {publicBoundary}");
        }
    }

    [Fact]
    public void Classifier_accepts_only_the_three_transitional_startup_forms()
    {
        Assert.Equal(
            PrivateServerProcessRole.TransitionalDevelopment,
            PrivateHostProcessEntry.Classify([]));
        Assert.Equal(
            PrivateServerProcessRole.Host,
            PrivateHostProcessEntry.Classify(["--host"]));
        Assert.Equal(
            PrivateServerProcessRole.Worker,
            PrivateHostProcessEntry.Classify(["--worker"]));
    }

    public static TheoryData<string[]> InvalidStartupForms => new()
    {
        new[] { "" },
        new[] { "host" },
        new[] { "worker" },
        new[] { "--HOST" },
        new[] { "--WORKER" },
        new[] { " --host" },
        new[] { "--host " },
        new[] { "--host", "extra" },
        new[] { "extra", "--host" },
        new[] { "--worker", "extra" },
        new[] { "extra", "--worker" },
        new[] { "--host", "--worker" },
    };

    [Theory]
    [MemberData(nameof(InvalidStartupForms))]
    public void Classifier_rejects_every_other_first_action(string[] arguments)
    {
        Assert.Equal(
            PrivateServerProcessRole.Invalid,
            PrivateHostProcessEntry.Classify(arguments));
    }

    [Fact]
    public async Task No_argument_mode_continues_without_touching_private_bootstrap_or_runtime()
    {
        var result = await PrivateHostProcessEntry.RunFirstActionAsync<string, string>(
            [],
            bootstrapBoundary: null,
            runHost: null,
            runWorker: null);

        Assert.True(result.ContinueTransitionalDevelopment);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Role_classification_precedes_every_injected_bootstrap_or_runtime_effect()
    {
        var timeline = new List<string>();
        var arguments = new TrackingArguments(["--host"], timeline);
        var bootstrap = new TrackingBootstrapBoundary(timeline);

        await PrivateHostProcessEntry.RunFirstActionAsync(
            arguments,
            bootstrap,
            (captured, _) =>
            {
                timeline.Add($"host:{captured}");
                return Task.FromResult(0);
            },
            (_, _) => throw new InvalidOperationException("Worker runtime must not run."));

        Assert.Equal(
            [
                "classify-count",
                "classify-count",
                "classify-item:0",
                "capture-host",
                "host:host-bootstrap",
            ],
            timeline);
    }

    [Fact]
    public async Task Host_bootstrap_is_captured_and_removed_before_only_the_host_delegate_runs()
    {
        var timeline = new List<string>();
        var bootstrap = new TrackingBootstrapBoundary(timeline);
        using var cancellation = new CancellationTokenSource();

        var result = await PrivateHostProcessEntry.RunFirstActionAsync(
            ["--host"],
            bootstrap,
            (captured, token) =>
            {
                timeline.Add($"host:{captured}");
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(17);
            },
            (_, _) => throw new InvalidOperationException("Worker runtime must not run."),
            cancellation.Token);

        Assert.False(result.ContinueTransitionalDevelopment);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(["capture-host", "host:host-bootstrap"], timeline);
    }

    [Fact]
    public async Task Worker_bootstrap_is_captured_and_removed_before_only_the_worker_delegate_runs()
    {
        var timeline = new List<string>();
        var bootstrap = new TrackingBootstrapBoundary(timeline);

        var result = await PrivateHostProcessEntry.RunFirstActionAsync(
            ["--worker"],
            bootstrap,
            (_, _) => throw new InvalidOperationException("Host runtime must not run."),
            (captured, _) =>
            {
                timeline.Add($"worker:{captured}");
                return Task.FromResult(23);
            });

        Assert.False(result.ContinueTransitionalDevelopment);
        Assert.Equal(23, result.ExitCode);
        Assert.Equal(["capture-worker", "worker:worker-bootstrap"], timeline);
    }

    [Theory]
    [MemberData(nameof(InvalidStartupForms))]
    public async Task Invalid_first_action_is_poisoned_before_any_private_runtime_effect(
        string[] arguments)
    {
        var timeline = new List<string>();
        var bootstrap = new TrackingBootstrapBoundary(timeline);

        var result = await PrivateHostProcessEntry.RunFirstActionAsync(
            arguments,
            bootstrap,
            (_, _) => throw new InvalidOperationException("Host runtime must not run."),
            (_, _) => throw new InvalidOperationException("Worker runtime must not run."));

        Assert.False(result.ContinueTransitionalDevelopment);
        Assert.Equal(PrivateHostProcessEntry.InvalidInvocationExitCode, result.ExitCode);
        Assert.Equal(["poison-all"], timeline);
    }

    [Theory]
    [InlineData("--host")]
    [InlineData("--worker")]
    public async Task Bootstrap_capture_failure_cannot_reach_a_private_runtime(string role)
    {
        var timeline = new List<string>();
        var bootstrap = new TrackingBootstrapBoundary(timeline, failCapture: true);
        var runtimeCalled = false;

        await Assert.ThrowsAsync<BootstrapCaptureTestException>(() =>
            PrivateHostProcessEntry.RunFirstActionAsync(
                [role],
                bootstrap,
                (_, _) =>
                {
                    runtimeCalled = true;
                    return Task.FromResult(0);
                },
                (_, _) =>
                {
                    runtimeCalled = true;
                    return Task.FromResult(0);
                }));

        Assert.False(runtimeCalled);
        Assert.Equal(
            [role == "--host" ? "capture-host" : "capture-worker"],
            timeline);
    }

    private sealed class TrackingBootstrapBoundary(
        List<string> timeline,
        bool failCapture = false) : IPrivateProcessBootstrapBoundary<string, string>
    {
        public string CaptureAndRemoveHost()
        {
            timeline.Add("capture-host");
            if (failCapture) throw new BootstrapCaptureTestException();
            return "host-bootstrap";
        }

        public string CaptureAndRemoveWorker()
        {
            timeline.Add("capture-worker");
            if (failCapture) throw new BootstrapCaptureTestException();
            return "worker-bootstrap";
        }

        public void PoisonAndRemove() => timeline.Add("poison-all");
    }

    private sealed class TrackingArguments(
        IReadOnlyList<string> arguments,
        List<string> timeline) : IReadOnlyList<string>
    {
        public string this[int index]
        {
            get
            {
                timeline.Add($"classify-item:{index}");
                return arguments[index];
            }
        }

        public int Count
        {
            get
            {
                timeline.Add("classify-count");
                return arguments.Count;
            }
        }

        public IEnumerator<string> GetEnumerator() => arguments.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class BootstrapCaptureTestException : Exception;

    private static int RequiredIndex(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source marker was absent: {value}");
        return index;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "server",
                    "PtkMcpServer",
                    "Program.cs")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException(
            "Repository root not found upward from the test base directory.");
    }
}
