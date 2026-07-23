using System.Runtime.InteropServices;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class OutputStoreTests : IDisposable
{
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Sealed_snapshot_has_repeatable_byte_chunks_and_bounded_literal_search()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(() => now);
        var source = "AéB\r\nneedle\r\nAéB";
        var sealedArtifact = Seal(store, source);

        Assert.True(sealedArtifact.Success);
        Assert.NotNull(sealedArtifact.Handle);
        Assert.StartsWith("ptko_", sealedArtifact.Handle, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, sealedArtifact.Handle!);
        SecureAuditStorage.VerifyExternalProtectedDirectory(store.RootPathForTests);
        AssertNoNamedArtifacts(store);

        var first = store.Read(sealedArtifact.Handle!, offset: 3, maximumBytes: 3);
        var repeated = store.Read(sealedArtifact.Handle!, offset: 3, maximumBytes: 3);
        Assert.Equal("B\r\n", first.Text);
        Assert.Equal(6, first.NextOffset);
        Assert.Equal(first, repeated);

        var clippedBeforeMultibyteScalar = store.Read(sealedArtifact.Handle!, offset: 0, maximumBytes: 2);
        Assert.Equal(OutputArtifactState.Available, clippedBeforeMultibyteScalar.State);
        Assert.Equal("A", clippedBeforeMultibyteScalar.Text);
        Assert.Equal(1, clippedBeforeMultibyteScalar.BytesRead);
        Assert.Equal(1, clippedBeforeMultibyteScalar.NextOffset);

        var scalarDoesNotFit = store.Read(sealedArtifact.Handle!, offset: 1, maximumBytes: 1);
        Assert.Equal(OutputArtifactState.InsufficientBound, scalarDoesNotFit.State);
        Assert.Empty(scalarDoesNotFit.Text);
        Assert.Equal(0, scalarDoesNotFit.BytesRead);
        Assert.Equal(1, scalarDoesNotFit.NextOffset);

        var scalar = store.Read(sealedArtifact.Handle!, offset: 1, maximumBytes: 2);
        Assert.Equal("é", scalar.Text);
        Assert.Equal(2, scalar.BytesRead);
        Assert.Equal(3, scalar.NextOffset);

        var search = store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 64);
        var searchRepeated = store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 64);
        var match = Assert.Single(search.Matches);
        Assert.Equal(6, match.Offset);
        Assert.Contains("needle", match.Preview, StringComparison.Ordinal);
        Assert.Equal(search.State, searchRepeated.State);
        Assert.Equal(search.Offset, searchRepeated.Offset);
        Assert.Equal(search.NextOffset, searchRepeated.NextOffset);
        Assert.Equal(search.TotalBytes, searchRepeated.TotalBytes);
        Assert.Equal(search.BytesScanned, searchRepeated.BytesScanned);
        Assert.Equal(search.Matches, searchRepeated.Matches);

        Assert.Equal("B\r\n", store.Read(sealedArtifact.Handle!, 3, 3).Text);
        Assert.Equal(OutputArtifactState.InvalidOffset, store.Read(sealedArtifact.Handle!, 2, 4).State);
    }

    [Fact]
    public void Search_finds_a_utf8_literal_split_across_bounded_windows()
    {
        using var store = CreateStore(() => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var sealedArtifact = Seal(store, "éxxneedle-tail");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Search(sealedArtifact.Handle!, "needle", offset: 0, maximumBytes: 5));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Search(sealedArtifact.Handle!, "need", offset: 0, maximumBytes: 3));

        var offset = 0L;
        var matches = new List<OutputSearchMatch>();
        var iterations = 0;
        while (offset < sealedArtifact.Bytes)
        {
            var result = store.Search(sealedArtifact.Handle!, "need", offset, maximumBytes: 5);
            var repeated = store.Search(sealedArtifact.Handle!, "need", offset, maximumBytes: 5);

            Assert.Equal(result.State, repeated.State);
            Assert.Equal(result.Offset, repeated.Offset);
            Assert.Equal(result.NextOffset, repeated.NextOffset);
            Assert.Equal(result.TotalBytes, repeated.TotalBytes);
            Assert.Equal(result.BytesScanned, repeated.BytesScanned);
            Assert.Equal(result.Matches, repeated.Matches);
            Assert.InRange(result.BytesScanned, 0, 5);
            Assert.True(result.NextOffset > offset, "Bounded search must make forward progress.");

            matches.AddRange(result.Matches);
            offset = result.NextOffset;
            Assert.True(++iterations < 10, "Bounded search did not reach the end of the artifact.");
        }

        var match = Assert.Single(matches);
        Assert.Equal(4, match.Offset);
        Assert.Contains("need", match.Preview, StringComparison.Ordinal);

        var emojiArtifact = Seal(store, "A😀tail", sessionAlias: "emoji");
        var beforeEmoji = store.Search(emojiArtifact.Handle!, "😀", offset: 0, maximumBytes: 4);
        Assert.Empty(beforeEmoji.Matches);
        Assert.Equal(1, beforeEmoji.NextOffset);
        var atEmoji = store.Search(
            emojiArtifact.Handle!,
            "😀",
            beforeEmoji.NextOffset,
            maximumBytes: 4);
        Assert.Equal(1, Assert.Single(atEmoji.Matches).Offset);
        Assert.Equal(5, atEmoji.NextOffset);
    }

    [Fact]
    public void Artifact_cap_never_publishes_a_partial_utf8_scalar()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 2,
            maximumSessionBytes: 2,
            maximumAggregateBytes: 2);

        var artifact = Seal(store, "A😀");

        Assert.Equal(OutputArtifactState.Incomplete, artifact.State);
        Assert.Equal("artifact_cap_exceeded", artifact.DetailCode);
        Assert.Equal(1, artifact.Bytes);
        Assert.Equal("A", store.Read(artifact.Handle!, 0, 2).Text);
    }

    [Fact]
    public void Retention_enforces_caps_with_distinct_incomplete_expired_and_evicted_states()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using (var capped = CreateStore(
                   () => now,
                   maximumArtifactBytes: 8,
                   maximumSessionBytes: 8,
                   maximumAggregateBytes: 8,
                   ttl: TimeSpan.FromMinutes(1)))
        {
            var truncated = Seal(capped, "abcdefghijkl");
            Assert.True(truncated.Success);
            Assert.Equal(OutputArtifactState.Incomplete, truncated.State);
            Assert.Equal(8, truncated.Bytes);
            Assert.Equal("artifact_cap_exceeded", truncated.DetailCode);

            var read = capped.Read(truncated.Handle!, 0, 8);
            Assert.Equal("abcdefgh", read.Text);
            Assert.False(read.Complete);

            now = now.AddMinutes(1).AddTicks(1);
            capped.RunRetentionForTests();
            var expired = capped.Status(truncated.Handle!);
            Assert.Equal(OutputArtifactState.Expired, expired.State);
            Assert.Equal("ttl_expired", expired.DetailCode);
            Assert.NotEqual(OutputArtifactState.NotFound, expired.State);
            AssertNoNamedArtifacts(capped);

            var expiredRead = capped.Read(truncated.Handle!, 0, 8);
            Assert.Equal(OutputArtifactState.Expired, expiredRead.State);
            Assert.Empty(expiredRead.Text);
            Assert.Equal(0, expiredRead.TotalBytes);
            Assert.Equal(0, expiredRead.BytesRead);

            var expiredSearch = capped.Search(truncated.Handle!, "a", 0, 8);
            Assert.Equal(OutputArtifactState.Expired, expiredSearch.State);
            Assert.Empty(expiredSearch.Matches);
            Assert.Equal(0, expiredSearch.TotalBytes);
            Assert.Equal(0, expiredSearch.BytesScanned);

            Assert.True(
                capped.TryReserve("replacement", out var replacement, out var replacementFailure),
                replacementFailure);
            replacement!.Dispose();
        }

        now = new DateTimeOffset(2026, 7, 13, 13, 0, 0, TimeSpan.Zero);
        using var evicting = CreateStore(
            () => now,
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 8);
        var first = Seal(evicting, "12345678", sessionAlias: "alpha");
        var second = Seal(evicting, "abcdefgh", sessionAlias: "beta");

        var evicted = evicting.Status(first.Handle!);
        Assert.Equal(OutputArtifactState.Evicted, evicted.State);
        Assert.Equal("aggregate_capacity", evicted.DetailCode);
        Assert.Equal(OutputArtifactState.Available, evicting.Status(second.Handle!).State);
        Assert.Equal("abcdefgh", evicting.Read(second.Handle!, 0, 8).Text);
        AssertNoNamedArtifacts(evicting);

        var evictedRead = evicting.Read(first.Handle!, 0, 8);
        Assert.Equal(OutputArtifactState.Evicted, evictedRead.State);
        Assert.Empty(evictedRead.Text);
        Assert.Equal(0, evictedRead.TotalBytes);
        Assert.Equal(0, evictedRead.BytesRead);

        var evictedSearch = evicting.Search(first.Handle!, "1", 0, 8);
        Assert.Equal(OutputArtifactState.Evicted, evictedSearch.State);
        Assert.Empty(evictedSearch.Matches);
        Assert.Equal(0, evictedSearch.TotalBytes);
        Assert.Equal(0, evictedSearch.BytesScanned);
        Assert.Equal(OutputArtifactState.NotFound, evicting.Status("ptko_unknown").State);
    }

    [Fact]
    public void Concurrent_reservations_count_toward_session_and_aggregate_capacity()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 16);

        Assert.True(store.TryReserve("alpha", out var alpha, out var alphaFailure), alphaFailure);
        OutputCaptureReservation? beta = null;
        OutputCaptureReservation? gamma = null;
        try
        {
            Assert.False(store.TryReserve("alpha", out var sameSession, out var sessionFailure));
            Assert.Null(sameSession);
            Assert.Equal("capacity", sessionFailure);

            Assert.True(store.TryReserve("beta", out beta, out var betaFailure), betaFailure);
            Assert.False(store.TryReserve("gamma", out var overAggregate, out var aggregateFailure));
            Assert.Null(overAggregate);
            Assert.Equal("capacity", aggregateFailure);

            alpha!.Dispose();
            alpha = null;
            Assert.True(store.TryReserve("gamma", out gamma, out var gammaFailure), gammaFailure);
        }
        finally
        {
            alpha?.Dispose();
            beta?.Dispose();
            gamma?.Dispose();
        }

        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
    }

    [Fact]
    public async Task Publishing_claim_beats_cancel_and_coordinator_returns_exact_handle()
    {
        var publishingClaimed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var cancellationRejected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sealDeadlineElapsed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var store = CreateStore(
            () => DateTimeOffset.UtcNow,
            artifactPublishingClaimedForTests: () =>
            {
                publishingClaimed.TrySetResult();
                releasePublication.Wait();
        });
        using var capture = new ForegroundOutputCapture(
            store,
            sealCancellationRejectedForTests: () => cancellationRejected.TrySetResult(),
            sealDelayForTests: _ => sealDeadlineElapsed.Task);
        await capture.PrepareAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var sealTask = capture.SealAsync(
            new OutputArtifactContent(
                "PUBLISHING_CLAIM_WINS",
                [],
                [],
                [],
                null,
                OutputProvenance.PowerShellObjects),
            TimeSpan.FromMilliseconds(25));

        try
        {
            await publishingClaimed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            sealDeadlineElapsed.TrySetResult();
            await cancellationRejected.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(sealTask.IsCompleted);
            releasePublication.Set();

            var recovery = await sealTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(recovery.Advertise);
            Assert.Equal(OutputArtifactState.Available, recovery.State);
            Assert.Null(recovery.DetailCode);
            var handle = Assert.IsType<string>(recovery.Handle);
            capture.Dispose();
            Assert.Equal(
                "PUBLISHING_CLAIM_WINS",
                store.Read(handle, 0, OutputStore.MaximumReadBytes).Text);
            AssertNoNamedArtifacts(store);
        }
        finally
        {
            releasePublication.Set();
        }
    }

    [Fact]
    public async Task Execution_capture_transfers_once_and_background_lease_owns_terminal_seal()
    {
        using var store = CreateStore(() => DateTimeOffset.UtcNow);
        using var owner = ExecutionOutputCaptureAdapter.Create(store);
        var preparation = await owner.PrepareAsync(
            DateTimeOffset.UtcNow.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(preparation.Available);
        Assert.Equal("capture_pending", preparation.Summary.DetailCode);
        Assert.True(owner.TryTransferToBackground(out var transferred));
        using var background = Assert.IsAssignableFrom<IExecutionOutputCapture>(transferred);
        Assert.False(owner.TryTransferToBackground(out var duplicate));
        Assert.Null(duplicate);
        owner.Dispose();

        var recovery = await background.SealAsync(
            new OutputArtifactContent(
                "TRANSFERRED_BACKGROUND_OUTPUT",
                [],
                [],
                [],
                null,
                OutputProvenance.DirectText),
            TimeSpan.FromSeconds(5));
        var duplicateTerminal = await background.SealIncompleteAsync(
            new OutputArtifactContent(
                "must-not-replace",
                [],
                [],
                [],
                null,
                OutputProvenance.DirectText),
            "duplicate_terminal",
            TimeSpan.FromSeconds(5));

        Assert.Equal(OutputArtifactState.Available, recovery.State);
        var handle = Assert.IsType<string>(recovery.Handle);
        Assert.Equal("capture_already_terminal", duplicateTerminal.DetailCode);
        Assert.Equal(
            "TRANSFERRED_BACKGROUND_OUTPUT",
            store.Read(handle, 0, OutputStore.MaximumReadBytes).Text);
    }

    [Fact]
    public async Task Expired_execution_capture_deadline_reserves_no_store_capacity()
    {
        using var store = CreateStore(() => DateTimeOffset.UtcNow);
        using var owner = ExecutionOutputCaptureAdapter.Create(store);

        var preparation = await owner.PrepareAsync(
            DateTimeOffset.UtcNow.AddMilliseconds(-1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.False(preparation.Available);
        Assert.Equal("output_store_prepare_timed_out", preparation.Summary.DetailCode);
        Assert.True(store.TryReserve(
            "default",
            out var independentReservation,
            out var failure), failure);
        independentReservation!.Dispose();
    }

    [Fact]
    public void Expiration_overflow_cannot_publish_or_leak_a_reservation()
    {
        var now = DateTimeOffset.MaxValue.AddMinutes(-1);
        using var store = CreateStore(
            () => now,
            maximumArtifactBytes: 8,
            maximumSessionBytes: 16,
            maximumAggregateBytes: 16,
            ttl: TimeSpan.FromMinutes(15));

        Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
        var result = reservation!.Seal(new OutputArtifactContent(
            "overflow",
            [],
            [],
            [],
            ExitCode: null,
            OutputProvenance.PowerShellObjects));

        Assert.False(result.Success);
        Assert.Null(result.Handle);
        Assert.Equal("storage_unavailable", result.DetailCode);
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));
        Assert.True(store.TryReserve("default", out var first, out var firstFailure), firstFailure);
        Assert.True(store.TryReserve("default", out var second, out var secondFailure), secondFailure);
        first!.Dispose();
        second!.Dispose();
    }

    [Fact]
    public void Retained_artifact_count_bounds_zero_byte_snapshots_and_open_handles()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumRetainedArtifacts: 2);
        var first = Seal(store, string.Empty, sessionAlias: "alpha");
        var second = Seal(store, string.Empty, sessionAlias: "beta");

        AssertNoNamedArtifacts(store);
        Assert.True(store.TryReserve("gamma", out var third, out var failure), failure);

        Assert.Equal(OutputArtifactState.Evicted, store.Status(first.Handle!).State);
        Assert.Equal("artifact_count_capacity", store.Status(first.Handle!).DetailCode);
        Assert.Equal(OutputArtifactState.Available, store.Status(second.Handle!).State);
        AssertNoNamedArtifacts(store);
        third!.Dispose();
    }

    [Fact]
    public void Periodic_retention_expires_anonymous_handle_and_releases_accounting_without_api_trigger()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var expiredNow = now.AddMinutes(1).AddTicks(1);
        using var retentionObserved = new ManualResetEventSlim();
        var exposeExpiredClock = 0;
        using var store = CreateStore(
            () =>
            {
                if (Volatile.Read(ref exposeExpiredClock) == 0) return now;
                retentionObserved.Set();
                return expiredNow;
            },
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 8,
            ttl: TimeSpan.FromMinutes(1),
            retentionInterval: TimeSpan.FromMilliseconds(10));
        var artifact = Seal(store, "12345678");

        Volatile.Write(ref exposeExpiredClock, 1);
        Assert.True(
            retentionObserved.Wait(TimeSpan.FromSeconds(5)),
            "The periodic retention callback did not observe the expired clock.");
        Volatile.Write(ref exposeExpiredClock, 0);

        var expired = store.Status(artifact.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
        Assert.Empty(store.Read(artifact.Handle!, 0, 8).Text);
        Assert.True(
            store.TryReserve("replacement", out var replacement, out var replacementFailure),
            replacementFailure);
        replacement!.Dispose();
        AssertNoNamedArtifacts(store);
    }

    [Fact]
    public void Anonymous_eviction_stays_nondisclosing_and_releases_retained_handle_accounting()
    {
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            maximumArtifactBytes: 8,
            maximumSessionBytes: 8,
            maximumAggregateBytes: 8);
        var artifact = Seal(store, "12345678", sessionAlias: "alpha");

        Assert.True(store.TryReserve("beta", out var admitted, out var failure), failure);
        Assert.Equal(OutputArtifactState.Evicted, store.Status(artifact.Handle!).State);
        Assert.Empty(store.Read(artifact.Handle!, 0, 8).Text);
        AssertNoNamedArtifacts(store);
        admitted!.Dispose();
    }

    [Fact]
    public void Anonymous_snapshot_preserves_content_and_never_deletes_a_later_path_substitute()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        string? anonymousPath = null;
        using var store = CreateStore(
            () => now,
            ttl: TimeSpan.FromMinutes(1),
            artifactCreateStartingForTests: path => anonymousPath = path);
        var artifact = Seal(store, "original snapshot");
        var path = Assert.IsType<string>(anonymousPath);
        Assert.False(File.Exists(path));

        Assert.Equal(
            "original snapshot",
            store.Read(artifact.Handle!, 0, OutputStore.MaximumReadBytes).Text);

        // A retained anonymous handle must be identity-bound. In particular,
        // its eventual close must not implement DeleteOnClose by pathname and
        // remove a different file created under the retired artifact name.
        using (var substitute = SecureAuditStorage.CreateExclusiveFile(path))
        {
            substitute.Write("substitute"u8);
            substitute.Flush(flushToDisk: true);
        }

        now = now.AddMinutes(1).AddTicks(1);
        store.RunRetentionForTests();
        Assert.Equal(OutputArtifactState.Expired, store.Status(artifact.Handle!).State);
        Assert.Empty(store.Read(artifact.Handle!, 0, OutputStore.MaximumReadBytes).Text);
        Assert.Equal("substitute", File.ReadAllText(path));
        File.Delete(path);
    }

    [Fact]
    public void Dispose_closes_retained_anonymous_artifact_handles_before_returning()
    {
        var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var artifact = Seal(store, "dispose-owned snapshot");
        var retainedHandle = store.RetainedArtifactHandleForTests(artifact.Handle!);

        Assert.False(retainedHandle.IsClosed);

        store.Dispose();

        Assert.True(retainedHandle.IsClosed);
    }

    [Fact]
    public void Rename_and_substitute_after_identity_check_fails_before_sensitive_rendering()
    {
        string? createdPath = null;
        string? verifiedPath = null;
        string? displacedPath = null;
        var writeStarted = false;
        using var store = CreateStore(
            () => DateTimeOffset.UtcNow,
            artifactCreateStartingForTests: path => createdPath = path,
            artifactWriteStartingForTests: _ => writeStarted = true,
            artifactUnlinkIdentityVerifiedForTests: path =>
            {
                verifiedPath = path;
                displacedPath = $"{path}.displaced";
                File.Move(path, displacedPath);
                using var substitute = SecureAuditStorage.CreateExclusiveFile(path);
                substitute.Write("substitute"u8);
                substitute.Flush(flushToDisk: true);
            });

        try
        {
            Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
            using (reservation)
            {
                var result = reservation!.Seal(new OutputArtifactContent(
                    "SENSITIVE_NEVER_WRITTEN",
                    [],
                    [],
                    [],
                    null,
                    OutputProvenance.PowerShellObjects));

                Assert.False(result.Success);
                Assert.Null(result.Handle);
                Assert.Equal("storage_unavailable", result.DetailCode);
            }

            Assert.Equal(createdPath, verifiedPath);
            Assert.False(writeStarted);
            var displaced = Assert.IsType<string>(displacedPath);
            Assert.True(File.Exists(displaced));
            Assert.Equal(0, new FileInfo(displaced).Length);
            Assert.DoesNotContain(
                "SENSITIVE_NEVER_WRITTEN",
                File.ReadAllText(displaced),
                StringComparison.Ordinal);
            Assert.False(File.Exists(Assert.IsType<string>(createdPath)));

            // The failed seal must release both reservation and aggregate
            // accounting. Two full-size replacements consume the session cap.
            Assert.True(
                store.TryReserve("default", out var firstReplacement, out var firstFailure),
                firstFailure);
            Assert.True(
                store.TryReserve("default", out var secondReplacement, out var secondFailure),
                secondFailure);
            firstReplacement!.Dispose();
            secondReplacement!.Dispose();
        }
        finally
        {
            if (createdPath is not null && File.Exists(createdPath))
                File.Delete(createdPath);
            if (displacedPath is not null && File.Exists(displacedPath))
                File.Delete(displacedPath);
        }
    }

    [Fact]
    public void Windows_hard_link_after_identity_check_fails_before_sensitive_rendering()
    {
        if (!OperatingSystem.IsWindows()) return;

        string? createdPath = null;
        string? hardLinkPath = null;
        var writeStarted = false;
        using var store = CreateStore(
            () => DateTimeOffset.UtcNow,
            artifactCreateStartingForTests: path => createdPath = path,
            artifactWriteStartingForTests: _ => writeStarted = true,
            artifactUnlinkIdentityVerifiedForTests: path =>
            {
                hardLinkPath = $"{path}.linked";
                if (!CreateHardLink(hardLinkPath, path, IntPtr.Zero))
                {
                    throw new IOException(
                        $"CreateHardLink failed with Windows error {Marshal.GetLastPInvokeError()}.");
                }
            });

        try
        {
            Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
            using (reservation)
            {
                var result = reservation!.Seal(new OutputArtifactContent(
                    "SENSITIVE_NEVER_WRITTEN",
                    [],
                    [],
                    [],
                    null,
                    OutputProvenance.PowerShellObjects));

                Assert.False(result.Success);
                Assert.Null(result.Handle);
                Assert.Equal("storage_unavailable", result.DetailCode);
            }

            Assert.False(writeStarted);
            var linked = Assert.IsType<string>(hardLinkPath);
            Assert.True(File.Exists(linked));
            Assert.Equal(0, new FileInfo(linked).Length);
            Assert.DoesNotContain(
                "SENSITIVE_NEVER_WRITTEN",
                File.ReadAllText(linked),
                StringComparison.Ordinal);
        }
        finally
        {
            if (createdPath is not null && File.Exists(createdPath))
                File.Delete(createdPath);
            if (hardLinkPath is not null && File.Exists(hardLinkPath))
                File.Delete(hardLinkPath);
        }
    }

    [Fact]
    public void Sensitive_rendering_starts_only_after_the_protected_name_is_gone()
    {
        string? createdPath = null;
        var writeStarted = false;
        using var store = CreateStore(
            () => DateTimeOffset.UtcNow,
            artifactCreateStartingForTests: path => createdPath = path,
            artifactWriteStartingForTests: path =>
            {
                writeStarted = true;
                Assert.Equal(createdPath, path);
                Assert.False(File.Exists(path));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
            });

        var artifact = Seal(store, "sensitive invocation output");

        Assert.True(artifact.Success);
        Assert.True(writeStarted);
        Assert.Equal(
            "sensitive invocation output",
            store.Read(artifact.Handle!, 0, OutputStore.MaximumReadBytes).Text);
    }

    [Fact]
    public void SealIncomplete_keeps_terminal_streams_labeled_and_forces_incomplete_state()
    {
        using var store = CreateStore(() => DateTimeOffset.UtcNow);
        Assert.True(store.TryReserve("default", out var reservation, out var failure), failure);
        using (reservation)
        {
            var result = reservation!.SealIncomplete(new OutputArtifactContent(
                "stdout",
                ["native diagnostic"],
                ["powershell error"],
                ["warning"],
                7,
                OutputProvenance.PowerShellObjects),
                "worker_died");
            Assert.True(result.Success);
            Assert.Equal(OutputArtifactState.Incomplete, result.State);

            var status = store.Status(result.Handle!);
            Assert.False(status.Complete);
            Assert.Equal("worker_died", status.DetailCode);
            var read = store.Read(result.Handle!, 0, OutputStore.MaximumReadBytes);
            Assert.Contains("stdout", read.Text, StringComparison.Ordinal);
            Assert.Contains("[exit] 7", read.Text, StringComparison.Ordinal);
            Assert.Contains("[stderr]", read.Text, StringComparison.Ordinal);
            Assert.Contains("native diagnostic", read.Text, StringComparison.Ordinal);
            Assert.Contains("[errors]", read.Text, StringComparison.Ordinal);
            Assert.Contains("[warnings]", read.Text, StringComparison.Ordinal);
            AssertNoNamedArtifacts(store);
        }
    }

    [Fact]
    public async Task Read_and_search_file_io_does_not_wedge_the_store_gate()
    {
        using var ioEntered = new ManualResetEventSlim(false);
        using var releaseIo = new ManualResetEventSlim(false);
        var wedgeArmed = false;
        using var store = CreateStore(
            () => new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            retainedReadStartingForTests: () =>
            {
                if (!Volatile.Read(ref wedgeArmed)) return;
                ioEntered.Set();
                Assert.True(
                    releaseIo.Wait(TimeSpan.FromSeconds(30)),
                    "wedged artifact io was never released");
            });
        var sealedArtifact = Seal(store, "needle in a haystack");

        Volatile.Write(ref wedgeArmed, true);
        var read = Task.Run(() => store.Read(sealedArtifact.Handle!, 0, 64));
        Assert.True(ioEntered.Wait(TimeSpan.FromSeconds(30)), "read never reached file io");

        // Status takes _gate; it must complete while the read io is wedged.
        var status = Task.Run(() => store.Status(sealedArtifact.Handle!));
        var statusCompleted =
            await Task.WhenAny(status, Task.Delay(TimeSpan.FromSeconds(5))) == status;
        releaseIo.Set();
        Assert.True(statusCompleted, "Status wedged behind retained-handle read io");
        Assert.Equal(OutputArtifactState.Available, (await status).State);
        Assert.Equal("needle in a haystack", (await read).Text);

        ioEntered.Reset();
        releaseIo.Reset();
        var search = Task.Run(() => store.Search(sealedArtifact.Handle!, "needle", 0, 64));
        Assert.True(ioEntered.Wait(TimeSpan.FromSeconds(30)), "search never reached file io");
        var statusDuringSearch = Task.Run(() => store.Status(sealedArtifact.Handle!));
        var statusDuringSearchCompleted =
            await Task.WhenAny(statusDuringSearch, Task.Delay(TimeSpan.FromSeconds(5))) ==
            statusDuringSearch;
        releaseIo.Set();
        Assert.True(statusDuringSearchCompleted, "Status wedged behind search io");
        Assert.Equal(OutputArtifactState.Available, (await statusDuringSearch).State);
        var searchResult = await search;
        Assert.Single(searchResult.Matches);
        Assert.Equal(0, searchResult.Matches[0].Offset);
    }

    [Fact]
    public async Task Retention_delete_io_does_not_wedge_the_store_gate()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var deleteEntered = new ManualResetEventSlim(false);
        using var releaseDelete = new ManualResetEventSlim(false);
        var wedgeArmed = false;
        using var store = CreateStore(
            () => now,
            maximumArtifactBytes: 32,
            maximumSessionBytes: 32,
            maximumAggregateBytes: 64,
            artifactDeleteStartingForTests: _ =>
            {
                if (!Volatile.Read(ref wedgeArmed)) return;
                deleteEntered.Set();
                Assert.True(
                    releaseDelete.Wait(TimeSpan.FromSeconds(30)),
                    "wedged artifact delete io was never released");
            });
        var doomed = Seal(store, "expired artifact", sessionAlias: "doomed");
        now += TimeSpan.FromMinutes(10);
        var survivor = Seal(store, "needle in a haystack", sessionAlias: "survivor");

        // Expire only the first artifact, then wedge its unlink: the delete
        // io must run outside _gate (rbc-14).
        now += TimeSpan.FromMinutes(6);
        Volatile.Write(ref wedgeArmed, true);
        var retention = Task.Run(() => store.RunRetentionForTests());
        Assert.True(
            deleteEntered.Wait(TimeSpan.FromSeconds(30)),
            "retention never reached delete io");

        // Status and Read take _gate; they must complete while the expired
        // artifact's delete io is wedged.
        var status = Task.Run(() => store.Status(survivor.Handle!));
        var statusCompleted =
            await Task.WhenAny(status, Task.Delay(TimeSpan.FromSeconds(5))) == status;
        Assert.True(statusCompleted, "Status wedged behind retention delete io");
        Assert.Equal(OutputArtifactState.Available, (await status).State);

        var read = Task.Run(() => store.Read(survivor.Handle!, 0, 64));
        var readCompleted =
            await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) == read;
        Assert.True(readCompleted, "Read wedged behind retention delete io");
        Assert.Equal("needle in a haystack", (await read).Text);

        releaseDelete.Set();
        await retention;
        Volatile.Write(ref wedgeArmed, false);

        // The released delete must have freed the expired artifact's bytes:
        // this reservation only fits the aggregate cap if they were
        // reclaimed, and it must not evict the survivor to make room.
        var expired = store.Status(doomed.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
        Assert.True(
            store.TryReserve("post", out var replacement, out var postFailure),
            postFailure);
        replacement!.Dispose();
        Assert.Equal(
            OutputArtifactState.Available,
            store.Status(survivor.Handle!).State);
        AssertNoNamedArtifacts(store);
    }

    [Fact]
    public void Reservation_settles_expired_deletes_before_evicting_live_artifacts()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(
            () => now,
            maximumArtifactBytes: 32,
            maximumSessionBytes: 32,
            maximumAggregateBytes: 64);
        var doomed = Seal(store, "expired artifact", sessionAlias: "doomed");
        now += TimeSpan.FromMinutes(10);
        var survivor = Seal(store, "needle in a haystack", sessionAlias: "survivor");

        // Expire only the first artifact, then reserve. The reservation's
        // inline retention pass claims the expired artifact, whose settling
        // bytes cover the aggregate shortfall; those bytes must be reclaimed
        // in preference to evicting the live survivor (rbc-14 follow-up:
        // settle before evicting).
        now += TimeSpan.FromMinutes(6);
        Assert.True(
            store.TryReserve("post", out var reservation, out var failure),
            failure);
        reservation!.Dispose();

        Assert.Equal(
            OutputArtifactState.Available,
            store.Status(survivor.Handle!).State);
        var expired = store.Status(doomed.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
        AssertNoNamedArtifacts(store);
    }

    [Fact]
    public void Reservation_settles_expired_deletes_before_evicting_for_artifact_slots()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var store = CreateStore(
            () => now,
            maximumRetainedArtifacts: 2);
        var doomed = Seal(store, "expired artifact", sessionAlias: "doomed");
        now += TimeSpan.FromMinutes(10);
        var survivor = Seal(store, "needle in a haystack", sessionAlias: "survivor");

        // Both retained-artifact slots are held; the expired artifact's
        // retained handle frees one once its claim settles. The reservation
        // must wait for that settle instead of evicting the survivor
        // (rbc-14 follow-up: settle before evicting).
        now += TimeSpan.FromMinutes(6);
        Assert.True(
            store.TryReserve("post", out var reservation, out var failure),
            failure);
        reservation!.Dispose();

        Assert.Equal(
            OutputArtifactState.Available,
            store.Status(survivor.Handle!).State);
        var expired = store.Status(doomed.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
        AssertNoNamedArtifacts(store);
    }

    [Fact]
    public async Task Expiry_during_unlocked_read_reports_the_tombstone_state()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var ioEntered = new ManualResetEventSlim(false);
        using var releaseIo = new ManualResetEventSlim(false);
        var wedgeArmed = false;
        using var store = CreateStore(
            () => now,
            retainedReadStartingForTests: () =>
            {
                if (!Volatile.Read(ref wedgeArmed)) return;
                ioEntered.Set();
                Assert.True(
                    releaseIo.Wait(TimeSpan.FromSeconds(30)),
                    "wedged artifact io was never released");
            });
        var sealedArtifact = Seal(store, "abc");

        Volatile.Write(ref wedgeArmed, true);
        var read = Task.Run(() => store.Read(sealedArtifact.Handle!, 0, 8));
        Assert.True(ioEntered.Wait(TimeSpan.FromSeconds(30)), "read never reached file io");

        // Expire and tombstone the artifact (disposing its stream) while the
        // read is parked between its snapshot and its file io.
        now += TimeSpan.FromHours(1);
        store.RunRetentionForTests();
        releaseIo.Set();

        var result = await read;
        Assert.Equal(OutputArtifactState.Expired, result.State);
        Assert.Equal("ttl_expired", result.DetailCode);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task Reservation_rechecks_capacity_when_settle_finalizes_before_wait()
    {
        // Lost-pulse race (codex rbc-14 turn 2): a reserver defers to a
        // settling claim held by a concurrent drainer, but the drainer
        // finalizes (reclaiming the bytes) before the reserver re-locks.
        // The reserver must re-check capacity instead of spuriously
        // failing with "capacity" while the store has room.
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using var unlinkStarted = new ManualResetEventSlim(false);
        using var releaseUnlink = new ManualResetEventSlim(false);
        using var drainerDone = new ManualResetEventSlim(false);
        var unlinkWedged = 0;
        var settleWindowed = 0;
        using var store = CreateStore(
            () => now,
            maximumArtifactBytes: 32,
            maximumSessionBytes: 32,
            maximumAggregateBytes: 64,
            artifactDeleteStartingForTests: _ =>
            {
                // Wedge only the concurrent drainer's unlink; later drains
                // (e.g. dispose-time cleanup) must run unimpeded.
                if (Interlocked.CompareExchange(ref unlinkWedged, 1, 0) != 0)
                {
                    return;
                }

                unlinkStarted.Set();
                releaseUnlink.Wait(TimeSpan.FromSeconds(30));
            },
            reservationSettlingForTests: () =>
            {
                // First hit: the reserver has deferred to the settling
                // claim and released the store lock. Let the drainer
                // finalize completely before the reserver drains, so the
                // finalize pulse fires with nobody waiting.
                if (Interlocked.CompareExchange(ref settleWindowed, 1, 0) != 0)
                {
                    return;
                }

                releaseUnlink.Set();
                Assert.True(drainerDone.Wait(TimeSpan.FromSeconds(30)));
            });
        var doomed = Seal(store, "expired artifact", sessionAlias: "doomed");
        now += TimeSpan.FromMinutes(10);
        var survivor = Seal(store, "needle in a haystack", sessionAlias: "survivor");

        // Expire the first artifact and let a background retention pass
        // claim it and wedge inside its off-gate unlink.
        now += TimeSpan.FromMinutes(6);
        var drainer = Task.Run(() =>
        {
            store.RunRetentionForTests();
            drainerDone.Set();
        });
        Assert.True(
            unlinkStarted.Wait(TimeSpan.FromSeconds(30)),
            "retention drain never reached the wedged unlink");

        Assert.True(
            store.TryReserve("post", out var reservation, out var failure),
            failure);
        reservation!.Dispose();
        await drainer.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, settleWindowed);
        Assert.Equal(
            OutputArtifactState.Available,
            store.Status(survivor.Handle!).State);
        var expired = store.Status(doomed.Handle!);
        Assert.Equal(OutputArtifactState.Expired, expired.State);
        Assert.Equal("ttl_expired", expired.DetailCode);
        AssertNoNamedArtifacts(store);
    }

    private OutputStore CreateStore(
        Func<DateTimeOffset> clock,
        long maximumArtifactBytes = 1024,
        long maximumSessionBytes = 2048,
        long maximumAggregateBytes = 4096,
        TimeSpan? ttl = null,
        TimeSpan? retentionInterval = null,
        int maximumRetainedArtifacts = 4096,
        Action<string>? artifactCreateStartingForTests = null,
        Action<string>? artifactWriteStartingForTests = null,
        Action<string>? artifactUnlinkIdentityVerifiedForTests = null,
        Action? artifactPublishingClaimedForTests = null,
        Action? retainedReadStartingForTests = null,
        Action<string>? artifactDeleteStartingForTests = null,
        Action? reservationSettlingForTests = null)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "output-tests",
            Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return new OutputStore(new OutputStoreOptions(
            root,
            ttl ?? TimeSpan.FromMinutes(15),
            retentionInterval ?? TimeSpan.FromHours(1),
            maximumArtifactBytes,
            maximumSessionBytes,
            maximumAggregateBytes,
            clock,
            ArtifactDeleteStartingForTests: artifactDeleteStartingForTests,
            MaximumRetainedArtifacts: maximumRetainedArtifacts,
            ArtifactCreateStartingForTests: artifactCreateStartingForTests,
            ArtifactWriteStartingForTests: artifactWriteStartingForTests,
            ArtifactUnlinkIdentityVerifiedForTests: artifactUnlinkIdentityVerifiedForTests,
            ArtifactPublishingClaimedForTests: artifactPublishingClaimedForTests,
            RetainedReadStartingForTests: retainedReadStartingForTests,
            ReservationSettlingForTests: reservationSettlingForTests));
    }

    private static OutputSealResult Seal(OutputStore store, string text, string sessionAlias = "default")
    {
        Assert.True(store.TryReserve(sessionAlias, out var reservation, out var failure), failure);
        using (reservation)
        {
            var result = reservation!.Seal(new OutputArtifactContent(
                text,
                [],
                [],
                [],
                null,
                OutputProvenance.PowerShellObjects));
            if (result.Success) AssertNoNamedArtifacts(store);
            return result;
        }
    }

    private static void AssertNoNamedArtifacts(OutputStore store) =>
        Assert.Empty(Directory.GetFiles(store.RootPathForTests));

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
