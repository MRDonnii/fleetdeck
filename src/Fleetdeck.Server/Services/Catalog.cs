using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Programkatalog med de populære gratis programmer (samme udvalg som ninite.com), som winget-pakker med en kort dansk
/// beskrivelse. De lægges i Pakker, men ikke på nogen profil – så installeres intet, før du selv vælger det.
/// </summary>
public static class AppCatalog
{
    private static Dictionary<string, string>? _byName;
    /// <summary>Katalogets beskrivelse ud fra programmets navn (fx "Mozilla Firefox (x64 da)" → Firefox), ellers null.</summary>
    public static string? DescriptionFor(string name)
    {
        _byName ??= All.GroupBy(i => Norm(i.Name)).ToDictionary(g => g.Key, g => g.First().Description);
        var n = Norm(name);
        if (n.Length == 0) return null;
        if (_byName.TryGetValue(n, out var d)) return d;
        // længste katalognavn, som programnavnet begynder med (mindst 4 tegn, så "Go" ikke rammer alt)
        return _byName.Where(kv => kv.Key.Length >= 4 && n.StartsWith(kv.Key)).OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }
    private static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("mozilla", "");

    public sealed record Item(string Name, string Id, string Category, string Description, string Type = PackageTypes.Winget, string Args = "", bool User = false);

    private const string X86 = "--architecture x86";

    public static readonly Item[] All =
    [
        // ---------------------------------------------------------------- browsere
        new("Google Chrome", "Google.Chrome", "Browsere", "Hurtig webbrowser fra Google med synkronisering via Google-konto."),
        new("Opera", "Opera.Opera", "Browsere", "Alternativ browser med indbygget reklameblokering og gratis VPN."),
        new("Firefox", "Mozilla.Firefox", "Browsere", "Åben browser fra Mozilla med fokus på privatliv og mange udvidelser."),
        new("Microsoft Edge", "Microsoft.Edge", "Browsere", "Microsofts browser bygget på Chromium. Følger normalt med Windows."),
        new("Brave", "Brave.Brave", "Browsere", "Browser der blokerer reklamer og sporing fra starten."),
        new("Vivaldi", "Vivaldi.Vivaldi", "Browsere", "Meget tilpasselig browser med fanestakke, noter og indbygget mail."),

        // ---------------------------------------------------------------- kommunikation
        new("Zoom", "Zoom.Zoom", "Kommunikation", "Videomøder, opkald og skærmdeling."),
        new("Discord", "Discord.Discord", "Kommunikation", "Tale-, video- og tekstchat – populært til spil og fællesskaber.", User: true),
        new("Microsoft Teams", "Microsoft.Teams", "Kommunikation", "Chat, møder og opkald fra Microsoft (arbejde og skole)."),
        new("Slack", "SlackTechnologies.Slack", "Kommunikation", "Teamchat med kanaler, tråde og integrationer."),
        new("Telegram", "Telegram.TelegramDesktop", "Kommunikation", "Hurtig beskedtjeneste med grupper, kanaler og synkronisering mellem enheder.", User: true),
        new("WhatsApp", "9NKSQGP7F2NH", "Kommunikation", "Beskeder og opkald – samme konto som på telefonen.", PackageTypes.MsStore, User: true),
        new("Pidgin", "Pidgin.Pidgin", "Kommunikation", "Chatprogram der samler flere chattjenester (XMPP, IRC m.fl.) i ét vindue."),
        new("Thunderbird", "Mozilla.Thunderbird", "Kommunikation", "Gratis mailprogram fra Mozilla med kalender og kontakter."),
        new("Trillian", "CeruleanStudios.Trillian", "Kommunikation", "Chatprogram der samler flere chattjenester."),

        // ---------------------------------------------------------------- medier
        new("iTunes", "Apple.iTunes", "Medier", "Apples musik- og medieafspiller, også til backup af iPhone/iPad."),
        new("VLC", "VideoLAN.VLC", "Medier", "Afspiller næsten alle video- og lydformater uden ekstra codecs."),
        new("AIMP", "AIMP.AIMP", "Medier", "Let og hurtig musikafspiller med god lydkvalitet."),
        new("foobar2000", "PeterPawlowski.foobar2000", "Medier", "Minimalistisk og meget tilpasselig musikafspiller for entusiaster."),
        new("Winamp", "Winamp.Winamp", "Medier", "Den klassiske musikafspiller."),
        new("MusicBee", "", "Medier", "Musikafspiller og -bibliotek med tags, podcasts og synkronisering. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("Audacity", "Audacity.Audacity", "Medier", "Optag og redigér lyd – klip, effekter og støjfjernelse."),
        new("K-Lite Codec Pack", "CodecGuide.K-LiteCodecPack.Standard", "Medier", "Codecs til alle videoformater plus Media Player Classic."),
        new("Spotify", "Spotify.Spotify", "Medier", "Musik- og podcaststreaming.", User: true),
        new("MediaMonkey", "VentisMedia.MediaMonkey.2024", "Medier", "Organiserer store musiksamlinger, tags, rip og synkronisering."),
        new("HandBrake", "HandBrake.HandBrake", "Medier", "Konverterer og komprimerer video, fx til mindre filer eller andre formater."),
        new("OBS Studio", "OBSProject.OBSStudio", "Medier", "Optag skærmen og livestream til Twitch/YouTube."),

        // ---------------------------------------------------------------- runtimes (nogle programmer og spil kræver dem)
        new(".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8", "Runtime", "Kræves af programmer bygget med .NET 8 (64-bit)."),
        new(".NET Desktop Runtime 8 (32-bit)", "Microsoft.DotNet.DesktopRuntime.8", "Runtime", "32-bit udgave til ældre .NET 8-programmer.", Args: X86),
        new(".NET Desktop Runtime 9", "Microsoft.DotNet.DesktopRuntime.9", "Runtime", "Kræves af programmer bygget med .NET 9 (64-bit)."),
        new(".NET Desktop Runtime 9 (32-bit)", "Microsoft.DotNet.DesktopRuntime.9", "Runtime", "32-bit udgave til .NET 9-programmer.", Args: X86),
        new(".NET Desktop Runtime 10", "Microsoft.DotNet.DesktopRuntime.10", "Runtime", "Kræves af programmer bygget med .NET 10 (64-bit)."),
        new(".NET Desktop Runtime 10 (32-bit)", "Microsoft.DotNet.DesktopRuntime.10", "Runtime", "32-bit udgave til .NET 10-programmer.", Args: X86),
        new("ASP.NET Core Runtime 8", "Microsoft.DotNet.AspNetCore.8", "Runtime", "Kræves af web-/serverprogrammer bygget med ASP.NET Core 8."),
        new("ASP.NET Core Runtime 9", "Microsoft.DotNet.AspNetCore.9", "Runtime", "Kræves af web-/serverprogrammer bygget med ASP.NET Core 9."),
        new("ASP.NET Core Runtime 10", "Microsoft.DotNet.AspNetCore.10", "Runtime", "Kræves af web-/serverprogrammer bygget med ASP.NET Core 10."),
        new("VSTO Runtime", "Microsoft.VSTOR", "Runtime", "Visual Studio Tools for Office – kræves af nogle Office-tilføjelser."),
        new("Java 8 (Temurin JRE)", "EclipseAdoptium.Temurin.8.JRE", "Runtime", "Java 8 til ældre Java-programmer (64-bit)."),
        new("Java 8 (Temurin JRE, 32-bit)", "EclipseAdoptium.Temurin.8.JRE", "Runtime", "32-bit Java 8 til meget gamle programmer.", Args: X86),
        new("Java 11 (Temurin JRE)", "EclipseAdoptium.Temurin.11.JRE", "Runtime", "Java 11 LTS til Java-programmer."),
        new("Java 17 (Temurin JRE)", "EclipseAdoptium.Temurin.17.JRE", "Runtime", "Java 17 LTS – fx til Minecraft-servere og mange værktøjer."),
        new("Java 21 (Temurin JRE)", "EclipseAdoptium.Temurin.21.JRE", "Runtime", "Java 21 LTS – nyere Java-programmer og Minecraft."),
        new("Java 25 (Temurin JRE)", "EclipseAdoptium.Temurin.25.JRE", "Runtime", "Java 25 LTS – den nyeste langtidsudgave."),
        new("JDK 8 (Temurin)", "EclipseAdoptium.Temurin.8.JDK", "Udvikling", "Java-udviklingsværktøjer, version 8."),
        new("JDK 8 (Temurin, 32-bit)", "EclipseAdoptium.Temurin.8.JDK", "Udvikling", "32-bit Java 8-udviklingsværktøjer.", Args: X86),
        new("JDK 11 (Temurin)", "EclipseAdoptium.Temurin.11.JDK", "Udvikling", "Java-udviklingsværktøjer, version 11 LTS."),
        new("JDK 17 (Temurin)", "EclipseAdoptium.Temurin.17.JDK", "Udvikling", "Java-udviklingsværktøjer, version 17 LTS."),
        new("JDK 21 (Temurin)", "EclipseAdoptium.Temurin.21.JDK", "Udvikling", "Java-udviklingsværktøjer, version 21 LTS."),
        new("JDK 25 (Temurin)", "EclipseAdoptium.Temurin.25.JDK", "Udvikling", "Java-udviklingsværktøjer, version 25 LTS."),
        new("JDK 8 (Amazon Corretto)", "Amazon.Corretto.8.JDK", "Udvikling", "Amazons gratis Java 8-udviklingsværktøjer."),
        new("JDK 11 (Amazon Corretto)", "Amazon.Corretto.11.JDK", "Udvikling", "Amazons gratis Java 11-udviklingsværktøjer."),
        new("JDK 17 (Amazon Corretto)", "Amazon.Corretto.17.JDK", "Udvikling", "Amazons gratis Java 17-udviklingsværktøjer."),
        new("JDK 21 (Amazon Corretto)", "Amazon.Corretto.21.JDK", "Udvikling", "Amazons gratis Java 21-udviklingsværktøjer."),
        new("JDK 25 (Amazon Corretto)", "Amazon.Corretto.25.JDK", "Udvikling", "Amazons gratis Java 25-udviklingsværktøjer."),
        new("JRE 8 (Amazon Corretto)", "Amazon.Corretto.8.JRE", "Runtime", "Amazons gratis Java 8 til at køre Java-programmer."),
        new("VC++ Redist 2015+ (x64)", "Microsoft.VCRedist.2015+.x64", "Runtime", "Microsoft C/C++-biblioteker som de fleste programmer og spil kræver."),
        new("VC++ Redist 2015+ (x86)", "Microsoft.VCRedist.2015+.x86", "Runtime", "32-bit udgave – kræves af mange spil og ældre programmer."),
        new("VC++ Redist 2013 (x64)", "Microsoft.VCRedist.2013.x64", "Runtime", "Ældre C/C++-biblioteker til programmer fra ca. 2013."),
        new("VC++ Redist 2013 (x86)", "Microsoft.VCRedist.2013.x86", "Runtime", "Ældre 32-bit C/C++-biblioteker (2013)."),
        new("VC++ Redist 2012 (x64)", "Microsoft.VCRedist.2012.x64", "Runtime", "Ældre C/C++-biblioteker (2012)."),
        new("VC++ Redist 2012 (x86)", "Microsoft.VCRedist.2012.x86", "Runtime", "Ældre 32-bit C/C++-biblioteker (2012)."),
        new("VC++ Redist 2010 (x64)", "Microsoft.VCRedist.2010.x64", "Runtime", "Ældre C/C++-biblioteker (2010) – bruges af gamle spil."),
        new("VC++ Redist 2010 (x86)", "Microsoft.VCRedist.2010.x86", "Runtime", "Ældre 32-bit C/C++-biblioteker (2010)."),
        new("VC++ Redist 2008 (x64)", "Microsoft.VCRedist.2008.x64", "Runtime", "Meget gamle C/C++-biblioteker (2008)."),
        new("VC++ Redist 2008 (x86)", "Microsoft.VCRedist.2008.x86", "Runtime", "Meget gamle 32-bit C/C++-biblioteker (2008)."),
        new("VC++ Redist 2005 (x64)", "Microsoft.VCRedist.2005.x64", "Runtime", "Meget gamle C/C++-biblioteker (2005)."),
        new("VC++ Redist 2005 (x86)", "Microsoft.VCRedist.2005.x86", "Runtime", "Meget gamle 32-bit C/C++-biblioteker (2005)."),

        // ---------------------------------------------------------------- billeder og design
        new("Krita", "KDE.Krita", "Design", "Tegne- og maleprogram til digital kunst."),
        new("Blender", "BlenderFoundation.Blender", "Design", "3D-modellering, animation, rendering og videoredigering."),
        new("Paint.NET", "dotPDN.PaintDotNet", "Design", "Nem og hurtig billedredigering med lag og effekter."),
        new("GIMP", "GIMP.GIMP.3", "Design", "Avanceret gratis billedbehandling – et alternativ til Photoshop."),
        new("IrfanView", "IrfanSkiljan.IrfanView", "Design", "Meget hurtig billedviser med simpel redigering og batch-konvertering."),
        new("XnView", "XnSoft.XnView.Classic", "Design", "Billedviser og -organisering der kan åbne hundredvis af formater."),
        new("Inkscape", "Inkscape.Inkscape", "Design", "Vektortegning (SVG) – logoer, illustrationer og diagrammer."),
        new("FastStone Image Viewer", "FastStone.Viewer", "Design", "Billedviser med redigering, sammenligning og diasshow."),
        new("Greenshot", "Greenshot.Greenshot", "Design", "Skærmbilleder med pile, tekst og sløring."),
        new("ShareX", "ShareX.ShareX", "Design", "Skærmbilleder og -optagelser med mange værktøjer og upload."),
        new("Bambu Studio", "Bambulab.Bambustudio", "Design", "Slicer til 3D-print, især Bambu Lab-printere."),
        new("UltiMaker Cura", "Ultimaker.Cura", "Design", "Slicer til 3D-print til de fleste printere."),

        // ---------------------------------------------------------------- dokumenter
        new("Foxit PDF Reader", "Foxit.FoxitReader", "Kontor", "PDF-læser med kommentarer og udfyldning af formularer."),
        new("LibreOffice", "TheDocumentFoundation.LibreOffice", "Kontor", "Gratis kontorpakke: tekst, regneark og præsentationer (læser Office-filer)."),
        new("SumatraPDF", "SumatraPDF.SumatraPDF", "Kontor", "Lille og lynhurtig læser til PDF, e-bøger og tegneserier."),
        new("CutePDF Writer", "AcroSoftware.CutePDFWriter", "Kontor", "Printerdriver, der gemmer alt, du udskriver, som PDF."),
        new("PDF24 Creator", "geeksoftwareGmbH.PDF24Creator", "Kontor", "PDF-værktøjskasse: lav, saml, del, komprimér og konvertér PDF'er."),
        new("Ghostscript", "", "Kontor", "Fortolker til PostScript og PDF – bruges af andre programmer. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("Apache OpenOffice", "Apache.OpenOffice", "Kontor", "Ældre gratis kontorpakke (LibreOffice anbefales i stedet)."),

        // ---------------------------------------------------------------- sikkerhed
        new("Malwarebytes", "Malwarebytes.Malwarebytes", "Sikkerhed", "Finder og fjerner malware. Gratis udgave scanner efter behov."),
        new("Avast Free Antivirus", "XPDNZJFNCR1B07", "Sikkerhed", "Gratis antivirus. Bemærk: Windows Defender slås fra, når et andet antivirus er installeret.", PackageTypes.MsStore),
        new("AVG AntiVirus Free", "XP8BX2DWV7TF50", "Sikkerhed", "Gratis antivirus. Bemærk: Windows Defender slås fra, når et andet antivirus er installeret.", PackageTypes.MsStore),
        new("Spybot Search & Destroy", "", "Sikkerhed", "Finder spyware og adware. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("Avira Free Antivirus", "XPFD23M0L795KD", "Sikkerhed", "Gratis antivirus. Bemærk: Windows Defender slås fra, når et andet antivirus er installeret.", PackageTypes.MsStore),
        new("SUPERAntiSpyware", "SUPERAntiSpyware.SUPERAntiSpyware", "Sikkerhed", "Scanner efter spyware og anden uønsket software."),
        new("KeePass 2", "DominikReichl.KeePass", "Sikkerhed", "Adgangskodeboks gemt lokalt i en krypteret fil."),
        new("KeePassXC", "KeePassXCTeam.KeePassXC", "Sikkerhed", "Moderne KeePass-kompatibel adgangskodeboks med browserudvidelse."),
        new("1Password", "AgileBits.1Password", "Sikkerhed", "Adgangskodeadministrator med synkronisering (kræver abonnement)."),
        new("Bitwarden", "Bitwarden.Bitwarden", "Sikkerhed", "Gratis, open source-adgangskodeadministrator med synkronisering."),
        new("Tailscale", "Tailscale.Tailscale", "Netværk", "Nem privat VPN mellem dine enheder (bygget på WireGuard)."),
        new("WireGuard", "WireGuard.WireGuard", "Netværk", "Hurtig og moderne VPN-tunnel."),

        // ---------------------------------------------------------------- online og deling
        new("qBittorrent", "qBittorrent.qBittorrent", "Netværk", "Gratis torrent-klient uden reklamer."),
        new("Dropbox", "Dropbox.Dropbox", "Online", "Synkroniserer filer med Dropbox-skyen."),
        new("Google Drive", "Google.GoogleDrive", "Online", "Synkroniserer filer med Google Drive."),
        new("OneDrive", "Microsoft.OneDrive", "Online", "Microsofts skylager. Følger normalt med Windows.", User: true),
        new("SugarSync", "IPVanish.SugarSync", "Online", "Skybackup og filsynkronisering."),
        new("Evernote", "Evernote.Evernote", "Online", "Noter, lister og dokumenter synkroniseret mellem enheder."),
        new("Google Earth Pro", "Google.EarthPro", "Online", "3D-globus med satellitbilleder og Street View."),

        // ---------------------------------------------------------------- spil
        new("Steam", "Valve.Steam", "Spil", "Valves spilbutik og launcher."),
        new("Epic Games Launcher", "EpicGames.EpicGamesLauncher", "Spil", "Epic Games Store – gratis spil hver uge og Fortnite."),

        // ---------------------------------------------------------------- AI og hjælp
        new("ChatGPT", "OpenAI.ChatGPT", "Kommunikation", "OpenAIs AI-assistent som program.", User: true),
        new("Claude", "Anthropic.Claude", "Kommunikation", "Anthropics AI-assistent som program.", User: true),
        new("Grammarly", "Grammarly.Grammarly", "Kontor", "Retter stavning og grammatik (engelsk) i alle programmer.", User: true),
        new("NVDA", "NVAccess.NVDA", "Værktøjer", "Gratis skærmlæser til blinde og svagtseende."),
        new("Everything", "voidtools.Everything", "Værktøjer", "Finder filer på hele pc'en på et øjeblik."),

        // ---------------------------------------------------------------- værktøjer
        new("Wireshark", "WiresharkFoundation.Wireshark", "Netværk", "Optager og analyserer netværkstrafik."),
        new("PowerToys", "Microsoft.PowerToys", "Værktøjer", "Microsofts ekstra værktøjer: FancyZones, farvevælger, omdøb mange filer m.m."),
        new("AnyDesk", "AnyDesk.AnyDesk", "Netværk", "Fjernskrivebord – styr en anden pc over internettet."),
        new("TeamViewer", "TeamViewer.TeamViewer", "Netværk", "Fjernsupport og fjernskrivebord."),
        new("Windows App", "9N1F85V9T8BN", "Netværk", "Microsofts fjernskrivebord til Windows 365, Azure Virtual Desktop og Remote Desktop.", PackageTypes.MsStore),
        new("ImgBurn", "LIGHTNINGUK.ImgBurn", "Værktøjer", "Brænder cd/dvd/Blu-ray og laver ISO-filer."),
        new("TightVNC", "GlavSoft.TightVNC", "Netværk", "VNC-fjernskrivebord (server og viewer)."),
        new("UltraVNC", "uvncbvba.UltraVNC", "Netværk", "VNC-fjernskrivebord med filoverførsel (server og viewer)."),
        new("RustDesk", "", "Netværk", "Open source-fjernskrivebord – alternativ til TeamViewer/AnyDesk. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("TeraCopy", "CodeSector.TeraCopy", "Værktøjer", "Hurtigere og sikrere kopiering af filer med kontrol og pause."),
        new("CDBurnerXP", "", "Værktøjer", "Brænder cd/dvd og laver lyd-cd'er. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("Revo Uninstaller", "RevoUninstaller.RevoUninstaller", "Værktøjer", "Afinstallerer programmer grundigt, inkl. rester i registreringsdatabasen."),
        new("Launchy", "CodeJelly.Launchy", "Værktøjer", "Start programmer hurtigt med en tastaturgenvej."),
        new("WinDirStat", "WinDirStat.WinDirStat", "Værktøjer", "Viser hvad der fylder på disken som et farvekort."),
        new("WizTree", "AntibodySoftware.WizTree", "Værktøjer", "Lynhurtig oversigt over hvad der fylder på disken."),
        new("Advanced IP Scanner", "Famatech.AdvancedIPScanner", "Netværk", "Finder alle enheder på netværket."),
        new("Glary Utilities", "Glarysoft.GlaryUtilities", "Værktøjer", "Samling af oprydnings- og vedligeholdelsesværktøjer."),
        new("InfraRecorder", "ChristianKindahl.InfraRecorder", "Værktøjer", "Simpelt program til at brænde cd/dvd."),
        new("Open-Shell", "Open-Shell.Open-Shell-Menu", "Værktøjer", "Klassisk startmenu som i Windows 7."),
        new("CCleaner", "Piriform.CCleaner", "Værktøjer", "Rydder midlertidige filer og browserdata."),
        new("7-Zip", "7zip.7zip", "Værktøjer", "Pakker og udpakker zip, 7z, rar og mange andre arkiver."),
        new("PeaZip", "Giorgiotani.Peazip", "Værktøjer", "Arkivprogram med kryptering og over 200 formater."),
        new("WinRAR", "RARLab.WinRAR", "Værktøjer", "Pakker og udpakker rar og zip (prøveversion, licens anbefales)."),

        // ---------------------------------------------------------------- udvikling
        new("Python 3", "Python.Python.3.15", "Udvikling", "Programmeringssproget Python 3.", Args: "--scope machine"),
        new("Python 2.7", "Python.Python.2", "Udvikling", "Gammel Python 2 – kun til meget gamle scripts."),
        new("Git", "Git.Git", "Udvikling", "Versionsstyring af kode (inkl. Git Bash)."),
        new("GitHub Desktop", "GitHub.GitHubDesktop", "Udvikling", "Grafisk Git-program til GitHub.", User: true),
        new("Docker Desktop", "Docker.DockerDesktop", "Udvikling", "Kør containere på Windows (kræver WSL2)."),
        new("Node.js 22 LTS", "OpenJS.NodeJS.22", "Udvikling", "JavaScript-runtime, LTS-linje 22."),
        new("Node.js LTS (nyeste)", "OpenJS.NodeJS.LTS", "Udvikling", "JavaScript-runtime, nyeste LTS-version."),
        new("Node.js (nyeste)", "OpenJS.NodeJS", "Udvikling", "JavaScript-runtime, nyeste version."),
        new("FileZilla", "", "Netværk", "FTP/SFTP-klient til at overføre filer til servere. Findes ikke i winget: hent installationsfilen fra producenten, læg den i Installers og vælg den her.", PackageTypes.File),
        new("Notepad++", "Notepad++.Notepad++", "Udvikling", "Hurtig teksteditor til kode med farver og faner."),
        new("Sublime Text 4", "SublimeHQ.SublimeText.4", "Udvikling", "Hurtig og elegant kodeeditor (prøveversion, licens til fast brug)."),
        new("WinSCP", "WinSCP.WinSCP", "Netværk", "SFTP/SCP-filoverførsel til Linux-servere som Unraid."),
        new("PuTTY", "PuTTY.PuTTY", "Netværk", "SSH- og telnet-klient til servere og netværksudstyr."),
        new("WinMerge", "WinMerge.WinMerge", "Udvikling", "Sammenligner og fletter filer og mapper."),
        new("Eclipse IDE (Java)", "EclipseFoundation.Eclipse.Java", "Udvikling", "Udviklingsmiljø til Java (kræver en JDK)."),
        new("Visual Studio Code", "Microsoft.VisualStudioCode", "Udvikling", "Microsofts populære kodeeditor med udvidelser.", User: true),
        new("Cursor", "Anysphere.Cursor", "Udvikling", "Kodeeditor med indbygget AI (bygget på VS Code).", User: true),
    ];

    /// <summary>Tilføj de pakker der mangler. Eksisterende pakker (samme ID og arkitektur) røres ikke.</summary>
    public static async Task<int> Import(AppDb db, AuditService audit, string by)
    {
        var existing = (await db.Packages.AsNoTracking().Select(p => new { p.Type, p.Identifier, p.Args }).ToListAsync())
            .Select(p => (p.Type, p.Identifier.ToLowerInvariant(), Arch(p.Args))).ToHashSet();
        var names = (await db.Packages.AsNoTracking().Select(p => p.Name.ToLower()).ToListAsync()).ToHashSet();
        foreach (var n in await db.Packages.AsNoTracking().Where(p => p.Type == PackageTypes.File).Select(p => p.Name.ToLower()).ToListAsync())
            existing.Add((PackageTypes.File, "navn:" + n, false));
        var added = 0;
        foreach (var i in All)
        {
            var key = i.Id.Length > 0 ? i.Id.ToLowerInvariant() : "navn:" + i.Name.ToLowerInvariant();
            if (!existing.Add((i.Type, key, Arch(i.Args)))) continue;
            if (i.Type == PackageTypes.File && names.Contains(i.Name.ToLowerInvariant())) continue;
            db.Packages.Add(new Package
            {
                Name = i.Name, Type = i.Type, Identifier = i.Id, Args = i.Args, Category = i.Category, Notes = i.Description,
                RunAs = i.User || PackageTypes.IsUserOnly(i.Type) ? RunAs.User : RunAs.System,
            });
            added++;
        }
        await db.SaveChangesAsync();
        // Eksisterende pakker uden beskrivelse får katalogets beskrivelse
        var byId = All.Where(i => i.Id.Length > 0).GroupBy(i => i.Id.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Description);
        foreach (var p in await db.Packages.Where(p => p.Notes == "").ToListAsync())
            if (byId.TryGetValue(p.Identifier.ToLowerInvariant(), out var d)) p.Notes = d;
        await db.SaveChangesAsync();
        await audit.Log(by, "catalog.import", $"{added} programmer tilføjet fra kataloget");
        return added;
    }

    private static bool Arch(string args) => args.Contains("x86", StringComparison.OrdinalIgnoreCase);
}
