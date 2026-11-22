using System.Security.Cryptography;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Serverens nøgle til at signere agentfiler. Laves første gang og gemmes i data/keys (kun læsbar for serveren).</summary>
public sealed class SigningKeys : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, (DateTime Mtime, string Hash, string Signature)> _cache = [];
    public string PublicPem { get; }
    public string Fingerprint { get; }

    public SigningKeys(Paths paths, ILogger<SigningKeys> log)
    {
        var dir = Path.Combine(paths.Data, "keys");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-signing.pem");
        if (File.Exists(file)) _key.ImportFromPem(File.ReadAllText(file));
        else
        {
            File.WriteAllText(file, _key.ExportECPrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            log.LogInformation("Ny nøgle til signering af agenter oprettet");
        }
        PublicPem = _key.ExportSubjectPublicKeyInfoPem();
        Fingerprint = AgentSigning.Fingerprint(PublicPem);
    }

    /// <summary>SHA-256 (hex) og signatur for en agentfil; regnes kun igen, når filen ændres.</summary>
    public (string Hash, string Signature) For(string file)
    {
        var mt = File.GetLastWriteTimeUtc(file);
        lock (_cache)
        {
            if (_cache.TryGetValue(file, out var c) && c.Mtime == mt) return (c.Hash, c.Signature);
            byte[] hash;
            using (var fs = File.OpenRead(file)) hash = SHA256.HashData(fs);
            var r = (mt, Convert.ToHexStringLower(hash), AgentSigning.Sign(_key, hash));
            _cache[file] = r;
            return (r.Item2, r.Item3);
        }
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Serverens nøgle til signerede check-in-svar (jobs, opdatering, flytning). Bevidst en anden nøgle end agentfilernes,
/// så en signatur over et svar aldrig kan bruges som signatur over en "agentfil" (og omvendt).
/// Ligger i data/keys som de andre nøgler og skal med i en backup af serveren: mistes den, afviser agenter med pinnet nøgle alle jobs.
/// </summary>
public sealed class JobSigningKey : IDisposable
{
    public const string FileName = "job-signing.pem";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicPem { get; }
    public string Fingerprint { get; }

    public JobSigningKey(Paths paths, ILogger<JobSigningKey> log) : this(Path.Combine(paths.Data, "keys"), log) { }

    public JobSigningKey(string keyDir, ILogger? log = null)
    {
        Directory.CreateDirectory(keyDir);
        var file = Path.Combine(keyDir, FileName);
        if (File.Exists(file)) _key.ImportFromPem(File.ReadAllText(file));
        else
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, _key.ExportECPrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, file);
            log?.LogInformation("Ny nøgle til signerede jobs oprettet");
        }
        PublicPem = _key.ExportSubjectPublicKeyInfoPem();
        Fingerprint = AgentSigning.Fingerprint(PublicPem);
    }

    public CommandEnvelope Sign(SignedCommands c) => CommandSigning.Sign(_key, PublicPem, c);

    public void Dispose() => _key.Dispose();
}
