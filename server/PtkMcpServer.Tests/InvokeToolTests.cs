using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PtkMcpServer.Tools;
using PtkRtkTestFixture;

namespace PtkMcpServer.Tests;

// ProcessEnvironment collection: mutates PTK_RTK_PATH and PATH, which a
// parallel reset-driven environment restore would otherwise wipe mid-test.
[Collection("ProcessEnvironment")]
public sealed class InvokeToolTests : IDisposable
{
    private readonly RunspaceHost _host = new(callTimeout: TimeSpan.FromSeconds(60))
    {
        RtkIdentityOverrideForTests = ResolveFixtureRtkIdentity,
    };
    private readonly JobManager _jobs = new(
        Path.Combine(Path.GetTempPath(), "ptk-invoke-jobs-" + Guid.NewGuid().ToString("N")));
    private readonly RawUsageCounter _rawUsage = new();
    private readonly List<string> _outputRoots = [];

    public void Dispose()
    {
        _host.Dispose();
        _jobs.Dispose();
        foreach (var root in _outputRoots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private OutputStore CreateOutputStore(
        Action<string>? artifactCreateStartingForTests = null,
        Action? reservationStartingForTests = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "invoke-output-tests",
            Guid.NewGuid().ToString("N"));
        _outputRoots.Add(root);
        return new OutputStore(new OutputStoreOptions(
            root,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1),
            MaximumArtifactBytes: 2 * 1024 * 1024,
            MaximumSessionBytes: 4 * 1024 * 1024,
            MaximumAggregateBytes: 8 * 1024 * 1024,
            ArtifactCreateStartingForTests: artifactCreateStartingForTests,
            ReservationStartingForTests: reservationStartingForTests));
    }

    private static string AssertSingleRecoveryHandle(string response)
    {
        var handles = Regex.Matches(response, @"ptko_[A-Za-z0-9_-]+")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Assert.Single(handles);
    }

    private static RtkExecutableIdentity? ResolveFixtureRtkIdentity(
        RtkExecutableIdentity? startupIdentity)
    {
        var configured = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        return configured is null
            ? startupIdentity
            : RtkExecutableIdentity.TryCapture(configured);
    }

    [Fact]
    public async Task Returns_plain_output_for_a_clean_call()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'hello from warm runspace'", CancellationToken.None);

        Assert.Contains("hello from warm runspace", text);
        Assert.DoesNotContain("[errors]", text);
        Assert.DoesNotContain("[warnings]", text);
    }

    [Fact]
    public async Task State_persists_across_tool_calls()
    {
        await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$warm = 41", CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$warm + 1", CancellationToken.None);

        Assert.Contains("42", text);
    }

    [Fact]
    public async Task PowerShell_capture_shapes_and_recovers_the_same_single_execution()
    {
        using var store = CreateOutputStore();
        await _host.InvokeAsync(
            "$global:ptkCaptureCount = 0",
            raw: true,
            route: "pwsh");
        var script =
            "$global:ptkCaptureCount++; $id = [guid]::NewGuid().ToString('N'); " +
            "1..41 | ForEach-Object { " +
            "$token = if ($_ -eq 41) { \"$id|RAW_ONLY_ROW_41\" } else { \"$id|ROW_$('{0:D2}' -f $_)\" }; " +
            "[pscustomobject]@{ Token = $token } }";

        var response = await InvokeTool.Invoke(
            _host,
            _jobs,
            _rawUsage,
            script,
            CancellationToken.None,
            route: "pwsh",
            outputStore: store);

        Assert.Contains("objects: 41", response, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"Ptk\.Detached\..+\.[0-9a-f]{32}",
            response);
        Assert.DoesNotContain("RAW_ONLY_ROW_41", response, StringComparison.Ordinal);
        var executionId = Regex.Match(response, @"([0-9a-f]{32})\|ROW_").Groups[1].Value;
        Assert.NotEmpty(executionId);
        var handle = AssertSingleRecoveryHandle(response);
        var status = store.Status(handle);
        Assert.Equal(OutputArtifactState.Available, status.State);
        Assert.True(status.Complete);
        Assert.Equal(OutputProvenance.PowerShellObjects, status.Provenance);
        var recovered = OutputTool.Output(
            store,
            handle,
            maxBytes: OutputStore.MaximumReadBytes);
        Assert.Contains($"{executionId}|RAW_ONLY_ROW_41", recovered, StringComparison.Ordinal);

        var count = await _host.InvokeAsync(
            "$global:ptkCaptureCount",
            raw: true,
            route: "pwsh");
        Assert.Equal("1", count.Output.Trim());
    }

    [Fact]
    public async Task Direct_foreground_text_returns_a_working_same_invocation_handle()
    {
        using var store = CreateOutputStore();
        var script =
            "Write-Warning 'CAPTURED_WARNING'; " +
            "Write-Error 'CAPTURED_ERROR' -ErrorAction Continue; " +
            "1..700 | ForEach-Object { 'DIRECT_ROW_{0:D3}' -f $_ }; " +
            NativeStderr("CAPTURED_STDERR", exit: 7);

        var response = await InvokeTool.Invoke(
            _host,
            _jobs,
            _rawUsage,
            script,
            CancellationToken.None,
            route: "pwsh",
            outputStore: store);

        Assert.Contains("lines elided", response, StringComparison.Ordinal);
        Assert.DoesNotContain("DIRECT_ROW_350", response, StringComparison.Ordinal);
        Assert.Contains("[stderr]", response, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_STDERR", response, StringComparison.Ordinal);
        Assert.Contains("[exit] 7", response, StringComparison.Ordinal);
        Assert.Contains("[errors]", response, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_ERROR", response, StringComparison.Ordinal);
        Assert.Contains("[warnings]", response, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_WARNING", response, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(response, "CAPTURED_STDERR"));
        Assert.Single(Regex.Matches(response, "CAPTURED_ERROR"));
        Assert.Single(Regex.Matches(response, "CAPTURED_WARNING"));
        Assert.Single(Regex.Matches(response, @"\[exit\] 7"));
        var handle = AssertSingleRecoveryHandle(response);
        Assert.Contains(
            $"lines elided - recovery=available: ptk_output handle={handle}",
            response,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"recovery=available: ptk_output handle={handle}",
            response,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "recovery=unavailable: output capture unavailable",
            response,
            StringComparison.Ordinal);
        var status = store.Status(handle);
        Assert.Equal(OutputArtifactState.Available, status.State);
        Assert.True(status.Complete);
        Assert.Equal(OutputProvenance.PowerShellObjects, status.Provenance);
        var recovered = OutputTool.Output(
            store,
            handle,
            maxBytes: OutputStore.MaximumReadBytes);
        Assert.Contains("DIRECT_ROW_350", recovered, StringComparison.Ordinal);
        Assert.Contains("[stderr]", recovered, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_STDERR", recovered, StringComparison.Ordinal);
        Assert.Contains("[exit] 7", recovered, StringComparison.Ordinal);
        Assert.Contains("[errors]", recovered, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_ERROR", recovered, StringComparison.Ordinal);
        Assert.Contains("[warnings]", recovered, StringComparison.Ordinal);
        Assert.Contains("CAPTURED_WARNING", recovered, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(recovered, "CAPTURED_STDERR"));
        Assert.Single(Regex.Matches(recovered, "CAPTURED_ERROR"));
        Assert.Single(Regex.Matches(recovered, "CAPTURED_WARNING"));
    }

    [Fact]
    public async Task Interleaved_streams_are_drained_once_into_the_same_artifact()
    {
        using var store = CreateOutputStore();
        using var capture = new ForegroundOutputCapture(store);
        var script =
            "1..96 | ForEach-Object { " +
            "'INTERLEAVED_OUTPUT_{0:D3}' -f $_; " +
            "Write-Error ('INTERLEAVED_ERROR_{0:D3}' -f $_) -ErrorAction Continue; " +
            "Write-Warning ('INTERLEAVED_WARNING_{0:D3}' -f $_) }; " +
            NativeStderr("INTERLEAVED_STDERR", exit: 9);

        var result = await _host.InvokeWithOutputCaptureAsync(
            script,
            capture,
            route: "pwsh");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(9, result.ExitCode);
        Assert.Equal(
            96,
            result.Errors.Count(error =>
                error.Contains("INTERLEAVED_ERROR_", StringComparison.Ordinal)));
        Assert.Equal(
            96,
            result.Warnings.Count(warning =>
                warning.Contains("INTERLEAVED_WARNING_", StringComparison.Ordinal)));
        Assert.Single(
            Assert.IsType<string[]>(result.Stderr),
            line => line.Contains("INTERLEAVED_STDERR", StringComparison.Ordinal));
        var handle = Assert.IsType<string>(result.OutputRecovery?.Handle);
        var status = store.Status(handle);
        Assert.Equal(OutputArtifactState.Available, status.State);
        Assert.True(status.Complete);
        var recovered = OutputTool.Output(
            store,
            handle,
            maxBytes: OutputStore.MaximumReadBytes);
        Assert.Equal(96, Regex.Matches(recovered, @"INTERLEAVED_OUTPUT_\d{3}").Count);
        Assert.Equal(96, Regex.Matches(recovered, @"INTERLEAVED_ERROR_\d{3}").Count);
        Assert.Equal(96, Regex.Matches(recovered, @"INTERLEAVED_WARNING_\d{3}").Count);
        Assert.Single(Regex.Matches(recovered, "INTERLEAVED_STDERR"));
        Assert.Contains("[exit] 9", recovered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_and_terminating_output_still_seal_complete_artifacts()
    {
        using var store = CreateOutputStore();
        using (var emptyCapture = new ForegroundOutputCapture(store))
        {
            var empty = await _host.InvokeWithOutputCaptureAsync(
                "$null",
                emptyCapture,
                route: "pwsh");

            Assert.True(empty.Success, string.Join(Environment.NewLine, empty.Errors));
            Assert.Equal(string.Empty, empty.Output.Trim());
            var emptyHandle = Assert.IsType<string>(empty.OutputRecovery?.Handle);
            var emptyStatus = store.Status(emptyHandle);
            Assert.Equal(OutputArtifactState.Available, emptyStatus.State);
            Assert.True(emptyStatus.Complete);
        }

        using var terminatingCapture = new ForegroundOutputCapture(store);
        var terminating = await _host.InvokeWithOutputCaptureAsync(
            "throw 'TERMINATING_EMPTY_CAPTURE'",
            terminatingCapture,
            route: "pwsh");

        Assert.False(terminating.Success);
        Assert.Equal(string.Empty, terminating.Output);
        Assert.Contains(
            terminating.Errors,
            error => error.Contains("TERMINATING_EMPTY_CAPTURE", StringComparison.Ordinal));
        var terminatingHandle = Assert.IsType<string>(terminating.OutputRecovery?.Handle);
        var terminatingStatus = store.Status(terminatingHandle);
        Assert.Equal(OutputArtifactState.Available, terminatingStatus.State);
        Assert.True(terminatingStatus.Complete);
        var recovered = OutputTool.Output(
            store,
            terminatingHandle,
            maxBytes: OutputStore.MaximumReadBytes);
        Assert.Contains("[errors]", recovered, StringComparison.Ordinal);
        Assert.Contains("TERMINATING_EMPTY_CAPTURE", recovered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_store_seal_failure_does_not_rerun_or_advertise_a_handle()
    {
        var createAttempts = 0;
        using var store = CreateOutputStore(_ =>
        {
            createAttempts++;
            throw new IOException("injected artifact creation failure");
        });
        await _host.InvokeAsync(
            "$global:ptkSealFailureCount = 0",
            raw: true,
            route: "pwsh");
        var script =
            "$global:ptkSealFailureCount++; " +
            "1..41 | ForEach-Object { [pscustomobject]@{ Row = $_ } }";

        var response = await InvokeTool.Invoke(
            _host,
            _jobs,
            _rawUsage,
            script,
            CancellationToken.None,
            route: "pwsh",
            outputStore: store);

        Assert.Equal(1, createAttempts);
        Assert.Contains("objects: 41", response, StringComparison.Ordinal);
        Assert.Contains(
            "recovery=unavailable: output capture unavailable; command was not rerun",
            response,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ptko_", response, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
        Assert.True(
            store.TryReserve("default", out var firstReservation, out var firstFailure),
            firstFailure);
        Assert.True(
            store.TryReserve("default", out var secondReservation, out var secondFailure),
            secondFailure);
        firstReservation!.Dispose();
        secondReservation!.Dispose();
        var count = await _host.InvokeAsync(
            "$global:ptkSealFailureCount",
            raw: true,
            route: "pwsh");
        Assert.Equal("1", count.Output.Trim());
    }

    [Fact]
    public async Task Slow_output_reservation_is_single_flight_bounded_and_canceled_late()
    {
        using var reserveEntered = new ManualResetEventSlim();
        using var releaseReserve = new ManualResetEventSlim();
        var reserveAttempts = 0;
        using var store = CreateOutputStore(
            reservationStartingForTests: () =>
            {
                if (Interlocked.Increment(ref reserveAttempts) != 1) return;
                reserveEntered.Set();
                releaseReserve.Wait();
            });
        _host.OutputSealLimitForTests = TimeSpan.FromMilliseconds(100);
        await _host.InvokeAsync(
            "$global:ptkSlowReserveCount = 0",
            raw: true,
            route: "pwsh");

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var firstResponse = await InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "$global:ptkSlowReserveCount++; 'FIRST_WITHOUT_CAPTURE'",
                CancellationToken.None,
                route: "pwsh",
                outputStore: store);
            var secondResponse = await InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "$global:ptkSlowReserveCount++; 'SECOND_WITHOUT_CAPTURE'",
                CancellationToken.None,
                route: "pwsh",
                outputStore: store);
            stopwatch.Stop();

            Assert.True(reserveEntered.IsSet);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
            Assert.Equal(1, Volatile.Read(ref reserveAttempts));
            Assert.Contains("FIRST_WITHOUT_CAPTURE", firstResponse, StringComparison.Ordinal);
            Assert.Contains("SECOND_WITHOUT_CAPTURE", secondResponse, StringComparison.Ordinal);
            Assert.DoesNotContain("ptko_", firstResponse, StringComparison.Ordinal);
            Assert.DoesNotContain("ptko_", secondResponse, StringComparison.Ordinal);
            Assert.Contains(
                "recovery=unavailable: output capture unavailable; command was not rerun",
                firstResponse,
                StringComparison.Ordinal);

            releaseReserve.Set();
            OutputCaptureReservation? first = null;
            OutputCaptureReservation? second = null;
            try
            {
                Assert.True(
                    SpinWait.SpinUntil(() =>
                    {
                        first?.Dispose();
                        second?.Dispose();
                        first = null;
                        second = null;
                        if (!store.TryReserve("default", out first, out _)) return false;
                        if (store.TryReserve("default", out second, out _)) return true;
                        first!.Dispose();
                        first = null;
                        return false;
                    }, TimeSpan.FromSeconds(5)),
                    "The late canceled reservation did not restore full capacity.");
            }
            finally
            {
                first?.Dispose();
                second?.Dispose();
            }

            Assert.Empty(Directory.GetFiles(store.RootPathForTests));
            var count = await _host.InvokeAsync(
                "$global:ptkSlowReserveCount",
                raw: true,
                route: "pwsh");
            Assert.Equal("2", count.Output.Trim());
        }
        finally
        {
            releaseReserve.Set();
            _host.OutputSealLimitForTests = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public async Task Slow_output_store_seal_is_bounded_and_never_reruns()
    {
        using var sealEntered = new ManualResetEventSlim();
        using var releaseSeal = new ManualResetEventSlim();
        using var sealHookReturned = new ManualResetEventSlim();
        using var store = CreateOutputStore(_ =>
        {
            sealEntered.Set();
            try { releaseSeal.Wait(); }
            finally { sealHookReturned.Set(); }
        });
        _host.OutputSealLimitForTests = TimeSpan.FromSeconds(2);
        await _host.InvokeAsync(
            "$global:ptkSlowSealCount = 0",
            raw: true,
            route: "pwsh");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        OutputCaptureReservation? firstReservation = null;
        OutputCaptureReservation? secondReservation = null;
        try
        {
            var response = await InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "$global:ptkSlowSealCount++; 'SEALED_OUTPUT'",
                CancellationToken.None,
                route: "pwsh",
                timeoutSeconds: 1,
                outputStore: store);
            stopwatch.Stop();

            Assert.True(sealEntered.IsSet);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
            Assert.Contains("SEALED_OUTPUT", response, StringComparison.Ordinal);
            Assert.Contains(
                "recovery=unavailable: output capture unavailable; command was not rerun",
                response,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ptko_", response, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "call budget expired before output shaping",
                response,
                StringComparison.OrdinalIgnoreCase);
            var count = await _host.InvokeAsync(
                "$global:ptkSlowSealCount",
                raw: true,
                route: "pwsh");
            Assert.Equal("1", count.Output.Trim());

            releaseSeal.Set();
            Assert.True(
                sealHookReturned.Wait(TimeSpan.FromSeconds(5)),
                "The delayed output-store create hook did not return.");
            var emptySince = DateTimeOffset.MinValue;
            Assert.True(
                SpinWait.SpinUntil(
                    () =>
                    {
                        if (Directory.GetFiles(store.RootPathForTests).Length != 0)
                        {
                            emptySince = DateTimeOffset.MinValue;
                            return false;
                        }

                        var now = DateTimeOffset.UtcNow;
                        if (emptySince == DateTimeOffset.MinValue) emptySince = now;
                        return now - emptySince >= TimeSpan.FromMilliseconds(250);
                    },
                    TimeSpan.FromSeconds(5)),
                "The timed-out seal left or later published an artifact file.");
            Assert.True(
                store.TryReserve("default", out firstReservation, out var firstFailure),
                firstFailure);
            Assert.True(
                store.TryReserve("default", out secondReservation, out var secondFailure),
                secondFailure);
            Assert.Empty(Directory.GetFiles(store.RootPathForTests));
        }
        finally
        {
            releaseSeal.Set();
            firstReservation?.Dispose();
            secondReservation?.Dispose();
            _host.OutputSealLimitForTests = TimeSpan.FromSeconds(5);
        }
    }

    [Fact]
    public async Task Timeout_seals_the_emitted_prefix_as_an_incomplete_surviving_artifact()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            RtkIdentityOverrideForTests = ResolveFixtureRtkIdentity,
        };
        using var store = CreateOutputStore();
        var sentinel = Path.Combine(
            Path.GetTempPath(),
            "ptk-timeout-sentinel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var warm = await host.InvokeAsync("'warm'", raw: true, route: "pwsh");
            Assert.True(warm.Success, string.Join(Environment.NewLine, warm.Errors));
            var escapedSentinel = sentinel.Replace("'", "''");
            var response = await InvokeTool.Invoke(
                host,
                _jobs,
                _rawUsage,
                $"[IO.File]::AppendAllText('{escapedSentinel}', 'once'); " +
                "'PREFIX_BEFORE_TIMEOUT'; Start-Sleep -Seconds 60",
                CancellationToken.None,
                route: "pwsh",
                timeoutSeconds: 1,
                outputStore: store);

            Assert.Contains("timed out", response, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("once", File.ReadAllText(sentinel));
            var handle = AssertSingleRecoveryHandle(response);
            var status = OutputTool.Output(store, handle, action: "status");
            Assert.Contains("state=incomplete", status, StringComparison.Ordinal);
            Assert.Contains("complete=false", status, StringComparison.Ordinal);
            Assert.Contains("detail=pipeline_timed_out", status, StringComparison.Ordinal);
            var recovered = OutputTool.Output(
                store,
                handle,
                maxBytes: OutputStore.MaximumReadBytes);
            Assert.Contains("PREFIX_BEFORE_TIMEOUT", recovered, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(sentinel); }
            catch { }
        }
    }

    [Fact]
    public async Task Caller_cancellation_seals_the_same_invocation_prefix_as_incomplete()
    {
        using var store = CreateOutputStore();
        using var cancellation = new CancellationTokenSource();
        var started = Path.Combine(
            Path.GetTempPath(),
            "ptk-cancel-started-" + Guid.NewGuid().ToString("N"));
        var prefix = "PREFIX_BEFORE_CANCEL_" + Guid.NewGuid().ToString("N");
        try
        {
            var setup = await _host.InvokeAsync(
                "$global:ptkCancellationExecutionCount = 0",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            var escapedStarted = started.Replace("'", "''");
            var invocation = InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "$global:ptkCancellationExecutionCount++; " +
                $"'{prefix}'; " +
                $"[IO.File]::WriteAllText('{escapedStarted}', 'started'); " +
                "while ($true) { Start-Sleep -Milliseconds 100 }",
                cancellation.Token,
                route: "pwsh",
                outputStore: store);

            Assert.True(
                SpinWait.SpinUntil(
                    () => File.Exists(started),
                    TimeSpan.FromSeconds(5)),
                "The cancellation probe did not reach its started sentinel.");
            cancellation.Cancel();
            var response = await invocation.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("canceled by the caller", response, StringComparison.OrdinalIgnoreCase);
            var handle = AssertSingleRecoveryHandle(response);
            Assert.Contains(
                $"recovery=available: ptk_output handle={handle}",
                response,
                StringComparison.Ordinal);
            Assert.DoesNotContain("recovery=unavailable", response, StringComparison.Ordinal);

            var status = OutputTool.Output(store, handle, action: "status");
            Assert.Contains("state=incomplete", status, StringComparison.Ordinal);
            Assert.Contains("complete=false", status, StringComparison.Ordinal);
            Assert.Contains("detail=pipeline_canceled", status, StringComparison.Ordinal);
            Assert.DoesNotContain("state=not_found", status, StringComparison.Ordinal);
            var recovered = OutputTool.Output(
                store,
                handle,
                maxBytes: OutputStore.MaximumReadBytes);
            Assert.Contains(prefix, recovered, StringComparison.Ordinal);

            var count = await _host.InvokeAsync(
                "$global:ptkCancellationExecutionCount",
                raw: true,
                route: "pwsh");
            Assert.Equal("1", count.Output.Trim());
        }
        finally
        {
            cancellation.Cancel();
            try { File.Delete(started); }
            catch { }
        }
    }

    [Fact]
    public async Task Errors_and_warnings_are_reported_in_labelled_sections()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, "Write-Warning 'careful'; Write-Error 'boom'; 'partial'", CancellationToken.None);

        Assert.Contains("partial", text);
        Assert.Contains("[errors]", text);
        Assert.Contains("boom", text);
        Assert.Contains("[warnings]", text);
        Assert.Contains("careful", text);
    }

    [Fact]
    public async Task Empty_output_says_so_instead_of_returning_nothing()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "$null", CancellationToken.None);

        Assert.Contains("(no output)", text);
    }

    private static string NativeExit(int code) =>
        OperatingSystem.IsWindows() ? $"cmd /c exit {code}" : $"sh -c 'exit {code}'";

    [Fact]
    public async Task Native_nonzero_exit_code_is_reported()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(7), CancellationToken.None);

        Assert.Contains("[exit] 7", text);
    }

    [Fact]
    public async Task Native_zero_exit_code_is_not_reported()
    {
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(0), CancellationToken.None);

        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Stale_exit_code_is_not_reported_against_a_later_pure_PowerShell_call()
    {
        await InvokeTool.Invoke(_host, _jobs, _rawUsage, NativeExit(7), CancellationToken.None);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "'clean call'", CancellationToken.None);

        Assert.Contains("clean call", text);
        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Native_exit_code_survives_rtk_log_shaping()
    {
        // Round-2 review repro: log-shaped output routes through the module's
        // native rtk leg, whose own exit code must not replace the script's.
        var stubDir = Directory.CreateTempSubdirectory("ptk-rtk-stub-");
        string stubPath;
        if (OperatingSystem.IsWindows())
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.cmd");
            File.WriteAllText(stubPath, "@echo off\r\necho RTKSTUB shaped\r\nexit /b 0\r\n");
        }
        else
        {
            stubPath = Path.Combine(stubDir.FullName, "rtk-stub.sh");
            File.WriteAllText(stubPath, "#!/bin/sh\necho 'RTKSTUB shaped'\nexit 0\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stubPath);
            var script =
                "1..8 | ForEach-Object { \"2026-07-03 10:00:0$_ ERROR worker: step $_ failed\" }; "
                + NativeExit(7);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, script, CancellationToken.None);

            Assert.Contains("[ptk:log via rtk]", text);
            Assert.Contains("[exit] 7", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            stubDir.Delete(recursive: true);
        }
    }

    private static (DirectoryInfo dir, string path) CreateRtkStub(
        string body,
        string? parentDirectory = null,
        string? fileName = null)
    {
        var dir = parentDirectory is null
            ? Directory.CreateTempSubdirectory("ptk-rtk-route-")
            : Directory.CreateDirectory(Path.Combine(
                parentDirectory,
                "ptk-rtk-route-" + Guid.NewGuid().ToString("N")));
        var requestedName = fileName ??
            (OperatingSystem.IsWindows() ? "rtk-stub.exe" : "rtk-stub.sh");
        var path = Path.Combine(
            dir.FullName,
            OperatingSystem.IsWindows()
                ? Path.ChangeExtension(requestedName, ".exe")
                : requestedName);
        WriteRtkStub(path, body);
        return (dir, path);
    }

    private static void WriteRtkStub(string path, string body)
    {
        if (OperatingSystem.IsWindows())
        {
            InstallOrMutateWindowsRtkFixture(path, body);
            File.WriteAllText(
                Path.ChangeExtension(path, ".cmd"),
                "@echo off\r\n" + body.Replace("\n", "\r\n") + "\r\n");
            return;
        }

        File.WriteAllText(path,
            "#!/bin/sh\n" + body.Replace("%*", "\"$@\"").Replace("exit /b ", "exit ") + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void InstallOrMutateWindowsRtkFixture(string path, string body)
    {
        if (File.Exists(path))
        {
            // PE loaders permit an overlay after the image. Appending a body
            // digest leaves the native fixture runnable while making the
            // same-path replacement visible to the production identity hash.
            using var executable = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            executable.Write(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
            return;
        }

        var fixtureAssembly = typeof(FixtureMarker).Assembly.Location;
        var fixtureDirectory = Path.GetDirectoryName(fixtureAssembly)
            ?? throw new InvalidOperationException("RTK fixture assembly directory is unavailable.");
        var fixtureBaseName = Path.GetFileNameWithoutExtension(fixtureAssembly);
        var fixtureAppHost = Path.Combine(fixtureDirectory, fixtureBaseName + ".exe");
        if (!File.Exists(fixtureAppHost))
            throw new FileNotFoundException("RTK fixture apphost is unavailable.", fixtureAppHost);

        File.Copy(fixtureAppHost, path);
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var source = Path.Combine(fixtureDirectory, fixtureBaseName + extension);
            if (!File.Exists(source))
                throw new FileNotFoundException("RTK fixture runtime file is unavailable.", source);
            File.Copy(source, Path.Combine(Path.GetDirectoryName(path)!, fixtureBaseName + extension));
        }
    }

    [Fact]
    public async Task Operator_pinned_rtk_identity_survives_warm_environment_poisoning()
    {
        const string trustedLogName = "PTK_RTK_PINNED_TRUSTED_LOG";
        const string fakeLogName = "PTK_RTK_PINNED_FAKE_LOG";
        var trustedBody = OperatingSystem.IsWindows()
            ? $">>\"%{trustedLogName}%\" echo %*\necho TRUSTED_PINNED_RTK %*\nexit /b 0"
            : $"printf '%s\\n' \"$*\" >> \"${trustedLogName}\"\n" +
              "echo \"TRUSTED_PINNED_RTK $*\"\nexit 0";
        var fakeBody = OperatingSystem.IsWindows()
            ? $">>\"%{fakeLogName}%\" echo %*\necho FAKE_RTK %*\nexit /b 0"
            : $"printf '%s\\n' \"$*\" >> \"${fakeLogName}\"\n" +
              "echo \"FAKE_RTK $*\"\nexit 0";
        var (trustedDir, trustedRtk) = CreateRtkStub(trustedBody);
        var (fakeDir, fakeRtk) = CreateRtkStub(
            fakeBody,
            fileName: OperatingSystem.IsWindows() ? "rtk.cmd" : "rtk");
        var trustedLog = Path.Combine(trustedDir.FullName, "trusted.log");
        var fakeLog = Path.Combine(fakeDir.FullName, "fake.log");
        var expectedDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(trustedRtk)))
            .ToLowerInvariant();
        var savedRtk = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedTrustedLog = Environment.GetEnvironmentVariable(trustedLogName);
        var savedFakeLog = Environment.GetEnvironmentVariable(fakeLogName);
        RunspaceHost? host = null;
        try
        {
            Environment.SetEnvironmentVariable(trustedLogName, trustedLog);
            Environment.SetEnvironmentVariable(fakeLogName, fakeLog);
            host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(60),
                rtkPathOverride: trustedRtk);
            var escapedFakeRtk = fakeRtk.Replace("'", "''");
            var escapedFakeDir = fakeDir.FullName.Replace("'", "''");
            var poisoned = await host.InvokeAsync(
                $"$env:PTK_RTK_PATH = '{escapedFakeRtk}'; " +
                $"$env:PATH = '{escapedFakeDir}' + [IO.Path]::PathSeparator + $env:PATH; " +
                "(Get-Command rtk -CommandType Application).Source",
                raw: true,
                route: "pwsh");
            Assert.True(poisoned.Success, string.Join(Environment.NewLine, poisoned.Errors));
            Assert.Contains(
                Path.GetFullPath(fakeRtk),
                poisoned.Output,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);

            ExecutionPlan? planned = null;
            ExecutionDispatch? auditedDispatch = null;
            var result = await host.InvokeAsync(
                "git status",
                new TestInvocationAuthorizer(
                    (plan, _) =>
                    {
                        planned = plan;
                        return ValueTask.FromResult(true);
                    },
                    (dispatch, _) =>
                    {
                        auditedDispatch = dispatch;
                        return ValueTask.FromResult(true);
                    }),
                route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("TRUSTED_PINNED_RTK git status", result.Output);
            Assert.DoesNotContain("FAKE_RTK", result.Output);
            Assert.Single(File.ReadAllLines(trustedLog));
            Assert.False(File.Exists(fakeLog));
            Assert.NotNull(planned);
            Assert.Equal(ExecutionPath.Rtk, planned.ExecutionPath);
            Assert.Equal(Path.GetFullPath(trustedRtk), planned.RtkExecutableIdentity?.ExecutablePath);
            Assert.Equal(expectedDigest, planned.RtkExecutableIdentity?.CapturedBinaryDigest);
            Assert.Equal(expectedDigest, planned.RtkExecutableIdentity?.AuditBinaryDigest);
            Assert.NotNull(auditedDispatch);
            Assert.Equal(ExecutionPath.Rtk, auditedDispatch.ExecutionPath);
            Assert.Same(planned.RtkExecutableIdentity, auditedDispatch.RtkExecutableIdentity);
            Assert.Equal(expectedDigest, auditedDispatch.RtkExecutableIdentity?.AuditBinaryDigest);
        }
        finally
        {
            host?.Dispose();
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", savedRtk);
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable(trustedLogName, savedTrustedLog);
            Environment.SetEnvironmentVariable(fakeLogName, savedFakeLog);
            trustedDir.Delete(recursive: true);
            fakeDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Same_path_rtk_replacement_during_dispatch_audit_falls_back_before_start()
    {
        const string fakeLogName = "PTK_RTK_REPLACEMENT_FAKE_LOG";
        var initialBody = "echo ORIGINAL_RTK_MUST_NOT_RUN %*\nexit /b 0";
        var replacementBody = OperatingSystem.IsWindows()
            ? $">>\"%{fakeLogName}%\" echo %*\necho REPLACEMENT_RTK_MUST_NOT_RUN %*\nexit /b 0"
            : $"printf '%s\\n' \"$*\" >> \"${fakeLogName}\"\n" +
              "echo \"REPLACEMENT_RTK_MUST_NOT_RUN $*\"\nexit 0";
        var (dir, pinnedRtk) = CreateRtkStub(initialBody);
        var fakeLog = Path.Combine(dir.FullName, "replacement-ran.log");
        var originalCount = Path.Combine(dir.FullName, "exact-original-count.txt");
        var expectedDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pinnedRtk)))
            .ToLowerInvariant();
        var savedFakeLog = Environment.GetEnvironmentVariable(fakeLogName);
        RunspaceHost? host = null;
        try
        {
            Environment.SetEnvironmentVariable(fakeLogName, fakeLog);
            host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(60),
                rtkPathOverride: pinnedRtk);
            var escapedCount = originalCount.Replace("'", "''");
            var original =
                "pwsh -NoProfile -NonInteractive -Command \"" +
                $"[IO.File]::AppendAllText('{escapedCount}', '1'); " +
                "'EXACT_REPLACEMENT_ORIGINAL_ONCE'\"";
            ExecutionPlan? planned = null;
            var auditedDispatches = new List<ExecutionDispatch>();
            var authorizer = new TestInvocationAuthorizer(
                (plan, _) =>
                {
                    planned = plan;
                    return ValueTask.FromResult(true);
                },
                (dispatch, _) =>
                {
                    auditedDispatches.Add(dispatch);
                    if (auditedDispatches.Count == 1)
                    {
                        Assert.Equal(ExecutionPath.Rtk, dispatch.ExecutionPath);
                        Assert.Equal(expectedDigest, dispatch.RtkExecutableIdentity?.AuditBinaryDigest);
                        WriteRtkStub(pinnedRtk, replacementBody);
                    }
                    return ValueTask.FromResult(true);
                });

            var result = await host.InvokeAsync(original, authorizer, route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(InvokeDisposition.Completed, result.Disposition);
            Assert.True(result.UserExecutionStarted);
            Assert.Contains("EXACT_REPLACEMENT_ORIGINAL_ONCE", result.Output);
            Assert.DoesNotContain("REPLACEMENT_RTK_MUST_NOT_RUN", result.Output);
            Assert.Equal("1", File.ReadAllText(originalCount));
            Assert.False(File.Exists(fakeLog));
            Assert.NotNull(planned);
            Assert.Equal(ExecutionPath.Rtk, planned.ExecutionPath);
            Assert.Equal(Path.GetFullPath(pinnedRtk), planned.RtkExecutableIdentity?.ExecutablePath);
            Assert.Equal(expectedDigest, planned.RtkExecutableIdentity?.CapturedBinaryDigest);
            Assert.Collection(
                auditedDispatches,
                first =>
                {
                    Assert.Same(planned, first.Plan);
                    Assert.Equal(ExecutionPath.Rtk, first.ExecutionPath);
                    Assert.Equal(expectedDigest, first.RtkExecutableIdentity?.AuditBinaryDigest);
                },
                fallback =>
                {
                    Assert.Same(planned, fallback.Plan);
                    Assert.Equal(ExecutionPath.PowerShellDirect, fallback.ExecutionPath);
                    Assert.Equal(original, fallback.ExecutionScript);
                    Assert.Equal(
                        ExecutionFallbackReason.RtkExecutableBecameUnavailable,
                        fallback.FallbackReason);
                    Assert.Null(fallback.RtkExecutableIdentity);
                });
            Assert.Equal(ExecutionPath.PowerShellDirect, result.Routing?.EffectivePath);
            Assert.Equal(
                ExecutionFallbackReason.RtkExecutableBecameUnavailable,
                result.Routing?.FallbackReason);
        }
        finally
        {
            host?.Dispose();
            Environment.SetEnvironmentVariable(fakeLogName, savedFakeLog);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rtk_routed_output_is_not_shaped_by_rtk_a_second_time()
    {
        var body = OperatingSystem.IsWindows()
            ? ">>\"%PTK_RTK_TEST_LOG%\" echo %*\n" +
              "if /I \"%~1\"==\"log\" (\n" +
              "  echo SECOND_RTK_LOG_PASS\n" +
              "  exit /b 0\n" +
              ")\n" +
              "echo \u001b[31m2026-07-12 12:00:00 ERROR worker: colored\u001b[0m\n" +
              "for /L %%i in (1,1,1000) do echo 2026-07-12 12:00:01 ERROR worker: line %%i\n" +
              "exit /b 0"
            : "printf '%s\\n' \"$*\" >> \"$PTK_RTK_TEST_LOG\"\n" +
              "if [ \"$1\" = \"log\" ]; then\n" +
              "  echo SECOND_RTK_LOG_PASS\n" +
              "  exit 0\n" +
              "fi\n" +
              "printf '\\033[31m2026-07-12 12:00:00 ERROR worker: colored\\033[0m\\n'\n" +
              "i=1\n" +
              "while [ \"$i\" -le 1000 ]; do\n" +
              "  printf '2026-07-12 12:00:01 ERROR worker: line %s\\n' \"$i\"\n" +
              "  i=$((i + 1))\n" +
              "done\n" +
              "exit 0";
        var (dir, stub) = CreateRtkStub(body);
        var invocationLog = Path.Combine(dir.FullName, "invocations.log");
        var savedRtk = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        var savedLog = Environment.GetEnvironmentVariable("PTK_RTK_TEST_LOG");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", invocationLog);
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                "git status",
                new TestInvocationAuthorizer((plan, _) =>
                {
                    observed = plan;
                    return ValueTask.FromResult(true);
                }),
                route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.NotNull(observed);
            Assert.Equal(ExecutionPath.Rtk, observed.ExecutionPath);
            Assert.Equal(OutputProvenance.RtkUnknown, observed.OutputProvenance);
            Assert.DoesNotContain("SECOND_RTK_LOG_PASS", result.Output);
            Assert.DoesNotContain("[ptk:log", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("[ptk:log via rtk]", result.Output);
            Assert.DoesNotContain('\u001b', result.Output);
            Assert.Contains("lines elided", result.Output, StringComparison.Ordinal);
            Assert.InRange(
                result.Output.Split(Environment.NewLine).Length,
                1,
                402);
            Assert.Equal(["git status"], File.ReadAllLines(invocationLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", savedRtk);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", savedLog);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rtk_capture_ignores_warm_native_error_preferences_and_preserves_error_state()
    {
        const string logName = "PTK_RTK_PREFERENCE_TEST_LOG";
        var body = OperatingSystem.IsWindows()
            ? $">>\"%{logName}%\" echo %*\n" +
              "echo ROUTED_STDOUT\n" +
              "echo ROUTED_STDERR>&2\n" +
              "exit /b 7"
            : $"printf '%s\\n' \"$*\" >> \"${logName}\"\n" +
              "printf '%s\\n' ROUTED_STDOUT\n" +
              "printf '%s\\n' ROUTED_STDERR 1>&2\n" +
              "exit 7";
        var (dir, stub) = CreateRtkStub(body);
        var invocationLog = Path.Combine(dir.FullName, "preference-invocations.log");
        var savedLog = Environment.GetEnvironmentVariable(logName);
        try
        {
            Environment.SetEnvironmentVariable(logName, invocationLog);
            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(60),
                rtkPathOverride: stub);
            var setup = await host.InvokeAsync(
                "$global:PSNativeCommandUseErrorActionPreference = $true; " +
                "$global:ErrorActionPreference = 'Stop'; " +
                "$global:Error.Clear(); 'ready'",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            var result = await host.InvokeAsync("git status", route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(InvokeDisposition.Completed, result.Disposition);
            Assert.True(result.UserExecutionStarted);
            Assert.Contains("ROUTED_STDOUT", result.Output);
            Assert.NotNull(result.Stderr);
            Assert.Contains("ROUTED_STDERR", result.Stderr);
            Assert.Empty(result.Errors);
            Assert.Equal(7, result.ExitCode);
            Assert.Equal(["git status"], File.ReadAllLines(invocationLog));

            var state = await host.InvokeAsync(
                "'native=' + $PSNativeCommandUseErrorActionPreference; " +
                "'action=' + $ErrorActionPreference; " +
                "'errors=' + $Error.Count",
                raw: true,
                route: "pwsh");
            Assert.True(state.Success, string.Join(Environment.NewLine, state.Errors));
            Assert.Contains("native=True", state.Output);
            Assert.Contains("action=Stop", state.Output);
            Assert.Contains("errors=0", state.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(logName, savedLog);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Relative_rtk_override_is_bound_before_the_warm_cwd_changes()
    {
        var processCwd = Directory.GetCurrentDirectory();
        var (rtkDir, stub) = CreateRtkStub(
            OperatingSystem.IsWindows()
                ? "echo RTKROUTE %*\n>rtk-cwd-marker.txt echo ran\nexit /b 0"
                : "echo RTKROUTE \"$@\"\n: > rtk-cwd-marker.txt\nexit 0",
            AppContext.BaseDirectory);
        var warmCwd = Directory.CreateTempSubdirectory("ptk-relative-rtk-cwd-");
        var relativeStub = Path.GetRelativePath(processCwd, stub);
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Assert.False(Path.IsPathFullyQualified(relativeStub));
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", relativeStub);
            var escapedCwd = warmCwd.FullName.Replace("'", "''");
            var moved = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedCwd}'",
                raw: true,
                route: "pwsh");
            Assert.True(moved.Success, string.Join(Environment.NewLine, moved.Errors));
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                "git status",
                new TestInvocationAuthorizer((plan, _) =>
                {
                    observed = plan;
                    return ValueTask.FromResult(true);
                }),
                route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("RTKROUTE git status", result.Output);
            Assert.Equal(Path.GetFullPath(stub), observed?.RtkExecutableIdentity?.ExecutablePath);
            Assert.Equal(warmCwd.FullName, observed?.WorkingDirectory);
            Assert.True(File.Exists(Path.Combine(warmCwd.FullName, "rtk-cwd-marker.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            rtkDir.Delete(recursive: true);
            warmCwd.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Authorization_observes_exact_rtk_preparation_before_exit_reset_and_execution()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var order = new List<string>();
            var authorizationCalls = 0;
            ExecutionPlan? observed = null;
            _host.ExitCodeResetObserverForTests = () => order.Add("reset");

            var result = await _host.InvokeAsync(
                "git status",
                new TestInvocationAuthorizer((preparation, cancellationToken) =>
                {
                    authorizationCalls++;
                    observed = preparation;
                    order.Add("authorize");
                    return ValueTask.FromResult(true);
                }),
                route: "auto");

            Assert.True(result.Success);
            Assert.Contains("RTKROUTE git status", result.Output);
            Assert.Equal(1, authorizationCalls);
            Assert.NotNull(observed);
            Assert.Equal("git status", observed.OriginalScript);
            Assert.Null(observed.ExecutionScript);
            Assert.Equal(["git", "status"], observed.RtkArgumentVector.ToArray());
            Assert.Equal(Directory.GetCurrentDirectory(), observed.WorkingDirectory);
            Assert.Equal(ExecutionDomain.NativeTerminal, observed.Domain);
            Assert.Equal(ExecutionPath.Rtk, observed.ExecutionPath);
            Assert.Equal("rtk", observed.EffectiveRoute);
            Assert.Equal(PreExecutionValidation.None, observed.PreExecutionValidation);
            Assert.Equal(ResolutionContext.Warm, observed.ResolutionContext);
            Assert.Equal(RequestedExecutionRoute.Auto, observed.RequestedRoute);
            Assert.Equal(OutputProvenance.RtkUnknown, observed.OutputProvenance);
            Assert.Collection(
                observed.PermittedFallbacks,
                path => Assert.Equal(ExecutionPath.PowerShellDirect, path));
            Assert.Null(observed.FallbackReason);
            Assert.Equal(stub, observed.RtkExecutableIdentity?.ExecutablePath);
            Assert.Equal(["authorize", "reset"], order);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rtk_disappearing_after_plan_authorization_falls_back_to_the_exact_original_once()
    {
        var (dir, stub) = CreateRtkStub("echo RTK_MUST_NOT_RUN %*\nexit /b 0");
        var originalCount = Path.Combine(dir.FullName, "original-count.txt");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var escapedCount = originalCount.Replace("'", "''");
            var original =
                "pwsh -NoProfile -NonInteractive -Command \"" +
                $"[IO.File]::AppendAllText('{escapedCount}', '1'); 'EXACT_ORIGINAL_ONCE'\"";
            var authorizer = new DeleteRtkAfterPlanAuthorizer(stub);

            var result = await _host.InvokeAsync(
                original,
                authorizer,
                route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("EXACT_ORIGINAL_ONCE", result.Output);
            Assert.DoesNotContain("RTK_MUST_NOT_RUN", result.Output);
            Assert.Equal("1", File.ReadAllText(originalCount));
            Assert.Equal(1, authorizer.PlanAuthorizationCalls);
            Assert.Equal(1, authorizer.DispatchAuthorizationCalls);
            Assert.NotNull(authorizer.Plan);
            Assert.Equal(ExecutionPath.Rtk, authorizer.Plan.ExecutionPath);
            Assert.Collection(
                authorizer.Plan.PermittedFallbacks,
                path => Assert.Equal(ExecutionPath.PowerShellDirect, path));
            Assert.NotNull(authorizer.Dispatch);
            Assert.Same(authorizer.Plan, authorizer.Dispatch.Plan);
            Assert.Equal(original, authorizer.Dispatch.ExecutionScript);
            Assert.Equal(ExecutionPath.PowerShellDirect, authorizer.Dispatch.ExecutionPath);
            Assert.Equal(OutputProvenance.PowerShellObjects, authorizer.Dispatch.OutputProvenance);
            Assert.Equal(
                ExecutionFallbackReason.RtkExecutableBecameUnavailable,
                authorizer.Dispatch.FallbackReason);
            Assert.Null(authorizer.Dispatch.RtkExecutableIdentity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Proven_rtk_start_failure_uses_the_audited_exact_fallback_once()
    {
        var (dir, stub) = CreateRtkStub("echo RTK_MUST_NOT_RUN %*\nexit /b 0");
        var originalCount = Path.Combine(dir.FullName, "start-fallback-count.txt");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(stub, "not a Windows executable");
            }
            else
            {
                File.SetUnixFileMode(
                    stub,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var escapedCount = originalCount.Replace("'", "''");
            var original =
                "pwsh -NoProfile -NonInteractive -Command \"" +
                $"[IO.File]::AppendAllText('{escapedCount}', '1'); " +
                "'EXACT_START_FAILURE_FALLBACK'\"";
            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(60),
                rtkPathOverride: stub);
            var dispatches = new List<ExecutionDispatch>();
            var authorizer = new TestInvocationAuthorizer(
                (_, _) => ValueTask.FromResult(true),
                (dispatch, _) =>
                {
                    dispatches.Add(dispatch);
                    return ValueTask.FromResult(true);
                });

            var result = await host.InvokeAsync(original, authorizer, route: "rtk");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("EXACT_START_FAILURE_FALLBACK", result.Output);
            Assert.Equal("1", File.ReadAllText(originalCount));
            Assert.Collection(
                dispatches,
                first => Assert.Equal(ExecutionPath.Rtk, first.ExecutionPath),
                second =>
                {
                    Assert.Equal(ExecutionPath.PowerShellDirect, second.ExecutionPath);
                    Assert.Equal(original, second.ExecutionScript);
                    Assert.Empty(second.RtkArgumentVector);
                    Assert.Equal(
                        ExecutionFallbackReason.RtkExecutionPreparationFailed,
                        second.FallbackReason);
                });
            Assert.Equal(ExecutionPath.PowerShellDirect, result.Routing?.EffectivePath);
            Assert.Equal(
                ExecutionFallbackReason.RtkExecutionPreparationFailed,
                result.Routing?.FallbackReason);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rtk_disappearing_during_dispatch_authorization_reauthorizes_the_exact_original_once()
    {
        var stubBody = OperatingSystem.IsWindows()
            ? "echo ran>>\"%PTK_RTK_TEST_LOG%\"\necho RTK_MUST_NOT_RUN %*\nexit /b 0"
            : "printf 'ran\\n' >> \"$PTK_RTK_TEST_LOG\"\n" +
              "echo RTK_MUST_NOT_RUN %*\nexit 0";
        var (dir, stub) = CreateRtkStub(stubBody);
        var stubLog = Path.Combine(dir.FullName, "stub-ran.txt");
        var originalCount = Path.Combine(dir.FullName, "dispatch-original-count.txt");
        var savedRtk = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        var savedLog = Environment.GetEnvironmentVariable("PTK_RTK_TEST_LOG");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", stubLog);
            var escapedCount = originalCount.Replace("'", "''");
            var original =
                "pwsh -NoProfile -NonInteractive -Command \"" +
                $"[IO.File]::AppendAllText('{escapedCount}', '1'); 'EXACT_DISPATCH_ORIGINAL_ONCE'\"";
            ExecutionPlan? observedPlan = null;
            var dispatches = new List<ExecutionDispatch>();
            var stubExistedAtFirstDispatch = false;
            var authorizer = new TestInvocationAuthorizer(
                (plan, _) =>
                {
                    observedPlan = plan;
                    return ValueTask.FromResult(true);
                },
                (dispatch, _) =>
                {
                    dispatches.Add(dispatch);
                    if (dispatches.Count == 1)
                    {
                        stubExistedAtFirstDispatch = File.Exists(stub);
                        File.Delete(stub);
                    }
                    return ValueTask.FromResult(true);
                });

            var result = await _host.InvokeAsync(
                original,
                authorizer,
                route: "auto");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(InvokeDisposition.Completed, result.Disposition);
            Assert.True(result.UserExecutionStarted);
            Assert.Contains("EXACT_DISPATCH_ORIGINAL_ONCE", result.Output);
            Assert.DoesNotContain("RTK_MUST_NOT_RUN", result.Output);
            Assert.True(stubExistedAtFirstDispatch);
            Assert.False(File.Exists(stubLog));
            Assert.Equal("1", File.ReadAllText(originalCount));
            Assert.NotNull(observedPlan);
            Assert.Equal(ExecutionPath.Rtk, observedPlan.ExecutionPath);
            Assert.Collection(
                dispatches,
                first =>
                {
                    Assert.Same(observedPlan, first.Plan);
                    Assert.Equal(ExecutionPath.Rtk, first.ExecutionPath);
                    Assert.Null(first.FallbackReason);
                },
                second =>
                {
                    Assert.Same(observedPlan, second.Plan);
                    Assert.Equal(original, second.ExecutionScript);
                    Assert.Equal(ExecutionPath.PowerShellDirect, second.ExecutionPath);
                    Assert.Equal(OutputProvenance.PowerShellObjects, second.OutputProvenance);
                    Assert.Equal(
                        ExecutionFallbackReason.RtkExecutableBecameUnavailable,
                        second.FallbackReason);
                    Assert.Null(second.RtkExecutableIdentity);
                });
            Assert.Equal(ExecutionPath.PowerShellDirect, result.Routing?.EffectivePath);
            Assert.Equal(
                ExecutionFallbackReason.RtkExecutableBecameUnavailable,
                result.Routing?.FallbackReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", savedRtk);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", savedLog);
            dir.Delete(recursive: true);
        }
    }

    private sealed class DeleteRtkAfterPlanAuthorizer(string rtkPath) : IInvocationAuthorizer
    {
        internal int PlanAuthorizationCalls { get; private set; }
        internal int DispatchAuthorizationCalls { get; private set; }
        internal ExecutionPlan? Plan { get; private set; }
        internal ExecutionDispatch? Dispatch { get; private set; }

        public ValueTask<bool> AuthorizePlanAsync(
            ExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            PlanAuthorizationCalls++;
            Plan = plan;
            File.Delete(rtkPath);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> AuthorizeDispatchAsync(
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken)
        {
            DispatchAuthorizationCalls++;
            Dispatch = dispatch;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> RecordValidatorStartedAsync(
            ExecutionDispatch dispatch,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> RecordValidatorCompletedAsync(
            ExecutionDispatch dispatch,
            BashSyntaxValidationResult result,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    [Theory]
    [InlineData("function global:git { param([Parameter(ValueFromRemainingArguments=$true)]$rest) 'shadow' }")]
    [InlineData("Set-Alias -Name git -Value Get-Date -Scope Global")]
    public async Task Warm_function_or_alias_shadow_keeps_native_name_on_direct_route(string shadowScript)
    {
        var (dir, stub) = CreateRtkStub("echo must-not-run\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var shadow = await _host.InvokeAsync(shadowScript, raw: true, route: "pwsh");
            Assert.True(shadow.Success, string.Join(Environment.NewLine, shadow.Errors));
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                "git status",
                new TestInvocationAuthorizer((preparation, _) =>
                {
                    observed = preparation;
                    return ValueTask.FromResult(false);
                }),
                route: "auto");

            Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
            Assert.NotNull(observed);
            Assert.Equal(ExecutionPath.PowerShellDirect, observed.ExecutionPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Forced_rtk_function_executes_the_original_once_without_running_rtk()
    {
        var (dir, stub) = CreateRtkStub("echo must-not-run\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var defined = await _host.InvokeAsync(
                "function global:ptkForcedFunction { " +
                "$global:ptkForcedCount = 1 + [int]$global:ptkForcedCount; " +
                "\"FUNCTION:$global:ptkForcedCount\" }",
                raw: true,
                route: "pwsh");
            Assert.True(defined.Success, string.Join(Environment.NewLine, defined.Errors));
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                "ptkForcedFunction",
                new TestInvocationAuthorizer((plan, _) =>
                {
                    observed = plan;
                    return ValueTask.FromResult(true);
                }),
                route: "rtk");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Contains("FUNCTION:1", result.Output);
            Assert.DoesNotContain("must-not-run", result.Output);
            Assert.NotNull(observed);
            Assert.Equal(ExecutionDomain.PowerShell, observed.Domain);
            Assert.Equal(ExecutionPath.PowerShellDirect, observed.ExecutionPath);
            Assert.Equal(
                ExecutionFallbackReason.RtkResolutionNotApplication,
                observed.FallbackReason);
            var count = await _host.InvokeAsync(
                "$global:ptkForcedCount",
                raw: true,
                route: "pwsh");
            Assert.Equal("1", count.Output.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Forced_rtk_direct_fallback_labels_the_single_execution_without_requesting_retry()
    {
        var (dir, stub) = CreateRtkStub("echo must-not-run\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var defined = await _host.InvokeAsync(
                "function global:ptkRouteLabelFunction { 'ROUTE_LABEL_ORIGINAL' }",
                raw: true,
                route: "pwsh");
            Assert.True(defined.Success, string.Join(Environment.NewLine, defined.Errors));

            var text = await InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "ptkRouteLabelFunction",
                CancellationToken.None,
                route: "rtk");

            Assert.Contains("ROUTE_LABEL_ORIGINAL", text);
            Assert.DoesNotContain("must-not-run", text);
            Assert.Contains(
                "[route] requested=rtk effective=powershell_direct " +
                "fallback=rtk_resolution_not_application;",
                text);
            Assert.Contains(
                "the original script was dispatched once and PTK did not retry it.",
                text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Windows_external_script_shadows_same_name_application_before_rtk_routing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var commandDir = Directory.CreateTempSubdirectory("ptk-script-shadow-");
        var commandName = "ptkshadow" + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(commandDir.FullName, commandName + ".ps1"), "'script-shadow'");
        File.WriteAllBytes(Path.Combine(commandDir.FullName, commandName + ".exe"), []);
        var (rtkDir, stub) = CreateRtkStub("echo must-not-run\nexit /b 0");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedRtk = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                commandDir.FullName + Path.PathSeparator + savedPath);
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                commandName,
                new TestInvocationAuthorizer((preparation, _) =>
                {
                    observed = preparation;
                    return ValueTask.FromResult(false);
                }),
                route: "auto");

            Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
            Assert.NotNull(observed);
            Assert.Equal(ExecutionPath.PowerShellDirect, observed.ExecutionPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", savedRtk);
            rtkDir.Delete(recursive: true);
            commandDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Forced_rtk_original_path_reports_its_exact_fallback_metadata()
    {
        var (dir, stub) = CreateRtkStub("echo must-not-run\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            ExecutionPlan? observed = null;

            var result = await _host.InvokeAsync(
                "$global:forcedFallbackRan = 1",
                new TestInvocationAuthorizer((plan, cancellationToken) =>
                {
                    observed = plan;
                    return ValueTask.FromResult(true);
                }),
                route: "rtk");

            Assert.True(result.Success);
            Assert.NotNull(observed);
            Assert.Equal(ExecutionDomain.MixedDataflow, observed.Domain);
            Assert.Equal(ExecutionPath.PowerShellDirect, observed.ExecutionPath);
            Assert.Equal(RequestedExecutionRoute.Rtk, observed.RequestedRoute);
            Assert.Empty(observed.PermittedFallbacks);
            Assert.Equal(ExecutionFallbackReason.RtkIneligibleShape, observed.FallbackReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_native_file_capture_executes_once_then_suggests_redirection()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-");
        var outputPath = Path.Combine(directory.FullName, "version capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var location = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'",
                raw: true,
                route: "pwsh");
            Assert.True(location.Success, string.Join(Environment.NewLine, location.Errors));

            var result = await _host.InvokeAsync(
                "dotnet --version | Set-Content -Path 'version capture.txt'");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.Equal(InvokeDisposition.Completed, result.Disposition);
            Assert.Empty(result.Errors);
            Assert.True(File.Exists(outputPath));
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(outputPath)));
            var guidance = Assert.Single(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
            Assert.Contains("completed unchanged", guidance, StringComparison.Ordinal);
            Assert.Contains(
                "dotnet --version > 'version capture.txt'",
                guidance,
                StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_when_the_original_pipeline_reports_errors()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-error-");
        var outputPath = Path.Combine(directory.FullName, "missing", "capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var location = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'",
                raw: true,
                route: "pwsh");
            Assert.True(location.Success, string.Join(Environment.NewLine, location.Errors));

            var result = await _host.InvokeAsync(
                "dotnet --version | Set-Content -Path 'missing/capture.txt'");

            Assert.NotEmpty(result.Errors);
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_for_a_nonzero_native_exit()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-exit-");
        var outputPath = Path.Combine(directory.FullName, "capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var location = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'",
                raw: true,
                route: "pwsh");
            Assert.True(location.Success, string.Join(Environment.NewLine, location.Errors));

            var result = await _host.InvokeAsync(
                "dotnet __ptk_command_that_does_not_exist__ | Set-Content -Path capture.txt");

            Assert.NotNull(result.ExitCode);
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_when_errors_are_suppressed()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-ignore-");
        var outputPath = Path.Combine(directory.FullName, "missing", "capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var setup = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'; " +
                "$global:ErrorActionPreference = 'Ignore'",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            var result = await _host.InvokeAsync(
                "dotnet --version | Set-Content -Path 'missing/capture.txt'");

            Assert.True(result.Success);
            Assert.Empty(result.Errors);
            Assert.True(result.PipelineHadErrors);
            Assert.False(File.Exists(outputPath));
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_under_whatif_preference()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-whatif-");
        var outputPath = Path.Combine(directory.FullName, "capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var setup = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'; " +
                "$global:WhatIfPreference = $true",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            var result = await _host.InvokeAsync(
                "dotnet --version | Set-Content -Path capture.txt");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.False(File.Exists(outputPath));
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_with_default_parameter_overrides()
    {
        var directory = Directory.CreateTempSubdirectory("ptk-mixed-guidance-defaults-");
        var outputPath = Path.Combine(directory.FullName, "capture.txt");
        var escapedDirectory = directory.FullName.Replace("'", "''");
        try
        {
            var setup = await _host.InvokeAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'; " +
                "$global:PSDefaultParameterValues = @{ 'Set-Content:NoNewline' = $true }",
                raw: true,
                route: "pwsh");
            Assert.True(setup.Success, string.Join(Environment.NewLine, setup.Errors));

            var result = await _host.InvokeAsync(
                "dotnet --version | Set-Content -Path capture.txt");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.True(File.Exists(outputPath));
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Mixed_guidance_is_not_emitted_outside_the_filesystem_provider()
    {
        var environmentName = "PTK_MIXED_PROVIDER_" + Guid.NewGuid().ToString("N");
        try
        {
            using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(30));
            var location = await host.InvokeAsync(
                "Set-Location Env:",
                raw: true,
                route: "pwsh");
            Assert.True(location.Success, string.Join(Environment.NewLine, location.Errors));

            var result = await host.InvokeAsync(
                $"dotnet --version | Set-Content {environmentName}");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            Assert.False(string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(environmentName)));
            Assert.DoesNotContain(result.Warnings, warning =>
                warning.Contains("[ptk:routing]", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Theory]
    [InlineData(true, "rtk")]
    [InlineData(false, "pwsh")]
    public async Task Explicit_direct_consent_permits_no_fallback(bool raw, string route)
    {
        ExecutionPlan? observed = null;

        var result = await _host.InvokeAsync(
            "'direct execution'",
            new TestInvocationAuthorizer((preparation, cancellationToken) =>
            {
                observed = preparation;
                return ValueTask.FromResult(true);
            }),
            raw: raw,
            route: route);

        Assert.True(result.Success);
        Assert.NotNull(observed);
        Assert.Equal(ExecutionPath.PowerShellDirect, observed.ExecutionPath);
        Assert.Equal(
            route == "pwsh"
                ? RequestedExecutionRoute.PowerShell
                : RequestedExecutionRoute.Rtk,
            observed.RequestedRoute);
        Assert.Empty(observed.PermittedFallbacks);
        Assert.Null(observed.FallbackReason);
    }

    [Fact]
    public async Task Authorization_refusal_runs_once_before_reset_and_prevents_user_execution()
    {
        var authorizationCalls = 0;
        var resetCalls = 0;
        _host.ExitCodeResetObserverForTests = () => resetCalls++;

        var refused = await _host.InvokeAsync(
            "$global:authorizationRefusalSentinel = 'RAN'",
            new TestInvocationAuthorizer((preparation, cancellationToken) =>
            {
                authorizationCalls++;
                return ValueTask.FromResult(false);
            }),
            route: "pwsh");

        Assert.False(refused.Success);
        Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
        Assert.False(refused.UserExecutionStarted);
        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, resetCalls);
        Assert.DoesNotContain("authorizationRefusalSentinel", string.Join('\n', refused.Errors));

        _host.ExitCodeResetObserverForTests = null;
        var sentinel = await _host.InvokeAsync(
            "if ($null -eq $global:authorizationRefusalSentinel) { 'never-ran' } else { $global:authorizationRefusalSentinel }",
            route: "pwsh");
        Assert.Contains("never-ran", sentinel.Output);
    }

    [Fact]
    public async Task Authorization_exception_is_sanitized_and_prevents_user_execution()
    {
        const string secret = "audit-secret-that-must-not-escape";
        var authorizationCalls = 0;
        var resetCalls = 0;
        _host.ExitCodeResetObserverForTests = () => resetCalls++;

        var refused = await _host.InvokeAsync(
            "$global:authorizationExceptionSentinel = 'RAN'",
            new TestInvocationAuthorizer((preparation, cancellationToken) =>
            {
                authorizationCalls++;
                throw new InvalidOperationException(secret);
            }),
            route: "pwsh");

        Assert.False(refused.Success);
        Assert.Equal(InvokeDisposition.NotStarted, refused.Disposition);
        Assert.False(refused.UserExecutionStarted);
        Assert.DoesNotContain(secret, string.Join('\n', refused.Errors));
        Assert.DoesNotContain("authorizationExceptionSentinel", string.Join('\n', refused.Errors));
        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, resetCalls);

        _host.ExitCodeResetObserverForTests = null;
        var sentinel = await _host.InvokeAsync(
            "if ($null -eq $global:authorizationExceptionSentinel) { 'never-ran' } else { $global:authorizationExceptionSentinel }",
            route: "pwsh");
        Assert.Contains("never-ran", sentinel.Output);
    }

    [Fact]
    public async Task Caller_cancellation_waits_for_server_owned_authorization_barrier_then_never_executes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierTokenCanBeCanceled = true;
        var resetCalls = 0;
        _host.ExitCodeResetObserverForTests = () => resetCalls++;
        using var cts = new CancellationTokenSource();

        var invocation = _host.InvokeAsync(
            "$global:barrierCancellationSentinel = 'RAN'",
            new TestInvocationAuthorizer((preparation, barrierToken) =>
            {
                barrierTokenCanBeCanceled = barrierToken.CanBeCanceled;
                entered.SetResult();
                return new ValueTask<bool>(release.Task);
            }),
            cancellationToken: cts.Token,
            route: "pwsh");

        await entered.Task;
        cts.Cancel();
        await Task.Delay(150);
        var completedBeforeRelease = invocation.IsCompleted;
        var resetCallsBeforeRelease = resetCalls;

        release.SetResult(true);
        var result = await invocation;
        Assert.False(barrierTokenCanBeCanceled);
        Assert.False(completedBeforeRelease);
        Assert.Equal(0, resetCallsBeforeRelease);
        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
        Assert.False(result.UserExecutionStarted);
        Assert.Equal(0, resetCalls);

        _host.ExitCodeResetObserverForTests = null;
        var sentinel = await _host.InvokeAsync(
            "if ($null -eq $global:barrierCancellationSentinel) { 'never-ran' } else { $global:barrierCancellationSentinel }",
            route: "pwsh");
        Assert.Contains("never-ran", sentinel.Output);
    }

    [Fact]
    public async Task Single_native_command_routes_through_rtk()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Seam_absent_rtk_route_reports_recovery_unavailable_without_a_handle()
    {
        var body = OperatingSystem.IsWindows()
            ? ">>\"%PTK_RTK_TEST_LOG%\" echo %*\necho RTKROUTE %*\nexit /b 0"
            : "printf '%s\\n' \"$*\" >> \"$PTK_RTK_TEST_LOG\"\necho RTKROUTE %*\nexit 0";
        var (dir, stub) = CreateRtkStub(body);
        var invocationLog = Path.Combine(dir.FullName, "capture-invocations.log");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        var savedLog = Environment.GetEnvironmentVariable("PTK_RTK_TEST_LOG");
        using var store = CreateOutputStore();
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", invocationLog);
            var text = await InvokeTool.Invoke(
                _host,
                _jobs,
                _rawUsage,
                "git status",
                CancellationToken.None,
                outputStore: store);

            Assert.Contains("RTKROUTE git status", text, StringComparison.Ordinal);
            Assert.Contains(
                "recovery=unavailable: rtk capture unsupported",
                text,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ptko_", text, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(store.RootPathForTests));
            Assert.Equal(["git status"], File.ReadAllLines(invocationLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            Environment.SetEnvironmentVariable("PTK_RTK_TEST_LOG", savedLog);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Route_pwsh_forces_plain_execution()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 0");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(
                _host, _jobs, _rawUsage, "git --version", CancellationToken.None, route: "pwsh");

            Assert.DoesNotContain("RTKROUTE", text);
            Assert.Contains("git version", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Routed_command_exit_code_is_reported()
    {
        var (dir, stub) = CreateRtkStub("echo RTKROUTE %*\nexit /b 5");
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
            Assert.Contains("[exit] 5", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_is_reported_with_the_state_loss_warning()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, _jobs, _rawUsage, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("timeout", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recycled", text);
    }

    [Fact]
    public async Task Timeout_error_teaches_both_recovery_paths()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));

        var text = await InvokeTool.Invoke(host, _jobs, _rawUsage, "Start-Sleep -Seconds 60", CancellationToken.None);

        Assert.Contains("background=true", text);
        Assert.Contains("timeoutSeconds", text);
        // Live use lost a debugging detour to changed resolution after a
        // recycle (v2-feedback slice 3): the message must point at ptk_state.
        Assert.Contains("ptk_state", text);
    }

    [Fact]
    public async Task Rtk_install_nag_is_filtered_but_real_stderr_survives()
    {
        // sh's echo may process the backslash in the nag's /!\ marker, so the
        // Unix sidecar uses printf with a quoted literal.
        var body = OperatingSystem.IsWindows()
            ? "echo [rtk] /!\\ No hook installed - run rtk init 1>&2\n" +
              "echo [rtk] /!\\ unexpected-real-diagnostic 1>&2\n" +
              "echo real-stderr-detail 1>&2\n" +
              "echo RTKROUTE %*\n" +
              "exit /b 0"
            : "printf '%s\\n' '[rtk] /!\\ No hook installed - run rtk init' 1>&2\n" +
              "printf '%s\\n' '[rtk] /!\\ unexpected-real-diagnostic' 1>&2\n" +
              "echo real-stderr-detail 1>&2\n" +
              "echo \"RTKROUTE $@\"\n" +
              "exit 0";
        var (dir, stub) = CreateRtkStub(body);
        var saved = Environment.GetEnvironmentVariable("PTK_RTK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", stub);
            var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, "git status", CancellationToken.None);

            Assert.Contains("RTKROUTE git status", text);
            Assert.DoesNotContain("No hook installed", text);
            Assert.Contains("real-stderr-detail", text);
            // Only the specific banner is filtered - an rtk-prefixed line that
            // is NOT the nag is a real diagnostic and must survive (v2fb-1).
            Assert.Contains("unexpected-real-diagnostic", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_RTK_PATH", saved);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Per_call_timeoutSeconds_overrides_the_default()
    {
        // Default is 60s here; the 1s override must fire first (a broken
        // override would let the sleep run 8s and return success).
        var result = await _host.InvokeAsync("Start-Sleep -Seconds 8", timeoutSeconds: 1);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task A_wedged_shaping_call_cannot_hold_the_gate_forever()
    {
        // A .ps1 rtk stub runs IN the warm runspace, so its sleep wedges the
        // shaping pipeline exactly like a hung rtk child would.
        var dir = Directory.CreateTempSubdirectory("ptk-wedge-stub-");
        var stub = Path.Combine(dir.FullName, "rtk-sleep.ps1");
        File.WriteAllText(stub, "param($verb, $path) Start-Sleep -Seconds 120");
        using var host = new RunspaceHost(
            callTimeout: TimeSpan.FromSeconds(2),
            rtkPathOverride: stub);
        try
        {
            var logShaped = string.Join('\n', Enumerable.Range(1, 8)
                .Select(i => $"2026-07-08 10:00:0{i % 10} INFO worker: step {i}"));

            var shaped = await host.ShapeTextAsync(logShaped);

            Assert.Contains("step 1", shaped);
            Assert.Contains("shaping timed out", shaped);

            var after = await host.InvokeAsync("1 + 1");
            Assert.True(after.Success);
            Assert.Contains("2", after.Output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // Issue #5 matrix: native stderr is neutral [stderr]; [errors] is reserved
    // for genuine PowerShell error records. The partition predicate is
    // invocation provenance (Application command), not the forgeable FQID or
    // exception type.
    private static string NativeStderr(string message, int exit = 0) =>
        OperatingSystem.IsWindows()
            ? $"cmd /c \"echo {message} 1>&2 & exit /b {exit}\""
            : $"sh -c 'echo {message} 1>&2; exit {exit}'";

    [Fact]
    public async Task Successful_native_stderr_is_neutral_not_an_error()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("normal diagnostic"), CancellationToken.None);

        Assert.Contains("[stderr]", text);
        Assert.Contains("normal diagnostic", text);
        Assert.DoesNotContain("[errors]", text);
        Assert.DoesNotContain("[exit]", text);
    }

    [Fact]
    public async Task Native_stderr_with_nonzero_exit_keeps_both_sections()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("failing diagnostic", exit: 7), CancellationToken.None);

        Assert.Contains("[stderr]", text);
        Assert.Contains("failing diagnostic", text);
        Assert.Contains("[exit] 7", text);
    }

    [Fact]
    public async Task Native_stderr_labeling_is_consistent_across_raw_and_route()
    {
        var raw = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("raw diagnostic"), CancellationToken.None, raw: true);
        Assert.Contains("[stderr]", raw);
        Assert.DoesNotContain("[errors]", raw);

        var routed = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, NativeStderr("routed diagnostic"), CancellationToken.None, route: "pwsh");
        Assert.Contains("[stderr]", routed);
        Assert.DoesNotContain("[errors]", routed);
    }

    [Fact]
    public async Task Forged_native_error_id_stays_under_errors()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -ErrorId NativeCommandError -Message forged-id", CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-id", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Forged_native_exception_stays_under_errors()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -Exception ([System.Management.Automation.RemoteException]::new('forged-ex'))",
            CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-ex", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Combined_forged_id_and_exception_stays_under_errors()
    {
        // An FQID+exception classifier passes the isolated forgeries but not
        // this one; only invocation provenance holds (plan finding i56p-11).
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage,
            "Write-Error -ErrorId NativeCommandError -Exception ([System.Management.Automation.RemoteException]::new('forged-both'))",
            CancellationToken.None);

        Assert.Contains("[errors]", text);
        Assert.Contains("forged-both", text);
        Assert.DoesNotContain("[stderr]", text);
    }

    [Fact]
    public async Task Terminating_native_error_path_keeps_exit_code_and_stderr()
    {
        // $PSNativeCommandUseErrorActionPreference + Stop routes a nonzero-exit
        // native command through the RuntimeException catch, which previously
        // dropped [exit] N (plan finding i56p-6).
        var script =
            "$ErrorActionPreference = 'Stop'; $PSNativeCommandUseErrorActionPreference = $true; " +
            NativeStderr("terminating diagnostic", exit: 7);
        var text = await InvokeTool.Invoke(_host, _jobs, _rawUsage, script, CancellationToken.None);

        Assert.Contains("[exit] 7", text);
        Assert.Contains("[stderr]", text);
        Assert.Contains("terminating diagnostic", text);
        Assert.Contains("[errors]", text); // the terminating record itself is a genuine error
    }

    // Issue #6 matrix: timeoutSeconds is a total wall-clock budget covering
    // queue wait, preflight, and execution.

    [Fact]
    public async Task Queued_call_whose_budget_expires_never_executes()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var slow = host.InvokeAsync("Start-Sleep -Seconds 6; 'slow-done'");
        await Task.Delay(500); // let the slow call own the gate

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var queued = await host.InvokeAsync("$global:queuedRan = 1", timeoutSeconds: 1);
        sw.Stop();

        Assert.False(queued.Success);
        Assert.True(queued.TimedOut);
        Assert.Contains("NOT executed", queued.Errors[0]);
        Assert.Equal(InvokeDisposition.NotStarted, queued.Disposition);
        Assert.False(queued.UserExecutionStarted);
        // Fails fast at budget expiry, not after the active call finishes.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"queued call took {sw.Elapsed}");

        // The active call and its warm state are untouched.
        var slowResult = await slow;
        Assert.True(slowResult.Success);
        Assert.Contains("slow-done", slowResult.Output);
        var check = await host.InvokeAsync("if ($null -eq $global:queuedRan) { 'never-ran' } else { 'RAN' }");
        Assert.Contains("never-ran", check.Output);
    }

    [Fact]
    public async Task Preflight_cannot_outlive_the_budget()
    {
        // A wedged trusted preflight must remain inside the main pipeline's
        // wall-clock budget (plan finding i56p-1, d3-1 class).
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            PreflightDelayForTests = TimeSpan.FromSeconds(8),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await host.InvokeAsync("'hello'", timeoutSeconds: 2);
        sw.Stop();

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.True(result.WarmStateLost);
        Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
        Assert.False(result.UserExecutionStarted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"preflight-stuck call took {sw.Elapsed}");

        // The wedged runspace was recycled; the next call works.
        host.PreflightDelayForTests = TimeSpan.Zero;
        var after = await host.InvokeAsync("1 + 1");
        Assert.True(after.Success);
        Assert.Contains("2", after.Output);
    }

    [Fact]
    public async Task Background_prestart_respects_the_budget_and_starts_no_job()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var slow = host.InvokeAsync("Start-Sleep -Seconds 6");
        await Task.Delay(500);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var text = await InvokeTool.Invoke(
            host, _jobs, _rawUsage, "'x'", CancellationToken.None, background: true, timeoutSeconds: 1);
        sw.Stop();

        // Busy expiry fails the start; no job may run in the server process
        // cwd (plan findings i56p-3, i56p-4).
        Assert.Contains("[job not started]", text);
        Assert.Contains("busy", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_jobs.List());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"background pre-start took {sw.Elapsed}");
        await slow;
    }

    [Fact]
    public async Task Timeout_response_does_not_wait_for_the_replacement_runspace()
    {
        // The slice-0 class, closed for good (codex finding i56-2): a stalled
        // replacement build must not withhold the labeled timeout answer.
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(2));
        host.CreationDelayForTests = TimeSpan.FromSeconds(8);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = await host.InvokeAsync("Start-Sleep -Seconds 60");
        sw.Stop();

        Assert.True(timedOut.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"timeout response took {sw.Elapsed}");

        // While the rebuild is in flight, a small-budget call reports
        // recovering instead of hanging; once it lands, calls work again.
        var during = await host.InvokeAsync("'probe'", timeoutSeconds: 1);
        Assert.True(during.TimedOut);
        Assert.True(during.Recovering); // discriminated from queue contention (i56-11)
        Assert.Contains("NOT executed", during.Errors[0]);
        Assert.Equal(InvokeDisposition.NotStarted, during.Disposition);
        Assert.False(during.UserExecutionStarted);

        host.CreationDelayForTests = TimeSpan.Zero;
        var after = await host.InvokeAsync("1 + 1", timeoutSeconds: 30);
        Assert.True(after.Success);
        Assert.Contains("2", after.Output);
    }

    [Fact]
    public async Task Wedged_exit_code_bookkeeping_is_bounded_and_surfaced()
    {
        // A stalled LASTEXITCODE read must neither hold the response past the
        // request budget nor hide its recycle as clean success (i56-3).
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        host.ExitCodeReaderOverrideForTests = () => { Thread.Sleep(8000); return 7; };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await host.InvokeAsync("'payload'", timeoutSeconds: 3);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"call took {sw.Elapsed}");
        Assert.True(result.Success);
        Assert.Contains("payload", result.Output);
        Assert.Null(result.ExitCode);
        Assert.True(result.WarmStateLost);
        Assert.Contains(result.Errors, e => e.Contains("bookkeeping wedged"));
        Assert.Equal(InvokeDisposition.Completed, result.Disposition);
        Assert.True(result.UserExecutionStarted);

        host.ExitCodeReaderOverrideForTests = null;
        var after = await host.InvokeAsync("1 + 1", timeoutSeconds: 30);
        Assert.True(after.Success);
    }

    [Fact]
    public async Task Cwd_probe_execution_timeout_reports_state_loss_not_queue_expiry()
    {
        // A wedged provider-intrinsic read times out EXECUTING: the recycle
        // must be reported - claiming "warm state untouched" here sends the
        // model on with dead connections (i56-6).
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            CurrentLocationReaderOverrideForTests = () =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(8));
                return null;
            },
        };

        var text = await InvokeTool.Invoke(
            host, _jobs, _rawUsage, "'x'", CancellationToken.None, background: true, timeoutSeconds: 3);

        Assert.Contains("[job not started]", text);
        Assert.Contains("recycled", text);
        Assert.DoesNotContain("untouched", text);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task Failed_cwd_probe_never_starts_the_job_in_the_server_directory()
    {
        // A probe that yields no usable path fails the start (i56-5): jobs
        // run in the session directory by contract, and the server process
        // cwd is the wrong project.
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60))
        {
            CurrentLocationReaderOverrideForTests = () => null,
        };

        var text = await InvokeTool.Invoke(
            host, _jobs, _rawUsage, "'x'", CancellationToken.None, background: true);

        Assert.Contains("[job not started]", text);
        Assert.Contains("current directory", text);
        Assert.Empty(_jobs.List());
    }

    [Fact]
    public async Task Superseded_rebuild_does_not_stomp_the_reset_runspaces_metadata()
    {
        // Timeline: a timeout starts a SLOW background rebuild; a reset then
        // synchronously publishes a good runspace. The stale rebuild is forced
        // to finish with ModuleLoaded=false after that publication. Its obsolete
        // metadata must not overwrite the winning runspace (i56-12). Source-file
        // deletion can no longer create this condition because module source is
        // intentionally frozen before the first user call.
        var moduleDir = Directory.CreateTempSubdirectory("ptk-stale-rebuild-");
        try
        {
            foreach (var f in Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, FindRepoSrc()), "PwshTokenCompressor.*"))
            {
                File.Copy(f, Path.Combine(moduleDir.FullName, Path.GetFileName(f)));
            }
            var manifest = Path.Combine(moduleDir.FullName, "PwshTokenCompressor.psd1");
            using var host = new RunspaceHost(
                callTimeout: TimeSpan.FromSeconds(2), modulePathOverride: manifest);
            Assert.True(host.ModuleLoaded);

            host.CreationDelayForTests = TimeSpan.FromSeconds(4);
            var timedOut = await host.InvokeAsync("Start-Sleep -Seconds 60");
            Assert.True(timedOut.TimedOut); // stale rebuild now sleeping

            host.CreationDelayForTests = TimeSpan.Zero;
            await host.ResetAsync(); // wins with the module still present
            Assert.True(host.ModuleLoaded);

            host.ModuleImportDisabledForTests = true;
            await host.ShutdownAsync(); // drains the stale rebuild deterministically

            Assert.True(host.ModuleLoaded, "obsolete rebuild stamped its failed import over the current runspace");
        }
        finally
        {
            moduleDir.Delete(recursive: true);
        }
    }

    private static string FindRepoSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent!)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && File.Exists(Path.Combine(dir.FullName, "src", "PwshTokenCompressor.psd1")))
            {
                return Path.GetRelativePath(AppContext.BaseDirectory, Path.Combine(dir.FullName, "src"));
            }
        }
        throw new InvalidOperationException("repo src not found");
    }

    [Fact]
    public async Task A_deadline_already_in_the_past_times_out_immediately()
    {
        // The post-sleep wake case (slice 0): deadlines are wall-clock, so a
        // call whose budget elapsed while the machine slept answers promptly
        // on the next check instead of running a monotonic timer to the end.
        var never = new TaskCompletionSource().Task;
        var outcome = await RunspaceHost.WaitForDeadlineAsync(
            never, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        Assert.Equal(RunspaceHost.WaitOutcome.TimedOut, outcome);

        var result = await _host.InvokeAsync(
            "$global:pastDeadlineRan = $true",
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.True(result.TimedOut);
        Assert.Equal(InvokeDisposition.NotStarted, result.Disposition);
        Assert.False(result.UserExecutionStarted);
        var check = await _host.InvokeAsync(
            "if ($null -eq $global:pastDeadlineRan) { 'never-ran' } else { 'RAN' }",
            route: "pwsh");
        Assert.Contains("never-ran", check.Output);
    }

    [Fact]
    public async Task Background_starts_a_job_and_its_output_is_pollable()
    {
        var text = await InvokeTool.Invoke(
            _host, _jobs, _rawUsage, "'hello from a ptk job'", CancellationToken.None, background: true);

        Assert.Contains("[job 1 started]", text);
        Assert.Contains("ptk_job", text);

        // Poll until the job exits and its output lands (cold pwsh start is
        // the slow part; 60s is generous).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string poll;
        do
        {
            await Task.Delay(250);
            poll = await JobTool.Job(_host, _jobs, "output", CancellationToken.None, id: 1, offset: 0);
        } while (!poll.Contains("exited 0") && DateTime.UtcNow < deadline);

        Assert.Contains("hello from a ptk job", poll);
        Assert.Contains("exited 0", poll);
        Assert.Contains("next offset:", poll);
    }
}
