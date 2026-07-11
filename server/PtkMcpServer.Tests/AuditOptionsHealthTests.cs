using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditOptionsHealthTests
{
    [Fact]
    public void Defaults_freeze_absolute_local_only_paths_without_export_identity()
    {
        var options = AuditOptions.CreateDefault();
        var expectedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk",
            "audit"));

        Assert.Equal(expectedRoot, options.RootDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "spool"), options.SpoolDirectory);
        Assert.Equal(Path.Combine(expectedRoot, "evidence"), options.EvidenceDirectory);
        Assert.True(Path.IsPathFullyQualified(options.RootDirectory));
        Assert.True(Path.IsPathFullyQualified(options.SpoolDirectory));
        Assert.True(Path.IsPathFullyQualified(options.EvidenceDirectory));
        Assert.Equal(AuditProtectionMode.LocalOnly, options.ProtectionMode);
        Assert.Null(options.ExportConfigurationIdentity);
        Assert.Equal(65_536, options.MaxRecordBytes);
    }

    [Fact]
    public void Explicit_small_configuration_is_valid_for_storage_boundary_tests()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptk-audit-options-" + Guid.NewGuid().ToString("N"));

        var options = AuditOptions.Create(
            root,
            maxRecordBytes: 256,
            segmentBytes: 1_024,
            aggregateBytes: 4_096,
            emergencyReserveBytes: 512,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 512,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));

        Assert.Equal(256, options.MaxRecordBytes);
        Assert.Equal(1_024, options.SegmentBytes);
        Assert.Equal(4_096, options.AggregateBytes);
        Assert.Equal(512, options.EmergencyReserveBytes);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RetentionAge);
        Assert.Equal(256, options.MaxEvidenceBytes);
        Assert.Equal(512, options.EvidenceAggregateBytes);
        Assert.Equal(TimeSpan.FromMinutes(1), options.EvidenceRetentionAge);
    }

    [Fact]
    public void Configuration_rejects_relative_paths_invalid_relationships_and_secret_like_identity_values()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptk-audit-options-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => AuditOptions.Create("relative/audit"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditOptions.Create(root, maxRecordBytes: 65_537));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditOptions.Create(root, maxEvidenceBytes: 131_073));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuditOptions.Create(root, segmentBytes: 1_024, aggregateBytes: 512));
        Assert.Throws<ArgumentException>(() => AuditOptions.Create(
            root,
            AuditProtectionMode.LocalOnly,
            new string('a', 64)));
        Assert.Throws<ArgumentException>(() => AuditOptions.Create(root, AuditProtectionMode.Anchored));
        Assert.Throws<ArgumentException>(() => AuditOptions.Create(
            root,
            AuditProtectionMode.Anchored,
            new string('A', 64)));

        var anchored = AuditOptions.Create(
            root,
            AuditProtectionMode.Anchored,
            new string('a', 64));
        Assert.Equal(new string('a', 64), anchored.ExportConfigurationIdentity);
    }

    [Fact]
    public void Health_preserves_degraded_start_and_tracks_emergency_probe_gap_with_injected_utc()
    {
        var times = new Queue<DateTimeOffset>(
        [
            new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero),
            new(2026, 7, 11, 12, 1, 0, TimeSpan.Zero),
            new(2026, 7, 11, 12, 2, 0, TimeSpan.Zero),
        ]);
        var health = CreateHealth(() => times.Dequeue());

        health.MarkDegraded("flush_failed");
        health.MarkUnavailable("spool_unwritable");
        health.RecordEmergencyStateProbe();
        health.RecordEmergencyStateProbe();

        var snapshot = health.Snapshot();
        Assert.Equal(AuditHealthState.Unavailable, snapshot.State);
        Assert.Equal("spool_unwritable", snapshot.FailureClass);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero), snapshot.DegradedSinceUtc);
        Assert.Equal(2, snapshot.EmergencyProbeCount);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 12, 1, 0, TimeSpan.Zero), snapshot.EmergencyProbeFirstUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 11, 12, 2, 0, TimeSpan.Zero), snapshot.EmergencyProbeLastUtc);
        Assert.Equal(AuditProtectionMode.LocalOnly, snapshot.ProtectionMode);
        Assert.Null(snapshot.ExportConfigurationIdentity);
    }

    [Fact]
    public void Recovery_summary_is_consumed_only_after_persistence_succeeds()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var health = CreateHealth(() => now);
        health.MarkUnavailable("spool_unwritable");
        health.RecordEmergencyStateProbe();

        Assert.False(health.TryRecover(_ => false, out var failedSummary));
        Assert.Null(failedSummary);
        Assert.Equal(AuditHealthState.Unavailable, health.Snapshot().State);
        Assert.Equal(1, health.Snapshot().EmergencyProbeCount);

        AuditRecoverySummary? persisted = null;
        Assert.True(health.TryRecover(
            candidate =>
            {
                persisted = candidate;
                return true;
            },
            out var consumed));

        Assert.Equal(persisted, consumed);
        Assert.Equal("spool_unwritable", consumed!.FailureClass);
        Assert.Equal(1, consumed.EmergencyProbeCount);
        Assert.Equal(now, consumed.EmergencyProbeFirstUtc);
        Assert.Equal(now, consumed.EmergencyProbeLastUtc);

        var recovered = health.Snapshot();
        Assert.Equal(AuditHealthState.Recovered, recovered.State);
        Assert.Equal(0, recovered.EmergencyProbeCount);
        Assert.Null(recovered.EmergencyProbeFirstUtc);
        Assert.Null(recovered.EmergencyProbeLastUtc);

        health.MarkHealthy();
        var healthy = health.Snapshot();
        Assert.Equal(AuditHealthState.Healthy, healthy.State);
        Assert.Null(healthy.FailureClass);
        Assert.Null(healthy.DegradedSinceUtc);
    }

    [Fact]
    public async Task Recovery_persistence_can_reenter_snapshot_and_capacity_updates_without_deadlock()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var health = CreateHealth(() => now);
        health.MarkUnavailable("spool_unwritable");
        health.RecordEmergencyStateProbe();

        var recovery = Task.Run(() => health.TryRecover(
            _ =>
            {
                Assert.Equal(AuditHealthState.Unavailable, health.Snapshot().State);
                health.UpdateStorageMetrics(256, 256, 128);
                return true;
            },
            out _));

        Assert.True(await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(AuditHealthState.Recovered, health.Snapshot().State);
        Assert.Equal(256, health.Snapshot().ReservedBytes);
    }

    [Fact]
    public async Task Emergency_probe_racing_recovery_returns_promptly_without_reopening_the_outage()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var health = CreateHealth(() => now);
        health.MarkUnavailable("spool_unwritable");
        var persistenceEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = Task.Run(() => health.TryRecover(
            _ =>
            {
                persistenceEntered.TrySetResult(true);
                releasePersistence.Task.GetAwaiter().GetResult();
                return true;
            },
            out _));

        try
        {
            await persistenceEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var started = DateTimeOffset.UtcNow;
            Assert.False(health.TryRecordEmergencyStateProbe(out var snapshot));
            Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
            Assert.Equal(AuditHealthState.Unavailable, snapshot.State);
            Assert.Equal(0, snapshot.EmergencyProbeCount);

            releasePersistence.TrySetResult(true);
            Assert.True(await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(AuditHealthState.Recovered, health.Snapshot().State);
            Assert.Equal(0, health.Snapshot().EmergencyProbeCount);
        }
        finally
        {
            releasePersistence.TrySetResult(true);
            try { await recovery.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* preserve primary failure */ }
        }
    }

    [Fact]
    public void Capacity_metrics_are_consistent_and_bounded()
    {
        var health = CreateHealth();
        health.UpdateStorageMetrics(spoolBytes: 1_024, reservedBytes: 512, emergencyReserveBytes: 256);

        var snapshot = health.Snapshot();
        Assert.Equal(1_024, snapshot.SpoolBytes);
        Assert.Equal(512, snapshot.ReservedBytes);
        Assert.Equal(2_560, snapshot.EffectiveFreeBytes);
        Assert.Equal(256, snapshot.EmergencyReserveBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            health.UpdateStorageMetrics(spoolBytes: 4_000, reservedBytes: 97, emergencyReserveBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            health.UpdateStorageMetrics(spoolBytes: 0, reservedBytes: 0, emergencyReserveBytes: 513));
    }

    [Fact]
    public void Emergency_probe_count_is_thread_safe()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var health = CreateHealth(() => now);
        health.MarkUnavailable("spool_full");

        Parallel.For(0, 2_000, _ => health.RecordEmergencyStateProbe());

        var snapshot = health.Snapshot();
        Assert.Equal(2_000, snapshot.EmergencyProbeCount);
        Assert.Equal(now, snapshot.EmergencyProbeFirstUtc);
        Assert.Equal(now, snapshot.EmergencyProbeLastUtc);
    }

    [Fact]
    public void Health_rejects_non_utc_clocks_and_unstructured_failure_text()
    {
        var health = CreateHealth(() => new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.FromHours(1)));

        Assert.Throws<ArgumentException>(() => health.MarkUnavailable("disk failed: token=secret"));
        Assert.Throws<InvalidOperationException>(() => health.MarkUnavailable("spool_unwritable"));
    }

    private static AuditHealth CreateHealth(Func<DateTimeOffset>? utcNow = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ptk-audit-health-" + Guid.NewGuid().ToString("N"));
        var options = AuditOptions.Create(
            root,
            maxRecordBytes: 256,
            segmentBytes: 1_024,
            aggregateBytes: 4_096,
            emergencyReserveBytes: 512,
            retentionAge: TimeSpan.FromMinutes(1),
            maxEvidenceBytes: 256,
            evidenceAggregateBytes: 512,
            evidenceRetentionAge: TimeSpan.FromMinutes(1));
        return new AuditHealth(options, utcNow);
    }
}
