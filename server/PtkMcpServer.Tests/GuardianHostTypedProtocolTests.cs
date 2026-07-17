using System.Reflection;
using System.Text;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class GuardianHostTypedProtocolTests
{
    private static readonly GuardianBootId Guardian = new(
        Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly HostBootId Host = new(
        Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly WorkerBootId WorkerBoot = new(
        Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
    private static readonly ManifestId Manifest = new(
        Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"));
    private static readonly PlanId Plan = new(
        Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
    private static readonly OperationId Operation = new(
        Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));
    private static readonly CallId Call = new(
        Guid.Parse("77777777-7777-7777-8777-777777777777"));
    private static readonly HostGeneration HostGeneration = new(1);
    private static readonly WorkerGeneration WorkerGeneration = new(2);
    private static readonly CanonicalAlias Alias = new("default");
    private static readonly SessionTransitionVersion Transition = new(1);
    private static readonly Sha256Digest HostExecutableDigest = new(new string('1', 64));
    private static readonly Sha256Digest HostBuildDigest = new(new string('2', 64));
    private static readonly Sha256Digest PublicContractDigest = new(new string('3', 64));
    private static readonly Sha256Digest ConfigurationDigest = new(new string('4', 64));
    private static readonly Sha256Digest PackageManifestDigest = new(new string('5', 64));
    private static readonly Sha256Digest ManifestDigest = new(new string('6', 64));
    private static readonly Sha256Digest Digest = new(new string('a', 64));
    private static readonly CapabilityToken Token = Capability(0);
    private static readonly CapabilityToken DispatchToken = Capability(1);
    private static readonly CapabilityToken OutputToken = Capability(2);
    private static readonly CapabilityToken JobToken = Capability(3);
    private static readonly GuardianHostWorkerIdentity Worker = new(WorkerBoot, WorkerGeneration);
    private static readonly GuardianHostOperationIdentity OperationIdentity = new(Plan, Operation);

    [Fact]
    public void Typed_codec_round_trips_the_complete_frozen_union_canonically()
    {
        var messages = AllMessages().ToArray();

        Assert.Equal(8, messages.Select(value => value.Kind).Distinct().Count());

        var requests = messages.OfType<GuardianHostRequest>().ToArray();
        Assert.Equal(8, requests.Select(value => value.Method).Distinct().Count());
        Assert.Equal(10, requests.OfType<OperationRequest>()
            .Select(value => value.Operation.Kind).Distinct().Count());

        var events = messages.OfType<GuardianHostEvent>().ToArray();
        Assert.Equal(12, events.Select(value => value.EventType).Distinct().Count());

        var successPayloads = messages.OfType<GuardianHostSuccessResponse>()
            .Select(value => value.Payload).ToArray();
        Assert.Equal(6, successPayloads.Select(value => value.ResponseType).Distinct().Count());
        Assert.Equal(10, successPayloads.OfType<OperationCompleted>()
            .Select(value => value.OperationKind).Distinct().Count());

        foreach (var message in messages)
        {
            var encoded = GuardianHostProtocolCodec.Encode(message);
            var decoded = GuardianHostProtocolCodec.Decode(encoded, message.Sender);

            Assert.Equal(message.GetType(), decoded.GetType());
            Assert.Equal(message.Kind, decoded.Kind);
            Assert.Equal(message.Sender, decoded.Sender);
            Assert.Equal(encoded, GuardianHostProtocolCodec.Encode(decoded));
        }
    }

    [Fact]
    public void Typed_codec_covers_all_37_exact_private_detail_to_12_message_mappings()
    {
        var mappings = new Dictionary<GuardianHostPrivateDetailCode, GuardianHostPrivateMessageCode>();
        foreach (var detail in Enum.GetValues<GuardianHostPrivateDetailCode>())
        {
            var message = new GuardianHostErrorResponse(
                Guardian, Host, HostGeneration, new PrivateRequestId((long)detail + 1),
                new GuardianHostPrivateError(detail));
            var encoded = GuardianHostProtocolCodec.Encode(message);
            var decoded = Assert.IsType<GuardianHostErrorResponse>(
                GuardianHostProtocolCodec.Decode(encoded, GuardianHostPeer.Host));
            Assert.Equal(detail, decoded.Error.DetailCode);
            Assert.Equal(GuardianHostPrivateError.MessageFor(detail), decoded.Error.MessageCode);
            Assert.Equal(encoded, GuardianHostProtocolCodec.Encode(decoded));
            mappings.Add(detail, decoded.Error.MessageCode);
        }

        Assert.Equal(37, mappings.Count);
        Assert.Equal(12, mappings.Values.Distinct().Count());
    }

    [Fact]
    public async Task Typed_reader_and_writer_preserve_incremental_frames_and_latch_ambiguous_failure()
    {
        var hello = Assert.IsType<GuardianHostHello>(AllMessages().First());
        await using var stream = new MemoryStream();
        var writer = new GuardianHostProtocolWriter(stream, GuardianHostPeer.Host);
        await writer.WriteAsync(hello);
        await writer.WriteAsync(hello);

        stream.Position = 0;
        var reader = new GuardianHostProtocolReader(stream, GuardianHostPeer.Host);
        Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
        Assert.IsType<GuardianHostHello>(await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync());

        await using var wrongDirection = new MemoryStream();
        var guardianWriter = new GuardianHostProtocolWriter(wrongDirection, GuardianHostPeer.Guardian);
        await Assert.ThrowsAsync<ArgumentException>(async () => await guardianWriter.WriteAsync(hello));
        Assert.Empty(wrongDirection.ToArray());

        var failing = new FailingWriteStream();
        var failingWriter = new GuardianHostProtocolWriter(failing, GuardianHostPeer.Host);
        await Assert.ThrowsAsync<IOException>(async () => await failingWriter.WriteAsync(hello));
        var latched = await Assert.ThrowsAsync<GuardianHostProtocolException>(async () =>
            await failingWriter.WriteAsync(hello));
        Assert.Equal("writer_faulted", latched.DetailCode);
        Assert.Equal(1, failing.WriteCount);
    }

    [Fact]
    public void Manifest_chunk_dispose_zeroes_its_owned_bytes_and_refuses_reuse()
    {
        byte[] source = [0x41, 0x42, 0x43, 0x44];
        var chunk = new ManifestChunkRequest(
            Guardian,
            Host,
            HostGeneration,
            new PrivateRequestId(9),
            Manifest,
            chunkIndex: 0,
            source);
        var field = typeof(ManifestChunkRequest).GetField(
            "_rawBytes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var owned = Assert.IsType<byte[]>(field!.GetValue(chunk));

        Assert.NotSame(source, owned);
        Assert.Equal(source, owned);
        Assert.Equal(source.Length, chunk.RawByteCount);

        chunk.Dispose();

        Assert.All(owned, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(chunk.GetRawBytes);
        Assert.Throws<ObjectDisposedException>(() => GuardianHostProtocolCodec.Encode(chunk));
        chunk.Dispose();
        Assert.Equal(source.Length, chunk.RawByteCount);
    }

    [Fact]
    public void Raw_events_dispose_zero_their_owned_bytes_and_refuse_reuse()
    {
        byte[] diagnosticSource = [0x41, 0x42, 0x43, 0x44];
        byte[] outputSource = [0x51, 0x52, 0x53, 0x54];
        GuardianHostEvent[] rawEvents =
        [
            new WorkerDiagnosticChunkEvent(
                Guardian,
                Host,
                HostGeneration,
                EventSequence(90),
                RequestId(10),
                Alias,
                Transition,
                Worker,
                OperationIdentity,
                GuardianHostDiagnosticStream.Stdout,
                chunkIndex: 0,
                offset: 0,
                diagnosticSource,
                endOfStream: true),
            new OutputChunkEvent(
                Guardian,
                Host,
                HostGeneration,
                EventSequence(91),
                RequestId(11),
                Alias,
                Transition,
                Worker,
                OperationIdentity,
                OutputToken,
                chunkIndex: 0,
                offset: 0,
                outputSource),
        ];

        foreach (var rawEvent in rawEvents)
        {
            var field = rawEvent.GetType().GetField(
                "_rawBytes",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var owned = Assert.IsType<byte[]>(field!.GetValue(rawEvent));
            var expected = rawEvent is WorkerDiagnosticChunkEvent
                ? diagnosticSource
                : outputSource;
            var rawByteCount = rawEvent switch
            {
                WorkerDiagnosticChunkEvent value => value.RawByteCount,
                OutputChunkEvent value => value.RawByteCount,
                _ => throw new InvalidOperationException(),
            };

            Assert.NotSame(expected, owned);
            Assert.Equal(expected, owned);
            Assert.Equal(expected.Length, rawByteCount);

            ((IDisposable)rawEvent).Dispose();

            Assert.All(owned, value => Assert.Equal(0, value));
            Assert.Throws<ObjectDisposedException>(() => rawEvent switch
            {
                WorkerDiagnosticChunkEvent value => value.GetRawBytes(),
                OutputChunkEvent value => value.GetRawBytes(),
                _ => throw new InvalidOperationException(),
            });
            Assert.Throws<ObjectDisposedException>(() =>
                GuardianHostProtocolCodec.Encode(rawEvent));
            ((IDisposable)rawEvent).Dispose();
            Assert.Equal(expected.Length, rawByteCount);
        }
    }

    [Fact]
    public void Public_codec_surface_exposes_typed_messages_not_raw_dictionary_envelopes()
    {
        var publicTypes = typeof(GuardianHostProtocolCodec).Assembly.GetExportedTypes();
        Assert.DoesNotContain(publicTypes, type => type.Name.Contains("Raw", StringComparison.Ordinal));

        var codecMethods = typeof(GuardianHostProtocolCodec).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.All(codecMethods, method =>
        {
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                typeof(System.Collections.IDictionary).IsAssignableFrom(parameter.ParameterType));
            Assert.NotEqual(typeof(System.Text.Json.JsonElement), method.ReturnType);
        });

        var dtoTypes = publicTypes.Where(type =>
            typeof(GuardianHostMessage).IsAssignableFrom(type) ||
            typeof(GuardianHostOperation).IsAssignableFrom(type) ||
            typeof(GuardianHostSuccessPayload).IsAssignableFrom(type) ||
            typeof(GuardianHostOperationResult).IsAssignableFrom(type) ||
            type == typeof(GuardianHostWorkerIdentity) ||
            type == typeof(GuardianHostOperationIdentity) ||
            type == typeof(GuardianHostContainmentIdentity) ||
            type == typeof(DispatchCapability) ||
            type == typeof(OutputCapability));
        foreach (var type in dtoTypes)
        {
            Assert.All(type.GetConstructors(), constructor =>
                Assert.DoesNotContain(constructor.GetParameters(), parameter =>
                    IsRawOrGenericEscape(parameter.ParameterType)));
            Assert.All(type.GetProperties(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance),
                property => Assert.False(IsRawOrGenericEscape(property.PropertyType),
                    $"{type.Name}.{property.Name} exposes {property.PropertyType}."));
        }

        Assert.Single(typeof(WorkerCreateCapabilityGrantRequest).GetConstructors());

        var grant = Requests().OfType<WorkerCreateCapabilityGrantRequest>().Single();
        var encodedGrant = Encoding.UTF8.GetString(GuardianHostProtocolCodec.Encode(grant));
        const string generation = "\"worker_generation\":3";
        var payloadGeneration = encodedGrant.LastIndexOf(generation, StringComparison.Ordinal);
        Assert.True(payloadGeneration >= 0);
        var mismatchedGrant = encodedGrant.Remove(payloadGeneration, generation.Length)
            .Insert(payloadGeneration, "\"worker_generation\":4");
        var mismatch = Assert.Throws<GuardianHostProtocolException>(() =>
            GuardianHostProtocolCodec.Decode(
                Encoding.UTF8.GetBytes(mismatchedGrant),
                GuardianHostPeer.Guardian));
        Assert.Equal("invalid_field", mismatch.DetailCode);
    }

    [Fact]
    public void Typed_codec_round_trips_nullable_and_conditional_subbranches()
    {
        var dispatch = new DispatchCapability(DispatchToken, Call, 100);
        var output = new OutputCapability(OutputToken, 1024, 100);
        GuardianHostMessage[] messages =
        [
            new OperationRequest(
                Guardian, Host, HostGeneration, RequestId(80), 100, Alias, Transition,
                Worker, OperationIdentity,
                new InvokeForegroundOperation(
                    Call, dispatch, output, "Get-Date", false, GuardianHostInvokeRoute.Rtk)),
            new OperationRequest(
                Guardian, Host, HostGeneration, RequestId(81), 100, Alias, Transition,
                null, null, new SessionCloseOperation(Call, dispatch, 2, false)),
            new OperationDeliveryEvent(
                Guardian, Host, HostGeneration, EventSequence(80), RequestId(82), Alias,
                Transition, Worker, null, DispatchToken,
                GuardianHostDeliveryState.NotDispatched, null),
            new OperationDeliveryEvent(
                Guardian, Host, HostGeneration, EventSequence(81), RequestId(83), Alias,
                Transition, Worker, OperationIdentity, DispatchToken,
                GuardianHostDeliveryState.TerminalDecoded, RequestId(7)),
            new SessionLifecycleEvent(
                Guardian, Host, HostGeneration, EventSequence(82), RequestId(84), Alias,
                Transition, Worker, null, PublicSessionState.Ready,
                GuardianHostSessionLifecycleReason.AutomaticRecovery,
                true, true, BootstrapState.Restored),
            Success(85, new OperationCompleted(new SessionCloseResult(
                Alias, PublicSessionState.Cold, null, Transition,
                false, true, BootstrapState.NotApplicable))),
        ];

        foreach (var message in messages)
        {
            var encoded = GuardianHostProtocolCodec.Encode(message);
            var decoded = GuardianHostProtocolCodec.Decode(encoded, message.Sender);
            Assert.Equal(message.GetType(), decoded.GetType());
            Assert.Equal(encoded, GuardianHostProtocolCodec.Encode(decoded));
        }
    }

    [Fact]
    public void Typed_decoder_accepts_every_schema_valid_integral_json_spelling_and_canonicalizes_it()
    {
        var hello = Assert.IsType<GuardianHostHello>(AllMessages().First());
        var canonical = Encoding.UTF8.GetString(GuardianHostProtocolCodec.Encode(hello));
        var alternate = canonical
            .Replace("\"host_generation\":1", "\"host_generation\":1e0", StringComparison.Ordinal)
            .Replace("\"host_pid\":42", "\"host_pid\":42.0", StringComparison.Ordinal);

        var decoded = Assert.IsType<GuardianHostHello>(GuardianHostProtocolCodec.Decode(
            Encoding.UTF8.GetBytes(alternate), GuardianHostPeer.Host));
        Assert.Equal(1, decoded.HostGeneration.Value);
        Assert.Equal(42, decoded.HostPid);
        Assert.Equal(Encoding.UTF8.GetBytes(canonical), GuardianHostProtocolCodec.Encode(decoded));

        var containment = Events().OfType<WorkerContainmentPendingEvent>().Single();
        var canonicalContainment = Encoding.UTF8.GetString(
            GuardianHostProtocolCodec.Encode(containment));
        var alternateContainment = canonicalContainment
            .Replace("\"broker_pid\":11", "\"broker_pid\":1.1e1", StringComparison.Ordinal)
            .Replace(
                "\"broker_start_identity_low\":102",
                "\"broker_start_identity_low\":102.0",
                StringComparison.Ordinal);
        var decodedContainment = Assert.IsType<WorkerContainmentPendingEvent>(
            GuardianHostProtocolCodec.Decode(
                Encoding.UTF8.GetBytes(alternateContainment), GuardianHostPeer.Host));
        Assert.Equal((uint)11, decodedContainment.ContainmentIdentity.BrokerPid);
        Assert.Equal((ulong)102, decodedContainment.ContainmentIdentity.BrokerStartIdentityLow);
    }

    private static IEnumerable<GuardianHostMessage> AllMessages()
    {
        yield return new GuardianHostHello(
            Guardian, Host, HostGeneration, 42, HostExecutableDigest, HostBuildDigest,
            PublicContractDigest, ConfigurationDigest);
        yield return new GuardianHostInitialize(
            Guardian, Host, HostGeneration, RequestId(1), HostExecutableDigest,
            HostBuildDigest, PublicContractDigest, ConfigurationDigest, PackageManifestDigest);
        yield return new GuardianHostReady(
            Guardian, Host, HostGeneration, RequestId(1), Manifest, ManifestDigest, 42);

        foreach (var request in Requests()) yield return request;

        yield return new GuardianHostCancel(
            Guardian, Host, HostGeneration, RequestId(30), RequestId(29),
            GuardianHostCancelReason.CallerCanceled);

        foreach (var item in Events()) yield return item;
        foreach (var item in SuccessResponses()) yield return item;

        yield return new GuardianHostErrorResponse(
            Guardian, Host, HostGeneration, RequestId(70),
            new GuardianHostPrivateError(GuardianHostPrivateDetailCode.OutcomeUnknown));
        yield return new GuardianHostShutdown(
            Guardian, Host, HostGeneration, RequestId(71), 100,
            GuardianHostShutdownReason.GuardianShutdown);
    }

    private static IEnumerable<GuardianHostRequest> Requests()
    {
        var raw = "manifest"u8.ToArray();
        yield return new ManifestHeaderRequest(
            Guardian, Host, HostGeneration, RequestId(2), Manifest,
            raw.Length, Sha256Digest.Compute(raw), 1, 0);
        yield return new ManifestChunkRequest(
            Guardian, Host, HostGeneration, RequestId(3), Manifest, 0, raw);
        yield return new ManifestSealRequest(
            Guardian, Host, HostGeneration, RequestId(4), Manifest,
            raw.Length, Sha256Digest.Compute(raw));

        var requestId = 5L;
        foreach (var operation in Operations())
        {
            var needsWorker = operation.Kind != GuardianHostOperationKind.SessionOpen;
            var needsIdentity = operation.Kind is GuardianHostOperationKind.InvokeForeground or
                GuardianHostOperationKind.InvokeBackground;
            yield return new OperationRequest(
                Guardian, Host, HostGeneration, RequestId(requestId++), 100, Alias, Transition,
                needsWorker ? Worker : null,
                needsIdentity ? OperationIdentity : null,
                operation);
        }

        yield return new WorkerCreateCapabilityGrantRequest(
            Guardian, Host, HostGeneration, RequestId(requestId++), 100, Alias, Transition,
            new WorkerGeneration(3), new HostEventSequence(1), Token);
        yield return new WorkerContainmentPendingAckRequest(
            Guardian, Host, HostGeneration, RequestId(requestId++), 100, Alias, Transition,
            Worker, new HostEventSequence(2));
        yield return new WorkerContainmentArmedAckRequest(
            Guardian, Host, HostGeneration, RequestId(requestId++), 100, Alias, Transition,
            Worker, new HostEventSequence(3));
        yield return new WorkerContainmentRemoveAckRequest(
            Guardian, Host, HostGeneration, RequestId(requestId), 100, Alias, Transition,
            Worker, new HostEventSequence(4));
    }

    private static IEnumerable<GuardianHostOperation> Operations()
    {
        var dispatch = new DispatchCapability(DispatchToken, Call, 100);
        var output = new OutputCapability(OutputToken, 1024, 100);
        yield return new InvokeForegroundOperation(
            Call, dispatch, output, "Get-Date", false, GuardianHostInvokeRoute.Pwsh);
        yield return new InvokeBackgroundOperation(
            Call, dispatch, output, "Get-Date", true, GuardianHostInvokeRoute.Auto,
            new PublicJobId(1));
        yield return new JobListOperation(Call, dispatch);
        yield return new JobStatusOperation(Call, dispatch, new PublicJobId(1), JobToken);
        yield return new JobOutputOperation(Call, dispatch, output, new PublicJobId(1), JobToken, 0);
        yield return new JobKillOperation(Call, dispatch, new PublicJobId(1), JobToken);
        yield return new ResetOperation(Call, dispatch, 2, false);
        yield return new SessionOpenOperation(Call, dispatch, null, false);
        yield return new SessionCloseOperation(Call, dispatch, 2, false);
        yield return new SessionRestartOperation(Call, dispatch, 2, true);
    }

    private static IEnumerable<GuardianHostEvent> Events()
    {
        var sequence = 1L;
        var containment = new GuardianHostContainmentIdentity(11, 101, 102, 22, 201, 202);
        var raw = "x"u8.ToArray();

        yield return new WorkerCreateCapabilityRequestedEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), Alias, Transition,
            Digest, 100);
        yield return new WorkerContainmentPendingEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), Alias, Transition,
            Worker, containment);
        yield return new WorkerContainmentArmedEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), Alias, Transition,
            Worker, containment);
        yield return new WorkerContainmentRemoveRequestedEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), Alias, Transition,
            Worker, containment);
        yield return new OperationDeliveryEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), RequestId(40),
            Alias, Transition, Worker, OperationIdentity, DispatchToken,
            GuardianHostDeliveryState.WriteStarted, RequestId(1));
        yield return new SessionLifecycleEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), null, Alias, Transition,
            null, PublicSessionState.Cold, PublicSessionState.Starting,
            GuardianHostSessionLifecycleReason.RequestedOpen, false, false, BootstrapState.Pending);
        yield return new WorkerLostEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), null, Alias, Transition,
            Worker, null, GuardianHostWorkerLostReason.ProcessExit, 1,
            GuardianHostTerminationCertainty.Confirmed, GuardianHostEffectsState.TerminalKnown);
        yield return new WorkerDiagnosticChunkEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), null, Alias, Transition,
            Worker, null, GuardianHostDiagnosticStream.Stderr, 0, 0, raw, true);
        yield return new WorkerDiagnosticTruncatedEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), null, Alias, Transition,
            Worker, null, GuardianHostDiagnosticStream.Stdout, 1);
        yield return new JobLifecycleEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), null, Alias, Transition,
            Worker, null, new PublicJobId(1), GuardianHostJobState.Running, null,
            GuardianHostOutputState.Streaming, 0, null);
        yield return new OutputChunkEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence++), RequestId(41), Alias,
            Transition, Worker, null, OutputToken, 0, 0, raw);
        yield return new OutputSealEvent(
            Guardian, Host, HostGeneration, EventSequence(sequence), RequestId(41), Alias,
            Transition, Worker, null, OutputToken, GuardianHostOutputSealState.Complete,
            raw.Length, Sha256Digest.Compute(raw));
    }

    private static IEnumerable<GuardianHostSuccessResponse> SuccessResponses()
    {
        var requestId = 50L;
        yield return Success(requestId++, new ManifestHeaderAccepted(Manifest));
        yield return Success(requestId++, new ManifestChunkAccepted(Manifest, 0, 1));
        yield return Success(requestId++, new ManifestSealed(Manifest, ManifestDigest, 1));
        yield return Success(requestId++, new ControlAcknowledged(new HostEventSequence(1)));
        yield return Success(requestId++, new ShutdownAccepted());

        var readyResultArguments = new object[]
        {
            Alias, PublicSessionState.Ready, Worker, Transition, true, false, BootstrapState.Restored,
        };
        GuardianHostOperationResult[] results =
        [
            new InvokeForegroundResult("ok"),
            new InvokeBackgroundResult(new PublicJobId(1), JobToken),
            new JobListResult("[]"),
            new JobStatusResult("{}"),
            new JobOutputResult("output"),
            new PtkSharedContracts.JobKillResult("killed"),
            new ResetResult(
                (CanonicalAlias)readyResultArguments[0], (PublicSessionState)readyResultArguments[1],
                (GuardianHostWorkerIdentity)readyResultArguments[2],
                (SessionTransitionVersion)readyResultArguments[3], true, false, BootstrapState.Restored),
            new SessionOpenResult(Alias, PublicSessionState.Ready, Worker, Transition,
                true, false, BootstrapState.Restored),
            new SessionCloseResult(Alias, PublicSessionState.Ready, Worker, Transition,
                true, false, BootstrapState.Restored),
            new SessionRestartResult(Alias, PublicSessionState.Ready, Worker, Transition,
                true, false, BootstrapState.Restored),
        ];
        foreach (var result in results)
            yield return Success(requestId++, new OperationCompleted(result));
    }

    private static GuardianHostSuccessResponse Success(
        long requestId,
        GuardianHostSuccessPayload payload) =>
        new(Guardian, Host, HostGeneration, RequestId(requestId), payload);

    private static PrivateRequestId RequestId(long value) => new(value);
    private static HostEventSequence EventSequence(long value) => new(value);

    private static CapabilityToken Capability(byte value)
    {
        var bytes = Enumerable.Repeat(value, ContractLimits.CapabilityTokenBytes).ToArray();
        return new CapabilityToken(Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_'));
    }

    private static bool IsRawOrGenericEscape(Type type)
    {
        if (type == typeof(object) || type == typeof(System.Text.Json.JsonElement) ||
            typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            return true;
        return type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(Dictionary<,>) ||
             definition == typeof(IDictionary<,>) ||
             definition == typeof(IReadOnlyDictionary<,>));
    }

    private sealed class FailingWriteStream : Stream
    {
        internal int WriteCount { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return ValueTask.FromException(new IOException("Injected transport ambiguity."));
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
