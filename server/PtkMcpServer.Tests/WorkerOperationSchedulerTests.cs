using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WorkerOperationSchedulerTests
{
    private static readonly Guid BootId =
        Guid.Parse("87c30be0-373f-4335-9761-445bf023be06");
    private const long Generation = 11;
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2035-06-07T08:09:10Z");

    [Fact]
    public async Task Dispatch_is_off_reader_thread_and_writes_one_correlated_response()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var safetyRelease = new Timer(
            _ => release.Set(),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);
        var executor = new DelegateExecutor((request, cancellationToken) =>
        {
            entered.Set();
            release.Wait(cancellationToken);
            return Task.FromResult(Result(request.Operation));
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);

        var stopwatch = Stopwatch.StartNew();
        scheduler.Admit(Request(1, "blocking"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Empty(writer.Snapshot());
        release.Set();

        var response = await writer.WaitForOneAsync();
        var parsed = WorkerOperationProtocol.ParseResponse(response, BootId, Generation);
        Assert.Equal(1, parsed.RequestId);
        Assert.Equal(WorkerOperationStatus.Completed, parsed.Status);
        Assert.Equal("blocking", parsed.Result!.Value.GetProperty("value").GetString());
        Assert.False(writer.LastCancellationToken.CanBeCanceled);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(writer.Snapshot());
    }

    [Fact]
    public async Task Slow_request_does_not_block_later_admission_or_response()
    {
        var slowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var safetyRelease = new Timer(
            _ => slowRelease.TrySetResult(),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            if (request.Operation == "slow")
                await slowRelease.Task.WaitAsync(cancellationToken);
            return Result(request.Operation);
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);

        scheduler.Admit(Request(1, "slow"));
        scheduler.Admit(Request(2, "fast"));

        var first = await writer.WaitForOneAsync();
        Assert.Equal(2, first.RequestId);
        slowRelease.SetResult();
        await writer.WaitForCountAsync(2);
        Assert.Equal([2L, 1L], writer.Snapshot().Select(frame => frame.RequestId!.Value));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancel_targets_only_one_and_late_duplicate_unknown_cancel_is_benign()
    {
        using var started = new SemaphoreSlim(0, 2);
        var secondToken = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            if (request.RequestId == 2) secondToken.SetResult(cancellationToken);
            started.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result("impossible");
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "first"));
        scheduler.Admit(Request(2, "second"));
        await started.WaitAsync(TimeSpan.FromSeconds(10));
        await started.WaitAsync(TimeSpan.FromSeconds(10));
        var unaffectedToken = await secondToken.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.Admit(Cancel(1));
        var first = await writer.WaitForOneAsync();
        Assert.Equal(1, first.RequestId);
        Assert.Equal(
            WorkerOperationStatus.Canceled,
            WorkerOperationProtocol.ParseResponse(first, BootId, Generation).Status);
        Assert.False(writer.LastCancellationToken.CanBeCanceled);
        Assert.Single(writer.Snapshot());
        Assert.False(unaffectedToken.IsCancellationRequested);

        scheduler.Admit(Cancel(1));
        scheduler.Admit(Cancel(99));
        Assert.False(scheduler.Fatal.IsCompleted);
        scheduler.Admit(Cancel(2));
        await writer.WaitForCountAsync(2);
        Assert.Equal([1L, 2L], writer.Snapshot().Select(frame => frame.RequestId!.Value));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Successful_result_after_cancel_is_authoritative_and_written_once()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor((_, _) =>
        {
            entered.SetResult();
            return release.Task;
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "race"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.Admit(Cancel(1));
        release.SetResult(Result("completed_after_signal"));

        var frame = await writer.WaitForOneAsync();
        var response = WorkerOperationProtocol.ParseResponse(frame, BootId, Generation);
        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(
            "completed_after_signal",
            response.Result!.Value.GetProperty("value").GetString());
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(writer.Snapshot());
    }

    [Fact]
    public async Task Cancel_after_executor_terminal_does_not_run_cancellation_callbacks()
    {
        var cancellationCallbacks = 0;
        var writeEntered = new TaskCompletionSource<WorkerEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor((request, cancellationToken) =>
        {
            _ = cancellationToken.Register(() =>
                Interlocked.Increment(ref cancellationCallbacks));
            return Task.FromResult(Result("terminal"));
        });
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            async (envelope, _) =>
            {
                writeEntered.SetResult(envelope);
                await releaseWrite.Task;
            },
            utcNow: () => Now);
        scheduler.Admit(Request(1, "late_cancel"));
        var frame = await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.Admit(Cancel(1));
        Assert.Equal(0, Volatile.Read(ref cancellationCallbacks));
        releaseWrite.SetResult();
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var response = WorkerOperationProtocol.ParseResponse(frame, BootId, Generation);
        Assert.Equal(WorkerOperationStatus.Completed, response.Status);
        Assert.Equal(0, Volatile.Read(ref cancellationCallbacks));
    }

    [Fact]
    public async Task Owned_cancellation_callbacks_finish_before_terminal_write()
    {
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var registered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var safetyRelease = new Timer(
            _ => releaseCallback.Set(),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            _ = cancellationToken.Register(() =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            });
            registered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result("impossible");
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "callback_drain"));

        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.Admit(Cancel(1));
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Empty(writer.Snapshot());
        releaseCallback.Set();

        var response = WorkerOperationProtocol.ParseResponse(
            await writer.WaitForOneAsync(),
            BootId,
            Generation);
        Assert.Equal(WorkerOperationStatus.Canceled, response.Status);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Foreign_cancellation_and_executor_exception_are_redacted_failures()
    {
        var executor = new DelegateExecutor((request, _) => request.Operation switch
        {
            "foreign_cancel" => Task.FromException<JsonElement>(
                new OperationCanceledException("secret cancellation")),
            _ => Task.FromException<JsonElement>(
                new InvalidOperationException("secret path / credential")),
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "foreign_cancel"));
        scheduler.Admit(Request(2, "exception"));
        await writer.WaitForCountAsync(2);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var responses = writer.Snapshot()
            .Select(frame => WorkerOperationProtocol.ParseResponse(frame, BootId, Generation))
            .OrderBy(response => response.RequestId)
            .ToArray();
        Assert.All(responses, response =>
        {
            Assert.Equal(WorkerOperationStatus.Failed, response.Status);
            Assert.Equal("operation_failed", response.DetailCode);
        });
        var wire = string.Join(
            "\n",
            writer.Snapshot().Select(frame => Encoding.UTF8.GetString(WorkerProtocol.Encode(frame))));
        Assert.DoesNotContain("secret", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Foreign_cancellation_racing_owned_cancel_remains_failed()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var throwForeign = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (_, _) =>
        {
            entered.SetResult();
            await throwForeign.Task;
            throw new OperationCanceledException(
                "foreign cancellation after owned signal",
                CancellationToken.None);
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "foreign_cancel_race"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scheduler.Admit(Cancel(1));
        throwForeign.SetResult();

        var response = WorkerOperationProtocol.ParseResponse(
            await writer.WaitForOneAsync(),
            BootId,
            Generation);
        Assert.Equal(WorkerOperationStatus.Failed, response.Status);
        Assert.Equal("operation_failed", response.DetailCode);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Expired_request_never_executes_and_active_deadline_targets_only_it()
    {
        var calls = 0;
        var peerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var peerRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            if (request.Operation == "peer")
            {
                peerEntered.SetResult();
                await peerRelease.Task.WaitAsync(cancellationToken);
            }
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result(request.Operation);
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(
            executor,
            writer,
            waitUntilDeadline: (deadline, cancellationToken) =>
                deadline == Now.AddMinutes(1)
                    ? Task.CompletedTask
                    : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        scheduler.Admit(Request(1, "expired", Now));
        scheduler.Admit(Request(2, "deadline", Now.AddMinutes(1)));
        scheduler.Admit(Request(3, "peer", Now.AddMinutes(2)));
        await writer.WaitForCountAsync(2);
        await peerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, calls);
        Assert.DoesNotContain(writer.Snapshot(), frame => frame.RequestId == 3);
        peerRelease.SetResult();
        await writer.WaitForCountAsync(3);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var responses = writer.Snapshot()
            .Select(frame => WorkerOperationProtocol.ParseResponse(frame, BootId, Generation))
            .OrderBy(response => response.RequestId)
            .ToArray();
        Assert.All(responses[..2], response =>
        {
            Assert.Equal(WorkerOperationStatus.TimedOut, response.Status);
            Assert.Equal("request_deadline_expired", response.DetailCode);
        });
        Assert.Equal(WorkerOperationStatus.Completed, responses[2].Status);
        Assert.Equal("peer", responses[2].Result!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Request_ids_are_reserved_before_replay_and_capacity_rejection()
    {
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result(request.Operation);
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);

        Assert.Equal(64, WorkerOperationScheduler.MaximumOutstandingRequests);
        for (var id = 1; id <= WorkerOperationScheduler.MaximumOutstandingRequests; id++)
            scheduler.Admit(Request(id, "hold"));

        Assert.Equal(WorkerOperationScheduler.MaximumOutstandingRequests, scheduler.OutstandingCount);
        Assert.Equal(
            "operation_capacity_exceeded",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(65, "overflow"))).DetailCode);
        Assert.Equal(
            "operation_request_replay",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(65, "replay"))).DetailCode);
        Assert.Equal(
            "operation_request_replay",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(64, "lower"))).DetailCode);

        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await writer.WaitForCountAsync(WorkerOperationScheduler.MaximumOutstandingRequests);
        Assert.Equal(
            WorkerOperationScheduler.MaximumOutstandingRequests,
            writer.Snapshot().Select(frame => frame.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task Initial_request_high_water_rejects_old_ids_before_execution()
    {
        var calls = 0;
        var executor = new DelegateExecutor((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result(request.Operation));
        });
        var writer = new RecordingWriter();
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 40,
            executor,
            writer.WriteAsync,
            utcNow: () => Now);

        Assert.Equal(
            "operation_request_replay",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(40, "old"))).DetailCode);
        scheduler.Admit(Request(41, "new"));
        var response = WorkerOperationProtocol.ParseResponse(
            await writer.WaitForOneAsync(),
            BootId,
            Generation);
        Assert.Equal(41, response.RequestId);
        Assert.Equal(1, calls);
        Assert.Equal(
            "operation_request_replay",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(41, "completed_replay"))).DetailCode);
        Assert.Equal(
            "operation_request_replay",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(40, "completed_lower"))).DetailCode);
        scheduler.Admit(Request(42, "next"));
        await writer.WaitForCountAsync(2);
        Assert.Equal(2, calls);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Injected_inline_scheduler_cannot_run_executor_on_admission_path()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var safetyRelease = new Timer(
            _ => release.Set(),
            null,
            TimeSpan.FromSeconds(10),
            Timeout.InfiniteTimeSpan);
        var executor = new DelegateExecutor((request, cancellationToken) =>
        {
            entered.Set();
            release.Wait(cancellationToken);
            return Task.FromResult(Result(request.Operation));
        });
        var writer = new RecordingWriter();
        var inlineScheduler = new InlineTaskScheduler();
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            writer.WriteAsync,
            utcNow: () => Now,
            taskScheduler: inlineScheduler);

        var stopwatch = Stopwatch.StartNew();
        scheduler.Admit(Request(1, "inline_scheduler"));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        await writer.WaitForOneAsync();
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // rbc-9 mutation guard: one admitted request must route BOTH hops
        // through the injected scheduler — the outer admit hop
        // (Admit -> RunScheduledRequestAsync) and the inner execution hop
        // (-> RunRequestAsync). Reverting the outer hop to
        // TaskScheduler.Default leaves QueueCount at 1, so `> 0` would stay
        // green and hide the regression; require both.
        Assert.True(
            inlineScheduler.QueueCount >= 2,
            $"expected both scheduler hops on the injected scheduler, saw {inlineScheduler.QueueCount}");
    }

    [Fact]
    public async Task Scheduling_failure_latches_fatal_and_releases_reserved_request()
    {
        var calls = 0;
        var executor = new DelegateExecutor((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result("impossible"));
        });
        var writer = new RecordingWriter();
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            writer.WriteAsync,
            utcNow: () => Now,
            taskScheduler: new ThrowingTaskScheduler());
        scheduler.Admit(Request(1, "schedule_failure"));

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls);
        Assert.Equal(0, scheduler.OutstandingCount);
        Assert.Empty(writer.Snapshot());
    }

    [Fact]
    public async Task Clock_failure_latches_fatal_and_releases_reserved_request()
    {
        var calls = 0;
        var executor = new DelegateExecutor((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Result("impossible"));
        });
        var writer = new RecordingWriter();
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            writer.WriteAsync,
            utcNow: () => throw new IOException("secret clock failure"));
        scheduler.Admit(Request(1, "clock_failure"));

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, calls);
        Assert.Equal(0, scheduler.OutstandingCount);
        Assert.Empty(writer.Snapshot());
    }

    [Fact]
    public async Task Fatal_executor_exception_is_owned_latched_and_cleaned_up()
    {
        var executor = new DelegateExecutor((_, _) =>
            Task.FromException<JsonElement>(
                new OutOfMemoryException("simulated fatal secret")));
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "fatal_executor"));

        var exception = await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Worker operation response transport failed.", exception.Message);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scheduler.OutstandingCount);
        Assert.Empty(writer.Snapshot());
        Assert.Equal(
            "operation_scheduler_failed",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(2, "after_fatal"))).DetailCode);
    }

    [Fact]
    public async Task Invalid_result_is_failed_without_leaking_or_second_execution()
    {
        var calls = 0;
        var executor = new DelegateExecutor((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(JsonSerializer.SerializeToElement(new[] { "not", "object" }));
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "invalid_result"));

        var response = WorkerOperationProtocol.ParseResponse(
            await writer.WaitForOneAsync(),
            BootId,
            Generation);
        Assert.Equal(1, calls);
        Assert.Equal(WorkerOperationStatus.Failed, response.Status);
        Assert.Equal("invalid_operation_result", response.DetailCode);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Writer_failure_is_one_attempt_latches_fatal_and_rejects_later_work()
    {
        var attempts = 0;
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var peerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var peerCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (request, cancellationToken) =>
        {
            if (request.Operation == "write_failure")
            {
                await releaseFailure.Task;
                return Result("ok");
            }

            peerStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    peerCanceled.SetResult();
            }
            return Result("impossible");
        });
        var scheduler = new WorkerOperationScheduler(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("secret transport failure");
            },
            utcNow: () => Now);
        scheduler.Admit(Request(1, "write_failure"));
        scheduler.Admit(Request(2, "peer"));
        await peerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseFailure.SetResult();

        var exception = await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Worker operation response transport failed.", exception.Message);
        Assert.InRange(Volatile.Read(ref attempts), 1, 2);
        Assert.Equal(
            "operation_scheduler_failed",
            Assert.Throws<WorkerProtocolException>(() =>
                scheduler.Admit(Request(3, "after_failure"))).DetailCode);
        await peerCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Deadline_wait_failure_latches_fatal_and_cancels_its_own_request()
    {
        var executor = new DelegateExecutor(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result("impossible");
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(
            executor,
            writer,
            waitUntilDeadline: (_, _) =>
                Task.FromException(new IOException("secret deadline failure")));
        scheduler.Admit(Request(1, "deadline_failure"));

        var exception = await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Worker operation response transport failed.", exception.Message);
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var response = WorkerOperationProtocol.ParseResponse(
            await writer.WaitForOneAsync(),
            BootId,
            Generation);
        Assert.Equal(WorkerOperationStatus.Canceled, response.Status);
        Assert.Equal("request_canceled", response.DetailCode);
    }

    [Fact]
    public async Task Fatal_deadline_wait_failure_latches_before_executor_finishes()
    {
        var executorCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    executorCanceled.SetResult();
            }
            return Result("impossible");
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(
            executor,
            writer,
            waitUntilDeadline: (_, _) =>
                Task.FromException(new OutOfMemoryException("simulated fatal deadline")));
        scheduler.Admit(Request(1, "fatal_deadline"));

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await scheduler.Fatal.WaitAsync(TimeSpan.FromSeconds(10)));
        await executorCanceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.CancelAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    [Fact]
    public async Task Drain_is_idempotent_cancels_and_observes_all_work_then_rejects_admission()
    {
        using var started = new SemaphoreSlim(0, 2);
        using var cancellationSeen = new SemaphoreSlim(0, 2);
        var cleanupRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new DelegateExecutor(async (_, cancellationToken) =>
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationSeen.Release();
                await cleanupRelease.Task;
                throw;
            }
            return Result("impossible");
        });
        var writer = new RecordingWriter();
        var scheduler = Scheduler(executor, writer);
        scheduler.Admit(Request(1, "drain_one"));
        scheduler.Admit(Request(2, "drain_two"));
        await started.WaitAsync(TimeSpan.FromSeconds(10));
        await started.WaitAsync(TimeSpan.FromSeconds(10));

        var first = scheduler.CancelAndDrainAsync();
        var second = scheduler.CancelAndDrainAsync();
        Assert.Same(first, second);
        await cancellationSeen.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellationSeen.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
            scheduler.Admit(Request(3, "during_drain")));
        cleanupRelease.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        await writer.WaitForCountAsync(2);
        var responses = writer.Snapshot()
            .Select(frame => WorkerOperationProtocol.ParseResponse(frame, BootId, Generation))
            .OrderBy(response => response.RequestId)
            .ToArray();
        Assert.Equal([1L, 2L], responses.Select(response => response.RequestId));
        Assert.All(responses, response =>
            Assert.Equal(WorkerOperationStatus.Canceled, response.Status));
        Assert.Throws<InvalidOperationException>(() => scheduler.Admit(Request(4, "late")));
    }

    private static WorkerOperationScheduler Scheduler(
        IWorkerOperationExecutor executor,
        RecordingWriter writer,
        Func<DateTimeOffset, CancellationToken, Task>? waitUntilDeadline = null) =>
        new(
            BootId,
            Generation,
            initialRequestIdHighWater: 0,
            executor,
            writer.WriteAsync,
            utcNow: () => Now,
            waitUntilDeadline: waitUntilDeadline);

    private static WorkerEnvelope Request(
        long requestId,
        string operation,
        DateTimeOffset? deadline = null) =>
        new(
            WorkerProtocol.Version,
            WorkerMessageKind.Request,
            BootId,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                generation = Generation,
                deadlineUnixTimeMilliseconds = (deadline ?? Now.AddMinutes(1))
                    .ToUnixTimeMilliseconds(),
                operation,
                arguments = new { },
            }));

    private static WorkerEnvelope Cancel(long requestId) =>
        new(
            WorkerProtocol.Version,
            WorkerMessageKind.Cancel,
            BootId,
            requestId,
            JsonSerializer.SerializeToElement(new { generation = Generation }));

    private static JsonElement Result(string value) =>
        JsonSerializer.SerializeToElement(new { value });

    private sealed class DelegateExecutor(
        Func<WorkerOperationRequest, CancellationToken, Task<JsonElement>> execute)
        : IWorkerOperationExecutor
    {
        public Task<JsonElement> ExecuteAsync(
            WorkerOperationRequest request,
            CancellationToken cancellationToken) => execute(request, cancellationToken);
    }

    private sealed class RecordingWriter
    {
        private readonly ConcurrentQueue<WorkerEnvelope> _frames = new();
        private readonly SemaphoreSlim _writes = new(0);

        internal CancellationToken LastCancellationToken { get; private set; }

        internal Task WriteAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
        {
            LastCancellationToken = cancellationToken;
            _frames.Enqueue(envelope);
            _writes.Release();
            return Task.CompletedTask;
        }

        internal WorkerEnvelope[] Snapshot() => _frames.ToArray();

        internal async Task<WorkerEnvelope> WaitForOneAsync()
        {
            await WaitForCountAsync(1);
            return Snapshot()[0];
        }

        internal async Task WaitForCountAsync(int count)
        {
            while (_frames.Count < count)
            {
                if (!await _writes.WaitAsync(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException($"Only {_frames.Count} of {count} responses arrived.");
            }
        }
    }

    private sealed class InlineTaskScheduler : TaskScheduler
    {
        private int _queueCount;

        internal int QueueCount => Volatile.Read(ref _queueCount);

        protected override IEnumerable<Task>? GetScheduledTasks() => [];

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _queueCount);
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(
            Task task,
            bool taskWasPreviouslyQueued) => TryExecuteTask(task);
    }

    private sealed class ThrowingTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task>? GetScheduledTasks() => [];

        protected override void QueueTask(Task task) =>
            throw new InvalidOperationException("secret scheduler failure");

        protected override bool TryExecuteTaskInline(
            Task task,
            bool taskWasPreviouslyQueued) => false;
    }
}
