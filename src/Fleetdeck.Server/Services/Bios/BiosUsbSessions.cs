using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>
/// "Lav USB-stik" (design afsnit 7): én guide = én session i serverens hukommelse (højst en time). Sessionen binder målmaskinen
/// (hvis BIOS skal opdateres), pakken og den pc, brugeren sidder ved. Pc'en findes uden at give browseren nye rettigheder:
/// browseren får en engangskode (2 minutter), sender den til agenten på http://localhost (/_fd/claim), og agenten sender den videre
/// over sin egen godkendte kanal. Kun en disk fra pc'ens egen liste, bekræftet lokalt, kan blive skrevet – og agenten tjekker alt igen.
/// Genstarter serveren, starter guiden forfra (intet er skrevet før det sidste, bekræftede trin).
/// </summary>
public sealed class BiosUsbSessions(Func<DateTime>? clock = null)
{
    public static readonly TimeSpan NonceFor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SessionFor = TimeSpan.FromHours(1);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, Session> _s = new();

    public sealed class Session
    {
        public string Id { get; init; } = "";
        public string Nonce { get; set; } = "";
        public DateTime NonceUntil { get; set; }
        public int TargetId { get; init; }
        public string TargetName { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Layout { get; init; } = "";
        public string Version { get; init; } = "";
        public int PackageId { get; init; }
        public string By { get; init; } = "";
        public DateTime Created { get; init; }
        public int? ThisId { get; set; }
        public string? ThisName { get; set; }
        public string? ThisPlatform { get; set; }
        public int? ListJobId { get; set; }
        public bool Baseline { get; set; }
        public List<BiosUsbDisk> Disks { get; set; } = [];
        public DateTime? DisksAt { get; set; }
        public string? ListMessage { get; set; }
        public BiosUsbDisk? Chosen { get; set; }
        public int? WriteJobId { get; set; }
        public BiosUsbReport? Result { get; set; }
        public long MaxBytes { get; init; } = BiosUsb.DefaultMaxBytes;
    }

    private void Prune()
    {
        foreach (var k in _s.Where(kv => _now() - kv.Value.Created > SessionFor).Select(kv => kv.Key).ToList()) _s.TryRemove(k, out _);
    }

    public Session Create(int targetId, string targetName, string vendor, string layout, string version, int packageId, string by)
    {
        Prune();
        var s = new Session
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            NonceUntil = _now() + NonceFor, TargetId = targetId, TargetName = targetName, Vendor = vendor, Layout = layout, Version = version,
            PackageId = packageId, By = by, Created = _now(),
        };
        _s[s.Id] = s;
        return s;
    }

    public Session? Get(string id)
    {
        Prune();
        return _s.TryGetValue(id, out var s) ? s : null;
    }

    public void Remove(string id) => _s.TryRemove(id, out _);

    /// <summary>Ny engangskode (fx når de 2 minutter er gået, før brugeren åbnede siden på localhost).</summary>
    public string? Renew(string id)
    {
        if (Get(id) is not { } s || s.ThisId is not null) return null;
        s.Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        s.NonceUntil = _now() + NonceFor;
        return s.Nonce;
    }

    /// <summary>Agenten på pc'en har fået engangskoden fra browseren på samme pc. Koden bruges én gang.</summary>
    public Session? Claim(string nonce, Machine m)
    {
        Prune();
        if (nonce.Length < 16) return null;
        var s = _s.Values.FirstOrDefault(x => x.ThisId is null && x.Nonce.Length > 0
            && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(x.Nonce), System.Text.Encoding.ASCII.GetBytes(nonce)));
        if (s is null || _now() > s.NonceUntil) return null;
        s.Nonce = "";
        s.ThisId = m.Id;
        s.ThisName = m.Label;
        s.ThisPlatform = m.Platform;
        return s;
    }

    public Session? ByJob(int jobId) => _s.Values.FirstOrDefault(x => x.ListJobId == jobId || x.WriteJobId == jobId);

    /// <summary>Hvorfor der ikke kan skrives til disken (null = det kan). Disken skal være fra pc'ens seneste liste med samme serie og størrelse.</summary>
    public static string? CannotWrite(Session s, string diskId, string serial, long size, bool allowLarge)
    {
        if (s.ThisId is null) return "Denne pc er ikke genkendt endnu.";
        if (s.WriteJobId is not null && s.Result is null) return "Stikket er ved at blive skrevet.";
        var d = s.Disks.FirstOrDefault(x => x.Id == diskId);
        if (d is null) return "Stikket er ikke på listen. Søg igen.";
        if (d.Serial != serial || d.Size != size) return "Stikket er ikke det samme som på listen. Søg igen.";
        return d.Usable(allowLarge, s.MaxBytes) ? null : d.Why ?? "Stikket kan ikke bruges.";
    }

    /// <summary>Resultatet af usb_disks eller bios_usb_write.</summary>
    public async Task OnResult(AppDb db, Job j, JobResult r, string status)
    {
        var p = Json.Des<JobParams>(j.ParamsJson).BiosUsb;
        if (p is null || Get(p.SessionId) is not { } s || s.ThisId != j.MachineId) return;
        if (j.Type == JobTypes.UsbDisks && s.ListJobId == j.Id)
        {
            s.Disks = r.BiosUsb?.Disks ?? [];
            s.Baseline |= r.BiosUsb?.Baseline == true;
            s.DisksAt = _now();
            s.ListMessage = status == JobStatus.Success ? r.BiosUsb?.Message : (r.Output.Length > 0 ? r.Output : "Diskene kunne ikke læses.");
        }
        else if (j.Type == JobTypes.BiosUsbWrite && s.WriteJobId == j.Id)
        {
            s.Result = r.BiosUsb ?? new BiosUsbReport
            {
                SessionId = s.Id, Phase = BiosUsbPhases.Write, Outcome = "failed", Message = r.Output.Length > 0 ? r.Output : "Stikket kunne ikke laves.",
            };
            await new AuditService(db).Log("agent:" + (s.ThisName ?? "?"), s.Result.Outcome == "ok" ? "bios.usb.done" : "bios.usb.failed",
                $"USB-stik til {s.TargetName} ({s.Version}) på {s.ThisName}: {s.Result.Message} · stik {s.Chosen?.Vendor} {s.Chosen?.Model} {s.Chosen?.Serial} {s.Chosen?.Size} B",
                s.TargetId);
        }
    }

    /// <summary>Trinene på målmaskinen, tilpasset leverandøren og det, Fleetdeck ved om den.</summary>
    public static List<string> TargetSteps(string vendor, string platform)
    {
        var o = new List<string> { "Tilslut strøm. Bærbare skal sidde i laderen hele vejen." };
        if (platform == "windows")
            o.Add("Har maskinen BitLocker, så sæt den på pause først (Suspend-BitLocker -RebootCount 2), ellers beder Windows om gendannelsesnøglen.");
        else o.Add("Låses disken op med TPM (LUKS), så hav gendannelsesnøglen klar.");
        switch (vendor)
        {
            case "lenovo":
                o.Add("Tryk F1 ved opstart og slå Secure Boot fra. Stikket starter et program, som Secure Boot ikke kender.");
                o.Add("Sæt stikket i, genstart, tryk F12 og vælg USB-stikket.");
                o.Add("Vent. Det tager op til 2 minutter, og maskinen kan genstarte op til 2 gange. Sluk ikke.");
                o.Add("Tag stikket ud, tryk F1 og slå Secure Boot til igen.");
                break;
            case "dell":
                o.Add("Sæt stikket i, genstart, tryk F12 og vælg \"BIOS Flash Update\" (nyere modeller: \"BIOS Update\").");
                break;
            default:
                o.Add("Sæt stikket i, genstart, og vælg \"Update BIOS using local media\".");
                break;
        }
        o.Add("Åbn maskinen i Fleetdeck bagefter. Fleetdeck tager selv en ny backup og viser, hvad der er ændret.");
        return o;
    }

    /// <summary>
    /// Et liste- eller skrivejob, der er slut uden resultat fra agenten (agenten genstartede midt i jobbet, jobbet blev annulleret
    /// eller udløb): giv guiden et svar i stedet for en evig "venter". Et senere, rigtigt resultat fra agenten vinder stadig.
    /// </summary>
    public void Settle(Session s, Job? list, Job? write)
    {
        static bool Dead(Job j) => j.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired or JobStatus.Hung;
        static string Why(Job j) => j.Status switch
        {
            JobStatus.Cancelled => "jobbet blev annulleret", JobStatus.Expired => "jobbet udløb, før pc'en hentede det",
            _ => "agenten svarede ikke (fx genstartet midt i jobbet)",
        };
        if (write is not null && s.WriteJobId == write.Id && s.Result is null && Dead(write))
            s.Result = new BiosUsbReport
            {
                SessionId = s.Id, Phase = BiosUsbPhases.Write, Outcome = "failed",
                Message = $"Stikket blev ikke færdigt: {Why(write)}. Det kan være halvt formateret og skal laves igen.",
            };
        if (list is not null && s.ListJobId == list.Id && Dead(list) && (s.DisksAt is null || s.DisksAt < list.Created))
        {
            s.ListMessage = $"Diskene kunne ikke læses: {Why(list)}.";
            s.DisksAt = _now();
        }
    }

    public static async Task<Job?> ActiveJob(AppDb db, int? id) =>
        id is int x ? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == x) : null;
}
