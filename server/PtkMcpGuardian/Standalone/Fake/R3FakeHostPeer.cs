using System.Security.Cryptography;
using System.Text.Json;
using PtkMcpGuardian.Lifecycle;
using PtkSharedContracts;

namespace PtkMcpGuardian.Standalone.Fake;

/// <summary>
/// A strict typed peer for supervisor tests. It implements only the R3 surface:
/// initialize with a complete recovery manifest, job-list, and shutdown.
/// </summary>
internal sealed class R3FakeHostPeer : IDisposable
{
    private const int MaximumRetainedRequestIds = 256;

    private static readonly GuardianBootId OtherGuardianBootId = new(
        Guid.Parse("381c6068-13a7-4b7d-8784-9cb720965d2d"));
    private static readonly HostBootId OtherHostBootId = new(
        Guid.Parse("aa768403-d3dc-4559-a81c-1b9201d3f33e"));
    private static readonly Sha256Digest FaultDigest = Sha256Digest.Compute(
        "r3-fake-host-fault"u8);
    private static readonly Sha256Digest AlternateFaultDigest = Sha256Digest.Compute(
        "r3-fake-host-alternate-fault"u8);

    private readonly GuardianHostIdentity _identity;
    private readonly GuardianHostSupervisorPins _pins;
    private readonly R3FakeHostProfile _profile;
    private readonly int _hostProcessId;
    private readonly GuardianHostProtocolReader _reader;
    private readonly GuardianHostProtocolWriter _writer;
    private readonly R3BoundedOneWayStream _eventStream;
    private readonly R3FakeHostControl _control;
    private readonly R3FakeHostAttemptPlan _attemptPlan;
    private readonly object _sync = new();
    private readonly List<long> _receivedRequestIds = [];

    private long _lastRequestId;
    private int _jobListEffectCount;
    private int _disposed;

    internal R3FakeHostPeer(
        GuardianHostIdentity identity,
        GuardianHostSupervisorPins pins,
        R3FakeHostProfile profile,
        int hostProcessId,
        R3BoundedOneWayStream requestStream,
        R3BoundedOneWayStream eventStream,
        R3FakeHostControl control,
        R3FakeHostAttemptPlan attemptPlan,
        Action<byte[]>? retiredTransportBufferObserver)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (hostProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(hostProcessId));
        ArgumentNullException.ThrowIfNull(requestStream);
        _eventStream = eventStream ?? throw new ArgumentNullException(nameof(eventStream));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _attemptPlan = attemptPlan ?? throw new ArgumentNullException(nameof(attemptPlan));
        _hostProcessId = hostProcessId;
        _reader = retiredTransportBufferObserver is null
            ? new GuardianHostProtocolReader(requestStream, GuardianHostPeer.Guardian)
            : new GuardianHostProtocolReader(
                requestStream,
                GuardianHostPeer.Guardian,
                retiredTransportBufferObserver);
        _writer = new GuardianHostProtocolWriter(eventStream, GuardianHostPeer.Host);
    }

    internal int JobListEffectCount => Volatile.Read(ref _jobListEffectCount);

    internal IReadOnlyList<long> ReceivedRequestIds
    {
        get
        {
            lock (_sync)
                return _receivedRequestIds.ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _reader.Dispose();
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteAsync(CreateHello(), cancellationToken).ConfigureAwait(false);

            var initialize = await ReadRequiredAsync<GuardianHostInitialize>(cancellationToken)
                .ConfigureAwait(false);
            ValidateInitialize(initialize);
            RecordRequest(initialize.RequestId);

            var header = await ReadRequiredAsync<ManifestHeaderRequest>(cancellationToken)
                .ConfigureAwait(false);
            ValidateRequestIdentity(header);
            RecordRequest(header.RequestId);
            if (header.AliasCount != 1 || header.TemplateCount != 0)
                throw new InvalidDataException(
                    "The R3 fake host accepts one default binding and no templates.");
            await WriteSuccessAsync(
                header.RequestId,
                new ManifestHeaderAccepted(header.ManifestId),
                cancellationToken).ConfigureAwait(false);

            var manifestBytes = GC.AllocateUninitializedArray<byte>(header.TotalBytes);
            try
            {
                await ReceiveManifestChunksAsync(header, manifestBytes, cancellationToken)
                    .ConfigureAwait(false);
                var seal = await ReadRequiredAsync<ManifestSealRequest>(cancellationToken)
                    .ConfigureAwait(false);
                ValidateRequestIdentity(seal);
                RecordRequest(seal.RequestId);
                ValidateSeal(header, seal, manifestBytes);
                RequireNoEncodedTemplates(manifestBytes);
                var manifest = RecoveryManifestCodec.DecodeForInitialize(
                    manifestBytes,
                    _pins.ConfigurationDigest);
                ValidateManifest(header, manifest);

                await WriteSuccessAsync(
                    seal.RequestId,
                    new ManifestSealed(header.ManifestId, header.ManifestDigest, header.TotalBytes),
                    cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(new GuardianHostReady(
                    _identity.GuardianBootId,
                    _identity.HostBootId,
                    _identity.HostGeneration,
                    initialize.RequestId,
                    header.ManifestId,
                    header.ManifestDigest,
                    _hostProcessId), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(manifestBytes);
            }

            await RunOperationalLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _eventStream.CompleteWriting();
        }
    }

    private GuardianHostHello CreateHello()
    {
        var guardianBootId = _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongGuardianBootId
            ? DifferentGuardianBootId(_identity.GuardianBootId)
            : _identity.GuardianBootId;
        var hostBootId = _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongHostBootId
            ? DifferentHostBootId(_identity.HostBootId)
            : _identity.HostBootId;
        var generation = _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongGeneration
            ? DifferentGeneration(_identity.HostGeneration)
            : _identity.HostGeneration;

        return new GuardianHostHello(
            guardianBootId,
            hostBootId,
            generation,
            _hostProcessId,
            _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongExecutableDigest
                ? DifferentDigest(_pins.HostExecutableDigest) : _pins.HostExecutableDigest,
            _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongBuildDigest
                ? DifferentDigest(_pins.HostBuildDigest) : _pins.HostBuildDigest,
            _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongPublicContractDigest
                ? DifferentDigest(_pins.PublicContractDigest) : _pins.PublicContractDigest,
            _attemptPlan.HelloFault == R3FakeHostHelloFault.WrongConfigurationDigest
                ? DifferentDigest(_pins.ConfigurationDigest) : _pins.ConfigurationDigest);
    }

    private void ValidateInitialize(GuardianHostInitialize initialize)
    {
        ValidateIdentity(initialize);
        if (initialize.HostExecutableDigest != _pins.HostExecutableDigest ||
            initialize.HostBuildDigest != _pins.HostBuildDigest ||
            initialize.PublicContractDigest != _pins.PublicContractDigest ||
            initialize.ConfigurationDigest != _pins.ConfigurationDigest ||
            initialize.PackageManifestDigest != _pins.PackageManifestDigest ||
            initialize.GuardianProtocolVersion != ContractLimits.GuardianHostProtocolVersion ||
            initialize.HostProtocolVersion != ContractLimits.GuardianHostProtocolVersion ||
            initialize.MaximumManifestBytes != ContractLimits.MaximumManifestBytes ||
            initialize.MaximumManifestChunkRawBytes != ContractLimits.MaximumManifestChunkBytes ||
            initialize.MaximumAliases != ContractLimits.MaximumAliases ||
            initialize.MaximumTemplates != ContractLimits.MaximumTemplates)
        {
            throw new InvalidDataException("Initialize did not match the fake host's frozen pins.");
        }
    }

    private async Task ReceiveManifestChunksAsync(
        ManifestHeaderRequest header,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        for (var expectedIndex = 0; expectedIndex < header.ChunkCount; expectedIndex++)
        {
            using var chunk = await ReadRequiredAsync<ManifestChunkRequest>(cancellationToken)
                .ConfigureAwait(false);
            ValidateRequestIdentity(chunk);
            RecordRequest(chunk.RequestId);

            var expectedLength = Math.Min(
                ContractLimits.MaximumManifestChunkBytes,
                header.TotalBytes - offset);
            if (chunk.ManifestId != header.ManifestId ||
                chunk.ChunkIndex != expectedIndex ||
                chunk.Offset != offset ||
                chunk.RawByteCount != expectedLength)
            {
                throw new InvalidDataException("Manifest chunk correlation was invalid.");
            }

            var raw = chunk.GetRawBytes();
            try
            {
                if (Sha256Digest.Compute(raw) != chunk.RawDigest)
                    throw new InvalidDataException("Manifest chunk digest was invalid.");
                raw.CopyTo(destination, offset);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(raw);
            }

            offset += expectedLength;
            await WriteSuccessAsync(
                chunk.RequestId,
                new ManifestChunkAccepted(header.ManifestId, expectedIndex, offset),
                cancellationToken).ConfigureAwait(false);
        }

        if (offset != header.TotalBytes)
            throw new InvalidDataException("Manifest transfer length was invalid.");
    }

    private void ValidateSeal(
        ManifestHeaderRequest header,
        ManifestSealRequest seal,
        byte[] manifestBytes)
    {
        if (seal.ManifestId != header.ManifestId ||
            seal.TotalBytes != header.TotalBytes ||
            seal.ManifestDigest != header.ManifestDigest ||
            Sha256Digest.Compute(manifestBytes) != header.ManifestDigest)
        {
            throw new InvalidDataException("Manifest seal did not match the transferred bytes.");
        }
    }

    private void ValidateManifest(ManifestHeaderRequest header, RecoveryManifest manifest)
    {
        var binding = manifest.Bindings.SingleOrDefault();
        var watermark = manifest.WorkerGenerationHighWatermarks.SingleOrDefault();
        if (manifest.GuardianBootId != _identity.GuardianBootId ||
            manifest.HostGeneration != _identity.HostGeneration ||
            manifest.HostGenerationHighWatermark != _identity.HostGeneration ||
            manifest.ConfigurationDigest != _pins.ConfigurationDigest ||
            manifest.CatalogDigest != _pins.CatalogDigest ||
            manifest.Bindings.Count != header.AliasCount ||
            manifest.Templates.Count != header.TemplateCount ||
            binding is null ||
            binding.Alias.Value != "default" ||
            binding.BindingKind != RecoveryBindingKind.Default ||
            binding.TemplateName is not null ||
            binding.TemplateDigest is not null ||
            binding.BootstrapDigest is not null ||
            binding.AllowColdBackground != _profile.AllowColdBackground ||
            binding.DesiredState != _profile.DesiredState ||
            binding.TransitionVersion != _profile.JobListTarget.TransitionVersion ||
            binding.BindingDigest != _profile.BindingDigest ||
            watermark is null ||
            watermark.Alias != _profile.JobListTarget.Alias ||
            watermark.Generation != _profile.WorkerGenerationHighWatermark)
        {
            throw new InvalidDataException("Recovery manifest did not match the attempt identity and pins.");
        }
    }

    private async Task RunOperationalLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false) ??
                throw new EndOfStreamException("Guardian transport ended before shutdown.");
            switch (message)
            {
                case OperationRequest operation:
                    await HandleOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                    break;
                case GuardianHostShutdown shutdown:
                    ValidateIdentity(shutdown);
                    RecordRequest(shutdown.RequestId);
                    await WriteSuccessAsync(
                        shutdown.RequestId,
                        new ShutdownAccepted(),
                        cancellationToken).ConfigureAwait(false);
                    return;
                default:
                    (message as IDisposable)?.Dispose();
                    throw new InvalidDataException(
                        $"The R3 fake host does not accept {message.Kind} in its operational loop.");
            }
        }
    }

    private async Task HandleOperationAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequestIdentity(request);
        RecordRequest(request.RequestId);
        var expected = _profile.JobListTarget;
        if (request.SessionAlias != expected.Alias ||
            request.SessionTransitionVersion != expected.TransitionVersion ||
            request.WorkerIdentity is null ||
            request.WorkerIdentity.BootId != expected.WorkerIdentity.BootId ||
            request.WorkerIdentity.Generation != expected.WorkerIdentity.Generation ||
            request.OperationIdentity is not null ||
            request.Operation is not JobListOperation)
        {
            throw new InvalidDataException("The R3 fake host accepts only a typed default job-list request.");
        }

        var plan = _control.TakeOperation();
        Interlocked.Increment(ref _jobListEffectCount);
        plan.Received.Reach();
        if (plan.Behavior == R3FakeHostOperationBehavior.CrashAfterReceive)
            throw new IOException("The R3 fake host crashed after receiving job-list.");

        await plan.BeforeResponse.ReachAndWaitAsync(cancellationToken).ConfigureAwait(false);
        var responseGeneration = plan.Behavior == R3FakeHostOperationBehavior.WrongGenerationResponse
            ? DifferentGeneration(_identity.HostGeneration)
            : _identity.HostGeneration;
        var response = new GuardianHostSuccessResponse(
            _identity.GuardianBootId,
            _identity.HostBootId,
            responseGeneration,
            request.RequestId,
            new OperationCompleted(new JobListResult(plan.ResponseText)));
        await _writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        plan.ResponseSent.Reach();
        if (plan.Behavior == R3FakeHostOperationBehavior.DuplicateResponse)
            await _writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> ReadRequiredAsync<T>(CancellationToken cancellationToken)
        where T : GuardianHostMessage
    {
        var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false) ??
            throw new EndOfStreamException($"Guardian transport ended before {typeof(T).Name}.");
        if (message is T typed)
            return typed;
        (message as IDisposable)?.Dispose();
        throw new InvalidDataException(
            $"Expected {typeof(T).Name}, received {message.GetType().Name}.");
    }

    private ValueTask WriteSuccessAsync(
        PrivateRequestId requestId,
        GuardianHostSuccessPayload payload,
        CancellationToken cancellationToken) =>
        _writer.WriteAsync(new GuardianHostSuccessResponse(
            _identity.GuardianBootId,
            _identity.HostBootId,
            _identity.HostGeneration,
            requestId,
            payload), cancellationToken);

    private void ValidateRequestIdentity(GuardianHostRequest request) => ValidateIdentity(request);

    private void ValidateIdentity(GuardianHostMessage message)
    {
        if (message.GuardianBootId != _identity.GuardianBootId ||
            message.HostBootId != _identity.HostBootId ||
            message.HostGeneration != _identity.HostGeneration)
        {
            throw new InvalidDataException("Private message identity did not match the fake attempt.");
        }
    }

    private void RecordRequest(PrivateRequestId requestId)
    {
        if (requestId.Value <= _lastRequestId)
            throw new InvalidDataException("Guardian request IDs must be strictly increasing.");
        _lastRequestId = requestId.Value;
        lock (_sync)
        {
            if (_receivedRequestIds.Count == MaximumRetainedRequestIds)
                _receivedRequestIds.RemoveAt(0);
            _receivedRequestIds.Add(requestId.Value);
        }
    }

    private static HostGeneration DifferentGeneration(HostGeneration generation) =>
        new(generation.Value == long.MaxValue ? 1 : generation.Value + 1);

    private static GuardianBootId DifferentGuardianBootId(GuardianBootId value) =>
        value != OtherGuardianBootId
            ? OtherGuardianBootId
            : new GuardianBootId(Guid.Parse("c754ee48-cbbd-4b21-bde8-8c7833934dc1"));

    private static HostBootId DifferentHostBootId(HostBootId value) =>
        value != OtherHostBootId
            ? OtherHostBootId
            : new HostBootId(Guid.Parse("c177d78e-fcb8-455e-83e2-322adad15435"));

    private static Sha256Digest DifferentDigest(Sha256Digest value) =>
        value != FaultDigest ? FaultDigest : AlternateFaultDigest;

    private static void RequireNoEncodedTemplates(ReadOnlySpan<byte> encoded)
    {
        var reader = new Utf8JsonReader(encoded, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = ContractLimits.MaximumJsonDepth,
        });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals("templates"u8))
            {
                continue;
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray ||
                !reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                throw new InvalidDataException(
                    "The R3 fake host does not materialize recovery templates.");
            }
            return;
        }
        throw new InvalidDataException("Recovery manifest omitted templates.");
    }
}
