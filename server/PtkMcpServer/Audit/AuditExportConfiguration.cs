using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace PtkMcpServer.Audit;

internal sealed class AuditExportConfigurationException : Exception
{
    internal AuditExportConfigurationException(string failureCode)
        : base($"audit_export_configuration_invalid: {failureCode}")
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

internal sealed class AuditExportHeader
{
    internal AuditExportHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    internal string Name { get; }

    internal string Value { get; }

    public override string ToString() => Name;
}

internal sealed class AuditExportOptions : IDisposable
{
    internal const string ProtocolName = "http/protobuf";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private int _disposed;

    internal AuditExportOptions(
        string endpointText,
        Uri endpoint,
        IReadOnlyList<AuditExportHeader> headers,
        IReadOnlyList<X509Certificate2> customCertificateAuthorities,
        X509Certificate2? clientCertificate,
        string configurationIdentity)
    {
        EndpointText = endpointText;
        Endpoint = endpoint;
        Headers = Array.AsReadOnly(headers.ToArray());
        CustomCertificateAuthorities = Array.AsReadOnly(customCertificateAuthorities.ToArray());
        ClientCertificate = clientCertificate;
        ConfigurationIdentity = configurationIdentity;
    }

    internal string EndpointText { get; }

    internal Uri Endpoint { get; }

    internal IReadOnlyList<AuditExportHeader> Headers { get; }

    internal IReadOnlyList<X509Certificate2> CustomCertificateAuthorities { get; }

    internal X509Certificate2? ClientCertificate { get; }

    internal string ConfigurationIdentity { get; }

    public override string ToString() => $"anchored export configuration {ConfigurationIdentity}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var certificate in CustomCertificateAuthorities) certificate.Dispose();
        ClientCertificate?.Dispose();
    }
}

internal static class AuditExportConfigurationLoader
{
    internal const int MaximumConfigurationBytes = 256 * 1024;
    internal const int MaximumAssetBytes = 4 * 1024 * 1024;
    internal const int MaximumHeaders = 64;
    internal const int MaximumEndpointBytes = 8 * 1024;
    internal const int MaximumHeaderValueBytes = 64 * 1024;

    private const string SchemaVersion = "ptk.export-config/1";
    private const string ProtectionMode = "anchored";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    private const string AnyExtendedKeyUsageOid = "2.5.29.37.0";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExpectedProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "protection_mode",
        "endpoint",
        "headers",
        "ca_file",
        "client_certificate_file",
        "client_private_key_file",
    };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Type",
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "User-Agent",
        "Content-Encoding",
        "Accept-Encoding",
    };

    internal static AuditExportOptions Load(
        string configurationPath,
        string auditRoot,
        DateTimeOffset? utcNow = null)
    {
        byte[]? configurationBytes = null;
        byte[]? caBytes = null;
        byte[]? certificateBytes = null;
        byte[]? privateKeyBytes = null;
        byte[]? identityKey = null;
        List<X509Certificate2> customRoots = [];
        X509Certificate2? clientCertificate = null;
        var transferred = false;
        try
        {
            if (string.IsNullOrWhiteSpace(configurationPath) ||
                !Path.IsPathFullyQualified(configurationPath))
            {
                Fail("config_path");
            }

            configurationBytes = SecureAuditStorage.ReadProtectedFile(
                Path.GetFullPath(configurationPath),
                MaximumConfigurationBytes,
                requireProtectedParent: true,
                verifyWithoutMutation: true);
            if (configurationBytes.Length == 0 || HasUtf8Bom(configurationBytes))
                Fail("config_json");

            var parsed = Parse(configurationBytes);
            var endpoint = ValidateEndpoint(parsed.Endpoint);
            var headers = ValidateHeaders(parsed.Headers);
            ValidatePemPair(parsed.ClientCertificateFile, parsed.ClientPrivateKeyFile);
            if (headers.Count == 0 && parsed.ClientCertificateFile is null)
                Fail("authentication_required");

            if (parsed.CaFile is not null)
            {
                caBytes = ReadAsset(parsed.CaFile);
                customRoots = ParseCertificateAuthorities(caBytes);
            }

            if (parsed.ClientCertificateFile is not null)
            {
                certificateBytes = ReadAsset(parsed.ClientCertificateFile);
                privateKeyBytes = ReadAsset(parsed.ClientPrivateKeyFile!);
                clientCertificate = ParseClientCertificate(
                    certificateBytes,
                    privateKeyBytes,
                    utcNow ?? DateTimeOffset.UtcNow);
            }

            identityKey = ExportConfigurationKeyStore.LoadOrCreate(auditRoot);
            var identity = ExportConfigurationIdentity.Compute(
                identityKey,
                new AuditExportConfigurationMaterial(
                    parsed.Endpoint,
                    headers.Select(header => new AuditExportHeaderMaterial(header.Name, header.Value)).ToArray(),
                    caBytes ?? Array.Empty<byte>(),
                    certificateBytes ?? Array.Empty<byte>(),
                    privateKeyBytes ?? Array.Empty<byte>(),
                    AuditExportOptions.ProtocolName,
                    checked((ulong)AuditExportOptions.RequestTimeout.TotalMilliseconds)));

            var options = new AuditExportOptions(
                parsed.Endpoint,
                endpoint,
                headers,
                customRoots,
                clientCertificate,
                identity);
            transferred = true;
            customRoots = [];
            clientCertificate = null;
            return options;
        }
        catch (AuditExportConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditExportConfigurationException("load_failed");
        }
        finally
        {
            Zero(configurationBytes);
            Zero(caBytes);
            Zero(certificateBytes);
            Zero(privateKeyBytes);
            Zero(identityKey);
            if (!transferred)
            {
                foreach (var certificate in customRoots) certificate.Dispose();
                clientCertificate?.Dispose();
            }
        }
    }

    private static ParsedConfiguration Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                Fail("config_json");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ExpectedProperties.Contains(property.Name)) Fail("config_property");
                if (!seen.Add(property.Name)) Fail("config_duplicate_property");
            }
            if (!seen.SetEquals(ExpectedProperties)) Fail("config_property_missing");

            var schemaVersion = RequiredString(document.RootElement, "schema_version");
            var protectionMode = RequiredString(document.RootElement, "protection_mode");
            if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
                Fail("schema_version");
            if (!string.Equals(protectionMode, ProtectionMode, StringComparison.Ordinal))
                Fail("protection_mode");

            return new ParsedConfiguration(
                RequiredString(document.RootElement, "endpoint"),
                ParseHeaders(document.RootElement.GetProperty("headers")),
                NullableString(document.RootElement, "ca_file"),
                NullableString(document.RootElement, "client_certificate_file"),
                NullableString(document.RootElement, "client_private_key_file"));
        }
        catch (AuditExportConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditExportConfigurationException("config_json");
        }
    }

    private static List<AuditExportHeader> ParseHeaders(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail("headers_kind");
        var headers = new List<AuditExportHeader>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (headers.Count >= MaximumHeaders) Fail("headers_limit");
            if (!names.Add(property.Name)) Fail("header_duplicate");
            if (property.Value.ValueKind != JsonValueKind.String) Fail("header_value_kind");
            headers.Add(new AuditExportHeader(property.Name, property.Value.GetString()!));
        }
        return headers;
    }

    private static Uri ValidateEndpoint(string endpoint)
    {
        int byteCount;
        try { byteCount = StrictUtf8.GetByteCount(endpoint); }
        catch (EncoderFallbackException) { Fail("endpoint_utf8"); return null!; }
        if (byteCount == 0 || byteCount > MaximumEndpointBytes ||
            !string.Equals(endpoint, endpoint.Trim(), StringComparison.Ordinal) ||
            endpoint.Any(character => char.IsControl(character) || character == '\\') ||
            endpoint.Contains('?', StringComparison.Ordinal) ||
            endpoint.Contains('#', StringComparison.Ordinal))
        {
            Fail("endpoint");
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            Authority(endpoint).Contains('@', StringComparison.Ordinal))
        {
            Fail("endpoint");
        }
        var rawPath = PathText(endpoint);
        var transportedPath = "/" + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (rawPath.Length != 0 &&
            !string.Equals(rawPath, transportedPath, StringComparison.Ordinal))
        {
            Fail("endpoint");
        }
        return uri;
    }

    private static List<AuditExportHeader> ValidateHeaders(List<AuditExportHeader> headers)
    {
        foreach (var header in headers)
        {
            if (!IsHttpToken(header.Name)) Fail("header_name");
            if (ForbiddenHeaders.Contains(header.Name)) Fail("header_forbidden");
            int valueBytes;
            try { valueBytes = StrictUtf8.GetByteCount(header.Value); }
            catch (EncoderFallbackException) { Fail("header_utf8"); return null!; }
            if (valueBytes == 0 || valueBytes > MaximumHeaderValueBytes ||
                header.Value.Any(character =>
                    character is '\r' or '\n' or '\0' ||
                    (char.IsControl(character) && character != '\t')))
            {
                Fail("header_value");
            }
        }
        return headers;
    }

    private static byte[] ReadAsset(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            Fail("asset_path");
        var bytes = SecureAuditStorage.ReadProtectedFile(
            Path.GetFullPath(path),
            MaximumAssetBytes,
            requireProtectedParent: true,
            verifyWithoutMutation: true);
        if (bytes.Length == 0)
        {
            Zero(bytes);
            Fail("asset_empty");
        }
        return bytes;
    }

    private static List<X509Certificate2> ParseCertificateAuthorities(byte[] bytes)
    {
        var characters = DecodePem(bytes);
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(characters);
            if (collection.Count == 0) Fail("ca_pem");
            var certificates = collection.Cast<X509Certificate2>().ToList();
            foreach (var certificate in certificates)
            {
                var basicConstraints = certificate.Extensions
                    .OfType<X509BasicConstraintsExtension>()
                    .SingleOrDefault();
                if (basicConstraints?.CertificateAuthority != true) Fail("ca_not_authority");
            }
            return certificates;
        }
        catch (AuditExportConfigurationException)
        {
            foreach (var certificate in collection.Cast<X509Certificate2>()) certificate.Dispose();
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            foreach (var certificate in collection.Cast<X509Certificate2>()) certificate.Dispose();
            throw new AuditExportConfigurationException("ca_pem");
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    private static X509Certificate2 ParseClientCertificate(
        byte[] certificateBytes,
        byte[] privateKeyBytes,
        DateTimeOffset utcNow)
    {
        var certificateCharacters = DecodePem(certificateBytes);
        var keyCharacters = DecodePem(privateKeyBytes);
        X509Certificate2? certificate = null;
        try
        {
            certificate = X509Certificate2.CreateFromPem(certificateCharacters, keyCharacters);
            if (!certificate.HasPrivateKey) Fail("client_key_missing");
            var now = utcNow.UtcDateTime;
            if (now < certificate.NotBefore.ToUniversalTime() ||
                now > certificate.NotAfter.ToUniversalTime())
            {
                Fail("client_certificate_time");
            }

            var ekuExtensions = certificate.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .ToArray();
            if (ekuExtensions.Length > 1) Fail("client_certificate_eku");
            if (ekuExtensions.Length == 1 &&
                !ekuExtensions[0].EnhancedKeyUsages.Cast<Oid>().Any(oid =>
                    string.Equals(oid.Value, ClientAuthenticationOid, StringComparison.Ordinal) ||
                    string.Equals(oid.Value, AnyExtendedKeyUsageOid, StringComparison.Ordinal)))
            {
                Fail("client_certificate_eku");
            }

            if (OperatingSystem.IsWindows())
            {
                var normalized = NormalizeWindowsClientCertificate(certificate);
                certificate.Dispose();
                certificate = normalized;
            }

            var result = certificate;
            certificate = null;
            return result;
        }
        catch (AuditExportConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new AuditExportConfigurationException("client_certificate_pem");
        }
        finally
        {
            certificate?.Dispose();
            Array.Clear(certificateCharacters);
            Array.Clear(keyCharacters);
        }
    }

    private static X509Certificate2 NormalizeWindowsClientCertificate(
        X509Certificate2 certificate)
    {
        byte[]? pkcs12 = null;
        try
        {
            // Schannel cannot reliably use the ephemeral key handle produced by
            // CreateFromPem. A PKCS#12 round trip gives Windows a native key
            // association whose temporary key container is tied to disposal of
            // the directly loaded certificate.
            pkcs12 = certificate.Export(X509ContentType.Pkcs12, string.Empty);
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                string.Empty,
                X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            Zero(pkcs12);
        }
    }

    private static char[] DecodePem(byte[] bytes)
    {
        try
        {
            var characters = new char[StrictUtf8.GetCharCount(bytes)];
            StrictUtf8.GetChars(bytes, characters);
            return characters;
        }
        catch (EncoderFallbackException)
        {
            Fail("pem_utf8");
            return null!;
        }
    }

    private static void ValidatePemPair(string? certificatePath, string? keyPath)
    {
        if ((certificatePath is null) != (keyPath is null)) Fail("client_pair");
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String) Fail("config_value_kind");
        return value.GetString()!;
    }

    private static string? NullableString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) Fail("config_value_kind");
        return value.GetString();
    }

    private static bool IsHttpToken(string name)
    {
        if (name.Length == 0) return false;
        foreach (var character in name)
        {
            if (character > 0x7f ||
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }
        return true;
    }

    private static string Authority(string endpoint)
    {
        var start = endpoint.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += 3;
        var end = endpoint.IndexOf('/', start);
        return end < 0 ? endpoint[start..] : endpoint[start..end];
    }

    private static string PathText(string endpoint)
    {
        var start = endpoint.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += 3;
        var path = endpoint.IndexOf('/', start);
        return path < 0 ? string.Empty : endpoint[path..];
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    [DoesNotReturn]
    private static void Fail(string failureCode) =>
        throw new AuditExportConfigurationException(failureCode);

    private sealed record ParsedConfiguration(
        string Endpoint,
        List<AuditExportHeader> Headers,
        string? CaFile,
        string? ClientCertificateFile,
        string? ClientPrivateKeyFile);
}
