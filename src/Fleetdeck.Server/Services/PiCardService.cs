using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Det, siden sender for at bygge et kort. Ved "erstat" kommer navn, bruger og profil fra maskinen, hvis de mangler.</summary>
public sealed record PiCardRequest(string Mode, int? MachineId, string? Kit, string? ImageId, string? Name, string? User, string? SshKeys,
    string? IpMode, string? Address, string? Gateway, string? Dns, string? Iface, int? ProfileId, List<string>? Exclude, string? ConfigLines,
    string? CmdlineFlags, bool? SaveToProfile, string? Confirm, string? Timezone, string? Keymap);

/// <summary>Det, kortet blev bygget med (gemmes på kortets række, vises på siden og bruges ved gendannelsen).</summary>
public sealed record PiCardChosen(string User, string IpMode, string Address, string Gateway, string Dns, string Iface, List<string> Exclude,
    List<string> ConfigLines, List<string> CmdlineFlags, string ImageTitle, string? KitSystem, List<string> MaskedUnits, List<string>? Keys = null);

public sealed record PiCardEvent(string Step, DateTime At, string? Text = null);

/// <summary>
/// "Lav SD-kort": planen for en maskine, bygning i baggrunden, hentning og tidslinjen for første start. Kortet bærer kun en
/// engangskode (24 timer, én brug, bundet til maskinen ved "erstat"); alt andet hentes over netværket, når Pi'en har meldt sig.
/// </summary>
public sealed class PiCardService(AppDb db, Paths paths, PiImageLibrary images, EnrollmentService enroll, SettingsService settings,
    TlsInfo tls, JobService jobs, AuditService audit, IServiceScopeFactory scopes, ILogger<PiCardService> log)
{
    public const string JobSource = "sd-kort", RestoreStep = "nytkort";
    public static readonly TimeSpan Life = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public string Dir(int id) => Path.Combine(paths.Data, "pi-cards", id.ToString());

    // ---------------------------------------------------------------- planen

    /// <summary>Det, guiden viser for en maskine: gendannelsespunkter (seneste først) med det basisbillede, der passer, og profilens felter.</summary>
    public async Task<object?> Plan(int machineId)
    {
        await Cleanup();
        var m = await db.Machines.Include(x => x.Profile).AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return null;
        var dir = Kits.Dir(paths, m.Id);
        var kits = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles("*.tar.gz").Where(f => !IpadBackups.IsIpad(f.Name)).OrderByDescending(f => f.Name).Take(10).ToList()
            : [];
        var points = kits.Select(f =>
        {
            var k = PiCardLogic.ReadKit(f.FullName);
            var (img, err) = k is null ? (null, "Gendannelsespunktet kan ikke læses") : PiCardLogic.MatchBase(k.System, null);
            return new
            {
                name = f.Name, size = f.Length, created = f.LastWriteTimeUtc, system = k is null ? null : PiCardLogic.Pretty(k.System), raw = k?.System.Raw,
                imageId = img?.Id, imageTitle = img?.Title, error = err, archCertain = k?.System.ArchCertain ?? false,
                masked = k?.MaskedUnits ?? [], enabled = k?.EnabledUnits ?? [],
            };
        }).ToList();
        var facts = Json.Des<SystemFacts>(m.FactsJson);
        var p = m.Profile;
        return new
        {
            machine = new { m.Id, m.Name, label = m.Label, m.Protected, m.Platform, ip = m.Ip, model = facts.Model, os = facts.Os, user = m.User, profileId = m.ProfileId, profile = p?.Name },
            points,
            profile = new { exclude = p?.PiExclude ?? [], configLines = p?.PiConfigLines ?? "", cmdlineFlags = p?.PiCmdlineFlags ?? "" },
            cards = await Cards(m.Id),
            tools = PiImageBuilder.ToolsPresent(),
        };
    }

    public async Task<List<object>> Cards(int? machineId)
    {
        var rows = await db.PiCards.AsNoTracking().Where(c => machineId == null || c.MachineId == machineId).OrderByDescending(c => c.Id).Take(10).ToListAsync();
        var list = new List<object>();
        foreach (var c in rows) list.Add(await View(c));
        return list;
    }

    // ---------------------------------------------------------------- byg

    public sealed record Started(PiCard? Card, string? Error);

    public async Task<Started> Start(PiCardRequest r, string by, HttpContext ctx)
    {
        await Cleanup();
        if (!PiImageBuilder.ToolsPresent()) return new(null, "Fleetdeck mangler værktøjerne sfdisk, mtools og xz (opdatér containeren).");
        var mode = r.Mode == "erstat" ? "erstat" : "ny";
        Machine? m = null;
        PiImage? image;
        string? kit = null, kitSystem = null;
        List<string> masked = [];
        if (mode == "erstat")
        {
            m = await db.Machines.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == r.MachineId);
            if (m is null) return new(null, "Maskinen findes ikke");
            if (m.Platform != Platforms.Linux) return new(null, "SD-kort kan kun laves til en Raspberry Pi med Linux");
            if (m.Protected && !string.Equals(r.Confirm?.Trim(), m.Label, StringComparison.OrdinalIgnoreCase) && !string.Equals(r.Confirm?.Trim(), m.Name, StringComparison.OrdinalIgnoreCase))
                return new(null, $"{m.Label} er beskyttet: skriv maskinens navn for at bekræfte");
            if (Kits.SafeName(r.Kit) is not { } k || !File.Exists(Path.Combine(Kits.Dir(paths, m.Id), k))) return new(null, "Vælg et gendannelsespunkt");
            var info = PiCardLogic.ReadKit(Path.Combine(Kits.Dir(paths, m.Id), k));
            if (info is null) return new(null, "Gendannelsespunktet kan ikke læses");
            var (img, err) = PiCardLogic.MatchBase(info.System, r.ImageId);
            if (img is null) return new(null, err);
            image = img; kit = k; kitSystem = PiCardLogic.Pretty(info.System); masked = info.MaskedUnits;
        }
        else
        {
            image = PiCardLogic.Image(r.ImageId);
            if (image is null) return new(null, "Vælg et styresystem");
            if (!image.Usable) return new(null, $"{image.Title} kan ikke bruges til et kort endnu: Fleetdecks førstestart er ikke afprøvet på det billede.");
        }

        var name = (r.Name ?? m?.Name ?? "").Trim().ToLowerInvariant();
        if (!PiCardLogic.HostName().IsMatch(name)) return new(null, "Skriv et gyldigt værtsnavn (bogstaver, tal og bindestreg)");
        if (mode == "ny" && await db.Machines.AnyAsync(x => x.Name.ToLower() == name)) return new(null, $"{name} findes allerede i Fleetdeck – brug \"Erstat kortet\" på maskinens side");
        var user = (r.User ?? "").Trim();
        if (!PiCardLogic.UserName().IsMatch(user) || user == "root") return new(null, "Skriv et gyldigt brugernavn (små bogstaver, ikke root)");
        var keys = (r.SshKeys ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (keys.Count == 0 || keys.Any(k => !PiCardLogic.SshKey().IsMatch(k))) return new(null, "Indsæt mindst én offentlig SSH-nøgle (ssh-ed25519 …). Kortet får ingen adgangskode.");
        var ipMode = r.IpMode == "static" ? "static" : "dhcp";
        string address = "", gateway = "", dns = "";
        var iface = string.IsNullOrWhiteSpace(r.Iface) ? "eth0" : r.Iface.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(iface, @"^(eth|end|enx|wlan)[0-9a-z]{0,14}$")) return new(null, "Ugyldigt navn på netkortet");
        if (ipMode == "static")
        {
            address = (r.Address ?? "").Trim(); gateway = (r.Gateway ?? "").Trim(); dns = (r.Dns ?? "").Trim();
            if (PiCardLogic.CheckAddress(address) is { } aerr) return new(null, aerr);
            if (!PiCardLogic.Ipv4(gateway)) return new(null, "Skriv gatewayens IP-adresse");
            if (dns.Length > 0 && dns.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Any(d => !PiCardLogic.Ipv4(d))) return new(null, "DNS skal være IP-adresser");
            dns = string.Join(';', dns.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries));
        }
        var (flags, badFlags) = PiCardLogic.SplitFlags(r.CmdlineFlags ?? m?.Profile?.PiCmdlineFlags);
        if (badFlags.Count > 0) return new(null, $"Disse flag må ikke lægges i cmdline.txt: {string.Join(' ', badFlags)}");
        var (lines, badLines) = PiCardLogic.SplitConfigLines(r.ConfigLines ?? m?.Profile?.PiConfigLines);
        if (badLines.Count > 0) return new(null, $"Disse linjer må ikke lægges i config.txt: {string.Join(" · ", badLines)}");
        var exclude = PiCardLogic.CleanExcludes(r.Exclude ?? m?.Profile?.PiExclude);
        var profileId = mode == "erstat" ? m!.ProfileId : r.ProfileId is > 0 ? r.ProfileId : null;
        if (profileId is int pid && !await db.Profiles.AnyAsync(x => x.Id == pid)) return new(null, "Profilen findes ikke");

        if (r.SaveToProfile == true && m?.Profile is { } prof)
        {
            prof.PiExclude = exclude; prof.PiConfigLines = string.Join('\n', lines); prof.PiCmdlineFlags = string.Join(' ', flags);
            await audit.Log(by, "picard.profile", $"{prof.Name}: udelad {exclude.Count}, config.txt {lines.Count}, cmdline {flags.Count}", m.Id);
        }

        var server = AgentEndpoints.AgentBase(ctx, await settings.Get("server_url"));
        var pin = server.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? await AgentEndpoints.TlsPin(settings, tls) ?? "" : "";
        var code = await enroll.CreateCode($"SD-kort: {name}", Life, m?.Id, profileId, by);
        var chosen = new PiCardChosen(user, ipMode, address, gateway, dns, iface, exclude, lines, flags, image.Title, kitSystem, masked, keys);   // offentlige nøgler: ingen hemmelighed
        var card = new PiCard
        {
            MachineId = m?.Id, Name = name, Mode = mode, KitName = kit, ImageId = image.Id, EnrollCodeId = code.Row.Id, ProfileId = profileId,
            Status = "bygger", Message = "Venter", CreatedBy = by, Expires = DateTime.UtcNow + Life, SettingsJson = JsonSerializer.Serialize(chosen, Web),
        };
        db.PiCards.Add(card);
        await db.SaveChangesAsync();
        await audit.Log(by, "picard.build", $"{name} ({(mode == "erstat" ? $"erstat fra {kit}" : "ny Pi")}) · {image.Title} · engangskode {code.Row.Hint}…", m?.Id);

        var cs = new PiCardSettings(server, pin, name, user, mode, profileId ?? 0, ipMode, iface, address, gateway, dns,
            r.Timezone is { Length: > 0 } tz && System.Text.RegularExpressions.Regex.IsMatch(tz, @"^[A-Za-z_]+/[A-Za-z_]+$") ? tz : "Europe/Copenhagen",
            r.Keymap is { Length: > 0 } km && System.Text.RegularExpressions.Regex.IsMatch(km, @"^[a-z]{2}$") ? km : "dk", "DK");
        var id = card.Id;
        var authorized = string.Join('\n', keys) + "\n";
        _ = Task.Run(() => BuildInBackground(id, image, cs, code.Code, authorized, flags, lines));
        return new(card, null);
    }

    private async Task BuildInBackground(int id, PiImage image, PiCardSettings cs, string code, string authorized, List<string> flags, List<string> lines)
    {
        using var scope = scopes.CreateScope();
        var sdb = scope.ServiceProvider.GetRequiredService<AppDb>();
        var lastSay = DateTime.MinValue;
        async Task Say(int pct, string text, string? status = null)
        {
            if (status is null && DateTime.UtcNow - lastSay < TimeSpan.FromSeconds(1) && pct < 100) return;
            lastSay = DateTime.UtcNow;
            await sdb.PiCards.Where(c => c.Id == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Progress, pct).SetProperty(c => c.Message, text.Length > 400 ? text[..400] : text)
                .SetProperty(c => c.Status, c => status ?? c.Status));
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
            await Say(2, "Tjekker basisbilledet");
            await images.Ensure(image, t => _ = Say(5, t), cts.Token);
            var firstrun = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "pi-firstrun.sh"), cts.Token);
            var firstboot = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Templates", "pi-firstboot.sh"), cts.Token);
            var file = $"{cs.Name}-{DateTime.Now:yyyy-MM-dd-HHmm}.img.xz";
            var res = await PiImageBuilder.Build(images.FileOf(image), Dir(id), file,
                (cmdline, config) => new PiImageBuilder.Files(PiCardLogic.MergeCmdline(cmdline, flags), PiCardLogic.MergeConfig(config, lines),
                    PiCardLogic.SettingsEnv(cs), code, authorized, firstrun, firstboot),
                (pct, text) => _ = Say(pct, text), cts.Token);
            await sdb.PiCards.Where(c => c.Id == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, "klar").SetProperty(c => c.Progress, 100)
                .SetProperty(c => c.Message, "Kortet er klar").SetProperty(c => c.File, Path.GetFileName(res.File)).SetProperty(c => c.Sha256, res.Sha256).SetProperty(c => c.Size, res.Size));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "SD-kort {Id} kunne ikke bygges", id);
            await Say(0, e is OperationCanceledException ? "Bygningen tog for lang tid og blev stoppet" : e.Message, "fejl");
            try { if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- hent og ryd op

    public async Task<(string? Path, string? Name)> FileFor(int id, string by)
    {
        var c = await db.PiCards.FindAsync(id);
        if (c is not { Status: "klar", File: { } f } || c.Expires < DateTime.UtcNow) return (null, null);
        var path = Path.Combine(Dir(id), f);
        if (!File.Exists(path)) return (null, null);
        c.Downloaded ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.Log(by, "picard.download", $"{c.Name}: {f} ({c.Size / 1048576} MB)", c.MachineId);
        return (path, f);
    }

    public async Task Delete(int id, string by)
    {
        var c = await db.PiCards.FindAsync(id);
        if (c is null) return;
        try { if (Directory.Exists(Dir(id))) Directory.Delete(Dir(id), true); } catch (IOException) { }
        c.Status = "slettet"; c.File = null;
        if (c.EnrollCodeId is int cid) await db.EnrollCodes.Where(x => x.Id == cid && x.UsedAt == null).ExecuteUpdateAsync(x => x.SetProperty(e => e.Expires, DateTime.UtcNow));
        await db.SaveChangesAsync();
        await audit.Log(by, "picard.delete", c.Name, c.MachineId);
    }

    /// <summary>Billeder ældre end 24 timer slettes (deres engangskode er alligevel udløbet).</summary>
    public async Task Cleanup()
    {
        var old = await db.PiCards.Where(c => c.Status == "klar" && c.Expires < DateTime.UtcNow).ToListAsync();
        foreach (var c in old)
        {
            try { if (Directory.Exists(Dir(c.Id))) Directory.Delete(Dir(c.Id), true); } catch (IOException) { }
            c.Status = "slettet"; c.File = null; c.Message = "Slettet efter 24 timer";
        }
        if (old.Count > 0) await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- første start

    /// <summary>Pi'en melder fra førstestarten (med engangskoden i en header, så den ikke havner i en log). Kun "startet" og "fejl".</summary>
    public async Task<bool> FromCard(string? code, string step)
    {
        if (step is not ("startet" or "fejl") || string.IsNullOrWhiteSpace(code)) return false;
        var hash = Crypto.Sha256(code.Trim());
        var ec = await db.EnrollCodes.AsNoTracking().FirstOrDefaultAsync(x => x.CodeHash == hash);
        if (ec is null || ec.Created < DateTime.UtcNow - TimeSpan.FromDays(3)) return false;
        var c = await db.PiCards.FirstOrDefaultAsync(x => x.EnrollCodeId == ec.Id);
        if (c is null) return false;
        AddEvent(c, step);
        await db.SaveChangesAsync();
        return true;
    }

    private static void AddEvent(PiCard c, string step, string? text = null)
    {
        var ev = JsonSerializer.Deserialize<List<PiCardEvent>>(c.EventsJson, Web) ?? [];
        if (ev.Count >= 40) return;
        ev.Add(new(step, DateTime.UtcNow, text));
        c.EventsJson = JsonSerializer.Serialize(ev, Web);
    }

    /// <summary>Kaldes ved tilmelding. Er koden et korts engangskode, sætter vi genskabelsen fra netop det gendannelsespunkt i gang
    /// (tilstanden "nyt kort") og én genstart – i stedet for den almindelige nyinstallation. true = klaret her.</summary>
    public async Task<bool> OnEnrolled(string? key, Machine m)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var hash = Crypto.Sha256(key.Trim());
        var ec = await db.EnrollCodes.AsNoTracking().FirstOrDefaultAsync(x => x.CodeHash == hash);
        if (ec is null) return false;
        var c = await db.PiCards.FirstOrDefaultAsync(x => x.EnrollCodeId == ec.Id);
        if (c is null) return false;
        c.MachineId = m.Id;
        AddEvent(c, "tilmeldt");
        if (c.Mode == "erstat" && c.KitName is { } kit)
        {
            var chosen = JsonSerializer.Deserialize<PiCardChosen>(c.SettingsJson, Web);
            await jobs.Create(m.Id, JobTypes.KitRestore, "Genskab fra gendannelsespunktet (nyt SD-kort)",
                new JobParams { Name = kit, Step = RestoreStep, Exclude = chosen?.Exclude ?? [], TimeoutMin = 120 }, RunAs.System, null, JobSource, "sd-kort", false);
            await jobs.Create(m.Id, JobTypes.Reboot, "Genstart efter genskabelsen",
                new JobParams { Delay = 60, OnlyIfPending = false, Message = "Det nye SD-kort er genskabt og genstarter om et minut." }, RunAs.System, null, JobSource, "sd-kort", false);
            await jobs.Commit(m.Id);
        }
        await db.SaveChangesAsync();
        await audit.Log("sd-kort", "picard.enrolled", $"{c.Name}: {(c.Mode == "erstat" ? $"genskaber fra {c.KitName}" : "ny Pi tilmeldt")}", m.Id);
        return true;
    }

    // ---------------------------------------------------------------- visning

    public async Task<object> View(PiCard c)
    {
        var chosen = JsonSerializer.Deserialize<PiCardChosen>(c.SettingsJson, Web);
        var ev = JsonSerializer.Deserialize<List<PiCardEvent>>(c.EventsJson, Web) ?? [];
        DateTime? At(string s) => ev.LastOrDefault(e => e.Step == s)?.At;
        var enrolled = At("tilmeldt");
        List<Job> js = [];
        Machine? m = null;
        if (c.MachineId is int mid && enrolled is { } t0)
        {
            js = await db.Jobs.AsNoTracking().Where(j => j.MachineId == mid && j.Source == JobSource && j.Created >= t0.AddMinutes(-1)).OrderBy(j => j.Id).ToListAsync();
            m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid);
        }
        var restore = js.FirstOrDefault(j => j.Type == JobTypes.KitRestore);
        var reboot = js.FirstOrDefault(j => j.Type == JobTypes.Reboot);
        var ready = c.Mode == "ny" ? enrolled is not null && m?.LastSeen > enrolled
            : reboot is { Status: JobStatus.Success, Finished: { } rf } && m?.LastSeen > rf.AddSeconds(60);
        var steps = new List<object>
        {
            new { step = "bygget", label = "Kortet er bygget", at = c.Status is "klar" or "slettet" && c.Sha256 is not null ? c.Created : (DateTime?)null },
            new { step = "hentet", label = "Hentet", at = c.Downloaded },
            new { step = "startet", label = "Startet", at = At("startet") },
            new { step = "tilmeldt", label = "Tilmeldt", at = enrolled },
        };
        if (c.Mode == "erstat")
        {
            steps.Add(new { step = "genskaber", label = "Pakker, filer og tjenester", at = restore?.Status == JobStatus.Success ? restore.Finished : null, running = restore?.Status == JobStatus.Running, detail = restore?.Step, failed = restore?.Status == JobStatus.Failed });
            steps.Add(new { step = "genstart", label = "Genstart", at = reboot?.Status == JobStatus.Success ? reboot.Finished : null });
        }
        steps.Add(new { step = "klar", label = "Klar", at = ready ? m?.LastSeen : null });
        return new
        {
            c.Id, c.Name, c.Mode, c.KitName, c.ImageId, imageTitle = chosen?.ImageTitle, c.Status, c.Progress, c.Message, c.File, c.Sha256, c.Size,
            c.Created, c.CreatedBy, c.Expires, c.Downloaded, c.MachineId,
            settings = chosen is null ? null : new { chosen.User, chosen.IpMode, chosen.Address, chosen.Gateway, chosen.Dns, chosen.Iface, chosen.Exclude, chosen.ConfigLines, chosen.CmdlineFlags, chosen.KitSystem, keys = chosen.Keys ?? [] },
            failedAt = At("fejl"), steps, services = Services(restore?.Output, chosen?.MaskedUnits ?? []),
        };
    }

    /// <summary>Tjenesterne efter genskabelsen, læst ud af genskabelsens egen udskrift, plus dem, der bevidst forbliver slået fra.</summary>
    public static List<object> Services(string? output, List<string> masked)
    {
        var list = new List<object>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            var l = line.Trim();
            foreach (var (prefix, state) in new[] { ("Startet: ", "kører"), ("Kører allerede: ", "kører"), ("KUNNE IKKE STARTE: ", "fejl"), ("Ikke startet (kørte ikke ved sikkerhedskopien): ", "stoppet"), ("Udeladt: ", "udeladt") })
                if (l.StartsWith(prefix, StringComparison.Ordinal)) list.Add(new { name = l[prefix.Length..].Trim(), state });
        }
        foreach (var u in masked) list.Add(new { name = u, state = "slået fra" });
        return list;
    }
}
