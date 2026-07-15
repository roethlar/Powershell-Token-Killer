using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PtkSiemReceiver.Ingest;

internal static class ReceiverCertificateLoader
{
    internal static X509Certificate2 LoadServerCertificate(
        string certificatePath,
        string privateKeyPath)
    {
        try
        {
            using var loaded = X509Certificate2.CreateFromPemFile(
                certificatePath,
                privateKeyPath);
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
            throw new SiemReceiverStartupException("server_certificate", exception);
        }
    }

    internal static IReadOnlyList<X509Certificate2> LoadAuthorities(
        IReadOnlyList<string> bundlePaths)
    {
        var authorities = new List<X509Certificate2>();
        try
        {
            foreach (var path in bundlePaths)
            {
                var bundle = new X509Certificate2Collection();
                bundle.ImportFromPemFile(path);
                foreach (var certificate in bundle)
                {
                    if (!certificate.Extensions.OfType<X509BasicConstraintsExtension>()
                            .Any(extension => extension.CertificateAuthority))
                    {
                        certificate.Dispose();
                        throw new SiemReceiverStartupException("client_ca_bundle");
                    }
                    authorities.Add(certificate);
                }
                if (bundle.Count == 0)
                    throw new SiemReceiverStartupException("client_ca_bundle");
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
            throw new SiemReceiverStartupException("client_ca_bundle", exception);
        }
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
