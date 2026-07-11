using System.ComponentModel;
using System.Text;

namespace PtkMcpServer.Audit;

internal static class AuditJournalFactory
{
    private const string HostIdentityFileName = "host.id";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AuditJournal Open(
        AuditOptions options,
        AuditHealth health,
        string producerVersion,
        string? binaryDigest = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<DateTimeOffset, Guid>? uuidV7Factory = null,
        Func<FileAuditSinkFaultPoint, int, bool>? sinkFaultInjector = null,
        Action<string>? hostIdentityReadCompletedForTests = null,
        Action? hostIdentityDestinationCheckedForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);

        var root = SecureAuditStorage.PrepareRoot(options.RootDirectory);
        _ = SecureAuditStorage.PrepareRoot(options.SpoolDirectory);
        var hostId = LoadOrCreateHostId(
            root,
            hostIdentityReadCompletedForTests,
            hostIdentityDestinationCheckedForTests);
        var supervisorBootId = Guid.NewGuid();
        var sink = new FileAuditJournalSink(options, supervisorBootId, utcNow, sinkFaultInjector);
        try
        {
            return new AuditJournal(
                options,
                health,
                sink,
                producerVersion,
                binaryDigest,
                hostId,
                supervisorBootId,
                utcNow,
                uuidV7Factory);
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    private static Guid LoadOrCreateHostId(
        string root,
        Action<string>? readCompletedForTests,
        Action? destinationCheckedForTests)
    {
        var publishedPath = Path.Combine(root, HostIdentityFileName);
        if (PathExists(publishedPath))
            return ReadHostId(publishedPath, readCompletedForTests);

        var hostId = Guid.NewGuid();
        var temporaryPath = Path.Combine(root, $".{HostIdentityFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
            {
                var bytes = Encoding.ASCII.GetBytes(hostId.ToString("D") + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    publishedPath,
                    root,
                    destinationCheckedForTests);
                return hostId;
            }
            catch (Exception exception) when (
                IsConcurrentPublishCollision(exception) &&
                PathExists(publishedPath))
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                return ReadHostId(publishedPath, readCompletedForTests);
            }
        }
        catch
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            throw;
        }
    }

    private static Guid ReadHostId(
        string path,
        Action<string>? readCompletedForTests)
    {
        SecureAuditStorage.VerifyProtectedFile(path);

        byte[] bytes;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 128,
                   FileOptions.SequentialScan))
        {
            if (stream.Length != 37)
                throw new IOException("The persisted audit host identity is invalid.");
            bytes = new byte[37];
            stream.ReadExactly(bytes);
        }

        readCompletedForTests?.Invoke(path);
        SecureAuditStorage.VerifyProtectedFile(path);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new IOException("The persisted audit host identity is invalid.");
        }

        if (text[^1] != '\n' ||
            !Guid.TryParseExact(text.AsSpan(0, 36), "D", out var hostId) ||
            hostId.ToString("D") != text[..36] ||
            text[14] != '4' ||
            text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            throw new IOException("The persisted audit host identity is invalid.");
        }
        return hostId;
    }

    private static bool PathExists(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists || file.LinkTarget is not null;
    }

    internal static bool IsConcurrentPublishCollision(Exception exception) =>
        exception is IOException ||
        exception is Win32Exception
        {
            NativeErrorCode: ErrorUnixFileExists or ErrorFileExists or ErrorAlreadyExists,
        };

    private const int ErrorUnixFileExists = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
}
