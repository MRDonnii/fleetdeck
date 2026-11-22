using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Fleetdeck.Server.Services;

/// <summary>Det, der ligger krypteret i nøgleboksen. Må aldrig serialiseres til et svar, en log eller et job.</summary>
public sealed class VaultSecret
{
    public string? Password { get; set; }
    public string? PrivateKey { get; set; }
    public string? Passphrase { get; set; }
}

/// <summary>
/// Nøgleboksen: AES-256-GCM med en hovednøgle i <c>vault/vault.key</c>, som selv er beskyttet med serverens DataProtection-nøgle.
/// Mappen ligger bevidst uden for <c>keys/</c>: selvopdateringens ukrypterede kopi (database + keys) kan derfor ikke låse hemmelighederne op.
/// Den krypterede .fdbackup tager mappen med.
/// </summary>
public sealed class TerminalVault(Paths paths, IDataProtectionProvider dp)
{
    public const string Dir = "vault";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("fleetdeck-terminal-vault-v1");
    private readonly IDataProtector _protector = dp.CreateProtector("Fleetdeck.Terminal.VaultKey");
    private readonly Lock _lock = new();
    private byte[]? _key;

    private byte[] Key()
    {
        lock (_lock)
        {
            if (_key is not null) return _key;
            var dir = Path.Combine(paths.Data, Dir); var file = Path.Combine(dir, "vault.key");
            if (File.Exists(file)) return _key = _protector.Unprotect(Convert.FromBase64String(File.ReadAllText(file).Trim()));
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var key = RandomNumberGenerator.GetBytes(32);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, Convert.ToBase64String(_protector.Protect(key)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, file);
            return _key = key;
        }
    }

    public string Seal(VaultSecret secret)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(), 16)) aes.Encrypt(nonce, plain, cipher, tag, Aad);
        CryptographicOperations.ZeroMemory(plain);
        return "v1." + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public VaultSecret Open(string sealedText)
    {
        if (!sealedText.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Ukendt format");
        var b = Convert.FromBase64String(sealedText[3..]);
        if (b.Length < 28) throw new CryptographicException("For kort");
        var plain = new byte[b.Length - 28];
        using (var aes = new AesGcm(Key(), 16)) aes.Decrypt(b.AsSpan(0, 12), b.AsSpan(28), b.AsSpan(12, 16), plain, Aad);
        try { return JsonSerializer.Deserialize<VaultSecret>(plain) ?? new VaultSecret(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

public sealed record SshKeyInfo(string PublicKey, string Fingerprint);
public sealed record GeneratedSshKey(string PrivateKey, string PublicKey, string Fingerprint);

public static partial class SshKeys
{
    [System.Text.RegularExpressions.GeneratedRegex(@"[^A-Za-z0-9._@-]")] private static partial System.Text.RegularExpressions.Regex CommentRx();

    /// <summary>Kommentaren ender i en kommando på værten, så kun ufarlige tegn er med.</summary>
    public static string Comment(string name) => "fleetdeck-" + CommentRx().Replace(name, "-");

    public static string Fingerprint(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');

    public static GeneratedSshKey Generate(string name)
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var blob = OpenSshPublicKeyUtilities.EncodePublicKey((Ed25519PublicKeyParameters)pair.Public);
        var priv = OpenSshPrivateKeyUtilities.EncodePrivateKey((Ed25519PrivateKeyParameters)pair.Private);
        var b64 = Convert.ToBase64String(priv, Base64FormattingOptions.None);
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < b64.Length; i += 70) pem.Append(b64.AsSpan(i, Math.Min(70, b64.Length - i))).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return new GeneratedSshKey(pem.ToString(), $"ssh-ed25519 {Convert.ToBase64String(blob)} {Comment(name)}", Fingerprint(blob));
    }

    /// <summary>Læser en indsat privat nøgle. Null, hvis den er ugyldig, eller løsenet er forkert.</summary>
    public static SshKeyInfo? Inspect(string? privateKey, string? passphrase, string name)
    {
        if (string.IsNullOrWhiteSpace(privateKey) || privateKey.Length > 32_000) return null;
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(privateKey.Replace("\r\n", "\n").Trim() + "\n"));
            using var file = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, passphrase);
            var alg = (KeyHostAlgorithm)file.HostKeyAlgorithms.First();
            return new SshKeyInfo($"{alg.Name} {Convert.ToBase64String(alg.Data)} {Comment(name)}", Fingerprint(alg.Data));
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or ArgumentException or InvalidOperationException or FormatException or IOException or NotSupportedException or CryptographicException) { return null; }
    }
}
