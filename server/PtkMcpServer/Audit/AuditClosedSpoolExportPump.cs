namespace PtkMcpServer.Audit;

internal enum AuditClosedSpoolExportStepKind
{
    Advanced,
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
    bool HasHealthWarning = false);

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
    private IAuditClosedSpoolRecordPosition? _next;
    private AuditExportBlockedRecord? _blocked;
    private bool _initialized;
    private bool _complete;
    private bool _faulted;
    private int _lifecycle;

    internal AuditClosedSpoolExportPump(
        AuditClosedSpoolChainReader reader,
        IAuditOtlpExportTransport transport,
        TimeProvider? timeProvider = null)
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
                return ChainComplete(null, "chain.complete");
            if (!_initialized)
            {
                try
                {
                    var initial = _reader.ResolveCheckpoint();
                    if (initial.EndPosition is { } initialEnd)
                    {
                        _reader.MarkChainComplete(initialEnd);
                        _complete = true;
                        return ChainComplete(null, "chain.complete");
                    }

                    _next = initial.NextRecord
                        ?? throw new IOException(
                            "The closed audit spool recovery lost its next record.");
                    _blocked = initial.BlockedRecord;
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

        var completion = _reader.ResolveCheckpoint();
        if (completion.NextRecord is not null || completion.EndPosition is not { } end)
        {
            throw new IOException(
                "The acknowledged closed audit spool did not resolve to its exact end.");
        }
        _reader.MarkChainComplete(end);
        _complete = true;
        return ChainComplete(position.EventId, detailCode, hasHealthWarning);
    }

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
