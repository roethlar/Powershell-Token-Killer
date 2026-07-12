namespace PtkMcpServer.Audit;

internal enum AuditClosedSpoolExportStepKind
{
    Advanced,
    PrefixComplete,
    ChainComplete,
    Retry,
    Blocked,
}

internal sealed record AuditClosedSpoolExportStep(
    AuditClosedSpoolExportStepKind Kind,
    Guid? EventId,
    string DetailCode,
    AuditExportFailureClass? FailureClass = null,
    TimeSpan? RetryAfter = null,
    bool HasHealthWarning = false,
    IAuditClosedSpoolPrefixEndPosition? PrefixEnd = null);

internal enum AuditClosedSpoolExportMode
{
    CompleteChain,
    ClosedPrefix,
    WriterClosedChain,
}

/// <summary>
/// Couples one validated, retained closed spool snapshot to one OTLP transport.
/// Each call performs at most one remote attempt. Only opaque positions issued
/// by the reader can advance or block the durable checkpoint.
/// </summary>
internal sealed class AuditClosedSpoolExportPump : IDisposable
{
    private readonly AuditClosedSpoolChainReader _reader;
    private readonly IDisposable _readerLease;
    private readonly IAuditOtlpExportTransport _transport;
    private readonly string _configurationIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly AuditClosedSpoolExportMode _mode;
    private readonly IAuditLiveSpoolRotationPosition? _rotation;
    private readonly IAuditLiveSpoolWriterClosedPosition? _writerClosed;
    private AuditClosedSpoolRecovery? _initialRecovery;
    private IAuditClosedSpoolRecordPosition? _next;
    private AuditExportBlockedRecord? _blocked;
    private IAuditClosedSpoolPrefixEndPosition? _prefixEnd;
    private bool _initialized;
    private bool _complete;
    private bool _faulted;
    private int _lifecycle;

    internal AuditClosedSpoolExportPump(
        AuditClosedSpoolChainReader reader,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
        : this(
            reader,
            transport,
            AuditClosedSpoolExportMode.CompleteChain,
            rotation: null,
            writerClosed: null,
            initialRecovery: null,
            timeProvider)
    {
    }

    private AuditClosedSpoolExportPump(
        AuditClosedSpoolChainReader reader,
        IAuditOtlpExportTransport transport,
        AuditClosedSpoolExportMode mode,
        IAuditLiveSpoolRotationPosition? rotation,
        IAuditLiveSpoolWriterClosedPosition? writerClosed,
        AuditClosedSpoolRecovery? initialRecovery,
        TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(transport);
        var configurationIdentity = transport.ConfigurationIdentity;
        RequireConfigurationIdentity(configurationIdentity);
        if (!string.Equals(
                reader.ExportConfigurationIdentity,
                configurationIdentity,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The audit export transport does not match the anchored reader configuration.",
                nameof(transport));
        }
        _reader = reader;
        _readerLease = reader.RetainExportPump();
        _transport = transport;
        _configurationIdentity = configurationIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mode = mode;
        _rotation = rotation;
        _writerClosed = writerClosed;
        _initialRecovery = initialRecovery;
    }

    internal static AuditClosedSpoolExportPump ForClosedPrefix(
        AuditClosedSpoolChainReader reader,
        IAuditLiveSpoolRotationPosition rotation,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return new AuditClosedSpoolExportPump(
            reader,
            transport,
            AuditClosedSpoolExportMode.ClosedPrefix,
            rotation,
            writerClosed: null,
            initialRecovery: null,
            timeProvider);
    }

    internal static AuditClosedSpoolExportPump AfterWriterClosed(
        AuditClosedSpoolChainReader reader,
        IAuditLiveSpoolWriterClosedPosition writerClosed,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writerClosed);
        return new AuditClosedSpoolExportPump(
            reader,
            transport,
            AuditClosedSpoolExportMode.WriterClosedChain,
            rotation: null,
            writerClosed,
            initialRecovery: null,
            timeProvider);
    }

    internal static AuditClosedSpoolExportPump ForAdoptedClosedChain(
        AuditClosedSpoolChainReader reader,
        AuditClosedSpoolRecovery initialRecovery,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(initialRecovery);
        if (initialRecovery is AuditClosedSpoolRecovery.PrefixEnd)
        {
            throw new ArgumentException(
                "An adopted closed chain cannot begin at a live-prefix end.",
                nameof(initialRecovery));
        }
        return new AuditClosedSpoolExportPump(
            reader,
            transport,
            AuditClosedSpoolExportMode.CompleteChain,
            rotation: null,
            writerClosed: null,
            initialRecovery,
            timeProvider);
    }

    internal async Task<AuditClosedSpoolExportStep> ExportNextAsync(
        CancellationToken cancellationToken)
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (prior == 2)
            throw new ObjectDisposedException(nameof(AuditClosedSpoolExportPump));
        if (prior != 0)
        {
            throw new InvalidOperationException(
                "A closed audit spool export step is already running.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_faulted)
            {
                throw new IOException(
                    "The closed audit spool export pump is faulted and requires checkpoint recovery.");
            }
            if (_complete)
            {
                return _mode == AuditClosedSpoolExportMode.ClosedPrefix
                    ? PrefixComplete(
                        _prefixEnd ?? throw new IOException(
                            "The closed-prefix exporter lost its terminal proof."),
                        eventId: null,
                        "prefix.complete")
                    : ChainComplete(null, "chain.complete");
            }
            if (!_initialized)
            {
                try
                {
                    var initial = _initialRecovery ?? ResolveCurrent();
                    _initialRecovery = null;
                    if (initial is AuditClosedSpoolRecovery.PrefixEnd initialPrefix)
                    {
                        if (_mode != AuditClosedSpoolExportMode.ClosedPrefix)
                        {
                            throw new IOException(
                                "A complete-chain exporter received a closed-prefix boundary.");
                        }
                        _prefixEnd = initialPrefix.Position;
                        _complete = true;
                        return PrefixComplete(
                            initialPrefix.Position,
                            eventId: null,
                            "prefix.complete");
                    }
                    if (initial is AuditClosedSpoolRecovery.ChainEnd initialEnd)
                    {
                        if (_mode == AuditClosedSpoolExportMode.ClosedPrefix)
                        {
                            throw new IOException(
                                "A closed-prefix exporter received a complete-chain end.");
                        }
                        _reader.MarkChainComplete(initialEnd.Position);
                        _complete = true;
                        return ChainComplete(null, "chain.complete");
                    }

                    if (initial is not AuditClosedSpoolRecovery.Record initialRecord)
                        throw new IOException("The closed audit spool recovery is invalid.");
                    _next = initialRecord.Position;
                    _blocked = initialRecord.BlockedRecord;
                    _initialized = true;
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    _faulted = true;
                    throw;
                }
            }

            var position = _next
                ?? throw new IOException("The closed audit spool exporter lost its current position.");
            if (_blocked is { } blocked &&
                (blocked.FailureClass != AuditExportFailureClass.Configuration ||
                 string.Equals(
                     blocked.ExportConfigurationIdentity,
                     _configurationIdentity,
                     StringComparison.Ordinal)))
            {
                return new AuditClosedSpoolExportStep(
                    AuditClosedSpoolExportStepKind.Blocked,
                    position.EventId,
                    blocked.DetailCode,
                    blocked.FailureClass);
            }

            AuditOtlpRecord mapped;
            try
            {
                mapped = AuditOtlpRecordMapper.Map(position.ExactJsonlBytes);
            }
            catch (AuditOtlpMappingException exception)
            {
                return PersistBlock(
                    position,
                    AuditExportFailureClass.Data,
                    $"mapping.{exception.FailureCode}",
                    responseDigest: null);
            }

            if (mapped.EventId != position.EventId ||
                !string.Equals(mapped.EventHash, position.EventHash, StringComparison.Ordinal))
            {
                return PersistBlock(
                    position,
                    AuditExportFailureClass.Protocol,
                    "mapping.identity",
                    responseDigest: null);
            }

            IAuditClosedSpoolConfigurationRetryAuthorization? configurationRetry = null;
            if (_blocked is
                {
                    FailureClass: AuditExportFailureClass.Configuration,
                } changedConfigurationBlock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                configurationRetry = _reader.AuthorizeConfigurationRetry(position);
                _blocked = ReidentifyConfigurationBlock(
                    changedConfigurationBlock,
                    _configurationIdentity);
            }

            var attempt = await _transport.ExportAsync(mapped, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                return attempt.Kind switch
                {
                    AuditExportAttemptKind.Acknowledged =>
                        Acknowledge(
                            position,
                            attempt.DetailCode,
                            attempt.HasHealthWarning,
                            configurationRetry),
                    AuditExportAttemptKind.Retry =>
                        configurationRetry is null
                            ? new AuditClosedSpoolExportStep(
                                AuditClosedSpoolExportStepKind.Retry,
                                position.EventId,
                                attempt.DetailCode,
                                RetryAfter: attempt.RetryAfter)
                            : PersistBlock(
                                position,
                                AuditExportFailureClass.Configuration,
                                "retry." + attempt.DetailCode,
                                attempt.ResponseDigest,
                                configurationRetry),
                    AuditExportAttemptKind.Blocked =>
                        PersistBlock(
                            position,
                            attempt.FailureClass ?? throw new InvalidOperationException(
                                "A blocked audit export result has no failure class."),
                            attempt.DetailCode,
                            attempt.ResponseDigest,
                            configurationRetry),
                    _ => throw new InvalidOperationException(
                        "The audit export transport returned an unknown result."),
                };
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _lifecycle, 0, 1) != 1)
            {
                throw new InvalidOperationException(
                    "The closed audit spool export pump lifecycle is invalid.");
            }
        }
    }

    public void Dispose()
    {
        var prior = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (prior == 2) return;
        if (prior == 1)
        {
            throw new InvalidOperationException(
                "A running closed audit spool export step must finish before disposal.");
        }
        _readerLease.Dispose();
    }

    private AuditClosedSpoolExportStep Acknowledge(
        IAuditClosedSpoolRecordPosition position,
        string detailCode,
        bool hasHealthWarning,
        IAuditClosedSpoolConfigurationRetryAuthorization? configurationRetry = null)
    {
        if (configurationRetry is null)
            _reader.Acknowledge(position, _configurationIdentity);
        else
            _reader.AcknowledgeConfigurationRetry(configurationRetry);
        _initialized = false;
        _next = null;
        _blocked = null;

        var next = _reader.ReadNext(position);
        if (next is not null)
        {
            _next = next;
            _initialized = true;
            return new AuditClosedSpoolExportStep(
                AuditClosedSpoolExportStepKind.Advanced,
                position.EventId,
                detailCode,
                HasHealthWarning: hasHealthWarning);
        }

        var completion = ResolveCurrent();
        if (completion is AuditClosedSpoolRecovery.PrefixEnd prefixEnd)
        {
            if (_mode != AuditClosedSpoolExportMode.ClosedPrefix)
            {
                throw new IOException(
                    "The acknowledged complete audit spool resolved as a prefix.");
            }
            _prefixEnd = prefixEnd.Position;
            _complete = true;
            return PrefixComplete(
                prefixEnd.Position,
                position.EventId,
                detailCode,
                hasHealthWarning);
        }
        if (completion is not AuditClosedSpoolRecovery.ChainEnd end ||
            _mode == AuditClosedSpoolExportMode.ClosedPrefix)
        {
            throw new IOException(
                "The acknowledged closed audit spool did not resolve to its exact end.");
        }
        _reader.MarkChainComplete(end.Position);
        _complete = true;
        return ChainComplete(position.EventId, detailCode, hasHealthWarning);
    }

    private AuditClosedSpoolRecovery ResolveCurrent() => _mode switch
    {
        AuditClosedSpoolExportMode.CompleteChain => _reader.ResolveCheckpoint(),
        AuditClosedSpoolExportMode.ClosedPrefix => _reader.ResolveClosedPrefix(
            _rotation ?? throw new IOException(
                "The closed-prefix exporter lost its rotation capability.")),
        AuditClosedSpoolExportMode.WriterClosedChain => _reader.ResolveAfterWriterClosed(
            _writerClosed ?? throw new IOException(
                "The writer-closed exporter lost its closure capability.")),
        _ => throw new IOException("The closed audit spool exporter mode is invalid."),
    };

    private AuditClosedSpoolExportStep PersistBlock(
        IAuditClosedSpoolRecordPosition position,
        AuditExportFailureClass failureClass,
        string detailCode,
        string? responseDigest,
        IAuditClosedSpoolConfigurationRetryAuthorization? configurationRetry = null)
    {
        try
        {
            DateTimeOffset firstFailureUtc;
            if (configurationRetry is null)
            {
                firstFailureUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                _reader.PersistBlock(
                    position,
                    failureClass,
                    detailCode,
                    responseDigest,
                    firstFailureUtc,
                    _configurationIdentity);
            }
            else
            {
                var authorizedBlock = _blocked;
                if (authorizedBlock is null ||
                    authorizedBlock.Spool != position.Spool ||
                    authorizedBlock.ByteOffset != position.StartOffset ||
                    authorizedBlock.Sequence != position.Sequence ||
                    authorizedBlock.EventId != position.EventId ||
                    authorizedBlock.FailureClass != AuditExportFailureClass.Configuration ||
                    !string.Equals(
                        authorizedBlock.ExportConfigurationIdentity,
                        _configurationIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The configuration retry lost its exact durable block.");
                }

                firstFailureUtc = authorizedBlock.FirstFailureUtc;
                _reader.PersistConfigurationRetryBlock(
                    configurationRetry,
                    failureClass,
                    detailCode,
                    responseDigest);
            }
            _blocked = new AuditExportBlockedRecord(
                position.Spool,
                position.StartOffset,
                position.Sequence,
                position.EventId,
                failureClass,
                detailCode,
                responseDigest,
                firstFailureUtc,
                _configurationIdentity);
            return new AuditClosedSpoolExportStep(
                AuditClosedSpoolExportStepKind.Blocked,
                position.EventId,
                detailCode,
                failureClass);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            _faulted = true;
            throw;
        }
    }

    private static AuditExportBlockedRecord ReidentifyConfigurationBlock(
        AuditExportBlockedRecord blocked,
        string configurationIdentity) =>
        new(
            blocked.Spool,
            blocked.ByteOffset,
            blocked.Sequence,
            blocked.EventId,
            AuditExportFailureClass.Configuration,
            blocked.DetailCode,
            blocked.ResponseDigest,
            blocked.FirstFailureUtc,
            configurationIdentity);

    private static AuditClosedSpoolExportStep ChainComplete(
        Guid? eventId,
        string detailCode,
        bool hasHealthWarning = false) =>
        new(
            AuditClosedSpoolExportStepKind.ChainComplete,
            eventId,
            detailCode,
            HasHealthWarning: hasHealthWarning);

    private static AuditClosedSpoolExportStep PrefixComplete(
        IAuditClosedSpoolPrefixEndPosition prefixEnd,
        Guid? eventId,
        string detailCode,
        bool hasHealthWarning = false) =>
        new(
            AuditClosedSpoolExportStepKind.PrefixComplete,
            eventId,
            detailCode,
            HasHealthWarning: hasHealthWarning,
            PrefixEnd: prefixEnd);

    private static void RequireConfigurationIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The audit export configuration identity is invalid.",
                nameof(value));
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or
            AccessViolationException or AppDomainUnloadedException;
}
