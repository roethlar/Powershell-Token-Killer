namespace PtkMcpServer.Audit;

/// <summary>
/// Lazily opens protected evidence storage so a broken evidence path does not
/// prevent the supervisor-only emergency state diagnostic from resolving. A
/// failed store is discarded and retried only through an explicit recovery
/// probe on a later script-bearing call.
/// </summary>
internal sealed class ScriptEvidenceStoreProvider
{
    private readonly object _gate = new();
    private readonly AuditOptions _options;
    private readonly Func<ScriptEvidenceStore>? _factory;
    private ScriptEvidenceStore? _store;

    internal ScriptEvidenceStoreProvider(
        AuditOptions options,
        Func<ScriptEvidenceStore>? factory = null)
    {
        _options = options;
        _factory = factory;
    }

    internal ScriptEvidenceStoreProvider(ScriptEvidenceStore store)
    {
        _options = null!;
        _store = store;
        _factory = () => store;
    }

    internal ScriptEvidenceReference Store(string script)
    {
        lock (_gate)
        {
            try
            {
                return GetOrCreateLocked().Store(script);
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (ArgumentException)
            {
                // Deterministic input/size rejection is not a storage outage.
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    internal IScriptEvidencePublication Publish(string script)
        => PublishCore(script, retentionJournal: null);

    internal IScriptEvidencePublication Publish(string script, AuditJournal retentionJournal)
    {
        ArgumentNullException.ThrowIfNull(retentionJournal);
        return PublishCore(script, retentionJournal);
    }

    private IScriptEvidencePublication PublishCore(
        string script,
        AuditJournal? retentionJournal)
    {
        lock (_gate)
        {
            try
            {
                return retentionJournal is null
                    ? GetOrCreateLocked().Publish(script)
                    : GetOrCreateLocked().Publish(script, retentionJournal);
            }
            catch (AuditUnavailableException)
            {
                throw;
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    internal bool Probe()
    {
        lock (_gate)
        {
            try
            {
                GetOrCreateLocked().Probe();
                return true;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                return false;
            }
        }
    }

    internal IAuditEvidenceAnchorLease MarkAnchored(
        AuditEvidenceAcknowledgmentPosition acknowledgment)
    {
        lock (_gate)
        {
            try
            {
                return GetOrCreateLocked().MarkAnchored(acknowledgment);
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    internal bool ReconcileExistingAwaiting(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        lock (_gate)
        {
            try
            {
                // Preserve lazy evidence storage when no artifact root has
                // ever existed. Startup is single-threaded before admission;
                // a later publication installs _store and makes periodic
                // reconciliation take the full protected path.
                if (_store is null && !EntryExists(_options.EvidenceDirectory))
                    return true;
                return GetOrCreateLocked().ReconcileAwaiting(journal);
            }
            catch (AuditUnavailableException)
            {
                throw;
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    internal bool ReconcileExistingAwaitingBeforeWriter()
    {
        lock (_gate)
        {
            try
            {
                if (_store is null && !EntryExists(_options.EvidenceDirectory))
                    return true;
                return GetOrCreateLocked().ReconcileAwaitingBeforeWriter(_options);
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    internal void RetainEligible(AuditJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        lock (_gate)
        {
            try
            {
                if (_store is null && !EntryExists(_options.EvidenceDirectory))
                    return;
                GetOrCreateLocked().RetainEligible(journal);
            }
            catch (AuditUnavailableException)
            {
                throw;
            }
            catch (ScriptEvidenceStorageException)
            {
                _store = null;
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                _store = null;
                throw new ScriptEvidenceStorageException();
            }
        }
    }

    private static bool EntryExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null || Directory.Exists(path);
    }

    private ScriptEvidenceStore GetOrCreateLocked() =>
        _store ??= _factory?.Invoke() ?? new ScriptEvidenceStore(_options);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
