using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Audit;

public sealed record ScriptEvidenceReference(
    string EvidenceId,
    string ScriptDigest,
    int ByteLength);

public sealed class ScriptEvidenceStorageException : IOException
{
    internal ScriptEvidenceStorageException()
        : base("Protected script evidence storage failed.")
    {
    }
}

/// <summary>
/// Writes the exact strict-UTF-8 bytes of submitted scripts into a protected
/// evidence directory. A reference is returned only after the payload and its
/// directory entry have been durably committed.
/// </summary>
public sealed class ScriptEvidenceStore
{
    public const int MaximumScriptBytes = 131_072;
    private const string QuotaLockFileName = ".ptk-evidence-quota.lock";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _root;
    private readonly int _maximumBytes;
    private readonly long _aggregateBytes;
    private readonly TimeSpan _retentionAge;
    private readonly AuditProtectionMode _protectionMode;
    private readonly string _quotaLockPath;
    private readonly object _gate = new();
    private readonly Action<SecureAuditStorageFaultStage>? _faultInjector;

    public ScriptEvidenceStore(
        string absoluteRootPath,
        Action<SecureAuditStorageFaultStage>? faultInjector = null)
        : this(
            absoluteRootPath,
            MaximumScriptBytes,
            AuditOptions.DefaultEvidenceAggregateBytes,
            AuditOptions.DefaultEvidenceRetentionAge,
            AuditProtectionMode.LocalOnly,
            faultInjector)
    {
    }

    internal ScriptEvidenceStore(
        AuditOptions options,
        Action<SecureAuditStorageFaultStage>? faultInjector = null)
        : this(
            options.EvidenceDirectory,
            options.MaxEvidenceBytes,
            options.EvidenceAggregateBytes,
            options.EvidenceRetentionAge,
            options.ProtectionMode,
            faultInjector)
    {
    }

    private ScriptEvidenceStore(
        string absoluteRootPath,
        int maximumBytes,
        long aggregateBytes,
        TimeSpan retentionAge,
        AuditProtectionMode protectionMode,
        Action<SecureAuditStorageFaultStage>? faultInjector)
    {
        _faultInjector = faultInjector;
        _maximumBytes = maximumBytes;
        _aggregateBytes = aggregateBytes;
        _retentionAge = retentionAge;
        _protectionMode = protectionMode;
        try
        {
            _root = SecureAuditStorage.PrepareRoot(absoluteRootPath);
            _quotaLockPath = Path.Combine(_root, QuotaLockFileName);
            EnsureQuotaLockFile();
            lock (_gate)
            using (AcquireQuotaLock())
                _ = SweepAndMeasure(requiredPayloadBytes: null);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new ScriptEvidenceStorageException();
        }
    }

    public ScriptEvidenceReference Store(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        byte[] bytes;
        try
        {
            var byteCount = StrictUtf8.GetByteCount(script);
            if (byteCount > _maximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(script),
                    "Script evidence exceeds the strict UTF-8 byte limit.");
            }

            bytes = StrictUtf8.GetBytes(script);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException(
                "Script evidence must be valid strict UTF-8 logical text.",
                nameof(script));
        }

        string? temporaryPath = null;
        try
        {
            lock (_gate)
            {
                using var quota = AcquireQuotaLock();
                _ = SweepAndMeasure(bytes.Length);
                var evidenceId = Guid.NewGuid().ToString("D");
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var reference = new ScriptEvidenceReference(evidenceId, digest, bytes.Length);
                temporaryPath = Path.Combine(_root, $".{evidenceId}.tmp");
                var publishedPath = Path.Combine(_root, $"{evidenceId}.{digest}.script");

                using (var stream = SecureAuditStorage.CreateExclusiveFile(
                           temporaryPath,
                           preallocationSize: bytes.Length))
                {
                    InvokeFault(SecureAuditStorageFaultStage.Write);
                    stream.Write(bytes);

                    InvokeFault(SecureAuditStorageFaultStage.Flush);
                    stream.Flush(flushToDisk: true);
                }

                InvokeFault(SecureAuditStorageFaultStage.Publish);
                SecureAuditStorage.PublishAtomically(temporaryPath, publishedPath, _root);
                return reference;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (temporaryPath is not null)
            {
                SecureAuditStorage.TryDelete(temporaryPath);
            }

            throw new ScriptEvidenceStorageException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal void Probe()
    {
        lock (_gate)
        {
            using var quota = AcquireQuotaLock();
            _ = SweepAndMeasure(requiredPayloadBytes: null);
            SecureAuditStorage.ProbeWritableDirectory(_root);
        }
    }

    internal IDisposable AcquireQuotaLockForTests() => AcquireQuotaLock();

    private long SweepAndMeasure(int? requiredPayloadBytes)
    {
        if (requiredPayloadBytes is < 0 || requiredPayloadBytes > _maximumBytes)
            throw new IOException("Evidence reservation exceeds its configured bound.");
        // Charge every artifact at its configured worst case, including an
        // empty script. Besides reserving payload capacity before publish,
        // this freezes a finite artifact-count ceiling and prevents an
        // unbounded population of zero/tiny files and full-directory scans.
        var requiredCharge = requiredPayloadBytes.HasValue ? _maximumBytes : 0L;

        SecureAuditStorage.VerifyProtectedDirectory(_root);
        var retained = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            if (Directory.Exists(path))
                throw new IOException("The evidence root contains an unexpected directory.");
            SecureAuditStorage.VerifyProtectedFile(path);
            var file = new FileInfo(path);
            if (string.Equals(file.Name, QuotaLockFileName, StringComparison.Ordinal))
                continue;
            if (IsTemporaryName(file.Name))
            {
                File.Delete(file.FullName);
                continue;
            }
            if (!TryParseEvidenceName(file.Name, out var expectedDigest) || file.Length > _maximumBytes)
                throw new IOException("The evidence root contains an invalid artifact.");
            using (var stream = new FileStream(
                       file.FullName,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 16 * 1024,
                       FileOptions.SequentialScan))
            {
                var actualDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
                    throw new IOException("The evidence artifact digest does not match its protected name.");
            }
            retained.Add(file);
        }

        var total = checked((long)retained.Count * _maximumBytes);
        if (total <= _aggregateBytes - requiredCharge) return total;
        // Published evidence is an already-accepted audit obligation. Local
        // journal retention is segment-based, so independently deleting an
        // evidence file by age or quota can strand a still-retained dispatch
        // reference. Coordinated journal/evidence GC belongs to the exporter
        // slice; until then every protection mode fails closed at capacity.
        throw new IOException("Evidence capacity is exhausted.");
    }

    private static bool TryParseEvidenceName(string name, out string digest)
    {
        digest = string.Empty;
        if (!name.EndsWith(".script", StringComparison.Ordinal)) return false;
        var stem = name[..^7];
        if (stem.Length != 36 + 1 + 64 || stem[36] != '.') return false;
        var id = stem[..36];
        digest = stem[37..];
        return Guid.TryParseExact(id, "D", out var value) &&
               value.ToString("D") == id &&
               id[14] == '4' &&
               id[19] is '8' or '9' or 'a' or 'b' &&
               digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsTemporaryName(string name)
    {
        const string probePrefix = ".ptk-audit-probe-";
        if (name.StartsWith(probePrefix, StringComparison.Ordinal) &&
            name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            var probeId = name[probePrefix.Length..^4];
            if (Guid.TryParseExact(probeId, "N", out _)) return true;
        }
        if (!name.StartsWith(".", StringComparison.Ordinal) ||
            !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var id = name[1..^4];
        return Guid.TryParseExact(id, "D", out var value) && value.ToString("D") == id;
    }

    private IDisposable AcquireQuotaLock()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                // An exclusive open is backed by the filesystem object, so
                // case/alias spellings converge on one lock unlike a mutex
                // named from path text. Disposing the handle releases it after
                // a crash on every supported platform.
                var stream = new FileStream(
                    _quotaLockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                return new FileLockLease(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void EnsureQuotaLockFile()
    {
        if (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
            return;
        }

        try
        {
            using var stream = SecureAuditStorage.CreateExclusiveFile(_quotaLockPath);
            stream.WriteByte(0x50);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(_quotaLockPath))
        {
            SecureAuditStorage.VerifyProtectedFile(_quotaLockPath);
        }
    }

    private sealed class FileLockLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _stream, null);
            if (value is null) return;
            value.Dispose();
        }
    }

    private void InvokeFault(SecureAuditStorageFaultStage stage) => _faultInjector?.Invoke(stage);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
