// Kun i "legacy"-bygget (net8.0, til Intel-Mac'er med macOS 10.15–11): de få .NET 9/10-API'er, Shared bruger, efterlignet.
#if !NET9_0_OR_GREATER
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace System.Threading
{
    /// <summary>System.Threading.Lock (.NET 9) som en almindelig Monitor-lås.</summary>
#pragma warning disable CS9216   // netop Monitor-låsen er meningen her
    public sealed class Lock
    {
        public Scope EnterScope() { Monitor.Enter(this); return new Scope(this); }
        public ref struct Scope
        {
            private readonly Lock _l;
            internal Scope(Lock l) => _l = l;
            public void Dispose() => Monitor.Exit(_l);
        }
    }
#pragma warning restore CS9216
}

namespace System.Security.Cryptography.X509Certificates
{
    public static class X509CertificateLoader
    {
        public static X509Certificate2 LoadCertificate(byte[] data) => new(data);
    }

    public enum Pkcs12ExportPbeParameters { Default, Pkcs12TripleDesSha1, Pbes2Aes256Sha256 }
}

namespace Fleetdeck.Shared
{
    public static class Net8Polyfills
    {
        extension(Convert)
        {
            public static string ToHexStringLower(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
            public static string ToHexStringLower(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
        }

        extension(X509Certificate2 c)
        {
            public byte[] ExportPkcs12(Pkcs12ExportPbeParameters _, string? password) => c.Export(X509ContentType.Pkcs12, password);
        }
    }
}
#endif
