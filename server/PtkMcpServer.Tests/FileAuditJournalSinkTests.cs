using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class FileAuditJournalSinkTests : IDisposable
{
    private readonly List<string> _roots = [];
    private static readonly DateTimeOffset BaseTime =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid HostId =
        Guid.Parse("12345678-1234-4abc-8def-0123456789ab");
    private static readonly Guid BootId =
        Guid.Parse("22345678-1234-4abc-8def-0123456789ab");

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the test failure that prevented ordinary cleanup.
            }
        }
    }

    [Fact]
    public void Physical_allocation_keeps_zero_eof_and_closed_file_is_only_exact_jsonl()
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 3);
        var health = new AuditHealth(options, () => BaseTime);
        var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        var path = sink.CurrentSegmentPath;
        Assert.Equal(0, new FileInfo(path).Length);

        SerializedAuditEvent appended;
        using (var journal = Journal(options, health, sink, BootId))
        {
            Assert.True(journal.TryReserve(1, out var lease, out _));
            appended = journal.Append(lease!, Input("call.accepted"));
            lease!.Release();
        }

        var fileBytes = File.ReadAllBytes(path);
        Assert.Equal(appended.Utf8Line.ToArray(), fileBytes);
        Assert.Equal((byte)'\n', fileBytes[^1]);
        Assert.DoesNotContain((byte)0, fileBytes);
        using var document = JsonDocument.Parse(fileBytes.AsMemory(0, fileBytes.Length - 1));
        Assert.Equal(1, document.RootElement.GetProperty("sequence").GetInt64());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(SecureAuditStorage.OwnerFileMode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Rotation_preserves_one_global_sequence_and_hash_chain_across_files()
    {
        // One ordinary record plus the separately held recovery slot.
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 4);
        var health = new AuditHealth(options, () => BaseTime);
        var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        using var journal = Journal(options, health, sink, BootId);

        Assert.True(journal.TryReserve(1, out var firstLease, out _));
        var first = journal.Append(firstLease!, Input("call.accepted"));
        firstLease!.Release();
        var firstPath = sink.CurrentSegmentPath;

        Assert.True(journal.TryReserve(1, out var secondLease, out _));
        var secondPath = sink.CurrentSegmentPath;
        Assert.NotEqual(firstPath, secondPath);
        var second = journal.Append(secondLease!, Input("call.completed"));
        secondLease!.Release();
        journal.Dispose();

        using var firstDocument = ParseFile(firstPath);
        using var secondDocument = ParseFile(secondPath);
        Assert.Equal(1, firstDocument.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(2, secondDocument.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(
            first.EventHash,
            secondDocument.RootElement.GetProperty("previous_event_hash").GetString());
        Assert.Equal(second.EventHash, secondDocument.RootElement.GetProperty("event_hash").GetString());
    }

    [Fact]
    public void Local_retention_skips_a_locked_live_segment_then_sweeps_it_after_close()
    {
        var root = NewRoot();
        var options = Options(
            root,
            segmentSlots: 1,
            aggregateSegments: 4,
            retentionAge: TimeSpan.FromMinutes(1));
        var now = BaseTime;
        var first = new FileAuditJournalSink(options, BootId, () => now);
        var firstPath = first.CurrentSegmentPath;
        File.SetLastWriteTimeUtc(firstPath, now.AddMinutes(-5).UtcDateTime);

        var secondBoot = Guid.Parse("32345678-1234-4abc-8def-0123456789ab");
        using var second = new FileAuditJournalSink(options, secondBoot, () => now);
        var secondPath = second.CurrentSegmentPath;
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(second.CanReserve(options.MaxRecordBytes));
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));

        first.Dispose();
        File.SetLastWriteTimeUtc(firstPath, now.AddMinutes(-5).UtcDateTime);
        Assert.True(second.CanReserve(options.MaxRecordBytes));
        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public void Factory_persists_one_owner_only_host_id_and_mints_a_new_boot_id()
    {
        var root = NewRoot();
        var options = Options(root, segmentSlots: 2, aggregateSegments: 4);
        Guid hostId;
        Guid bootId;
        using (var first = AuditJournalFactory.Open(
                   options,
                   new AuditHealth(options, () => BaseTime),
                   "test-version",
                   utcNow: () => BaseTime))
        {
            hostId = first.HostId;
            bootId = first.SupervisorBootId;
        }

        using var second = AuditJournalFactory.Open(
            options,
            new AuditHealth(options, () => BaseTime.AddMinutes(1)),
            "test-version",
            utcNow: () => BaseTime.AddMinutes(1));
        Assert.Equal(hostId, second.HostId);
        Assert.NotEqual(bootId, second.SupervisorBootId);
        Assert.Equal('4', hostId.ToString("D")[14]);

        var hostPath = Path.Combine(root, "host.id");
        Assert.Equal(hostId.ToString("D") + "\n", File.ReadAllText(hostPath, Encoding.ASCII));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(SecureAuditStorage.OwnerDirectoryMode, File.GetUnixFileMode(root));
            Assert.Equal(SecureAuditStorage.OwnerDirectoryMode, File.GetUnixFileMode(options.SpoolDirectory));
            Assert.Equal(SecureAuditStorage.OwnerFileMode, File.GetUnixFileMode(hostPath));
        }
    }

    [Fact]
    public async Task Concurrent_factories_converge_on_one_host_identity_without_temporary_files()
    {
        var root = NewRoot();
        var options = Options(root, segmentSlots: 2, aggregateSegments: 32);
        using var start = new ManualResetEventSlim(initialState: false);
        using var destinationChecked = new Barrier(participantCount: 8);
        var factories = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                using var journal = AuditJournalFactory.Open(
                    options,
                    new AuditHealth(options, () => BaseTime),
                    "test-version",
                    utcNow: () => BaseTime,
                    hostIdentityDestinationCheckedForTests: () =>
                    {
                        if (!destinationChecked.SignalAndWait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("Concurrent host identity publishers did not rendezvous.");
                    });
                return journal.HostId;
            }))
            .ToArray();

        start.Set();
        var hostIds = await Task.WhenAll(factories).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(hostIds.Distinct());
        Assert.Empty(Directory.EnumerateFiles(root, ".host.id.*.tmp"));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(80)]
    [InlineData(183)]
    public void Factory_recognizes_windows_destination_collision_errors(int nativeErrorCode)
    {
        Assert.True(AuditJournalFactory.IsConcurrentPublishCollision(
            new Win32Exception(nativeErrorCode)));
        Assert.False(AuditJournalFactory.IsConcurrentPublishCollision(
            new Win32Exception(5)));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Factory_rechecks_host_identity_protection_after_read_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        var options = Options(root, segmentSlots: 2, aggregateSegments: 4);
        using (AuditJournalFactory.Open(
                   options,
                   new AuditHealth(options, () => BaseTime),
                   "test-version",
                   utcNow: () => BaseTime))
        {
        }

        var hostPath = Path.Combine(root, "host.id");
        try
        {
            Assert.ThrowsAny<IOException>(() => AuditJournalFactory.Open(
                options,
                new AuditHealth(options, () => BaseTime),
                "test-version",
                utcNow: () => BaseTime,
                hostIdentityReadCompletedForTests: path => File.SetUnixFileMode(
                    path,
                    SecureAuditStorage.OwnerFileMode | UnixFileMode.GroupRead)));
        }
        finally
        {
            File.SetUnixFileMode(hostPath, SecureAuditStorage.OwnerFileMode);
        }
    }

    [Fact]
    public void Factory_sets_current_windows_user_as_owner_with_one_explicit_ace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot(create: true);
        SetAlternateWindowsOwner(root);
        var options = Options(root, segmentSlots: 2, aggregateSegments: 4);
        string segmentPath;
        using (AuditJournalFactory.Open(
                   options,
                   new AuditHealth(options, () => BaseTime),
                   "test-version",
                   utcNow: () => BaseTime))
        {
            segmentPath = Assert.Single(
                Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl"));
        }

        AssertCurrentWindowsUserOwnsProtectedPath(root, isDirectory: true);
        AssertCurrentWindowsUserOwnsProtectedPath(options.SpoolDirectory, isDirectory: true);
        AssertCurrentWindowsUserOwnsProtectedPath(
            Path.Combine(root, "host.id"),
            isDirectory: false);
        AssertCurrentWindowsUserOwnsProtectedPath(segmentPath, isDirectory: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_refuses_corrupt_or_torn_retained_jsonl(bool tearFinalLf)
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 4);
        string path;
        using (var journal = AuditJournalFactory.Open(
                   options,
                   new AuditHealth(options, () => BaseTime),
                   "test-version",
                   utcNow: () => BaseTime))
        {
            Assert.True(journal.TryReserve(1, out var lease, out _));
            journal.Append(lease!, Input("call.accepted"));
            lease!.Release();
            path = Directory.GetFiles(options.SpoolDirectory, "*.jsonl").Single();
        }

        if (tearFinalLf)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(stream.Length - 1);
        }
        else
        {
            var bytes = File.ReadAllBytes(path);
            bytes[0] = (byte)'[';
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, SecureAuditStorage.OwnerFileMode);
        }

        Assert.ThrowsAny<IOException>(() => new FileAuditJournalSink(
            options,
            Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime));
    }

    [Fact]
    public void Missing_live_segment_path_is_detected_before_more_capacity_is_admitted()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 3);
        using var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        File.Delete(sink.CurrentSegmentPath);

        Assert.ThrowsAny<IOException>(() => sink.CanReserve(options.MaxRecordBytes));
    }

    [Fact]
    public void Permission_broadened_retained_segment_is_rejected_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 3);
        string path;
        using (var sink = new FileAuditJournalSink(options, BootId, () => BaseTime))
            path = sink.CurrentSegmentPath;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        Assert.ThrowsAny<IOException>(() => new FileAuditJournalSink(
            options,
            Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime));
    }

    [Fact]
    public void Global_physical_quota_counts_another_supervisors_live_preallocation()
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 1);
        var first = new FileAuditJournalSink(options, BootId, () => BaseTime);
        try
        {
            Assert.ThrowsAny<IOException>(() => new FileAuditJournalSink(
                options,
                Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
                () => BaseTime));
        }
        finally
        {
            first.Dispose();
        }

        using var afterClose = new FileAuditJournalSink(
            options,
            Guid.Parse("42345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime);
    }

    [Fact]
    public async Task Case_aliases_of_one_spool_share_the_filesystem_quota_lock()
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 4);
        using var first = new FileAuditJournalSink(options, BootId, () => BaseTime);
        var aliasRoot = FindCaseAlias(options.RootDirectory);
        if (aliasRoot is null) return;
        var aliasOptions = Options(aliasRoot, segmentSlots: 2, aggregateSegments: 4);
        using var second = new FileAuditJournalSink(
            aliasOptions,
            Guid.Parse("32345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime);

        var held = first.AcquireQuotaLockForTests();
        var contender = Task.Run(second.AcquireQuotaLockForTests);
        try
        {
            await Task.Delay(150);
            Assert.False(contender.IsCompleted, "case alias bypassed the physical quota lock");
            held.Dispose();
            using var acquired = await contender.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            held.Dispose();
            try
            {
                using var cleanup = await contender.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { /* preserve primary failure */ }
        }
    }

    [Fact]
    public void Live_sink_refuses_a_broadened_spool_directory()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 2);
        using var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        try
        {
            File.SetUnixFileMode(
                options.SpoolDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

            Assert.ThrowsAny<IOException>(() => sink.CanReserve(1));
        }
        finally
        {
            File.SetUnixFileMode(options.SpoolDirectory, SecureAuditStorage.OwnerDirectoryMode);
        }
    }

    [Fact]
    public void Factory_refuses_a_symlinked_root_without_writing_through_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        var parent = NewRoot(create: true);
        var real = Path.Combine(parent, "real");
        var linked = Path.Combine(parent, "linked");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(linked, real);
        var options = Options(linked, segmentSlots: 1, aggregateSegments: 2);

        Assert.ThrowsAny<IOException>(() => AuditJournalFactory.Open(
            options,
            new AuditHealth(options, () => BaseTime),
            "test-version",
            utcNow: () => BaseTime));
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public void Startup_removes_a_valid_crash_left_allocation_before_charging_quota()
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 2);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        var allocatingPaths = new[]
        {
            Path.Combine(
                options.SpoolDirectory,
                $".ptk-audit-{BootId:N}-00000000.jsonl." +
                "3234567812344abc8def0123456789ab.allocating"),
            Path.Combine(
                options.SpoolDirectory,
                $".ptk-audit-{BootId:N}-00000001.jsonl." +
                "4234567812344abc8def0123456789ab.allocating"),
        };
        foreach (var allocatingPath in allocatingPaths)
        {
            using (SecureAuditStorage.CreateExclusiveFile(allocatingPath, options.SegmentBytes))
            {
            }
        }

        Assert.All(allocatingPaths, path => Assert.True(File.Exists(path)));
        using var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);

        Assert.All(allocatingPaths, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(sink.CurrentSegmentPath));
        Assert.Equal(0, new FileInfo(sink.CurrentSegmentPath).Length);
    }

    [Theory]
    [InlineData("unexpected.bin")]
    [InlineData(".ptk-audit-malformed.allocating")]
    [InlineData(".ptk-audit-00000000000000000000000000000000-00000000.jsonl.00000000000000000000000000000000.allocating")]
    [InlineData("ptk-audit-00000000000000000000000000000000-00000000.jsonl")]
    public void Startup_rejects_an_unknown_spool_entry(string entryName)
    {
        var options = Options(NewRoot(), segmentSlots: 2, aggregateSegments: 2);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        var unknownPath = Path.Combine(options.SpoolDirectory, entryName);
        using (SecureAuditStorage.CreateExclusiveFile(unknownPath))
        {
        }

        Assert.ThrowsAny<IOException>(() =>
            new FileAuditJournalSink(options, BootId, () => BaseTime));
        Assert.True(File.Exists(unknownPath));
        var files = Directory.EnumerateFiles(options.SpoolDirectory).ToArray();
        Assert.Contains(unknownPath, files);
        Assert.Contains(
            Path.Combine(options.SpoolDirectory, ".ptk-audit-quota.lock"),
            files);
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Age_retention_does_not_delete_behind_a_newer_per_boot_prefix()
    {
        var options = Options(
            NewRoot(),
            segmentSlots: 2,
            aggregateSegments: 8,
            retentionAge: TimeSpan.FromMinutes(1));
        var health = new AuditHealth(options, () => BaseTime);
        var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        using (var journal = Journal(options, health, sink, BootId))
        {
            for (var index = 0; index < 3; index++)
            {
                Assert.True(journal.TryReserve(1, out var lease, out _));
                journal.Append(lease!, Input("call.accepted"));
                lease!.Release();
            }
        }

        var retained = Directory.GetFiles(options.SpoolDirectory, "*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3, retained.Length);
        File.SetLastWriteTimeUtc(retained[0], BaseTime.UtcDateTime);
        File.SetLastWriteTimeUtc(retained[1], BaseTime.AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(retained[2], BaseTime.UtcDateTime);

        using var next = new FileAuditJournalSink(
            options,
            Guid.Parse("42345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime);

        Assert.All(retained, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Physical_quota_evicts_only_the_lowest_retained_index_for_each_boot()
    {
        var root = NewRoot();
        var roomyOptions = Options(root, segmentSlots: 2, aggregateSegments: 8);
        var health = new AuditHealth(roomyOptions, () => BaseTime);
        var sink = new FileAuditJournalSink(roomyOptions, BootId, () => BaseTime);
        using (var journal = Journal(roomyOptions, health, sink, BootId))
        {
            for (var index = 0; index < 4; index++)
            {
                Assert.True(journal.TryReserve(1, out var lease, out _));
                journal.Append(lease!, Input("call.accepted"));
                lease!.Release();
            }
        }

        var retained = Directory.GetFiles(roomyOptions.SpoolDirectory, "*.jsonl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, retained.Length);
        File.SetLastWriteTimeUtc(retained[0], BaseTime.UtcDateTime);
        File.SetLastWriteTimeUtc(retained[1], BaseTime.AddMinutes(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(retained[2], BaseTime.UtcDateTime);
        File.SetLastWriteTimeUtc(retained[3], BaseTime.UtcDateTime);

        var constrainedOptions = Options(root, segmentSlots: 2, aggregateSegments: 2);
        using var next = new FileAuditJournalSink(
            constrainedOptions,
            Guid.Parse("52345678-1234-4abc-8def-0123456789ab"),
            () => BaseTime);

        Assert.False(File.Exists(retained[0]));
        Assert.All(retained[1..], path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Macos_close_releases_blocks_reserved_beyond_logical_eof()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var options = Options(NewRoot(), segmentSlots: 255, aggregateSegments: 2);
        var sink = new FileAuditJournalSink(options, BootId, () => BaseTime);
        var path = sink.CurrentSegmentPath;
        var liveBlocks = GetMacAllocatedBlocks(path);

        sink.Dispose();

        var closedBlocks = GetMacAllocatedBlocks(path);
        Assert.True(liveBlocks > 0);
        Assert.Equal(0, closedBlocks);
    }

    [Fact]
    public void Physical_allocation_failure_is_fail_closed_and_leaves_no_segment()
    {
        var options = Options(NewRoot(), segmentSlots: 1, aggregateSegments: 2);
        Assert.Throws<IOException>(() => new FileAuditJournalSink(
            options,
            BootId,
            () => BaseTime,
            (point, attempt) => point == FileAuditSinkFaultPoint.PhysicalAllocation && attempt == 1));
        Assert.Empty(Directory.EnumerateFiles(options.SpoolDirectory, "*.jsonl"));
    }

    [Fact]
    public void Rotation_allocation_failure_preserves_an_existing_terminal_reservation()
    {
        var options = Options(NewRoot(), segmentSlots: 3, aggregateSegments: 4);
        var health = new AuditHealth(options, () => BaseTime);
        var sink = new FileAuditJournalSink(
            options,
            BootId,
            () => BaseTime,
            (point, attempt) =>
                point == FileAuditSinkFaultPoint.PhysicalAllocation && attempt == 2);
        using var journal = Journal(options, health, sink, BootId);
        Assert.True(journal.TryReserve(2, out var existing, out _));
        journal.Append(existing!, Input("call.accepted"));
        var originalSegment = sink.CurrentSegmentPath;

        Assert.False(journal.TryReserve(1, out var refused, out var failure));
        Assert.Null(refused);
        Assert.Equal("journal.storage", failure);
        Assert.False(journal.IsPoisoned);
        Assert.Equal(originalSegment, sink.CurrentSegmentPath);

        journal.Append(existing!, Input("call.completed"));
        existing!.Release();
        Assert.False(journal.IsPoisoned);
        journal.Dispose();
        var eventTypes = File.ReadLines(originalSegment)
            .Select(line => JsonDocument.Parse(line))
            .Select(document =>
            {
                using (document)
                    return document.RootElement.GetProperty("event_type").GetString()!;
            })
            .ToArray();
        Assert.Equal(
            ["call.accepted", "audit.degraded", "call.completed"],
            eventTypes);
    }

    private static long GetMacAllocatedBlocks(string path)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/stat")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("%b");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Could not start macOS stat.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"macOS stat failed: {error}");
        return long.Parse(output.Trim());
    }

    private static AuditJournal Journal(
        AuditOptions options,
        AuditHealth health,
        IAuditJournalSink sink,
        Guid bootId) => new(
        options,
        health,
        sink,
        "test-version",
        binaryDigest: null,
        HostId,
        bootId,
            () => BaseTime);

    [SupportedOSPlatform("windows")]
    private static void SetAlternateWindowsOwner(string path)
    {
        var alternateOwner = new SecurityIdentifier(
            WellKnownSidType.BuiltinUsersSid,
            domainSid: null);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        security.SetOwner(alternateOwner);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);

        var changed = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        Assert.Equal(
            alternateOwner,
            Assert.IsType<SecurityIdentifier>(
                changed.GetOwner(typeof(SecurityIdentifier))));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentWindowsUserOwnsProtectedPath(
        string path,
        bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = Assert.IsType<SecurityIdentifier>(identity.User);
        var owner = Assert.IsType<SecurityIdentifier>(
            security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(currentUser, owner);
        Assert.True(security.AreAccessRulesProtected);

        var rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var rule = Assert.Single(rules);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
        Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }

    private AuditOptions Options(
        string root,
        int segmentSlots,
        int aggregateSegments,
        TimeSpan? retentionAge = null)
    {
        const int maxRecordBytes = 4096;
        var physicalSegmentBytes = maxRecordBytes * (long)(segmentSlots + 1);
        return AuditOptions.Create(
            root,
            maxRecordBytes: maxRecordBytes,
            segmentBytes: physicalSegmentBytes,
            aggregateBytes: physicalSegmentBytes * aggregateSegments,
            emergencyReserveBytes: maxRecordBytes * 2L,
            retentionAge: retentionAge ?? TimeSpan.FromMinutes(10),
            maxEvidenceBytes: maxRecordBytes,
            evidenceAggregateBytes: maxRecordBytes,
            evidenceRetentionAge: TimeSpan.FromMinutes(10));
    }

    private string NewRoot(bool create = false)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ptk-file-audit-tests-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        if (create)
            Directory.CreateDirectory(root);
        return root;
    }

    private static string? FindCaseAlias(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (!char.IsLetter(path[index])) continue;
            var characters = path.ToCharArray();
            characters[index] = char.IsUpper(characters[index])
                ? char.ToLowerInvariant(characters[index])
                : char.ToUpperInvariant(characters[index]);
            var candidate = new string(characters);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static AuditEventInput Input(string eventType) => new()
    {
        EventType = eventType,
        Session = new AuditSession(),
        Actor = new AuditActor { AttributionStrength = "transport_only", Transport = "mcp_stdio" },
        Correlation = new AuditCorrelation(),
        Request = new AuditRequest(),
        Routing = new AuditRouting(),
        Outcome = new AuditOutcome { TerminationCertainty = "not_applicable" },
        Coverage = new AuditCoverage
        {
            PtkRequest = true,
            RootProcessObserved = "not_applicable",
            DescendantsObserved = "not_applicable",
            RemoteEffectObserved = "not_applicable"
        },
        Audit = new AuditEventHealth { ProtectionMode = "local-only", HealthState = "healthy" }
    };

    private static JsonDocument ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 1));
    }
}
