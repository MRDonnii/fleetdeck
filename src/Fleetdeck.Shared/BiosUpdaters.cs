using System.Globalization;
using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>
/// Linux: leverandørens ROM lægges klar som UEFI-kapsel med fwupd (plugin uefi_capsule), uden LVFS og uden at ændre OnlyTrusted.
/// fwupd sætter selv kapselheaderen på, lægger filen på EFI-partitionen og sætter BootNext; firmwaren tjekker leverandørens signatur.
/// Secure Boot kan blive slået til (fwupd starter via shim). Genstarten vælger brugeren selv.
/// Alle stier går gennem <see cref="FirmwareFs"/> og alle programmer gennem <see cref="BiosCmd"/>, så det kan testes uden hardware.
/// </summary>
public sealed class LinuxCapsuleUpdater(FirmwareFs fs, BiosCmd run, string workRoot = LinuxCapsuleUpdater.DefaultWork, Func<string, long?>? freeBytes = null) : IBiosUpdater
{
    public const string DefaultWork = "/var/lib/fleetdeck/bios";
    private const string Dmi = "sys/class/dmi/id";
    /// <summary>Mulige monteringer af EFI-partitionen.</summary>
    public static readonly string[] EspMounts = ["boot/efi", "efi", "boot"];
    /// <summary>
    /// install-blob med enhedens id. --no-reboot-check: fwupdtool spørger ikke om genstart og genstarter aldrig selv (lukket stdin er ikke nok
    /// at stole på); genstarten er brugerens eget valg i Fleetdeck.
    /// </summary>
    public static IReadOnlyList<string> InstallArgs(string rom, string deviceId) => ["install-blob", rom, deviceId, "--no-reboot-check"];

    public string Route => BiosUpdateRoutes.Capsule;
    public string WorkRoot => workRoot;

    private string? Read(string rel) => fs.Read(rel)?.Trim() is { Length: > 0 } v ? v : null;

    private async Task<FwupdJson.Device?> Device(CancellationToken ct)
    {
        var r = await run("fwupdmgr", ["get-devices", "--json"], TimeSpan.FromMinutes(2), null, true, ct);
        return r.ExitCode == 0 ? FwupdJson.SystemFirmware(r.Output) : null;
    }

    private async Task<bool> FwupdPresent(CancellationToken ct) =>
        (await run("fwupdmgr", ["--version"], TimeSpan.FromSeconds(30), null, true, ct)).ExitCode != BiosProcess.NotFound;

    public async Task<BiosProbe> Probe(BiosUpdateOrder o, CancellationToken ct)
    {
        var (ac, bat) = BiosLinux.Power(fs);
        var p = new BiosProbe
        {
            BiosVersion = Read($"{Dmi}/bios_version"), Model = Read($"{Dmi}/product_version") ?? Read($"{Dmi}/product_name"),
            Virtual = BiosFacts.Virtual(Read($"{Dmi}/sys_vendor"), Read($"{Dmi}/product_name"), Read($"{Dmi}/bios_vendor")),
            OnAc = ac, Battery = bat, TpmUnlock = BiosLinux.TpmUnlock(fs.Read("etc/crypttab")),
        };
        p.Fwupd = await FwupdPresent(ct);
        if (p.Fwupd && await Device(ct) is { } d)
        {
            p.DeviceId = d.DeviceId; p.DeviceGuid = d.Guid; p.VersionRaw = d.VersionRaw; p.VersionLowestRaw = d.VersionLowestRaw;
            p.Updatable = d.Updatable; p.UpdateState = d.UpdateState;
        }
        if (Esp() is { } esp) p.EspFree = (freeBytes ?? Free)(esp);
        var sb = await run("mokutil", ["--sb-state"], TimeSpan.FromSeconds(30), null, true, ct);
        p.SecureBoot = BiosLinux.SecureBoot(sb.ExitCode == BiosProcess.NotFound ? null : sb.Output);
        var eb = await run("efibootmgr", [], TimeSpan.FromSeconds(30), null, true, ct);
        p.BootOrder = eb.ExitCode == 0 ? BiosLinux.BootOrder(eb.Output) : null;
        return p;
    }

    /// <summary>Den monterede EFI-partition (har en EFI-mappe), som fuld sti.</summary>
    private string? Esp() => EspMounts.Where(m => fs.DirExists($"{m}/EFI")).Select(m => Path.Combine(fs.Root, m)).FirstOrDefault();

    private static long? Free(string path)
    {
        try { return new DriveInfo(path).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>fwupd-*.cap på EFI-partitionen (fwupd lægger kapslen i EFI/&lt;distro&gt;/fw).</summary>
    private bool CapsuleOnEsp()
    {
        if (Esp() is not { } esp) return false;
        try { return Directory.EnumerateFiles(Path.Combine(esp, "EFI"), "fwupd-*.cap", SearchOption.AllDirectories).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<BiosStageResult> Stage(BiosUpdateOrder o, string package, string workDir, BiosProbe p, CancellationToken ct)
    {
        var res = new BiosStageResult();
        string rom;
        if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            (var path, var err) = BiosRom.FromZip(package, workDir);
            if (err is not null) { res.Checks.Add(err); res.Message = err.Text; return res; }
            rom = path!;
        }
        else rom = package;
        var bytes = await File.ReadAllBytesAsync(rom, ct);
        var check = BiosRom.Check(bytes, o.TargetVersion, p.DeviceGuid);
        res.Checks.Add(check);
        if (check.State != BiosCheckStates.Ok) { res.Message = check.Text; return res; }
        var cap = BiosRom.Capsule(bytes, p.DeviceGuid);
        res.Checks.Add(cap);
        if (cap.State != BiosCheckStates.Ok) { res.Message = cap.Text; return res; }
        if (p.DeviceId is not { } dev) { res.Message = "fwupd fandt ingen UEFI-kapsel til systemfirmwaren."; return res; }

        // fwupdtool må ikke køre samtidig med dæmonen; den startes igen bagefter, så den ser den ventende opdatering
        res.Installed = true;
        await run("systemctl", ["stop", "fwupd.service"], TimeSpan.FromMinutes(1), null, true, CancellationToken.None);
        BiosCmdResult r;
        try { r = await run("fwupdtool", InstallArgs(rom, dev), TimeSpan.FromMinutes(15), null, false, CancellationToken.None); }
        finally { await run("systemctl", ["start", "fwupd.service"], TimeSpan.FromMinutes(1), null, true, CancellationToken.None); }
        res.Output = r.Output;
        if (r.ExitCode != 0)
        {
            res.ToolFailed = r.ExitCode != BiosProcess.TimedOut;
            res.Message = r.ExitCode == BiosProcess.TimedOut
                ? "fwupd svarede ikke inden for 15 minutter."
                : $"fwupd kunne ikke lægge opdateringen klar (kode {r.ExitCode}).";
            return res;
        }
        if (await Pending(new BiosUpdateState { DeviceId = dev, InstallStarted = true }, CancellationToken.None) != true)
        {
            res.Message = "fwupd meldte OK, men der ligger ingen opdatering klar til næste genstart.";
            return res;
        }
        res.Ok = true;
        res.Message = BiosUpdateText.Staged;
        return res;
    }

    public async Task<bool?> Pending(BiosUpdateState s, CancellationToken ct)
    {
        if (CapsuleOnEsp()) return true;
        var d = await Device(ct);
        return d is null ? null : d.UpdateState == FwupdJson.Pending;
    }

    public async Task<BiosAfterBoot> AfterBoot(BiosUpdateState s, CancellationToken ct)
    {
        var a = new BiosAfterBoot { BiosVersion = Read($"{Dmi}/bios_version") };
        if (await Device(ct) is { } d)
        {
            a.VersionRaw = d.VersionRaw; a.FwupdState = d.UpdateState; a.FwupdError = d.UpdateError;
            s.DeviceGuid ??= d.Guid;
        }
        if (s.DeviceGuid is { } g && Esrt.Read(fs).FirstOrDefault(e => e.Guid == g.ToLowerInvariant()) is { } e)
        {
            a.EsrtStatus = e.LastAttemptStatus; a.EsrtAttemptVersion = e.LastAttemptVersion; a.VersionRaw ??= e.Version;
        }
        var eb = await run("efibootmgr", [], TimeSpan.FromSeconds(30), null, true, ct);
        a.BootOrder = eb.ExitCode == 0 ? BiosLinux.BootOrder(eb.Output) : null;
        return a;
    }

    public DateTime? BootAt()
    {
        var up = fs.Read("proc/uptime")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? BootTime.FromUptime(DateTime.UtcNow, sec) : null;
    }
}

/// <summary>
/// Windows (Lenovo ThinkCentre/ThinkStation): Lenovos pakke pakkes ud (/verysilent /extract=yes), BitLocker sættes på pause i to genstarter,
/// og wFlashGUIx64.exe /quiet skriver første fase. Anden fase sker ved næste rigtige genstart, som brugeren vælger. /clr (nulstil indstillinger)
/// bruges aldrig. Resultatet læses fra Win32_BIOS og ESRT i registreringsdatabasen efter genstarten.
/// </summary>
public sealed class LenovoWindowsUpdater(BiosCmd run, string workRoot, Func<DateTime?>? bootAt = null) : IBiosUpdater
{
    public const string FlashTool = "wFlashGUIx64.exe";
    public static readonly IReadOnlyList<string> FlashArgs = ["/quiet"];

    public string Route => BiosUpdateRoutes.LenovoExe;
    public string WorkRoot => workRoot;

    /// <summary>Alt, forkontrollen skal bruge, i ét PowerShell-kald som JSON.</summary>
    public const string ProbeScript = """
        $ErrorActionPreference='SilentlyContinue'
        $b=Get-CimInstance Win32_BIOS; $cs=Get-CimInstance Win32_ComputerSystem; $pr=Get-CimInstance Win32_ComputerSystemProduct
        $bat=@(Get-CimInstance Win32_Battery | ForEach-Object { [pscustomobject]@{ Charge=$_.EstimatedChargeRemaining; Status=$_.BatteryStatus } })
        $bl=$null; try { $bl=[string](Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop).ProtectionStatus } catch {}
        $sb=$null; try { $sb=Confirm-SecureBootUEFI -ErrorAction Stop } catch {}
        $esrt=@(Get-ChildItem 'HKLM:\HARDWARE\UEFI\ESRT' | ForEach-Object { $x=Get-ItemProperty $_.PSPath; [pscustomobject]@{ Guid=$_.PSChildName; Type=$x.Type; Version=$x.Version; LastAttemptVersion=$x.LastAttemptVersion; LastAttemptStatus=$x.LastAttemptStatus } })
        [pscustomobject]@{ BiosVersion=$b.SMBIOSBIOSVersion; BiosVendor=$b.Manufacturer; Manufacturer=$cs.Manufacturer; Model=$cs.Model; ProductVersion=$pr.Version; Batteries=$bat; BitLocker=$bl; SecureBoot=$sb; Esrt=$esrt } | ConvertTo-Json -Depth 4 -Compress
        """;

    public sealed record EsrtRow(string Guid, int? Type, long? Version, long? LastAttemptVersion, int? LastAttemptStatus);
    public sealed record Snapshot(BiosProbe Probe, List<EsrtRow> Esrt);

    /// <summary>Læs JSON fra <see cref="ProbeScript"/>. ESRT-type 1 er systemfirmwaren.</summary>
    public static Snapshot Parse(string json)
    {
        var p = new BiosProbe();
        var esrt = new List<EsrtRow>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return new(p, esrt); }
        using (doc)
        {
            var r = doc.RootElement;
            p.BiosVersion = S(r, "BiosVersion")?.Trim();
            p.Model = S(r, "ProductVersion") is { Length: > 0 } pv ? pv : S(r, "Model");
            p.Virtual = BiosFacts.Virtual(S(r, "Manufacturer"), S(r, "Model"), S(r, "BiosVendor"));
            foreach (var b in Arr(r, "Batteries"))
            {
                if (N(b, "Charge") is long c) p.Battery = p.Battery is int x ? Math.Min(x, (int)c) : (int)c;
                // Win32_Battery.BatteryStatus: 1, 4, 5 = på batteri; 2 og 6–9 = på lysnet
                p.OnAc = N(b, "Status") switch { 1 or 4 or 5 => false, 2 or 6 or 7 or 8 or 9 => p.OnAc ?? true, _ => p.OnAc };
            }
            p.BitLocker = S(r, "BitLocker") switch { "On" or "1" => true, "Off" or "0" => false, _ => null };
            p.SecureBoot = r.TryGetProperty("SecureBoot", out var sb) && sb.ValueKind is JsonValueKind.True or JsonValueKind.False ? sb.GetBoolean() : null;
            foreach (var e in Arr(r, "Esrt"))
                if (S(e, "Guid")?.Trim('{', '}').ToLowerInvariant() is { Length: > 0 } g)
                    esrt.Add(new(g, (int?)N(e, "Type"), N(e, "Version"), N(e, "LastAttemptVersion"), (int?)N(e, "LastAttemptStatus")));
            if (esrt.FirstOrDefault(x => x.Type == 1) is { } sys) { p.DeviceGuid = sys.Guid; p.VersionRaw = sys.Version; }
        }
        return new(p, esrt);
    }

    private static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null,
    } : null;
    private static long? N(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;
    private static IEnumerable<JsonElement> Arr(JsonElement e, string n) => e.TryGetProperty(n, out var v)
        ? v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : v.ValueKind == JsonValueKind.Object ? [v] : []
        : [];

    private Task<BiosCmdResult> Ps(string script, TimeSpan timeout, CancellationToken ct) =>
        run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], timeout, null, true, ct);

    private async Task<Snapshot> Read(CancellationToken ct) => Parse((await Ps(ProbeScript, TimeSpan.FromMinutes(2), ct)).Output);

    public async Task<BiosProbe> Probe(BiosUpdateOrder o, CancellationToken ct) => (await Read(ct)).Probe;

    public async Task<BiosStageResult> Stage(BiosUpdateOrder o, string package, string workDir, BiosProbe p, CancellationToken ct)
    {
        var res = new BiosStageResult();
        // Lenovos pakke og flash-program køres som SYSTEM: kun med en gyldig Authenticode-signatur fra Lenovo (eller BIOS-leverandøren)
        if (await Signature(package, ct) is var ps && !SignedByLenovo(ps))
            return SignatureFail(res, $"Pakkens signatur er ikke Lenovos ({ps}). Intet er ændret.");
        res.Checks.Add(BiosCheck.Ok("signature", "Pakken er signeret af Lenovo."));
        var x = Path.Combine(workDir, "x");
        Directory.CreateDirectory(x);
        var ex = await run(package, ["/verysilent", $"/dir={x}", "/extract=yes"], TimeSpan.FromMinutes(5), workDir, true, ct);
        var files = Directory.Exists(x) ? Directory.EnumerateFiles(x, "*", SearchOption.AllDirectories).ToList() : [];
        var flash = files.FirstOrDefault(f => Path.GetFileName(f).Equals(FlashTool, StringComparison.OrdinalIgnoreCase));
        if (ex.ExitCode != 0 || flash is null)
        {
            res.Message = ex.ExitCode != 0 ? $"Lenovos pakke kunne ikke pakkes ud (kode {ex.ExitCode})." : $"Pakken indeholder ikke Lenovos flash-program ({FlashTool}).";
            res.Output = ex.Output;
            res.Checks.Add(BiosCheck.Fail("rom", res.Message));
            return res;
        }
        if (BiosRom.Pick(files.Select(f => Path.GetRelativePath(x, f))) is not { } romRel)
        {
            res.Message = "Pakken indeholder ingen entydig firmwarefil (*.rom).";
            res.Checks.Add(BiosCheck.Fail("rom", res.Message));
            return res;
        }
        var check = BiosRom.Check(await File.ReadAllBytesAsync(Path.Combine(x, romRel), ct), o.TargetVersion, p.DeviceGuid);
        res.Checks.Add(check);
        if (check.State != BiosCheckStates.Ok) { res.Message = check.Text; return res; }
        if (await Signature(flash, ct) is var fsig && !SignedByLenovo(fsig, flashTool: true))
            return SignatureFail(res, $"Flash-programmets signatur er ikke Lenovos ({fsig}). Intet er ændret.");

        if (p.BitLocker == true)
        {
            var bl = await Ps("Suspend-BitLocker -MountPoint $env:SystemDrive -RebootCount 2 -ErrorAction Stop | Out-Null; 'ok'", TimeSpan.FromMinutes(2), CancellationToken.None);
            if (bl.ExitCode != 0 || !bl.Output.Contains("ok", StringComparison.Ordinal))
            {
                res.Message = "BitLocker kunne ikke sættes på pause. Intet er opdateret.";
                res.Output = bl.Output;
                res.Checks.Add(BiosCheck.Fail("bitlocker", res.Message));
                return res;
            }
            res.Checks.Add(BiosCheck.Warn("bitlocker",
                "BitLocker er sat på pause, indtil maskinen er genstartet to gange. Indtil da kan disken læses uden TPM'en, så genstart snarest."));
        }

        res.Installed = true;
        var r = await run(flash, FlashArgs, TimeSpan.FromMinutes(30), Path.GetDirectoryName(flash), false, CancellationToken.None);
        res.Output = r.Output;
        if (r.ExitCode != 0)
        {
            res.ToolFailed = r.ExitCode != BiosProcess.TimedOut;
            res.Message = r.ExitCode == BiosProcess.TimedOut
                ? "Lenovos flash-program svarede ikke inden for 30 minutter. Sluk ikke maskinen; Fleetdeck kontrollerer versionen efter næste genstart."
                : $"Lenovos flash-program sluttede med kode {r.ExitCode}.";
            if (p.BitLocker == true && r.ExitCode != BiosProcess.TimedOut)
                await Ps("Resume-BitLocker -MountPoint $env:SystemDrive -ErrorAction SilentlyContinue | Out-Null", TimeSpan.FromMinutes(2), CancellationToken.None);
            return res;
        }
        res.Ok = true;
        res.Message = BiosUpdateText.Staged;
        return res;
    }

    private async Task<string> Signature(string file, CancellationToken ct)
    {
        var r = await Ps($"$s = Get-AuthenticodeSignature -LiteralPath '{file.Replace("'", "''")}'; \"$($s.Status)|$($s.SignerCertificate.Subject)\"", TimeSpan.FromMinutes(3), ct);
        return r.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
    }

    private static BiosStageResult SignatureFail(BiosStageResult res, string msg)
    {
        res.Message = msg;
        res.Checks.Add(BiosCheck.Fail("signature", msg));
        return res;
    }

    /// <summary>
    /// "Status|Subject" fra Get-AuthenticodeSignature: gyldig og udstedt til Lenovo. Flash-programmet i pakken må også være signeret af
    /// BIOS-leverandøren (AMI, Insyde, Phoenix).
    /// </summary>
    public static bool SignedByLenovo(string statusAndSubject, bool flashTool = false)
    {
        var parts = statusAndSubject.Split('|', 2);
        if (parts.Length != 2 || parts[0] != "Valid") return false;
        var org = flashTool ? @"Lenovo|American Megatrends|Insyde|Phoenix Technologies" : "Lenovo";
        return System.Text.RegularExpressions.Regex.IsMatch(parts[1], $@"(^|,\s*)O=""?({org})\b[^,]*""?(,|$)");
    }

    /// <summary>Lenovos første fase kan ikke aflæses fra Windows: null = ved ikke (så kontrolleres versionen efter næste genstart).</summary>
    public Task<bool?> Pending(BiosUpdateState s, CancellationToken ct) => Task.FromResult<bool?>(null);

    public async Task<BiosAfterBoot> AfterBoot(BiosUpdateState s, CancellationToken ct)
    {
        var snap = await Read(ct);
        var a = new BiosAfterBoot { BiosVersion = snap.Probe.BiosVersion, VersionRaw = snap.Probe.VersionRaw };
        var g = (s.DeviceGuid ?? snap.Probe.DeviceGuid)?.ToLowerInvariant();
        if (snap.Esrt.FirstOrDefault(e => e.Guid == g) is { } e)
        {
            a.EsrtStatus = e.LastAttemptStatus; a.EsrtAttemptVersion = e.LastAttemptVersion; a.VersionRaw = e.Version;
        }
        return a;
    }

    public DateTime? BootAt() => bootAt is null ? DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) : bootAt();
}
