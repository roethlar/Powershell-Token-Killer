using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace PtkMcpServer.Audit;

internal sealed record AuditExportHeaderMaterial(string Name, string Value);

internal sealed record AuditExportConfigurationMaterial(
    string Endpoint,
    IReadOnlyList<AuditExportHeaderMaterial> Headers,
    ReadOnlyMemory<byte> CustomCaBytes,
    ReadOnlyMemory<byte> ClientCertificateBytes,
    ReadOnlyMemory<byte> ClientPrivateKeyBytes,
    string Protocol,
    ulong TimeoutMilliseconds,
    string RevocationCheckMode);

internal static class ExportConfigurationIdentity
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("ptk.export-config/2\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Compute(
        ReadOnlySpan<byte> hmacKey,
        AuditExportConfigurationMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (hmacKey.Length != ExportConfigurationKeyStore.KeyBytes)
            throw new ArgumentException("The export configuration HMAC key must be 32 bytes.", nameof(hmacKey));
        ArgumentException.ThrowIfNullOrEmpty(material.Endpoint);
        ArgumentNullException.ThrowIfNull(material.Headers);
        ArgumentException.ThrowIfNullOrEmpty(material.Protocol);
        ArgumentException.ThrowIfNullOrEmpty(material.RevocationCheckMode);

        var normalizedHeaders = material.Headers
            .Select(header => new AuditExportHeaderMaterial(
                NormalizeHeaderName(header.Name),
                header.Value ?? throw new ArgumentException("An export header value is null.", nameof(material))))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ToArray();
        for (var index = 1; index < normalizedHeaders.Length; index++)
        {
            if (string.Equals(
                    normalizedHeaders[index - 1].Name,
                    normalizedHeaders[index].Name,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Export header names must be unique case-insensitively.",
                    nameof(material));
            }
        }

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey);
        hmac.AppendData(Domain);
        AppendFramedString(hmac, material.Endpoint);
        foreach (var header in normalizedHeaders)
        {
            AppendFramedString(hmac, header.Name);
            AppendFramedString(hmac, header.Value);
        }
        AppendFramedBytes(hmac, material.CustomCaBytes.Span);
        AppendFramedBytes(hmac, material.ClientCertificateBytes.Span);
        AppendFramedBytes(hmac, material.ClientPrivateKeyBytes.Span);
        AppendFramedString(hmac, material.Protocol);
        AppendUInt64(hmac, material.TimeoutMilliseconds);
        AppendFramedString(hmac, material.RevocationCheckMode);
        return Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeHeaderName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        for (var index = 0; index < name.Length; index++)
        {
            if (name[index] > 0x7f)
                throw new ArgumentException("Export header names must be ASCII.", nameof(name));
        }
        return name.ToLowerInvariant();
    }

    private static void AppendFramedString(IncrementalHash hmac, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            AppendFramedBytes(hmac, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void AppendFramedBytes(IncrementalHash hmac, ReadOnlySpan<byte> value)
    {
        AppendUInt32(hmac, checked((uint)value.Length));
        hmac.AppendData(value);
    }

    private static void AppendUInt32(IncrementalHash hmac, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        hmac.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hmac, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        hmac.AppendData(bytes);
    }
}

internal static class ExportConfigurationKeyStore
{
    internal const int KeyBytes = 32;
    internal const string FileName = "export-config.hmac.key";

    internal static byte[] LoadOrCreate(
        string auditRoot,
        Action? destinationCheckedForTests = null)
    {
        var root = SecureAuditStorage.PrepareRoot(auditRoot);
        var keyPath = Path.Combine(root, FileName);
        if (File.Exists(keyPath)) return ReadKey(keyPath);
        if (HasExporterStateWithoutKey(root))
            throw new IOException("Exporter state exists without its configuration identity key.");

        var candidate = RandomNumberGenerator.GetBytes(KeyBytes);
        var temporaryPath = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                using (var stream = SecureAuditStorage.CreateExclusiveFile(temporaryPath))
                {
                    stream.Write(candidate);
                    stream.Flush(flushToDisk: true);
                }
                SecureAuditStorage.PublishAtomically(
                    temporaryPath,
                    keyPath,
                    root,
                    destinationCheckedForTests);
                return candidate;
            }
            catch (Exception exception) when (
                File.Exists(keyPath) && exception is IOException or Win32Exception)
            {
                SecureAuditStorage.TryDelete(temporaryPath);
                CryptographicOperations.ZeroMemory(candidate);
                return ReadKey(keyPath);
            }
        }
        catch
        {
            SecureAuditStorage.TryDelete(temporaryPath);
            CryptographicOperations.ZeroMemory(candidate);
            throw;
        }
    }

    private static byte[] ReadKey(string keyPath)
    {
        var key = SecureAuditStorage.ReadProtectedFile(keyPath, KeyBytes);
        if (key.Length == KeyBytes) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new IOException("The export configuration identity key has an invalid length.");
    }

    private static bool HasExporterStateWithoutKey(string root) =>
        Directory.EnumerateFileSystemEntries(root, "export.checkpoint*.json").Any() ||
        Directory.EnumerateFileSystemEntries(root, "export.block*.json").Any();
}
