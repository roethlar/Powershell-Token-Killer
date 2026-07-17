using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkResilienceTestFixture;

/// <summary>
/// Lets the test project locate this deliberately disposable executable.
/// Nothing in the product references this assembly.
/// </summary>
public static class FixtureAssemblyMarker
{
}

internal static class Program
{
    private const string ControlRootEnvironmentVariable = "PTK_RESILIENCE_FIXTURE_CONTROL_ROOT";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var controlRoot = Environment.GetEnvironmentVariable(ControlRootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(controlRoot) || !Path.IsPathFullyQualified(controlRoot))
                return 64;

            Directory.CreateDirectory(controlRoot);
            if (args is ["--fake-host", var generationText, var guardianBootText] &&
                long.TryParse(generationText, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) &&
                generation > 0 &&
                Guid.TryParseExact(guardianBootText, "D", out var guardianBootId) &&
                guardianBootId != Guid.Empty)
            {
                return await FakePrivateHostV1.RunAsync(controlRoot, generation, guardianBootId)
                    .ConfigureAwait(false);
            }

            if (args.Length != 0) return 64;

            await using var guardian = new FakeGuardian(controlRoot);
            return await guardian.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The fixture shares the production invariant that public stdout is
            // protocol-only. Even top-level diagnostics go only to stderr.
            await Console.Error.WriteLineAsync($"resilience-fixture:{exception.GetType().Name}").ConfigureAwait(false);
            return 70;
        }
    }

    internal static string ControlPath(string root, string kind, string token, long? generation = null)
    {
        if (token.Length is < 1 or > 64 || token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException("The fixture call token is invalid.");
        }

        var generationSuffix = generation is null
            ? string.Empty
            : $"-g{generation.Value.ToString(CultureInfo.InvariantCulture)}";
        return Path.Combine(root, $"{kind}-{token}{generationSuffix}.json");
    }

    internal static void WriteControlFile(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static ProcessStartInfo CreateSelfStartInfo()
    {
        var processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The fixture process path is unavailable.");
        var assemblyPath = Assembly.GetEntryAssembly()?.Location ??
            throw new InvalidOperationException("The fixture entry assembly is unavailable.");

        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assemblyPath);
        return start;
    }
}

internal sealed class FakeGuardian : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly TimeSpan PrivateOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecoveryVisibilityDelay = TimeSpan.FromMilliseconds(750);

    private readonly string _controlRoot;
    private readonly Guid _guardianBootId = Guid.NewGuid();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _publicWriteGate = new(1, 1);
    private readonly SemaphoreSlim _backendCallGate = new(1, 1);
    private readonly SemaphoreSlim _hostLifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentBag<HostInstance> _allHosts = [];
    private readonly ConcurrentDictionary<long, Task> _publicCalls = new();

    private HostInstance? _host;
    private Task _recoveryLoopTask = Task.CompletedTask;
    private long _hostGeneration;
    private long _callSequence;
    private long _privateRequestSequence;
    private long _recoveryAttempt;
    private bool _recoveryLoopRunning;
    private bool _stopping;
    private bool _stopCompleted;
    private string _hostState = "starting";
    private string? _recoveryPhase = "attempting";
    private long? _nextAttemptTimestamp;
    private int _initializeCount;

    public FakeGuardian(string controlRoot)
    {
        _controlRoot = controlRoot;
    }

    public async Task<int> RunAsync()
    {
        await StartHostAsync(initialStart: true).ConfigureAwait(false);

        try
        {
            while (true)
            {
                var line = await Console.In.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line))
                {
                    await WriteJsonRpcErrorAsync(
                        id: null,
                        code: -32700,
                        message: "Empty public frame.").ConfigureAwait(false);
                    continue;
                }

                JsonElement request;
                try
                {
                    request = JsonSerializer.Deserialize<JsonElement>(line).Clone();
                }
                catch (JsonException)
                {
                    await WriteJsonRpcErrorAsync(
                        id: null,
                        code: -32700,
                        message: "Invalid JSON.").ConfigureAwait(false);
                    continue;
                }

                var callNumber = Interlocked.Increment(ref _callSequence);
                var call = HandlePublicFrameAsync(request);
                _publicCalls[callNumber] = call;
                _ = call.ContinueWith(
                    completedCall => _publicCalls.TryRemove(callNumber, out var removedCall),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal fixture shutdown.
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private async Task HandlePublicFrameAsync(JsonElement request)
    {
        JsonElement? id = request.TryGetProperty("id", out var idProperty)
            ? idProperty.Clone()
            : null;
        var terminalSent = false;

        try
        {
            if (!request.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
                !request.TryGetProperty("method", out var methodProperty) ||
                methodProperty.ValueKind != JsonValueKind.String)
            {
                if (id is not null)
                {
                    await WriteJsonRpcErrorAsync(id, -32600, "Invalid request.").ConfigureAwait(false);
                }
                return;
            }

            var method = methodProperty.GetString();
            if (id is null)
            {
                // The initialized notification is the only notification the
                // fixture needs. Notifications never produce public output.
                return;
            }

            switch (method)
            {
                case "initialize":
                    if (Interlocked.Increment(ref _initializeCount) != 1)
                    {
                        await WriteJsonRpcErrorAsync(id, -32600, "Initialize may occur only once.")
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteJsonRpcResultAsync(id, new Dictionary<string, object?>
                        {
                            ["protocolVersion"] = "2025-06-18",
                            ["capabilities"] = new Dictionary<string, object?>
                            {
                                ["tools"] = new Dictionary<string, object?>
                                {
                                    ["listChanged"] = false,
                                },
                            },
                            ["serverInfo"] = new Dictionary<string, object?>
                            {
                                ["name"] = "ptk-resilience-test-fixture",
                                ["version"] = "0.0.0-test-only",
                            },
                        }).ConfigureAwait(false);
                    }
                    terminalSent = true;
                    break;

                case "ping":
                    await WriteJsonRpcResultAsync(id, new Dictionary<string, object?>()).ConfigureAwait(false);
                    terminalSent = true;
                    break;

                case "tools/list":
                    await WriteJsonRpcResultAsync(id, ToolList()).ConfigureAwait(false);
                    terminalSent = true;
                    break;

                case "tools/call":
                    var outcome = await HandleToolCallAsync(request).ConfigureAwait(false);
                    await WriteJsonRpcResultAsync(id, ToolResult(outcome.Text, outcome.IsError))
                        .ConfigureAwait(false);
                    terminalSent = true;
                    if (outcome.AfterPublicTerminalBarrier is not null)
                    {
                        try
                        {
                            SignalBarrier(outcome.AfterPublicTerminalBarrier, "public_terminal_sent");
                        }
                        catch (Exception exception)
                        {
                            await Console.Error.WriteLineAsync(
                                $"resilience-fixture:barrier:{exception.GetType().Name}").ConfigureAwait(false);
                        }
                    }
                    break;

                default:
                    await WriteJsonRpcErrorAsync(id, -32601, "Method not found.").ConfigureAwait(false);
                    terminalSent = true;
                    break;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Public EOF owns shutdown and no replacement/public frame follows.
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"resilience-fixture:request:{exception.GetType().Name}").ConfigureAwait(false);
            if (!terminalSent && id is not null)
            {
                await WriteJsonRpcErrorAsync(id, -32603, "Fixture request failed.").ConfigureAwait(false);
            }
        }
    }

    private async Task<BackendOutcome> HandleToolCallAsync(JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String)
        {
            return BackendOutcome.Error(SimpleError("invalid_tool_call"));
        }

        var name = nameProperty.GetString();
        if (name == "ptk_state")
            return BackendOutcome.Success(JsonSerializer.Serialize(CaptureState(), JsonOptions));
        if (name == "fixture_stdout_drain_probe")
        {
            await WritePublicLineAsync("[]").ConfigureAwait(false);
            return BackendOutcome.Success("{\"probe\":\"completed\"}");
        }

        if (name != "fixture_backend_call" ||
            !parameters.TryGetProperty("arguments", out var arguments) ||
            !arguments.TryGetProperty("barrier", out var barrierProperty) ||
            !arguments.TryGetProperty("token", out var tokenProperty) ||
            barrierProperty.ValueKind != JsonValueKind.String ||
            tokenProperty.ValueKind != JsonValueKind.String)
        {
            return BackendOutcome.Error(SimpleError("unknown_fixture_tool"));
        }

        var barrier = barrierProperty.GetString() ?? string.Empty;
        var token = tokenProperty.GetString() ?? string.Empty;
        _ = Program.ControlPath(_controlRoot, "validate", token);
        return await DispatchBackendCallAsync(barrier, token).ConfigureAwait(false);
    }

    private async Task<BackendOutcome> DispatchBackendCallAsync(string barrier, string token)
    {
        if (barrier is not ("not_dispatched" or "pre_write_revalidation" or "write_started" or
            "terminal_decoded" or "public_terminal_sent" or "normal" or
            "malformed_response" or "wrong_generation_response" or
            "duplicate_response" or "writer_failure"))
        {
            return BackendOutcome.Error(SimpleError("invalid_fixture_barrier"));
        }

        if (!await _backendCallGate.WaitAsync(0, _shutdown.Token).ConfigureAwait(false))
            return BackendOutcome.Error(SimpleError("fixture_backend_busy"));

        try
        {
            HostInstance? host;
            HostSnapshot snapshot;
            lock (_sync)
            {
                snapshot = CaptureStateLocked();
                host = snapshot.ReadyForEffects ? _host : null;
            }

            if (host is null)
                return BackendOutcome.Error(RecoveryError("host_recovering", snapshot));

            if (barrier == "not_dispatched")
            {
                SignalBarrier(token, barrier);
                var lossSnapshot = await AwaitHostLossAsync(host).ConfigureAwait(false);
                return BackendOutcome.Error(RecoveryError(
                    "backend_lost_before_dispatch",
                    lossSnapshot));
            }

            var privateRequestId = NextPrivateRequestId();
            var callId = Guid.CreateVersion7();
            var deadline = DateTimeOffset.UtcNow.Add(PrivateOperationTimeout)
                .ToUnixTimeMilliseconds();
            var privatePayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["operation"] = "job_list",
                ["call_id"] = callId.ToString("D"),
                ["dispatch_capability"] = new Dictionary<string, object?>
                {
                    ["token"] = CreateCapabilityToken(),
                    ["call_id"] = callId.ToString("D"),
                    ["expires_unix_time_milliseconds"] = deadline,
                },
                ["output_capability"] = null,
                ["arguments"] = new Dictionary<string, object?>(),
            }, JsonOptions);
            var privateRequest = GuardianHostRawProtocol.Create(
                GuardianHostMessageKind.Request,
                _guardianBootId,
                host.Connection.HostBootId,
                host.Generation,
                ("request_id", privateRequestId),
                ("method", "operation"),
                ("deadline_unix_time_milliseconds", deadline),
                ("session_alias", "default"),
                ("session_transition_version", 1L),
                ("worker_boot_id", host.WorkerBootId),
                ("worker_generation", host.WorkerGeneration),
                ("plan_id", null),
                ("operation_id", null),
                ("payload", privatePayload));
            var privateResponse = host.Connection.RegisterResponse(privateRequestId);
            Program.WriteControlFile(
                Program.ControlPath(
                    _controlRoot,
                    "operation",
                    privateRequestId.ToString(CultureInfo.InvariantCulture)),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["barrier"] = barrier,
                    ["token"] = token,
                }, JsonOptions));

            if (barrier == "pre_write_revalidation")
            {
                SignalBarrier(token, barrier);
                await AwaitHostLossAsync(host).ConfigureAwait(false);
            }

            ValueTask privateWrite = default;
            HostSnapshot? dispatchRefusal = null;
            lock (_sync)
            {
                var dispatchSnapshot = CaptureStateLocked();
                if (!ReferenceEquals(_host, host) ||
                    _hostState != "ready" ||
                    host.Process.HasExited ||
                    host.Connection.GenerationFailure is not null ||
                    !dispatchSnapshot.ReadyForEffects)
                {
                    dispatchRefusal = dispatchSnapshot;
                }
                else
                {
                    // Invoke the first possibly-writing API while the same
                    // lifecycle lock still owns the exact readiness and
                    // generation revalidation. Loss after this boundary is
                    // ambiguous; loss observed before it sent no request byte.
                    privateWrite = host.Connection.WriteAsync(
                        privateRequest,
                        injectWriterFailure: barrier == "writer_failure",
                        _shutdown.Token);
                }
            }

            if (dispatchRefusal is not null)
            {
                host.Connection.CancelResponse(privateRequestId, _shutdown.Token);
                var refusalSnapshot = dispatchRefusal.RecoveryPhase is null
                    ? await AwaitHostLossAsync(host).ConfigureAwait(false)
                    : dispatchRefusal;
                return BackendOutcome.Error(RecoveryError(
                    "backend_lost_before_dispatch",
                    refusalSnapshot));
            }

            try
            {
                // Delivery truth advances before the first API that may write a
                // private byte. Any exception from here is therefore ambiguous.
                await privateWrite.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or InvalidOperationException or
                    OperationCanceledException)
            {
                host.Connection.CancelResponse(privateRequestId, _shutdown.Token);
                await AwaitHostLossAsync(host).ConfigureAwait(false);
                return BackendOutcome.Error(OutcomeUnknown());
            }

            GuardianHostRawEnvelope terminal;
            try
            {
                var completed = await Task.WhenAny(privateResponse, host.Lost.Task)
                    .WaitAsync(PrivateOperationTimeout, _shutdown.Token).ConfigureAwait(false);
                if (privateResponse.IsCompletedSuccessfully)
                {
                    terminal = await privateResponse.ConfigureAwait(false);
                }
                else
                {
                    _ = completed;
                    await AwaitHostLossAsync(host).ConfigureAwait(false);
                    return BackendOutcome.Error(OutcomeUnknown());
                }
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or TimeoutException or
                    InvalidOperationException or OperationCanceledException)
            {
                if (!host.Process.HasExited)
                {
                    try { host.Process.Kill(entireProcessTree: true); } catch { /* already lost */ }
                }
                await AwaitHostLossAsync(host).ConfigureAwait(false);
                return BackendOutcome.Error(OutcomeUnknown());
            }

            if (terminal.Kind != GuardianHostMessageKind.Response ||
                terminal.Value("request_id").GetInt64() != privateRequestId ||
                terminal.Value("status").GetString() != "ok" ||
                terminal.Value("error").ValueKind != JsonValueKind.Null ||
                terminal.Value("payload").ValueKind != JsonValueKind.Object ||
                terminal.Value("payload").GetProperty("response_type").GetString() !=
                    "operation_completed" ||
                terminal.Value("payload").GetProperty("operation").GetString() != "job_list" ||
                terminal.Value("payload").GetProperty("result").GetProperty("text").ValueKind !=
                    JsonValueKind.String)
            {
                if (!host.Process.HasExited)
                {
                    try { host.Process.Kill(entireProcessTree: true); } catch { /* already lost */ }
                }
                await AwaitHostLossAsync(host).ConfigureAwait(false);
                return BackendOutcome.Error(OutcomeUnknown());
            }

            var exactTerminal = terminal.Value("payload")
                .GetProperty("result")
                .GetProperty("text")
                .GetString()!;

            if (barrier == "terminal_decoded")
            {
                SignalBarrier(token, barrier);
                await AwaitHostLossAsync(host).ConfigureAwait(false);
                return BackendOutcome.Success(exactTerminal);
            }

            return BackendOutcome.Success(exactTerminal, afterPublicTerminalBarrier: token);
        }
        finally
        {
            _backendCallGate.Release();
        }
    }

    private long NextPrivateRequestId()
    {
        var requestId = Interlocked.Increment(ref _privateRequestSequence);
        if (requestId <= 0)
            throw new InvalidOperationException("Private request identifiers are exhausted.");
        return requestId;
    }

    private static string CreateCapabilityToken()
    {
        Span<byte> randomBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(randomBytes);
        try
        {
            return Convert.ToBase64String(randomBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomBytes);
        }
    }

    private async Task<HostSnapshot> AwaitHostLossAsync(HostInstance host)
    {
        return await host.Lost.Task.WaitAsync(PrivateOperationTimeout, _shutdown.Token).ConfigureAwait(false);
    }

    private Dictionary<string, object?> RecoveryError(
        string detailCode,
        HostSnapshot snapshot)
    {
        if (snapshot.ReadyForEffects || snapshot.RecoveryPhase is null ||
            snapshot.RetryAfterMilliseconds is null || snapshot.RecoveryAttempt < 1)
        {
            throw new InvalidOperationException("A retryable result requires one atomic recovery snapshot.");
        }

        return new Dictionary<string, object?>
        {
            ["detail_code"] = detailCode,
            ["retryable"] = true,
            ["retry_after_ms"] = snapshot.RetryAfterMilliseconds.Value,
            ["recovery_phase"] = snapshot.RecoveryPhase,
            ["recovery_attempt"] = snapshot.RecoveryAttempt,
            ["retry_gate"] = new Dictionary<string, object?>
            {
                ["kind"] = "host_ready",
            },
        };
    }

    private static Dictionary<string, object?> OutcomeUnknown()
    {
        return new Dictionary<string, object?>
        {
            ["detail_code"] = "outcome_unknown",
            ["retryable"] = false,
            ["retry_after_ms"] = null,
            ["recovery_phase"] = null,
            ["recovery_attempt"] = null,
            ["retry_gate"] = null,
        };
    }

    private static Dictionary<string, object?> SimpleError(string detailCode)
    {
        return new Dictionary<string, object?>
        {
            ["detail_code"] = detailCode,
            ["retryable"] = false,
            ["retry_after_ms"] = null,
            ["recovery_phase"] = null,
            ["recovery_attempt"] = null,
            ["retry_gate"] = null,
        };
    }

    private Dictionary<string, object?> CaptureState()
    {
        lock (_sync)
        {
            var snapshot = CaptureStateLocked();
            return new Dictionary<string, object?>
            {
                ["fixture"] = "disposable_r0_fake_guardian",
                ["guardian_pid"] = Environment.ProcessId,
                ["initialize_count"] = Volatile.Read(ref _initializeCount),
                ["host"] = new Dictionary<string, object?>
                {
                    ["pid"] = snapshot.HostPid,
                    ["generation"] = snapshot.HostGeneration,
                    ["state"] = snapshot.State,
                    ["recovery_phase"] = snapshot.RecoveryPhase,
                    ["recovery_attempt"] = snapshot.RecoveryAttempt,
                    ["retry_after_ms"] = snapshot.RetryAfterMilliseconds,
                    ["ready_for_effects"] = snapshot.ReadyForEffects,
                },
            };
        }
    }

    private HostSnapshot CaptureStateLocked()
    {
        if (_host is { } observedHost && observedHost.Process.HasExited)
            TransitionHostLossLocked(observedHost);

        return CreateStateSnapshotLocked();
    }

    private HostSnapshot CreateStateSnapshotLocked()
    {
        return new HostSnapshot(
            _host?.Process.Id,
            _host?.Generation ?? _hostGeneration,
            _hostState,
            _recoveryPhase,
            _recoveryAttempt,
            RetryAfterMillisecondsLocked(),
            _hostState == "ready" && _host is not null && !_host.Process.HasExited);
    }

    private int? RetryAfterMillisecondsLocked()
    {
        if (_recoveryPhase is null)
            return null;
        if (_nextAttemptTimestamp is not { } due)
            return 250;

        var remainingTicks = Math.Max(0, due - Stopwatch.GetTimestamp());
        var remainingMilliseconds = (long)Math.Ceiling(
            remainingTicks * 1000d / Stopwatch.Frequency);
        return (int)Math.Clamp(remainingMilliseconds, 250, 60_000);
    }

    private async Task StartHostAsync(bool initialStart)
    {
        await _hostLifecycleGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        Process? process = null;
        HostInstance? instance = null;
        try
        {
            lock (_sync)
            {
                if (_stopping)
                    throw new OperationCanceledException(_shutdown.Token);
            }

            var generation = Interlocked.Increment(ref _hostGeneration);
            var start = Program.CreateSelfStartInfo();
            start.ArgumentList.Add("--fake-host");
            start.ArgumentList.Add(generation.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(_guardianBootId.ToString("D"));
            start.Environment["PTK_RESILIENCE_FIXTURE_CONTROL_ROOT"] = _controlRoot;

            process = Process.Start(start) ??
                throw new InvalidOperationException("The private fake host did not start.");
            instance = new HostInstance(process, generation, _guardianBootId);
            _allHosts.Add(instance);
            Program.WriteControlFile(
                Path.Combine(_controlRoot, $"host-started-g{generation.ToString(CultureInfo.InvariantCulture)}.json"),
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["pid"] = process.Id,
                    ["start_utc_ticks"] = instance.StartTimeUtc.Ticks,
                }, JsonOptions));

            lock (_sync)
            {
                if (_stopping)
                    throw new OperationCanceledException(_shutdown.Token);
                _host = instance;
                _hostState = initialStart ? "starting" : "recovering";
                _recoveryPhase = "attempting";
                _nextAttemptTimestamp = null;
            }

            instance.Observer = ObserveHostExitAsync(instance);
            await instance.Connection.InitializeAsync(NextPrivateRequestId, _shutdown.Token)
                .WaitAsync(PrivateOperationTimeout, _shutdown.Token).ConfigureAwait(false);

            lock (_sync)
            {
                if (_stopping)
                    throw new OperationCanceledException(_shutdown.Token);
                if (!ReferenceEquals(_host, instance) || process.HasExited)
                    throw new InvalidOperationException("The private fake host was lost during initialization.");
                _hostState = "ready";
                _recoveryPhase = null;
                _nextAttemptTimestamp = null;
            }
        }
        catch
        {
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already lost */ }
            }
            if (instance is not null)
            {
                try { await instance.Process.WaitForExitAsync().ConfigureAwait(false); }
                catch { /* the observer reports the failed identity when available */ }
                if (instance.Observer is not null)
                {
                    try { await instance.Observer.ConfigureAwait(false); }
                    catch { /* its own bounded diagnostic owns observer failure */ }
                }
            }
            else if (process is not null)
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
                process.Dispose();
            }
            throw;
        }
        finally
        {
            _hostLifecycleGate.Release();
        }
    }

    private async Task ObserveHostExitAsync(HostInstance instance)
    {
        try
        {
            await instance.Process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"resilience-fixture:host-observer:{exception.GetType().Name}").ConfigureAwait(false);
        }

        if (File.Exists(Path.Combine(_controlRoot, "delay-host-observer.json")))
        {
            Program.WriteControlFile(
                Path.Combine(_controlRoot, "host-observer-delayed.json"),
                "{\"delayed\":true}");
            while (!File.Exists(Path.Combine(_controlRoot, "release-host-observer.json")) &&
                !_shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), _shutdown.Token)
                    .ConfigureAwait(false);
            }
        }

        lock (_sync)
            TransitionHostLossLocked(instance);
    }

    private HostSnapshot TransitionHostLossLocked(HostInstance instance)
    {
        if (ReferenceEquals(_host, instance))
        {
            _host = null;
            if (!_stopping)
            {
                _hostState = "recovering";
                _recoveryPhase = "backoff";
                _recoveryAttempt = checked(_recoveryAttempt + 1);
                _nextAttemptTimestamp = Stopwatch.GetTimestamp() +
                    (long)Math.Ceiling(RecoveryVisibilityDelay.TotalSeconds * Stopwatch.Frequency);
                if (!_recoveryLoopRunning)
                {
                    _recoveryLoopRunning = true;
                    _recoveryLoopTask = RecoverHostLoopAsync();
                }
            }
        }

        var lossSnapshot = CreateStateSnapshotLocked();
        instance.Lost.TrySetResult(lossSnapshot);
        return lossSnapshot;
    }

    private async Task RecoverHostLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            long attemptedRecovery;
            lock (_sync)
                attemptedRecovery = _recoveryAttempt;

            try
            {
                if (File.Exists(Path.Combine(_controlRoot, "hold-recovery.json")))
                {
                    Program.WriteControlFile(
                        Path.Combine(_controlRoot, "recovery-held.json"),
                        "{\"held\":true}");
                    while (!File.Exists(Path.Combine(_controlRoot, "release-recovery.json")))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), _shutdown.Token)
                            .ConfigureAwait(false);
                    }
                }
                await Task.Delay(RecoveryVisibilityDelay, _shutdown.Token).ConfigureAwait(false);
                await StartHostAsync(initialStart: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                if (File.Exists(Path.Combine(
                        _controlRoot,
                        "delay-recovery-cancellation.json")))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None)
                        .ConfigureAwait(false);
                    Program.WriteControlFile(
                        Path.Combine(_controlRoot, "recovery-loop-stopped.json"),
                        "{\"stopped\":true}");
                }
                return;
            }
            catch (Exception exception)
            {
                // Preserve the disposable R0 fixture's asserted diagnostic
                // surface while its implementation consumes the extracted
                // production shared codec.
                var exceptionName = exception is GuardianHostProtocolException
                    ? "FakePrivateProtocolException"
                    : exception.GetType().Name;
                await Console.Error.WriteLineAsync(
                    $"resilience-fixture:recovery:{exceptionName}").ConfigureAwait(false);
                lock (_sync)
                {
                    if (!_stopping)
                    {
                        _host = null;
                        _hostState = "recovering";
                        _recoveryPhase = "backoff";
                        if (_recoveryAttempt <= attemptedRecovery)
                            _recoveryAttempt = checked(attemptedRecovery + 1);
                        _nextAttemptTimestamp = Stopwatch.GetTimestamp() +
                            (long)Math.Ceiling(
                                RecoveryVisibilityDelay.TotalSeconds * Stopwatch.Frequency);
                    }
                }
                continue;
            }

            lock (_sync)
            {
                if (_hostState == "ready" && _host is not null && !_host.Process.HasExited)
                {
                    _recoveryLoopRunning = false;
                    return;
                }
            }
        }
    }

    private void SignalBarrier(string token, string barrier)
    {
        var path = Program.ControlPath(_controlRoot, $"barrier-{barrier}", token);
        Program.WriteControlFile(path, "{\"reached\":true}");
    }

    private async Task WriteJsonRpcResultAsync(JsonElement? id, object result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is null) writer.WriteNullValue(); else id.Value.WriteTo(writer);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, JsonOptions);
            writer.WriteEndObject();
        }
        await WritePublicLineAsync(Encoding.UTF8.GetString(stream.ToArray())).ConfigureAwait(false);
    }

    private async Task WriteJsonRpcErrorAsync(JsonElement? id, int code, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is null) writer.WriteNullValue(); else id.Value.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        await WritePublicLineAsync(Encoding.UTF8.GetString(stream.ToArray())).ConfigureAwait(false);
    }

    private async Task WritePublicLineAsync(string line)
    {
        await _publicWriteGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(line.AsMemory(), _shutdown.Token).ConfigureAwait(false);
            await Console.Out.FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            _publicWriteGate.Release();
        }
    }

    private static Dictionary<string, object?> ToolResult(string text, bool isError)
    {
        return new Dictionary<string, object?>
        {
            ["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
            ["isError"] = isError,
        };
    }

    private static Dictionary<string, object?> ToolList()
    {
        return new Dictionary<string, object?>
        {
            ["tools"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "ptk_state",
                    ["description"] = "Fixture-only guardian-local recovery state.",
                    ["inputSchema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                    },
                },
                new Dictionary<string, object?>
                {
                    ["name"] = "fixture_backend_call",
                    ["description"] = "Fixture-only delivery-barrier call.",
                    ["inputSchema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "barrier", "token" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["barrier"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                            },
                            ["token"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                            },
                        },
                        ["additionalProperties"] = false,
                    },
                },
            },
        };
    }

    private async Task StopAsync()
    {
        var initiateStop = false;
        lock (_sync)
        {
            if (!_stopping)
            {
                _stopping = true;
                initiateStop = true;
                _host = null;
                _hostState = "stopped";
                _recoveryPhase = null;
                _nextAttemptTimestamp = null;
            }
        }

        if (initiateStop)
            _shutdown.Cancel();

        await _hostLifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var cleanupRequired = false;
            lock (_sync)
                cleanupRequired = !_stopCompleted;

            if (cleanupRequired)
            {
                var hosts = _allHosts.ToArray();
                foreach (var host in hosts)
                {
                    if (host.Process.HasExited) continue;
                    try { host.Process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                }
                foreach (var host in hosts)
                {
                    try
                    {
                        await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    }
                    catch { /* bounded best effort in a disposable fixture */ }
                }

                var publicCalls = _publicCalls.Values.ToArray();
                try
                {
                    await Task.WhenAll(publicCalls).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch { /* public EOF owns cancellation; no terminal is required afterward */ }

                foreach (var host in hosts)
                {
                    if (host.Observer is not null)
                    {
                        try { await host.Observer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                        catch { /* the process witnesses above own the hard cleanup proof */ }
                    }
                    try
                    {
                        await host.Connection.ResponseReaderTask.WaitAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    }
                    catch { /* EOF/protocol fault is the expected generation terminal */ }
                    try
                    {
                        await host.Connection.DiagnosticTask.WaitAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    }
                    catch { /* diagnostics never own process cleanup */ }
                    host.Connection.Dispose();
                }

                Task recoveryLoop;
                lock (_sync)
                    recoveryLoop = _recoveryLoopTask;
                try
                {
                    await recoveryLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    // Public EOF canceled the one retained recovery loop.
                }

                var admittedGenerations = hosts
                    .Select(host => host.Generation)
                    .Order()
                    .ToArray();
                Program.WriteControlFile(
                    Path.Combine(_controlRoot, "guardian-shutdown.json"),
                    JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["admitted_host_count"] = admittedGenerations.Length,
                        ["admitted_host_generations"] = admittedGenerations,
                    }, JsonOptions));

                lock (_sync)
                    _stopCompleted = true;
            }
        }
        finally
        {
            _hostLifecycleGate.Release();
        }

    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _publicWriteGate.Dispose();
        _backendCallGate.Dispose();
        _hostLifecycleGate.Dispose();
        _shutdown.Dispose();
    }

    private sealed class HostInstance
    {
        private Task? _observer;

        public HostInstance(Process process, long generation, Guid guardianBootId)
        {
            Connection = new FakePrivateHostConnection(process, generation, guardianBootId);
            Generation = generation;
            WorkerBootId = Guid.NewGuid();
            WorkerGeneration = generation;
            StartTimeUtc = Connection.StartTimeUtc;
        }

        public FakePrivateHostConnection Connection { get; }
        public Process Process => Connection.Process;
        public long Generation { get; }
        public Guid WorkerBootId { get; }
        public long WorkerGeneration { get; }
        public DateTime StartTimeUtc { get; }
        public Task? Observer
        {
            get => _observer;
            set
            {
                _observer = value;
                Connection.Observer = value;
            }
        }
        public TaskCompletionSource<HostSnapshot> Lost { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record BackendOutcome(
        string Text,
        bool IsError,
        string? AfterPublicTerminalBarrier)
    {
        public static BackendOutcome Success(string text, string? afterPublicTerminalBarrier = null) =>
            new(text, IsError: false, afterPublicTerminalBarrier);

        public static BackendOutcome Error(object error) =>
            new(JsonSerializer.Serialize(error, JsonOptions), IsError: true, null);
    }

    private sealed record HostSnapshot(
        int? HostPid,
        long HostGeneration,
        string State,
        string? RecoveryPhase,
        long RecoveryAttempt,
        int? RetryAfterMilliseconds,
        bool ReadyForEffects);
}
