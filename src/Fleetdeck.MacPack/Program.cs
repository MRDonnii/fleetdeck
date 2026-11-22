// fleetdeck-macpack – Mac-pakken bygget på Linux (Docker-billedet):
//   app    --publish <mappe> --version <x.y.z> [--icon fleetdeck.ico] [--menu <mappe>] --out Fleetdeck.app     .app-pakke + ad hoc-signatur
//   dmg    --app Fleetdeck.app [--icon fleetdeck.ico] --out Fleetdeck.dmg                     disk-image: appen, genvej til Programmer, vejledning
//   all    --publish <mappe> --version <x.y.z> [--icon fleetdeck.ico] [--menu <mappe>] --out Fleetdeck.dmg     begge dele
//          --menu: mappe med FleetdeckMenu (+ menu-icon@2x.png), bygget af CI på macOS – lægges i Contents/Resources/menu
//   verify --app Fleetdeck.app                                                                 tjek signaturen (siderne, Info.plist, CodeResources)
using Fleetdeck.MacPack;

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string Need(string name) => Arg(name) ?? throw new ArgumentException($"Mangler {name}");
var cmd = args.FirstOrDefault() ?? "";
try
{
    byte[]? icns = Arg("--icon") is { } ico ? Icns.FromPngs(Icns.PngsFromIco(File.ReadAllBytes(ico))) : null;
    switch (cmd)
    {
        case "app":
            AppBundle.Build(new AppBundle.Options(Need("--publish"), Need("--out"), Need("--version"), Icns: icns, MenuDir: Arg("--menu")));
            Console.WriteLine($"{Need("--out")}: {Verify(Need("--out"))}");
            return 0;
        case "dmg":
            Dmg.Build(Need("--app"), Need("--out"), icns, Arg("--volume") ?? "Fleetdeck");
            Console.WriteLine($"{Need("--out")}: {new FileInfo(Need("--out")).Length / 1024 / 1024} MB");
            return 0;
        case "all":
        {
            var work = Directory.CreateTempSubdirectory("fleetdeck-macpack-").FullName;
            try
            {
                var app = Path.Combine(work, "Fleetdeck.app");
                AppBundle.Build(new AppBundle.Options(Need("--publish"), app, Need("--version"), Icns: icns, MenuDir: Arg("--menu")));
                Console.WriteLine($"Fleetdeck.app: {Verify(app)}");
                Dmg.Build(app, Need("--out"), icns, Arg("--volume") ?? "Fleetdeck");
                Console.WriteLine($"{Need("--out")}: {new FileInfo(Need("--out")).Length / 1024 / 1024} MB");
            }
            finally { Directory.Delete(work, true); }
            return 0;
        }
        case "verify":
            var v = Verify(Need("--app"));
            Console.WriteLine(v);
            return v.StartsWith("OK") ? 0 : 1;
        default:
            Console.Error.WriteLine("Brug: fleetdeck-macpack app|dmg|all|verify … (se kommentaren øverst i Program.cs)");
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException or InvalidDataException)
{
    Console.Error.WriteLine($"Fejl: {e.Message}");
    return 1;
}

static string Verify(string app)
{
    var c = Path.Combine(app, "Contents");
    var r = MachOSigner.Verify(File.ReadAllBytes(Path.Combine(c, "MacOS", "Fleetdeck")),
        File.ReadAllBytes(Path.Combine(c, "Info.plist")), File.ReadAllBytes(Path.Combine(c, "_CodeSignature", "CodeResources")));
    return r.Ok ? $"OK – ad hoc-signeret som {r.Identifier}" : $"FEJL – {r.Why}";
}

namespace Fleetdeck.MacPack
{
    /// <summary>Disk-imaget, brugeren henter: Fleetdeck.app, en genvej til Programmer og en kort vejledning (inkl. Gatekeeper).</summary>
    public static class Dmg
    {
        public const string ReadmeName = "Læs mig først.txt";

        public static void Build(string app, string outDmg, byte[]? icns, string volume = "Fleetdeck")
        {
            var hfs = new HfsPlus(volume);
            var appFolder = hfs.AddTree(hfs.Root, Path.GetFileName(app.TrimEnd('/')), app);
            appFolder.FinderFlags = 0x2000;   // kHasBundle
            hfs.AddSymlink(hfs.Root, "Programmer", "/Applications");
            hfs.AddFile(hfs.Root, ReadmeName, System.Text.Encoding.UTF8.GetBytes(Readme));
            if (icns is not null)
            {
                hfs.AddFile(hfs.Root, ".VolumeIcon.icns", icns).FinderFlags = HfsPlus.FinderInvisible;
                hfs.Root.FinderFlags = HfsPlus.FinderHasCustomIcon;
            }
            var raw = outDmg + ".hfs.tmp";
            try
            {
                using (var fs = new FileStream(raw, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose))
                {
                    hfs.Write(fs);
                    using var o = File.Create(outDmg);
                    Udif.Write(fs, o);
                }
            }
            finally { if (File.Exists(raw)) File.Delete(raw); }
        }

        public const string Readme = """
            Fleetdeck til Mac
            =================

            1. Træk Fleetdeck over i mappen Programmer.
            2. Åbn Fleetdeck fra Programmer.

               Første gang siger macOS, at programmet ikke kan kontrolleres (det er ikke købt
               signeret hos Apple – det kommer fra din egen Fleetdeck-server):
               - macOS 15 (Sequoia) og nyere: tryk "Udført", åbn Systemindstillinger →
                 Anonymitet og sikkerhed, rul ned og tryk "Åbn alligevel" ud for Fleetdeck.
                 Bekræft med din adgangskode, og tryk "Åbn alligevel" igen.
               - macOS 14 og ældre: højreklik (ctrl-klik) på Fleetdeck → Åbn → Åbn.

            3. Skriv serverens adresse (eller tryk "Find automatisk"), indsæt
               tilmeldingsnøglen, tryk "Test forbindelse", vælg en profil og tryk
               "Installér og forbind". Mac'en beder om din adgangskode.
            4. Giv Fleetdeck Fuld diskadgang, når vinduet viser hvordan
               (Systemindstillinger → Anonymitet og sikkerhed → Fuld diskadgang).

            Mens Fleetdeck kører, står ikonet i Dock (og i Cmd-Tab) – så kan du se, at den er
            åben, og klikke på ikonet for at hente vinduet frem. Bagefter ligger Fleetdeck også i
            menulinjen øverst til højre: status, "Synkronisér nu", pause, "Åbn Fleetdeck" og fejlfinding.

            Kræver macOS 12 (Monterey) eller nyere. En Intel-Mac med macOS 10.15 (Catalina) eller
            11 (Big Sur) skal bruge "Ældre Intel-Mac" på serverens downloadside. Hvilken macOS du
            har, står under Apple-menuen → Om denne Mac.

            Starter Fleetdeck ikke? Se ~/Library/Logs/Fleetdeck/app-start.log (Finder → Gå →
            Gå til mappe…, skriv ~/Library/Logs/Fleetdeck).

            macOS-opdateringer på Apple silicon: Fleetdeck henter dem, men macOS kræver, at
            ejeren selv trykker "Genstart nu" i Softwareopdatering (Fleetdeck åbner vinduet).

            Kan du ikke åbne appen, kan du stadig installere med kommandoen fra Fleetdeck
            (Maskiner → Tilføj maskine → Mac) i Terminal.

            """;
    }
}
