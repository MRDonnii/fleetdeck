using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public sealed record ExtensionStatus(string Id, string Name, string Description, string Gives, bool Installed, bool Pending);

/// <summary>Udvidelser pr. maskine: hvilke findes til dens styresystem, hvilke er installeret, og installér med ét klik.</summary>
public sealed class ExtensionService(AppDb db, JobService jobs, Paths paths, UsbIpadStore usb, ILogger<ExtensionService> log)
{
    private static (DateTime Mtime, ExtensionDef[] Defs)? _extra;

    /// <summary>De indbyggede plus egne fra /data/extensions.json (samme felter som ExtensionDef; samme id erstatter en indbygget).</summary>
    public IReadOnlyList<ExtensionDef> All()
    {
        var file = Path.Combine(paths.Data, "extensions.json");
        ExtensionDef[] extra = [];
        if (File.Exists(file))
        {
            var mt = File.GetLastWriteTimeUtc(file);
            if (_extra is { } c && c.Mtime == mt) extra = c.Defs;
            else
            {
                try { extra = JsonSerializer.Deserialize<ExtensionDef[]>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
                catch (JsonException e) { log.LogWarning("extensions.json kunne ikke læses: {Msg}", e.Message); }
                _extra = (mt, extra);
            }
        }
        return [.. Extensions.BuiltIn.Where(b => extra.All(x => x.Id != b.Id)), .. extra];
    }

    public async Task<List<ExtensionStatus>> For(Machine m)
    {
        var names = await db.Software.Where(s => s.MachineId == m.Id).Select(s => s.Name).ToListAsync();
        var hasWinget = await db.WingetIds.AnyAsync(w => w.MachineId == m.Id);
        var active = await db.Jobs.Where(j => j.MachineId == m.Id && JobStatus.Active.Contains(j.Status) && j.Title.StartsWith("Udvidelse: "))
            .Select(j => j.Title).ToListAsync();
        var list = new List<ExtensionStatus>();
        var hasTemps = Json.Des<HealthReport>(m.HealthJson).Live.Temps.Cpu is not null;
        foreach (var d in All())
        {
            if (d.For(m.Platform) is not { } how) continue;
            var installed = how.Detect switch
            {
                "winget" => hasWinget,
                Extensions.UsbIpadDetect => usb.Ready(m.Id),   // agenten melder selv, at pymobiledevice3 virker
                MacTemps.Detect => hasTemps,                   // Mac: målingen er indbygget i agenten – "installeret", når den melder en temperatur
                { Length: > 0 } rx => names.Any(n => System.Text.RegularExpressions.Regex.IsMatch(n, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                _ => false,
            };
            list.Add(new(d.Id, d.Name, d.Description, d.Gives, installed, active.Any(t => t.StartsWith($"Udvidelse: {d.Name}"))));
        }
        return list;
    }

    /// <summary>Opretter jobbene, der installerer udvidelsen (og dens efterfølgende opsætning). Returnerer antal jobs.</summary>
    public async Task<int> Install(Machine m, string id, string source, string by)
    {
        var d = All().FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Ukendt udvidelse: {id}");
        var how = d.For(m.Platform) ?? throw new InvalidOperationException($"{d.Name} findes ikke til {m.Platform switch { Platforms.Linux => "Linux", Platforms.Mac => "Mac", _ => "Windows" }}.");
        var title = $"Udvidelse: {d.Name}";
        var n = 0;
        switch (how.Type)
        {
            case "winget":
                await jobs.Create(m.Id, JobTypes.Install, title, new JobParams { Name = d.Name, PackageType = PackageTypes.Winget, Identifier = how.Identifier, Args = "--scope machine" },
                    RunAs.System, null, source, by); n++;
                break;
            case "brew":
                await jobs.Create(m.Id, JobTypes.Install, title, new JobParams { Name = d.Name, PackageType = PackageTypes.Brew, Identifier = how.Identifier },
                    RunAs.System, null, source, by); n++;
                break;
            case "pkg" or "flatpak":
                await jobs.Create(m.Id, JobTypes.Install, title, new JobParams { Name = d.Name, PackageType = how.Type == "pkg" ? PackageTypes.Pkg : PackageTypes.Flatpak, Identifier = how.Identifier },
                    RunAs.System, null, source, by); n++;
                break;
            case "script":
                await jobs.Create(m.Id, JobTypes.Script, title, new JobParams { Name = d.Name, Body = how.Script, TimeoutMin = 30 }, RunAs.System, null, source, by);
                return 1;
            case "helper":
                var fix = HelperCatalog.Get(how.Identifier ?? "") ?? throw new InvalidOperationException($"Hjælperen kender ikke {how.Identifier}");
                await jobs.Create(m.Id, JobTypes.Script, title, new JobParams { Name = d.Name, Body = fix.Script, TimeoutMin = fix.TimeoutMin }, RunAs.System, null, source, by);
                return 1;
            default: throw new InvalidOperationException($"Ukendt installationstype: {how.Type}");
        }
        if (how.Script is { Length: > 0 } after)
        {
            await jobs.Create(m.Id, JobTypes.Script, $"{title} – opsætning", new JobParams { Name = d.Name, Body = after, TimeoutMin = 15 }, RunAs.System, null, source, by);
            n++;
        }
        return n;
    }
}
