using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PtkSiemReceiver.Ingest;

internal static class ReceiverCertificateLoader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static X509Certificate2 LoadServerCertificate(
        ReadOnlySpan<byte> certificatePem,
        ReadOnlySpan<byte> privateKeyPem)
    {
        char[]? certificateCharacters = null;
        char[]? privateKeyCharacters = null;
        try
        {
            certificateCharacters = DecodePem(certificatePem);
            privateKeyCharacters = DecodePem(privateKeyPem);
            using var loaded = X509Certificate2.CreateFromPem(
                certificateCharacters,
                privateKeyCharacters);
            var pkcs12 = loaded.Export(X509ContentType.Pkcs12, string.Empty);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    string.Empty,
                    X509KeyStorageFlags.DefaultKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new SiemReceiverStartupException("server_certificate");
        }
        finally
        {
            Clear(certificateCharacters);
            Clear(privateKeyCharacters);
        }
    }

    internal static IReadOnlyList<X509Certificate2> LoadAuthorities(
        IReadOnlyList<byte[]> bundleBytes)
    {
        var authorities = new List<X509Certificate2>();
        try
        {
            foreach (var bytes in bundleBytes)
            {
                var bundle = new X509Certificate2Collection();
                char[]? characters = null;
                var transferred = false;
                try
                {
                    characters = DecodePem(bytes);
                    bundle.ImportFromPem(characters);
                    if (bundle.Count == 0)
                        throw new SiemReceiverStartupException("client_ca_bundle");
                    foreach (var certificate in bundle.Cast<X509Certificate2>())
                    {
                        if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                                .Any(extension => extension.CertificateAuthority))
                        {
                            throw new SiemReceiverStartupException("client_ca_bundle");
                        }
                    }
                    authorities.AddRange(bundle.Cast<X509Certificate2>());
                    transferred = true;
                }
                finally
                {
                    Clear(characters);
                    if (!transferred)
                    {
                        foreach (var certificate in bundle.Cast<X509Certificate2>())
                            certificate.Dispose();
                    }
                }
            }

            if (authorities.Count == 0)
                throw new SiemReceiverStartupException("client_ca_bundle");
            return authorities;
        }
        catch (SiemReceiverStartupException)
        {
            foreach (var authority in authorities) authority.Dispose();
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            foreach (var authority in authorities) authority.Dispose();
            throw new SiemReceiverStartupException("client_ca_bundle");
        }
    }

    private static char[] DecodePem(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[StrictUtf8.GetCharCount(bytes)];
        StrictUtf8.GetChars(bytes, characters);
        return characters;
    }

    private static void Clear(char[]? characters)
    {
        if (characters is not null)
            Array.Clear(characters);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal static class ClientCertificateValidator
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    internal static bool Validate(
        X509Certificate2? certificate,
        X509Chain? providedChain,
        SslPolicyErrors errors,
        IReadOnlyList<X509Certificate2> customRoots,
        X509RevocationMode revocationMode)
    {
        if (certificate is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None ||
            !HasClientAuthenticationEku(certificate))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        using var chain = new X509Chain();
        ConfigurePolicy(
            chain.ChainPolicy,
            customRoots,
            providedChain,
            revocationMode);
        return chain.Build(certificate);
    }

    internal static void ConfigurePolicy(
        X509ChainPolicy policy,
        IReadOnlyList<X509Certificate2> customRoots,
        X509Chain? providedChain,
        X509RevocationMode revocationMode)
    {
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.RevocationMode = revocationMode;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.VerificationFlags = X509VerificationFlags.NoFlag;
        policy.DisableCertificateDownloads = true;
        policy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
        foreach (var root in customRoots) policy.CustomTrustStore.Add(root);
        if (providedChain is not null)
        {
            foreach (var element in providedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                policy.ExtraStore.Add(element.Certificate);
        }
    }

    private static bool HasClientAuthenticationEku(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        return extensions.Length == 1 &&
               extensions[0].EnhancedKeyUsages.Cast<Oid>()
                   .Any(oid => string.Equals(oid.Value, ClientAuthenticationOid, StringComparison.Ordinal));
    }
}

internal sealed class SiemReceiverStartupException : Exception
{
    internal SiemReceiverStartupException(string failureCode, Exception? innerException = null)
        : base($"siem_receiver_startup_failed: {failureCode}", innerException)
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

internal sealed class ReceiverTrustStore : IDisposable
{
    private int _disposed;

    internal ReceiverTrustStore(IReadOnlyList<X509Certificate2> authorities)
    {
        Authorities = authorities;
    }

    internal IReadOnlyList<X509Certificate2> Authorities { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var authority in Authorities) authority.Dispose();
    }
}
