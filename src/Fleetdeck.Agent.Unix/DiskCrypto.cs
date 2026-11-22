using System.Diagnostics;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Diskkryptering på Mac (FileVault) og Linux (LUKS): kun status i helbredet.
/// FileVault-nøglen kan kun læses, når den laves; det gør jobbet "Ny gendannelsesnøgle til Fleetdeck" (<see cref="RotateFileVault"/>).
/// </summary>
public static class DiskCrypto
{
    public static EncryptionInfo? Status()
    {
        if (Unraid.Is) return null;   // Unraid krypterer pr. array-disk; det vises under Unraid
        return Mac.Is ? FileVault() : Luks();
    }

    private static EncryptionInfo? FileVault()
    {
        var st = FileVaultParse.Status(Shell.Out("/usr/bin/fdesetup status 2>/dev/null"));
        if (st is null) return null;
        var personal = Shell.Out("/usr/bin/fdesetup haspersonalrecoverykey 2>/dev/null") switch { "true" => true, "false" => false, _ => (bool?)null };
        st.RecoveryKeys = personal == true ? 1 : 0;
        return new EncryptionInfo
        {
            Kind = EncryptionKinds.FileVault, Volumes = [st], HasPersonalKey = personal,
            Portable = MacPortable(),
        };
    }

    private static EncryptionInfo? Luks()
    {
        var json = Shell.Out("lsblk -J -o NAME,TYPE,FSTYPE,MOUNTPOINT 2>/dev/null");
        if (json.Length == 0) return null;
        List<LuksParse.Container> found; bool rootFound, rootEnc;
        try { (found, rootFound, rootEnc) = LuksParse.Lsblk(json); }
        catch (System.Text.Json.JsonException) { return null; }
        var vols = new List<EncryptedVolume>();
        foreach (var c in found)
        {
            var (active, method) = c.Mapper is { } mp ? LuksParse.Status(Shell.Out($"cryptsetup status {Q(mp)} 2>/dev/null")) : (false, null);
            vols.Add(new EncryptedVolume
            {
                Mount = c.Mounts.Count > 0 ? string.Join(", ", c.Mounts.Take(3)) : c.Device, System = c.Mounts.Contains("/"),
                Encrypted = true, Protected = true, Status = c.Mapper is null ? "Locked" : active || c.Mounts.Count > 0 ? "Unlocked" : "Open", Method = method,
            });
        }
        if (rootFound && !rootEnc)
            vols.Insert(0, new EncryptedVolume { Mount = "/", System = true, Encrypted = false, Protected = false, Status = "FullyDecrypted" });
        return new EncryptionInfo { Kind = EncryptionKinds.Luks, Volumes = vols, Portable = LinuxPortable() };
    }

    /// <summary>Bærbar: MacBook eller batteri (Mac), batteri eller bærbart kabinet (Linux). Null = ukendt (fx Raspberry Pi).</summary>
    public static bool? Portable() => Mac.Is ? MacPortable() : LinuxPortable();

    private static bool MacPortable() =>
        Shell.Out("/usr/sbin/sysctl -n hw.model 2>/dev/null").Contains("MacBook", StringComparison.OrdinalIgnoreCase)
        || Shell.Out("/usr/bin/pmset -g batt 2>/dev/null").Contains("InternalBattery", StringComparison.Ordinal);

    private static bool? LinuxPortable()
    {
        try
        {
            if (Directory.Exists("/sys/class/power_supply") && Directory.GetDirectories("/sys/class/power_supply", "BAT*").Length > 0) return true;
            var t = File.Exists("/sys/class/dmi/id/chassis_type") ? File.ReadAllText("/sys/class/dmi/id/chassis_type").Trim() : "";
            return int.TryParse(t, out var n) ? n is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32 : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Ny personlig FileVault-gendannelsesnøgle, som gemmes i Fleetdeck. Den indloggede bruger skriver sin egen adgangskode
    /// i en dialog på Mac'en; den gives kun til fdesetup via stdin og forlader aldrig Mac'en. Den nye nøgle sendes krypteret
    /// til serveren og står aldrig i jobbets output.
    /// </summary>
    public static async Task<JobResult> RotateFileVault(Api api, AgentJob j, CancellationToken ct)
    {
        if (!Mac.Is) return Fail("Kun på Mac.");
        var st = FileVaultParse.Status(Shell.Out("/usr/bin/fdesetup status 2>/dev/null"));
        if (st?.Protected != true) return Fail("FileVault er ikke slået til på Mac'en. Slå det til under Systemindstillinger → Anonymitet og sikkerhed → FileVault.");
        if (Mac.Session() is not { } s) return Fail("Ingen er logget ind på Mac'en. En bruger, der kan låse disken op, skal være logget ind og skrive sin adgangskode.");
        if (!FileVaultParse.Users(Shell.Out("/usr/bin/fdesetup list 2>/dev/null")).Contains(s.User, StringComparer.Ordinal))
            return Fail($"{s.User} kan ikke låse FileVault op på denne Mac. Log ind som en bruger, der kan.");

        LiveJob.Phase(10, 60, $"Venter på, at {s.User} skriver sin adgangskode på Mac'en");
        var pw = AskPassword(s.Uid, s.User, ct);
        if (pw is null) return Fail($"{s.User} annullerede eller svarede ikke inden for 5 minutter. Intet er ændret.");

        LiveJob.Phase(60, 100, "Laver ny gendannelsesnøgle");
        var (exit, output) = Exec("/usr/bin/fdesetup", ["changerecovery", "-personal", "-inputplist", "-outputplist"], FileVaultParse.InputPlist(s.User, pw), TimeSpan.FromMinutes(2));
        pw = null;
        var key = FileVaultParse.NewKey(output);
        if (exit != 0 || key is null)
            return Fail($"fdesetup kunne ikke lave en ny nøgle (kode {exit}). Oftest er adgangskoden forkert. Den gamle nøgle gælder stadig.\n{RecoveryKeys.Scrub(Tail(output))}");

        // den gamle nøgle virker ikke længere: prøv flere gange, så den nye ikke går tabt
        var post = new RecoveryKeyPost { Full = false, JobId = j.Id, Keys = [new RecoveryKeyItem { Kind = EncryptionKinds.FileVault, Volume = "/", ProtectorId = "personal", Key = key }] };
        for (var attempt = 1; ; attempt++)
        {
            try { await api.Post("/api/agent/recovery-keys", post, ct); break; }
            catch (HttpRequestException e) when (attempt < 5)
            {
                Log.Warn($"Gendannelsesnøgle: forsøg {attempt} mislykkedes ({e.StatusCode?.ToString() ?? e.GetType().Name})");
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
            catch (HttpRequestException)
            {
                return Fail("Den nye nøgle er lavet, men kunne ikke sendes til Fleetdeck. Kør jobbet igen, når forbindelsen virker – " +
                            "indtil da kan Mac'en kun låses op med brugerens adgangskode.");
            }
        }
        return new JobResult { Status = JobStatus.Success, ExitCode = 0, Output = $"Ny personlig gendannelsesnøgle er lavet (godkendt af {s.User}) og gemt krypteret i Fleetdeck. Den gamle nøgle virker ikke længere." };
    }

    /// <summary>Dialog i brugerens session med skjult svar. Null = annulleret eller ingen svar.</summary>
    private static string? AskPassword(int uid, string user, CancellationToken ct)
    {
        const string text = "Fleetdeck vil lave en ny gendannelsesnøgle til FileVault og gemme den sikkert hos din administrator. " +
                            "Skriv din adgangskode til Mac'en for at godkende. Adgangskoden bliver på denne Mac.";
        var script = $"text returned of (display dialog \"{text}\" default answer \"\" with hidden answer with title \"Fleetdeck\" " +
                     "buttons {\"Annullér\", \"Godkend\"} default button \"Godkend\" cancel button \"Annullér\" with icon caution giving up after 300)";
        var (exit, output) = Exec("/bin/launchctl", ["asuser", uid.ToString(), "/usr/bin/sudo", "-u", user, "/usr/bin/osascript", "-e", script], null, TimeSpan.FromMinutes(6), ct);
        if (exit != 0) return null;
        var pw = output.EndsWith('\n') ? output[..^1] : output;
        return pw.Length == 0 ? null : pw;
    }

    /// <summary>Kør et program direkte, med evt. hemmeligt indput på stdin. Uddata går hverken til log eller jobbets fremdrift.</summary>
    private static (int Exit, string Output) Exec(string file, string[] args, string? stdin, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        try { if (stdin is not null) p.StandardInput.Write(stdin); p.StandardInput.Close(); } catch (IOException) { }
        var until = DateTime.UtcNow + timeout;
        bool done;
        while (!(done = p.WaitForExit(500)) && DateTime.UtcNow < until && !ct.IsCancellationRequested && !Shell.JobCancel.IsCancellationRequested) { }
        if (!done)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "");
        }
        p.WaitForExit();
        var sb = new StringBuilder();
        try { sb.Append(o.Result); sb.Append(e.Result); } catch (AggregateException) { }
        return (p.ExitCode, sb.ToString());
    }

    private static string Tail(string s) => s.Length > 600 ? s[^600..] : s;
    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };
}
