using System.Security.Cryptography;
using PtkSharedContracts;

namespace PtkMcpServer.GuardianHost;

internal enum PrivateHostServerState
{
    Created,
    HelloSent,
    ReceivingManifest,
    Ready,
    Faulted,
}

internal sealed record PrivateHostServerIdentity
{
    internal PrivateHostServerIdentity(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        int hostPid)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(hostBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        if (hostPid <= 0) throw new ArgumentOutOfRangeException(nameof(hostPid));

        GuardianBootId = guardianBootId;
        HostBootId = hostBootId;
        HostGeneration = hostGeneration;
        HostPid = hostPid;
    }

    internal GuardianBootId GuardianBootId { get; }
    internal HostBootId HostBootId { get; }
    internal HostGeneration HostGeneration { get; }
    internal int HostPid { get; }
}

internal sealed record PrivateHostServerPins
{
    internal PrivateHostServerPins(
        Sha256Digest hostExecutableDigest,
        Sha256Digest hostBuildDigest,
        Sha256Digest publicContractDigest,
        Sha256Digest configurationDigest,
        Sha256Digest packageManifestDigest)
    {
        ArgumentNullException.ThrowIfNull(hostExecutableDigest);
        ArgumentNullException.ThrowIfNull(hostBuildDigest);
        ArgumentNullException.ThrowIfNull(publicContractDigest);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(packageManifestDigest);

        HostExecutableDigest = hostExecutableDigest;
        HostBuildDigest = hostBuildDigest;
        PublicContractDigest = publicContractDigest;
        ConfigurationDigest = configurationDigest;
        PackageManifestDigest = packageManifestDigest;
    }

    internal Sha256Digest HostExecutableDigest { get; }
    internal Sha256Digest HostBuildDigest { get; }
    internal Sha256Digest PublicContractDigest { get; }
    internal Sha256Digest ConfigurationDigest { get; }
    internal Sha256Digest PackageManifestDigest { get; }
}

internal sealed record PrivateHostInitialization(
    RecoveryManifest Manifest,
    PrivateRequestId InitializeRequestId,
    ManifestId ManifestId,
    Sha256Digest ManifestDigest);

/// <summary>
/// Strict, single-use private-host initialization server. It owns only the
/// typed stream lifecycle and recovery-manifest transfer; it cannot execute a
/// tool, create a session runtime, launch a process, or change MCP registration.
/// </summary>
internal sealed class PrivateHostServer
{
    private readonly GuardianHostProtocolReader _reader;
    private readonly GuardianHostProtocolWriter _writer;
    private readonly PrivateHostServerIdentity _identity;
    private readonly PrivateHostServerPins _pins;
    private readonly Func<int, byte[]> _manifestBufferFactory;

    private int _started;
    private int _state;

    internal PrivateHostServer(
        Stream guardianRequestStream,
        Stream hostEventStream,
        PrivateHostServerIdentity identity,
        PrivateHostServerPins pins,
        Func<int, byte[]>? manifestBufferFactory = null)
    {
        ArgumentNullException.ThrowIfNull(guardianRequestStream);
        ArgumentNullException.ThrowIfNull(hostEventStream);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(pins);
        if (!guardianRequestStream.CanRead)
            throw new ArgumentException("Guardian request stream must be readable.", nameof(guardianRequestStream));
        if (!hostEventStream.CanWrite)
            throw new ArgumentException("Host event stream must be writable.", nameof(hostEventStream));

        _reader = new GuardianHostProtocolReader(
            guardianRequestStream,
            GuardianHostPeer.Guardian);
        _writer = new GuardianHostProtocolWriter(
            hostEventStream,
            GuardianHostPeer.Host);
        _identity = identity;
        _pins = pins;
        _manifestBufferFactory = manifestBufferFactory ??
            (length => GC.AllocateUninitializedArray<byte>(length));
        _state = (int)PrivateHostServerState.Created;
    }

    internal PrivateHostServerState State =>
        (PrivateHostServerState)Volatile.Read(ref _state);

    internal async ValueTask<PrivateHostInitialization> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Private-host initialization is single-use.");

        try
        {
            await WriteHelloAsync(cancellationToken).ConfigureAwait(false);
            SetState(PrivateHostServerState.HelloSent);

            var initialize = await ReadRequiredAsync<GuardianHostInitialize>(
                "initialize_required",
                cancellationToken).ConfigureAwait(false);
            ValidateInitialize(initialize);
            var lastRequestId = initialize.RequestId.Value;

            var header = await ReadRequiredAsync<ManifestHeaderRequest>(
                "manifest_header_required",
                cancellationToken).ConfigureAwait(false);
            ValidateRequest(header, ref lastRequestId);
            SetState(PrivateHostServerState.ReceivingManifest);

            byte[]? manifestBytes = null;
            try
            {
                manifestBytes = _manifestBufferFactory(header.TotalBytes) ??
                    throw new InvalidOperationException("Manifest buffer factory returned null.");
                if (manifestBytes.Length != header.TotalBytes)
                {
                    throw Protocol(
                        "manifest_buffer_invalid",
                        "Manifest buffer factory returned an incorrectly sized buffer.");
                }
                await WriteSuccessAsync(
                    header.RequestId,
                    new ManifestHeaderAccepted(header.ManifestId),
                    cancellationToken).ConfigureAwait(false);

                var nextOffset = 0;
                for (var expectedChunk = 0; expectedChunk < header.ChunkCount; expectedChunk++)
                {
                    var chunk = await ReadRequiredAsync<ManifestChunkRequest>(
                        "manifest_chunk_required",
                        cancellationToken).ConfigureAwait(false);
                    using (chunk)
                    {
                        ValidateRequest(chunk, ref lastRequestId);
                        ValidateChunk(header, chunk, expectedChunk, nextOffset);
                        chunk.RawSpan.CopyTo(manifestBytes.AsSpan(nextOffset));
                        nextOffset = checked(nextOffset + chunk.RawByteCount);
                    }

                    await WriteSuccessAsync(
                        chunk.RequestId,
                        new ManifestChunkAccepted(
                            header.ManifestId,
                            expectedChunk,
                            nextOffset),
                        cancellationToken).ConfigureAwait(false);
                }

                if (nextOffset != header.TotalBytes)
                    throw Protocol("manifest_length_mismatch", "Recovery manifest length does not match its header.");

                var seal = await ReadRequiredAsync<ManifestSealRequest>(
                    "manifest_seal_required",
                    cancellationToken).ConfigureAwait(false);
                ValidateRequest(seal, ref lastRequestId);
                ValidateSeal(header, seal, manifestBytes);

                RecoveryManifest manifest;
                try
                {
                    manifest = RecoveryManifestCodec.DecodeForInitialize(
                        manifestBytes,
                        _pins.ConfigurationDigest);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw Protocol(
                        "manifest_invalid",
                        "Recovery manifest failed strict canonical validation.",
                        exception);
                }

                ValidateManifest(header, manifest);
                await WriteSuccessAsync(
                    seal.RequestId,
                    new ManifestSealed(
                        header.ManifestId,
                        header.ManifestDigest,
                        header.TotalBytes),
                    cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(
                    new GuardianHostReady(
                        _identity.GuardianBootId,
                        _identity.HostBootId,
                        _identity.HostGeneration,
                        initialize.RequestId,
                        header.ManifestId,
                        header.ManifestDigest,
                        _identity.HostPid),
                    cancellationToken).ConfigureAwait(false);

                SetState(PrivateHostServerState.Ready);
                return new PrivateHostInitialization(
                    manifest,
                    initialize.RequestId,
                    header.ManifestId,
                    header.ManifestDigest);
            }
            finally
            {
                if (manifestBytes is not null)
                    CryptographicOperations.ZeroMemory(manifestBytes);
            }
        }
        catch
        {
            SetState(PrivateHostServerState.Faulted);
            throw;
        }
    }

    private ValueTask WriteHelloAsync(CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new GuardianHostHello(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                _identity.HostPid,
                _pins.HostExecutableDigest,
                _pins.HostBuildDigest,
                _pins.PublicContractDigest,
                _pins.ConfigurationDigest),
            cancellationToken);

    private async ValueTask<TMessage> ReadRequiredAsync<TMessage>(
        string detailCode,
        CancellationToken cancellationToken)
        where TMessage : GuardianHostMessage
    {
        var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
            throw Protocol("unexpected_eof", "Private guardian channel ended during initialization.");
        ValidateIdentity(message);
        return message as TMessage ?? throw Protocol(
            detailCode,
            $"Expected private message '{typeof(TMessage).Name}'.");
    }

    private void ValidateIdentity(GuardianHostMessage message)
    {
        if (message.GuardianBootId != _identity.GuardianBootId ||
            message.HostBootId != _identity.HostBootId ||
            message.HostGeneration != _identity.HostGeneration)
        {
            throw Protocol("identity_mismatch", "Private initialization identity does not match this host generation.");
        }
    }

    private void ValidateInitialize(GuardianHostInitialize initialize)
    {
        if (initialize.HostExecutableDigest != _pins.HostExecutableDigest ||
            initialize.HostBuildDigest != _pins.HostBuildDigest ||
            initialize.PublicContractDigest != _pins.PublicContractDigest ||
            initialize.ConfigurationDigest != _pins.ConfigurationDigest ||
            initialize.PackageManifestDigest != _pins.PackageManifestDigest)
        {
            throw Protocol("initialize_pin_mismatch", "Guardian initialization pins do not match the private host.");
        }
    }

    private static void ValidateRequest(
        GuardianHostRequest request,
        ref long lastRequestId)
    {
        if (request.RequestId.Value <= lastRequestId)
        {
            throw Protocol(
                "request_id_not_increasing",
                "Private guardian request IDs must be strictly increasing.");
        }

        lastRequestId = request.RequestId.Value;
    }

    private static void ValidateChunk(
        ManifestHeaderRequest header,
        ManifestChunkRequest chunk,
        int expectedChunk,
        int nextOffset)
    {
        var expectedBytes = Math.Min(
            ContractLimits.MaximumManifestChunkBytes,
            header.TotalBytes - nextOffset);
        if (chunk.ManifestId != header.ManifestId ||
            chunk.ChunkIndex != expectedChunk ||
            chunk.Offset != nextOffset ||
            chunk.RawByteCount != expectedBytes)
        {
            throw Protocol(
                "manifest_chunk_mismatch",
                "Recovery manifest chunk order, identity, offset, or length is invalid.");
        }
    }

    private static void ValidateSeal(
        ManifestHeaderRequest header,
        ManifestSealRequest seal,
        byte[] manifestBytes)
    {
        if (seal.ManifestId != header.ManifestId ||
            seal.TotalBytes != header.TotalBytes ||
            seal.ManifestDigest != header.ManifestDigest ||
            Sha256Digest.Compute(manifestBytes) != header.ManifestDigest)
        {
            throw Protocol("manifest_seal_mismatch", "Recovery manifest seal does not match the transferred bytes.");
        }
    }

    private void ValidateManifest(
        ManifestHeaderRequest header,
        RecoveryManifest manifest)
    {
        if (manifest.GuardianBootId != _identity.GuardianBootId ||
            manifest.HostGeneration != _identity.HostGeneration ||
            manifest.HostGenerationHighWatermark != _identity.HostGeneration ||
            manifest.Bindings.Count != header.AliasCount ||
            manifest.Templates.Count != header.TemplateCount)
        {
            throw Protocol(
                "manifest_identity_mismatch",
                "Recovery manifest identity or declared collection counts do not match initialization.");
        }
    }

    private ValueTask WriteSuccessAsync(
        PrivateRequestId requestId,
        GuardianHostSuccessPayload payload,
        CancellationToken cancellationToken) =>
        _writer.WriteAsync(
            new GuardianHostSuccessResponse(
                _identity.GuardianBootId,
                _identity.HostBootId,
                _identity.HostGeneration,
                requestId,
                payload),
            cancellationToken);

    private void SetState(PrivateHostServerState state) =>
        Volatile.Write(ref _state, (int)state);

    private static GuardianHostProtocolException Protocol(
        string detailCode,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new GuardianHostProtocolException(detailCode, message)
            : new GuardianHostProtocolException(detailCode, message, innerException);
}
