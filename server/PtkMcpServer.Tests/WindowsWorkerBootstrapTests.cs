using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

public sealed class WindowsWorkerBootstrapTests
{
    [Fact]
    public void Capture_reads_each_value_once_then_removes_each_value_once()
    {
        var source = new RecordingEnvironmentSource("11", "22");

        var values = WorkerBootstrapCapture.CaptureAndRemove(source);

        Assert.Equal(new WorkerBootstrapValues("11", "22"), values);
        Assert.Equal(
            [
                $"get:{WorkerBootstrapEnvironment.RequestHandle}",
                $"get:{WorkerBootstrapEnvironment.EventHandle}",
                $"remove:{WorkerBootstrapEnvironment.RequestHandle}",
                $"remove:{WorkerBootstrapEnvironment.EventHandle}",
            ],
            source.Calls);
        Assert.Equal(1, source.GetCount(WorkerBootstrapEnvironment.RequestHandle));
        Assert.Equal(1, source.GetCount(WorkerBootstrapEnvironment.EventHandle));
        Assert.Equal(1, source.RemoveCount(WorkerBootstrapEnvironment.RequestHandle));
        Assert.Equal(1, source.RemoveCount(WorkerBootstrapEnvironment.EventHandle));
        Assert.Empty(source.Values);
    }

    [Theory]
    [InlineData("get_request", "bootstrap_failure")]
    [InlineData("get_event", "bootstrap_failure")]
    [InlineData("remove_request", "environment_removal_failed")]
    [InlineData("remove_event", "environment_removal_failed")]
    public void Capture_attempts_both_gets_and_both_removals_when_a_boundary_fails(
        string failingBoundary,
        string expectedDetailCode)
    {
        var source = new RecordingEnvironmentSource("11", "22")
        {
            FailingBoundary = failingBoundary,
        };

        var failure = Assert.Throws<WorkerBootstrapException>(
            () => WorkerBootstrapCapture.CaptureAndRemove(source));

        Assert.Equal(expectedDetailCode, failure.DetailCode);
        Assert.Equal(
            [
                $"get:{WorkerBootstrapEnvironment.RequestHandle}",
                $"get:{WorkerBootstrapEnvironment.EventHandle}",
                $"remove:{WorkerBootstrapEnvironment.RequestHandle}",
                $"remove:{WorkerBootstrapEnvironment.EventHandle}",
            ],
            source.Calls);
        Assert.Equal(1, source.GetCount(WorkerBootstrapEnvironment.RequestHandle));
        Assert.Equal(1, source.GetCount(WorkerBootstrapEnvironment.EventHandle));
        Assert.Equal(1, source.RemoveCount(WorkerBootstrapEnvironment.RequestHandle));
        Assert.Equal(1, source.RemoveCount(WorkerBootstrapEnvironment.EventHandle));
    }

    [Theory]
    [InlineData("1", 4, 1UL)]
    [InlineData("4294967294", 4, 4294967294UL)]
    [InlineData("1", 8, 1UL)]
    [InlineData("18446744073709551614", 8, 18446744073709551614UL)]
    public void Canonical_pointer_width_values_are_accepted(
        string value,
        int pointerSize,
        ulong expected)
    {
        var parsed = WindowsWorkerBootstrap.ParseHandle(value, pointerSize);

        Assert.Equal(expected, (ulong)parsed);
    }

    [Theory]
    [InlineData(null, 8, "handle_missing")]
    [InlineData("", 8, "handle_invalid")]
    [InlineData("0", 8, "handle_invalid")]
    [InlineData("00", 8, "handle_invalid")]
    [InlineData("01", 8, "handle_invalid")]
    [InlineData("+1", 8, "handle_invalid")]
    [InlineData("-1", 8, "handle_invalid")]
    [InlineData(" 1", 8, "handle_invalid")]
    [InlineData("1 ", 8, "handle_invalid")]
    [InlineData("1,0", 8, "handle_invalid")]
    [InlineData("1_0", 8, "handle_invalid")]
    [InlineData("0x1", 8, "handle_invalid")]
    [InlineData("１", 8, "handle_invalid")]
    [InlineData("1\n", 8, "handle_invalid")]
    [InlineData("4294967295", 4, "handle_invalid")]
    [InlineData("4294967296", 4, "handle_invalid")]
    [InlineData("18446744073709551615", 8, "handle_invalid")]
    [InlineData("18446744073709551616", 8, "handle_invalid")]
    public void Noncanonical_out_of_range_and_invalid_pointer_values_are_rejected(
        string? value,
        int pointerSize,
        string expectedDetailCode)
    {
        var failure = Assert.Throws<WorkerBootstrapException>(
            () => WindowsWorkerBootstrap.ParseHandle(value, pointerSize));

        Assert.Equal(expectedDetailCode, failure.DetailCode);
    }

    [Fact]
    public void Aliased_handles_are_rejected_before_native_acquisition()
    {
        var native = new RecordingNative();

        var failure = Assert.Throws<WorkerBootstrapException>(() =>
            WindowsWorkerBootstrap.Open(
                new WorkerBootstrapValues("11", "11"),
                native,
                isWindows: () => true,
                pointerSize: 8));

        Assert.Equal("handle_alias", failure.DetailCode);
        Assert.Empty(native.Trace);
        Assert.Empty(native.Resources);
    }

    [Fact]
    public void Open_uses_the_active_noninheriting_duplicate_and_ordered_stream_path()
    {
        var native = new RecordingNative();
        var owner = WindowsWorkerBootstrap.Open(
            new WorkerBootstrapValues("11", "22"),
            native,
            isWindows: () => true,
            pointerSize: 8);

        Assert.Same(native.Streams.Single(stream => stream.Role == "request"), owner.RequestStream);
        Assert.Same(native.Streams.Single(stream => stream.Role == "event"), owner.EventStream);
        Assert.Equal(
            [
                "own:request:11",
                "own:event:22",
                "duplicate:request:desired=0:inherit=False:options=2",
                "duplicate:event:desired=0:inherit=False:options=2",
                "close:request_original:handle",
                "close:event_original:handle",
                "pipe:request",
                "pipe:event",
                "inherit:request",
                "inherit:event",
                "stream:request:Read",
                "stream:event:Write",
            ],
            native.Trace);

        owner.Dispose();
        owner.Dispose();

        Assert.Equal(
            [
                "dispose_stream:request",
                "close:request_duplicate:stream",
                "dispose_stream:event",
                "close:event_duplicate:stream",
            ],
            native.Trace[^4..]);
        Assert.All(native.Resources, resource => Assert.Equal(1, resource.CloseCount));
        Assert.All(native.Streams, stream => Assert.Equal(1, stream.DisposeCount));
        Assert.Throws<ObjectDisposedException>(() => _ = owner.RequestStream);
        Assert.Throws<ObjectDisposedException>(() => _ = owner.EventStream);
    }

    [Theory]
    [MemberData(nameof(AcquisitionBoundaryFailures))]
    public void Every_acquisition_boundary_failure_closes_each_acquired_resource_once(
        string failingBoundary,
        string expectedDetailCode)
    {
        var native = new RecordingNative { FailingBoundary = failingBoundary };

        var failure = Assert.Throws<WorkerBootstrapException>(() =>
            WindowsWorkerBootstrap.Open(
                new WorkerBootstrapValues("11", "22"),
                native,
                isWindows: () => true,
                pointerSize: 8));

        Assert.Equal(expectedDetailCode, failure.DetailCode);
        Assert.All(native.Resources, resource => Assert.Equal(1, resource.CloseCount));
        Assert.All(native.Streams, stream => Assert.Equal(1, stream.DisposeCount));
    }

    public static TheoryData<string, string> AcquisitionBoundaryFailures =>
        new()
        {
            { "own_request", "bootstrap_failure" },
            { "own_event", "bootstrap_failure" },
            { "duplicate_request", "handle_duplication_failed" },
            { "duplicate_event", "handle_duplication_failed" },
            { "pipe_request", "handle_invalid" },
            { "pipe_event", "handle_invalid" },
            { "inherit_request", "handle_invalid" },
            { "inherit_event", "handle_invalid" },
            { "stream_request", "stream_creation_failed" },
            { "stream_event", "stream_creation_failed" },
        };

    [Theory]
    [InlineData("request", false, "handle_not_pipe")]
    [InlineData("event", false, "handle_not_pipe")]
    [InlineData("request", true, "handle_inheritable")]
    [InlineData("event", true, "handle_inheritable")]
    public void Negative_pipe_and_inheritance_validation_closes_every_handle_once(
        string role,
        bool inheritableFailure,
        string expectedDetailCode)
    {
        var native = new RecordingNative
        {
            NonPipeRole = inheritableFailure ? null : role,
            InheritableRole = inheritableFailure ? role : null,
        };

        var failure = Assert.Throws<WorkerBootstrapException>(() =>
            WindowsWorkerBootstrap.Open(
                new WorkerBootstrapValues("11", "22"),
                native,
                isWindows: () => true,
                pointerSize: 8));

        Assert.Equal(expectedDetailCode, failure.DetailCode);
        Assert.Empty(native.Streams);
        Assert.All(native.Resources, resource => Assert.Equal(1, resource.CloseCount));
    }

    private sealed class RecordingEnvironmentSource(
        string requestValue,
        string eventValue) : IWorkerBootstrapEnvironmentSource
    {
        internal Dictionary<string, string> Values { get; } =
            new(StringComparer.Ordinal)
            {
                [WorkerBootstrapEnvironment.RequestHandle] = requestValue,
                [WorkerBootstrapEnvironment.EventHandle] = eventValue,
            };

        internal List<string> Calls { get; } = [];
        internal string? FailingBoundary { get; init; }

        public string? Get(string variable)
        {
            Calls.Add($"get:{variable}");
            if (FailingBoundary == Boundary("get", variable))
                throw new IOException("injected environment get failure");
            return Values.TryGetValue(variable, out var value) ? value : null;
        }

        public void Remove(string variable)
        {
            Calls.Add($"remove:{variable}");
            if (FailingBoundary == Boundary("remove", variable))
                throw new IOException("injected environment removal failure");
            Values.Remove(variable);
        }

        internal int GetCount(string variable) =>
            Calls.Count(call => call == $"get:{variable}");

        internal int RemoveCount(string variable) =>
            Calls.Count(call => call == $"remove:{variable}");

        private static string Boundary(string operation, string variable) =>
            variable == WorkerBootstrapEnvironment.RequestHandle
                ? $"{operation}_request"
                : $"{operation}_event";
    }

    private sealed class RecordingNative : IWindowsWorkerBootstrapNative
    {
        private int _ownCount;

        internal List<string> Trace { get; } = [];
        internal List<FakeResource> Resources { get; } = [];
        internal List<FakeStream> Streams { get; } = [];
        internal string? FailingBoundary { get; init; }
        internal string? NonPipeRole { get; init; }
        internal string? InheritableRole { get; init; }

        public IWorkerBootstrapHandle OwnInherited(nuint handleValue)
        {
            var role = ++_ownCount == 1 ? "request" : "event";
            Trace.Add($"own:{role}:{handleValue}");
            ThrowIf($"own_{role}");
            return Handle(role, "original");
        }

        public IWorkerBootstrapHandle Duplicate(
            IWorkerBootstrapHandle source,
            uint desiredAccess,
            bool inheritHandle,
            uint options)
        {
            var handle = Require(source);
            Trace.Add(
                $"duplicate:{handle.Role}:desired={desiredAccess}:" +
                $"inherit={inheritHandle}:options={options}");
            ThrowIf($"duplicate_{handle.Role}");
            return Handle(handle.Role, "duplicate");
        }

        public bool IsPipe(IWorkerBootstrapHandle handle)
        {
            var role = Require(handle).Role;
            Trace.Add($"pipe:{role}");
            ThrowIf($"pipe_{role}");
            return role != NonPipeRole;
        }

        public bool IsInheritable(IWorkerBootstrapHandle handle)
        {
            var role = Require(handle).Role;
            Trace.Add($"inherit:{role}");
            ThrowIf($"inherit_{role}");
            return role == InheritableRole;
        }

        public Stream CreateStream(IWorkerBootstrapHandle handle, FileAccess access)
        {
            var owned = Require(handle);
            Trace.Add($"stream:{owned.Role}:{access}");
            ThrowIf($"stream_{owned.Role}");
            var stream = new FakeStream(owned.Role, access, owned.Transfer(), Trace);
            Streams.Add(stream);
            return stream;
        }

        private FakeHandle Handle(string role, string kind)
        {
            var resource = new FakeResource($"{role}_{kind}", Trace);
            Resources.Add(resource);
            return new FakeHandle(role, resource);
        }

        private static FakeHandle Require(IWorkerBootstrapHandle handle) =>
            Assert.IsType<FakeHandle>(handle);

        private void ThrowIf(string boundary)
        {
            if (FailingBoundary == boundary)
                throw new IOException($"injected {boundary} failure");
        }
    }

    private sealed class FakeHandle(
        string role,
        FakeResource resource) : IWorkerBootstrapHandle
    {
        private int _ownershipReleased;

        internal string Role { get; } = role;

        internal FakeResource Transfer()
        {
            if (Interlocked.Exchange(ref _ownershipReleased, 1) != 0)
                throw new InvalidOperationException("Fake handle ownership was already released.");
            return resource;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownershipReleased, 1) == 0)
                resource.Close("handle");
        }
    }

    private sealed class FakeResource(
        string name,
        List<string> trace)
    {
        internal int CloseCount { get; private set; }

        internal void Close(string owner)
        {
            CloseCount++;
            trace.Add($"close:{name}:{owner}");
        }
    }

    private sealed class FakeStream(
        string role,
        FileAccess access,
        FakeResource resource,
        List<string> trace) : Stream
    {
        private int _disposed;

        internal string Role { get; } = role;
        internal int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                DisposeCount++;
                trace.Add($"dispose_stream:{Role}");
                resource.Close("stream");
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => access is FileAccess.Read or FileAccess.ReadWrite;
        public override bool CanSeek => false;
        public override bool CanWrite => access is FileAccess.Write or FileAccess.ReadWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
