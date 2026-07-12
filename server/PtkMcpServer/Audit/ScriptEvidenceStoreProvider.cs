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
    {
        lock (_gate)
        {
            try
            {
                return GetOrCreateLocked().Publish(script);
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

    private ScriptEvidenceStore GetOrCreateLocked() =>
        _store ??= _factory?.Invoke() ?? new ScriptEvidenceStore(_options);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
