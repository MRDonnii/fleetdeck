using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

// USB-stik med BIOS-opdateringen fra en Windows-pc (design afsnit 6.3 og 7). Samme protokol og samme sikkerhedsmodel som Linux
// (BiosUsb.cs): øjebliksbillede → kun stik sat i bagefter → lokal bekræftelse (/_fd/confirm) under 5 minutter → alt tjekkes igen lige
// før skrivning. Windows-agenten kører som SYSTEM-tjeneste; diskene læses med Storage-cmdlets (Get-Disk/MSFT_Disk), og stikket
// klargøres i ét PowerShell-script, der selv tjekker disken igen, før det sletter noget. Scriptene indeholder kun tal og hex – aldrig
// tekst fra brugeren eller fra stikket.

/// <summary>En disk, som Get-Disk ser den (fra <see cref="BiosUsbWin.ListScript"/>).</summary>
public sealed record WinDisk
{
    public int Number { get; init; }
    public string Bus { get; init; } = "";
    /// <summary>MSFT_Disk.BusType som tal (7 = USB).</summary>
    public int BusId { get; init; }
    public long Size { get; init; }
    public string Model { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Serial { get; init; } = "";
    public string UniqueId { get; init; } = "";
    /// <summary>MSFT_Disk.Path (\\?\usbstor#disk&amp;…#{guid}) – bruges til at skubbe stikket ud.</summary>
    public string DevicePath { get; init; } = "";
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public List<WinPart> Parts { get; init; } = [];
}

public sealed record WinPart
{
    public string Letter { get; init; } = "";
    public long Size { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public string FileSystem { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>Der ligger en Windows-installation på partitionen (\Windows\System32\config\SYSTEM).</summary>
    public bool Windows { get; init; }
    public bool PageFile { get; init; }
    public bool SystemDrive { get; init; }
    /// <summary>Win32_EncryptableVolume.ProtectionStatus (-1 = ingen BitLocker-oplysning).</summary>
    public int BlProt { get; init; } = -1;
    /// <summary>Win32_EncryptableVolume.ConversionStatus (-1 = ingen BitLocker-oplysning).</summary>
    public int BlConv { get; init; } = -1;
    public bool BitLocker => BlProt is 1 or 2 || BlConv > 0;
}

/// <summary>Et kørt PowerShell-script: exitkode (null = intet svar), uddata og om tidsgrænsen blev nået.</summary>
public sealed record WinPs(int? Exit, string Output, bool TimedOut = false);

/// <summary>Det, Windows-vejen skal bruge fra maskinen (den rigtige i Fleetdeck.Agent.Windows, en falsk i testene).</summary>
public interface IWinUsbHost
{
    /// <summary>Kør et PowerShell-script som SYSTEM (scriptet lægges i en fil – intet i kommandolinjen).</summary>
    Task<WinPs> PowerShell(string script, TimeSpan timeout, CancellationToken ct);
    /// <summary>Roden af drevet (E → "E:\").</summary>
    string Root(string letter);
    /// <summary>Skriv cachen ud og afmontér volumen, så filerne bagefter læses fra stikket selv. null = ok, ellers fejlteksten.</summary>
    string? Dismount(string letter);
    /// <summary>Skub stikket sikkert ud (som "Sikker fjernelse af hardware").</summary>
    bool Eject(string devicePath);
}

public static partial class BiosUsbWin
{
    public const int UsbBus = 7;
    /// <summary>Windows' formatering afviser FAT32 over 32 GB: partitionen bliver højst 32000 MiB (lidt under grænsen).</summary>
    public const long FatMaxBytes = 32000L * 1024 * 1024;
    public static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromMinutes(5);

    public const string BitLocker = "Stikket har en BitLocker-krypteret del. Fjern BitLocker fra stikket i Windows først, eller brug et andet stik.";
    public const string ReadOnly = "Stikket er skrivebeskyttet.";
    public const string Offline = "Disken er offline i Windows.";
    public const string ListFailed = "Windows kunne ikke læse diskene.";

    // ------------------------------------------------------------------ disklisten

    /// <summary>
    /// Læser alle diske med partitioner, volumener, BitLocker, sidefil og Windows-installationer. Kun læsning. Uddata: én JSON-linje.
    /// Remove-TypeData: Windows PowerShell 5.1 skriver ellers nogle lister som {"value":[…],"Count":n}.
    /// </summary>
    public const string ListScript = """
        $ErrorActionPreference = 'Stop'
        Remove-TypeData System.Array -ErrorAction SilentlyContinue
        $page = @()
        try { $page = @(Get-CimInstance Win32_PageFileUsage -ErrorAction Stop | ForEach-Object { ([string]$_.Name).Substring(0, 1).ToUpper() }) } catch { }
        $sysDrive = ([string]$env:SystemDrive).TrimEnd(':').ToUpper()
        $bl = @{}
        try {
          Get-CimInstance -Namespace 'root/cimv2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -ErrorAction Stop | ForEach-Object {
            $conv = -1; if ($null -ne $_.ConversionStatus) { $conv = [int]$_.ConversionStatus }
            $bl[[string]$_.DeviceID] = @([int]$_.ProtectionStatus, $conv)
          }
        } catch { }
        $out = foreach ($d in @(Get-Disk)) {
          $parts = foreach ($p in @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)) {
            $v = $null
            try { $v = $p | Get-Volume -ErrorAction Stop } catch { }
            $letter = ''
            if ([string]$p.DriveLetter -match '^[A-Za-z]$') { $letter = ([string]$p.DriveLetter).ToUpper() }
            $win = $false
            if ($letter) { $win = [bool](Test-Path -LiteralPath ($letter + ':\Windows\System32\config\SYSTEM') -ErrorAction SilentlyContinue) }
            $prot = -1; $conv = -1; $fs = ''; $label = ''
            if ($v) {
              $fs = [string]$v.FileSystem; $label = [string]$v.FileSystemLabel
              $e = $bl[[string]$v.Path]
              if ($e) { $prot = $e[0]; $conv = $e[1] }
            }
            [pscustomobject]@{
              Letter = $letter; Size = [int64]$p.Size; IsSystem = [bool]$p.IsSystem; IsBoot = [bool]$p.IsBoot; FileSystem = $fs; Label = $label
              Windows = $win; PageFile = [bool]($letter -and ($page -contains $letter)); SystemDrive = [bool]($letter -and $letter -eq $sysDrive)
              BlProt = $prot; BlConv = $conv
            }
          }
          $busId = -1
          try { $busId = [int]$d.CimInstanceProperties['BusType'].Value } catch { }
          [pscustomobject]@{
            Number = [int]$d.Number; Bus = [string]$d.BusType; BusId = $busId; Size = [int64]$d.Size; Model = [string]$d.FriendlyName
            Vendor = [string]$d.Manufacturer; Serial = ([string]$d.SerialNumber).Trim(); UniqueId = [string]$d.UniqueId; DevicePath = [string]$d.Path
            IsSystem = [bool]$d.IsSystem; IsBoot = [bool]$d.IsBoot; IsOffline = [bool]$d.IsOffline; IsReadOnly = [bool]$d.IsReadOnly
            Parts = @($parts)
          }
        }
        Write-Output (ConvertTo-Json -InputObject @($out) -Depth 5 -Compress)
        """;

    /// <summary>Uddata fra <see cref="ListScript"/> → diskene. null = uddata kunne ikke læses.</summary>
    public static List<WinDisk>? ParseList(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end < start) return null;
        try
        {
            using var doc = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            return Items(doc.RootElement).Select(e => new WinDisk
            {
                Number = (int)Num(e, "Number", -1), Bus = Str(e, "Bus"), BusId = (int)Num(e, "BusId", -1), Size = Num(e, "Size", 0),
                Model = Str(e, "Model"), Vendor = Str(e, "Vendor"), Serial = Str(e, "Serial").Trim(), UniqueId = Str(e, "UniqueId"),
                DevicePath = Str(e, "DevicePath"), IsSystem = Bool(e, "IsSystem"), IsBoot = Bool(e, "IsBoot"), IsOffline = Bool(e, "IsOffline"),
                IsReadOnly = Bool(e, "IsReadOnly"),
                Parts = Prop(e, "Parts") is { } ps ? Items(ps).Select(p => new WinPart
                {
                    Letter = Str(p, "Letter").Trim().ToUpperInvariant(), Size = Num(p, "Size", 0), IsSystem = Bool(p, "IsSystem"), IsBoot = Bool(p, "IsBoot"),
                    FileSystem = Str(p, "FileSystem"), Label = Str(p, "Label"), Windows = Bool(p, "Windows"), PageFile = Bool(p, "PageFile"),
                    SystemDrive = Bool(p, "SystemDrive"), BlProt = (int)Num(p, "BlProt", -1), BlConv = (int)Num(p, "BlConv", -1),
                }).ToList() : [],
            }).Where(d => d.Number >= 0).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList(),
        JsonValueKind.Object when Prop(e, "value") is { ValueKind: JsonValueKind.Array } v => Items(v),
        JsonValueKind.Object => [e],
        _ => [],
    };

    private static JsonElement? Prop(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string Str(JsonElement e, string name) => Prop(e, name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        _ => "",
    } : "";

    private static long Num(JsonElement e, string name, long fallback) => Prop(e, name) is { } v ? v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => fallback,
    } : fallback;

    private static bool Bool(JsonElement e, string name) => Prop(e, name) is { } v && (v.ValueKind == JsonValueKind.True
        || v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ identitet

    /// <summary>SHA-256 af "UniqueId|størrelse" (hex). Udregnes på samme måde i <see cref="PrepareScript"/> lige før sletningen.</summary>
    public static string Fingerprint(WinDisk d) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        d.UniqueId.Trim() + "|" + d.Size.ToString(CultureInfo.InvariantCulture))));

    /// <summary>Navnet i guiden: disknummer + fingeraftryk (følger stikket; flyttes det til et andet nummer, er det et "nyt" stik).</summary>
    public static string IdOf(WinDisk d) => $"win-{d.Number}-{Fingerprint(d)[..12]}";

    /// <summary>
    /// Nøglen til øjebliksbilledet. Windows har ingen disksekvens, så nøglen har kun ét "|": et stik, der har manglet i listen i flere
    /// scanninger i træk (<see cref="BiosUsbLocal.Forget"/>), glemmes og tæller som nyt, når det sættes i igen.
    /// </summary>
    public static string Key(WinDisk d) => $"win{d.Number}|{Fingerprint(d)}";

    public static string DevPath(WinDisk d) => $@"\\.\PhysicalDrive{d.Number}";

    // ------------------------------------------------------------------ udelukkelser

    /// <summary>Systemets disk: IsSystem/IsBoot, systemdrevet, sidefilen eller en Windows-installation. Den vises slet ikke.</summary>
    public static bool InUse(WinDisk d) => d.IsSystem || d.IsBoot
        || d.Parts.Any(p => p.IsSystem || p.IsBoot || p.Windows || p.PageFile || p.SystemDrive);

    public static bool IsUsb(WinDisk d) => d.BusId == UsbBus || d.BusId < 0 && string.Equals(d.Bus, "USB", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hvorfor disken ikke må skrives (null = den må). Samme rækkefølge som Linux; grænsen for store diske til sidst.</summary>
    public static string? Refusal(WinDisk d, ISet<string>? baseline, long maxBytes, bool allowLarge)
    {
        if (!IsUsb(d)) return BiosUsb.NotUsb;
        if (InUse(d)) return BiosUsb.SystemDisk;
        if (d.Size <= 0) return BiosUsb.NoMedia;
        if (d.Parts.Any(p => p.BitLocker)) return BitLocker;
        if (d.IsReadOnly) return ReadOnly;
        if (d.IsOffline) return Offline;
        if (baseline is null || baseline.Contains(Key(d))) return BiosUsb.NotNew;
        if (d.Size > maxBytes && !allowLarge) return BiosUsb.TooLarge(maxBytes);
        return null;
    }

    private static string Model(WinDisk d)
    {
        var m = d.Model.Trim();
        return m.EndsWith(" USB Device", StringComparison.OrdinalIgnoreCase) ? m[..^" USB Device".Length].TrimEnd() : m;
    }

    /// <summary>USB-diskene til guiden. Systemdiske (også en USB-disk, Windows kører fra) meldes ikke.</summary>
    public static List<BiosUsbDisk> Evaluate(List<WinDisk> disks, ISet<string>? baseline, long maxBytes) =>
        disks.Where(d => IsUsb(d) && !InUse(d)).OrderBy(d => d.Number).Select(d =>
        {
            var why = Refusal(d, baseline, maxBytes, allowLarge: false);
            return new BiosUsbDisk
            {
                Id = IdOf(d), Path = DevPath(d), Size = d.Size, Model = Model(d), Vendor = d.Vendor.Trim(), Serial = d.Serial, Partitions = d.Parts.Count,
                Labels = d.Parts.Select(p => string.Join(' ', new[] { p.Letter.Length > 0 ? p.Letter + ":" : "", p.Label, p.FileSystem }.Where(s => s.Length > 0)))
                    .Where(s => s.Length > 0).ToList(),
                Selectable = why is null, Why = why, Large = d.Size > maxBytes,
            };
        }).ToList();

    // ------------------------------------------------------------------ klargøring

    /// <summary>Plads, MBR-partitionen ikke kan bruge: den starter 1 MiB inde på disken (plus 1 MiB i margen).</summary>
    public const long PartitionSlack = 2L * 1024 * 1024;

    /// <summary>
    /// Partitionens størrelse i bytes (0 = hele stikket). Kun når der er plads til 32000 MiB efter starten 1 MiB inde; en disk lige over
    /// grænsen får hele stikket (som så er under FAT32's grænse) i stedet for en -Size, der ikke kan være der.
    /// </summary>
    public static long PartitionSize(long diskSize) => diskSize >= FatMaxBytes + PartitionSlack ? FatMaxBytes : 0;

    /// <summary>
    /// Klargør disk <paramref name="number"/>: tjek igen (fingeraftryk, USB, ikke system), ryd → MBR → én aktiv FAT32-partition (højst
    /// 32000 MiB) → formatér med etiketten → giv den et drevbogstav, hvis Windows ikke selv har givet den et. Uddata: "FDOK|bogstav|filsystem|etiket|størrelse|fingeraftryk" eller "FDFAIL|trin|tekst".
    /// </summary>
    public static string PrepareScript(int number, string fingerprint, long partitionSize)
    {
        if (number < 0) throw new ArgumentOutOfRangeException(nameof(number));
        if (!HexRx().IsMatch(fingerprint)) throw new ArgumentException("Ugyldigt fingeraftryk.", nameof(fingerprint));
        if (partitionSize < 0) throw new ArgumentOutOfRangeException(nameof(partitionSize));
        var size = partitionSize > 0 ? $"-Size {partitionSize.ToString(CultureInfo.InvariantCulture)}" : "-UseMaximumSize";
        return $$"""
            $ErrorActionPreference = 'Stop'
            $n = {{number.ToString(CultureInfo.InvariantCulture)}}
            function Fail($step, $e) { Write-Output ('FDFAIL|' + $step + '|' + (([string]$e) -replace '[\r\n|]+', ' ')); exit 3 }
            function Fp($d) {
              $s = ([string]$d.UniqueId).Trim() + '|' + ([int64]$d.Size).ToString([Globalization.CultureInfo]::InvariantCulture)
              $h = [Security.Cryptography.SHA256]::Create()
              try { -join ($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString('x2') }) } finally { $h.Dispose() }
            }
            function Raw($o, $name) { try { [int]$o.CimInstanceProperties[$name].Value } catch { -1 } }
            try { $d = Get-Disk -Number $n } catch { Fail 'find' $_ }
            if ((Fp $d) -ne '{{fingerprint}}') { Fail 'changed' '' }
            if ((Raw $d 'BusType') -ne {{UsbBus}} -or $d.IsSystem -or $d.IsBoot -or $d.IsReadOnly) { Fail 'unsafe' '' }
            $sysDrive = ([string]$env:SystemDrive).TrimEnd(':').ToUpper()
            foreach ($p in @(Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue)) {
              if ($p.IsSystem -or $p.IsBoot -or ([string]$p.DriveLetter).ToUpper() -eq $sysDrive) { Fail 'unsafe' '' }
            }
            try { if ((Raw $d 'PartitionStyle') -ne 0) { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false } } catch { Fail 'clear' $_ }
            try {
              $st = Raw (Get-Disk -Number $n) 'PartitionStyle'
              if ($st -eq 0) { Initialize-Disk -Number $n -PartitionStyle MBR -Confirm:$false }
              elseif ($st -eq 2) { Set-Disk -Number $n -PartitionStyle MBR }
            } catch { Fail 'init' $_ }
            try { $p = New-Partition -DiskNumber $n {{size}} -IsActive -MbrType FAT32 } catch { Fail 'partition' $_ }
            try { $null = Format-Volume -Partition $p -FileSystem FAT32 -NewFileSystemLabel '{{BiosUsb.VolumeLabel}}' -Force -Confirm:$false } catch { Fail 'format' $_ }
            # Windows giver ofte flytbare drev et bogstav af sig selv; ellers bedes om et (en fejl her tjekkes af løkken nedenfor)
            $q = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber
            if (-not ([string]$q.DriveLetter -match '^[A-Za-z]$')) {
              try { Add-PartitionAccessPath -DiskNumber $n -PartitionNumber $p.PartitionNumber -AssignDriveLetter } catch { }
            }
            $letter = ''
            for ($i = 0; $i -lt 30 -and -not $letter; $i++) {
              $q = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber
              if ([string]$q.DriveLetter -match '^[A-Za-z]$') { $letter = ([string]$q.DriveLetter).ToUpper() } else { Start-Sleep -Milliseconds 500 }
            }
            if (-not $letter) { Fail 'letter' '' }
            $v = Get-Volume -DriveLetter $letter
            Write-Output ('FDOK|' + $letter + '|' + [string]$v.FileSystem + '|' + [string]$v.FileSystemLabel + '|' + [int64]$q.Size + '|' + (Fp (Get-Disk -Number $n)))
            """;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex HexRx();

    /// <summary>Resultatet af <see cref="PrepareScript"/>.</summary>
    public sealed record Prepared(bool Ok, string Letter = "", string FileSystem = "", string Label = "", long Size = 0, string Fingerprint = "", string Step = "", string Text = "");

    public static Prepared ParsePrepare(string output)
    {
        foreach (var line in output.Split('\n').Select(l => l.Trim()).Reverse())
        {
            var f = line.Split('|');
            if (f[0] == "FDOK" && f.Length == 6 && f[1].Length == 1 && char.IsAsciiLetter(f[1][0]) && long.TryParse(f[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz))
                return new(true, f[1].ToUpperInvariant(), f[2], f[3], sz, f[5]);
            if (f[0] == "FDFAIL" && f.Length >= 2) return new(false, Step: f[1], Text: f.Length > 2 ? string.Join(' ', f[2..]).Trim() : "");
        }
        return new(false, Step: "unknown", Text: output.Trim());
    }

    /// <summary>Tydelig dansk tekst for et trin, der fejlede. Før "clear" er intet slettet.</summary>
    public static string PrepareError(Prepared p)
    {
        var why = p.Text.Length > 0 ? ": " + (p.Text.Length > 300 ? p.Text[..300] + " …" : p.Text) : ".";
        return p.Step switch
        {
            "find" => "Stikket er ikke sat i længere. Intet er skrevet.",
            "changed" => "Stikket er ikke det samme som det, du bekræftede. Intet er skrevet.",
            "unsafe" => BiosUsb.SystemDisk + " Intet er skrevet.",
            "clear" => "Windows kunne ikke slette stikket" + why,
            "init" => "Windows kunne ikke lave en ny partitionstabel (MBR) på stikket" + why,
            "partition" => "Windows kunne ikke lave partitionen på stikket" + why,
            "format" => "Windows kunne ikke formatere stikket som FAT32" + why,
            "letter" => "Windows kunne ikke give stikket et drevbogstav. Fjern et drev, der ikke bruges, og prøv igen.",
            _ => "Klargøringen af stikket fejlede" + why,
        };
    }

    /// <summary>MSFT_Disk.Path → enhedens instans-id: \\?\usbstor#disk&amp;ven_x#123&amp;0#{guid} → usbstor\disk&amp;ven_x\123&amp;0.</summary>
    public static string? InstanceId(string devicePath)
    {
        var p = devicePath.Trim();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
        var g = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (g > 0) p = p[..g];
        if (p.Length == 0 || p.Contains('\\') || p.Any(char.IsControl)) return null;
        return p.Replace('#', '\\');
    }

    // ------------------------------------------------------------------ jobbene og /_fd/

    private static async Task<List<WinDisk>?> Scan(IWinUsbHost h, CancellationToken ct)
    {
        var r = await h.PowerShell(ListScript, ListTimeout, ct);
        return r.Exit == 0 && !r.TimedOut ? ParseList(r.Output) : null;
    }

    /// <summary>
    /// usb_disks: første gang pr. guide tages øjebliksbilledet; derefter ventes op til <paramref name="wait"/> (90 s) på et stik, der
    /// ikke sad i før. Kun læsning. null = diskene kunne ikke læses.
    /// </summary>
    public static async Task<BiosUsbReport?> List(IWinUsbHost h, BiosUsbOrder o, BiosUsbLocal local, CancellationToken ct, TimeSpan? wait = null, TimeSpan? every = null)
    {
        var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.List };
        if (local.Baseline(o.SessionId) is null)
        {
            if (await Scan(h, ct) is not { } first) return null;
            local.SetBaseline(o.SessionId, first.Select(Key));
            rep.Baseline = true;
            rep.Disks = Evaluate(first, local.Baseline(o.SessionId), o.MaxBytes);
            rep.Message = "Øjebliksbilledet er taget. Sæt USB-stikket i nu.";
            return rep;
        }
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(90));
        while (true)
        {
            if (await Scan(h, ct) is not { } s) return null;
            local.Forget(o.SessionId, s.Select(Key));
            rep.Disks = Evaluate(s, local.Baseline(o.SessionId), o.MaxBytes);
            if (rep.Disks.Any(d => d.Why != BiosUsb.NotNew) || DateTime.UtcNow > until) break;
            await Task.Delay(every ?? TimeSpan.FromSeconds(2), ct);
        }
        rep.Message = rep.Disks.Any(d => d.Selectable || d.Large) ? "Stikket er fundet." : "Intet nyt stik fundet.";
        return rep;
    }

    /// <summary>
    /// /_fd/claim og /_fd/confirm fra browseren på denne pc (LocalWeb har allerede tjekket loopback, Host, Origin og X-Fleetdeck) – samme
    /// protokol som Linux. claim: engangskoden sendes til serveren over agentens egen kanal. confirm: brugeren har bekræftet stikket her.
    /// </summary>
    public static async Task<LocalWeb.LocalReply> Local(string path, IReadOnlyDictionary<string, string> q, IWinUsbHost h, BiosUsbLocal local,
        Func<string, CancellationToken, Task<BiosUsbClaimResponse>>? claim, CancellationToken ct)
    {
        switch (path)
        {
            case "/_fd/claim":
                if (!q.TryGetValue("nonce", out var nonce) || nonce.Length is < 16 or > 128) return LocalWeb.LocalReply.Error(400, "Engangskoden mangler.");
                if (claim is null) return LocalWeb.LocalReply.Error(503, "Agenten er ikke tilmeldt Fleetdeck endnu. Prøv igen om lidt.");
                try { return LocalWeb.LocalReply.Json(await claim(nonce, ct)); }
                catch (HttpRequestException e) { return LocalWeb.LocalReply.Error(409, e.StatusCode is null ? "Fleetdeck kan ikke nås fra denne pc." : "Engangskoden er ikke gyldig længere. Start guiden igen."); }
            case "/_fd/confirm":
            {
                if (!q.TryGetValue("session", out var sid) || !q.TryGetValue("disk", out var disk) || !q.TryGetValue("serial", out var serial)
                    || !q.TryGetValue("size", out var sz) || !long.TryParse(sz, out var size) || !SpareCard.SafeId(disk) || sid.Length is 0 or > 64)
                    return LocalWeb.LocalReply.Error(400, "Ugyldig bekræftelse.");
                if (await Scan(h, ct) is not { } s) return LocalWeb.LocalReply.Error(500, ListFailed);
                var max = q.TryGetValue("max", out var m) && long.TryParse(m, out var mm) && mm > 0 ? mm : BiosUsb.DefaultMaxBytes;
                var d = Evaluate(s, local.Baseline(sid), max).FirstOrDefault(x => x.Id == disk);
                if (d is null) return LocalWeb.LocalReply.Error(409, "Stikket er ikke sat i længere.");
                if (d.Serial != serial || d.Size != size) return LocalWeb.LocalReply.Error(409, "Stikket er ikke det samme som det, der blev vist.");
                if (!d.Usable(q.TryGetValue("large", out var lg) && lg == "1", max)) return LocalWeb.LocalReply.Error(409, d.Why ?? "Stikket kan ikke bruges.");
                return LocalWeb.LocalReply.Json(new { hash = local.Confirm(sid, disk, serial, size) });
            }
            default: return LocalWeb.LocalReply.Error(404, "Ukendt.");
        }
    }

    /// <summary>
    /// bios_usb_write på Windows. Rækkefølge: pakken passer → disken findes stadig med samme navn, serie og størrelse → alle udelukkelser
    /// holder → lokal bekræftelse under 5 minutter → klargøring (scriptet tjekker fingeraftryk, USB og system igen lige før sletningen)
    /// → kopiér → skriv cachen ud og afmontér → læs alt tilbage → afmontér → skub ud.
    /// </summary>
    public static async Task<BiosUsbReport> Write(IWinUsbHost h, BiosUsbOrder o, string package, BiosUsbLocal local, Action<string> log,
        Action<int, int, string>? phase, CancellationToken ct)
    {
        var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.Write };
        BiosUsbReport Stop(string outcome, string text) { rep.Outcome = outcome; rep.Message = text; log(text); return rep; }
        phase ??= (_, _, _) => { };

        // 1. pakken
        phase(0, 5, "Kontrollerer pakken …");
        if (BiosUsb.PackageProblem(o, package) is { } bad) return Stop(bad.Outcome, bad.Text);

        // 2. disken: samme stik som bekræftet, og alle udelukkelser holder stadig
        if (!SpareCard.SafeId(o.DiskId) || o.Serial is null) return Stop("refused", "Ugyldigt disknavn.");
        if (await Scan(h, ct) is not { } disks) return Stop("failed", ListFailed + " Intet er skrevet.");
        var d = disks.FirstOrDefault(x => IdOf(x) == o.DiskId);
        if (d is null) return Stop("refused", "Stikket er ikke sat i længere. Intet er skrevet.");
        if (d.Serial != o.Serial || d.Size != o.Size) return Stop("refused", "Stikket er ikke det samme som det, du bekræftede. Intet er skrevet.");
        if (Refusal(d, local.Baseline(o.SessionId), o.MaxBytes, o.AllowLarge) is { } why) return Stop("refused", why + " Intet er skrevet.");
        if (!local.Take(o.SessionId, o.DiskId!, o.Serial, o.Size, o.ConfirmHash))
            return Stop("refused", "Stikket er ikke bekræftet på denne pc inden for de sidste 5 minutter. Intet er skrevet.");
        var fp = Fingerprint(d);
        rep.DiskLabel = $"{string.Join(' ', new[] { d.Vendor.Trim(), Model(d) }.Where(x => x.Length > 0))} · {SpareCard.Size(d.Size)} · {d.Serial}".Trim();
        log($"Stik: {rep.DiskLabel} (disk {d.Number})");

        // 3. formatér
        phase(5, 25, "Formaterer …");
        var pr = await h.PowerShell(PrepareScript(d.Number, fp, PartitionSize(d.Size)), PrepareTimeout, ct);
        if (pr.TimedOut)
            return Stop("failed", $"Windows blev ikke færdig med at formatere stikket inden for {PrepareTimeout.TotalMinutes:0} minutter. Tag stikket ud, sæt det i igen, og start guiden forfra.");
        var prep = ParsePrepare(pr.Output);
        if (!prep.Ok) return Stop(prep.Step is "find" or "changed" or "unsafe" ? "refused" : "failed", PrepareError(prep));
        if (prep.Fingerprint != fp) return Stop("failed", "Stikket ændrede sig under formateringen. Brug et andet stik.");
        if (!string.Equals(prep.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) || !string.Equals(prep.Label, BiosUsb.VolumeLabel, StringComparison.OrdinalIgnoreCase))
            return Stop("failed", $"Stikket blev ikke formateret som FAT32 med etiketten {BiosUsb.VolumeLabel} (Windows meldte {prep.FileSystem} \"{prep.Label}\").");
        log($"Formateret som FAT32 ({SpareCard.Size(prep.Size)}) på {prep.Letter}:");

        // 4. kopiér og kontrollér
        var root = h.Root(prep.Letter);
        phase(25, 75, "Kopierer …");
        List<(string Path, string Sha, long Size)> manifest;
        try { manifest = BiosUsb.Extract(package, o.Layout!, o.FileName!, root, o.Readme); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { return Stop("failed", "Kopieringen fejlede: " + e.Message); }
        rep.Files = manifest.Count;
        rep.Bytes = manifest.Sum(m => m.Size);
        log($"Kopieret {rep.Files} filer ({SpareCard.Size(rep.Bytes)}).");
        phase(75, 92, "Kontrollerer …");
        if (h.Dismount(prep.Letter) is { } e1) return Stop("failed", "Stikket kunne ikke afmonteres efter kopieringen: " + e1);
        List<string> wrong;
        try { wrong = BiosUsb.Verify(root, manifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Stop("failed", "Filerne på stikket kunne ikke læses tilbage: " + e.Message); }
        if (wrong.Count > 0) return Stop("failed", $"{wrong.Count} fil(er) på stikket passer ikke med pakken (fx {wrong[0]}). Brug et andet stik.");
        rep.Verified = true;
        if (h.Dismount(prep.Letter) is { } e2) return Stop("failed", "Stikket kunne ikke afmonteres efter kontrollen: " + e2);

        // 5. skub ud
        phase(92, 100, "Skubber ud …");
        rep.Ejected = d.DevicePath.Length > 0 && h.Eject(d.DevicePath);
        rep.Outcome = "ok";
        rep.Message = $"Stikket er klar: {rep.Files} filer kopieret og kontrolleret." + (rep.Ejected ? " Det er skubbet ud og kan tages ud." : $" Skub det ud fra Windows ({prep.Letter}:), og tag det ud.");
        log(rep.Message);
        return rep;
    }
}
