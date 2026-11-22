using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// "fleetdeck-agent setup": forbind maskinen til Fleetdeck – med de samme trin og tekster som opsætningsvinduet på Mac og Windows
/// (forslag B, <see cref="SetupFlow"/>): Velkommen → Server → Tilmelding → Profil og tilvalg → Installation → Færdig ("Trin n af 6" i titlen).
/// Med skærm: små vinduer (zenity på GNOME/Xfce/Cinnamon, kdialog på KDE) med radiolister og en pulserende bjælke, mens der søges,
/// tjekkes, installeres og ventes. Uden skærm (server/SSH): samme trin i terminalen. Fejl vises over feltet, der spørges om igen.
/// Selve installationen kræver root og sker med pkexec (grafisk adgangskode) eller sudo.
/// </summary>
public static class Setup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static SetupOs Os => Mac.Is ? SetupOs.Mac : SetupOs.Linux;

    /// <summary>Hvordan hver skærm fra opsætningsvinduet vises som dialog (zenity / kdialog / terminal). Testes mod <see cref="SetupFlow.ScreensFor"/>.</summary>
    internal static readonly IReadOnlyDictionary<SetupScreen, string> Dialogs = new Dictionary<SetupScreen, string>
    {
        [SetupScreen.Welcome] = "--question med punkterne og \"Indsæt kommandoen i næste felt\"",
        [SetupScreen.Searching] = "--progress --pulsate \"Søger på netværket…\"",
        [SetupScreen.Found] = "--list --radiolist med de fundne servere + \"Eller skriv adressen\"",
        [SetupScreen.NoServer] = "--entry med \"Ingen server fundet automatisk …\" over feltet",
        [SetupScreen.ServerError] = "--entry igen med ✖ fejlen (og tekniske detaljer) over feltet",
        [SetupScreen.Key] = "--list --radiolist (nøgle/anmod) og --entry --hide-text",
        [SetupScreen.KeyError] = "--entry --hide-text igen med ✖ \"Nøglen passer ikke.\" over feltet",
        [SetupScreen.Request] = "--entry: besked til administratoren og de tre trin",
        [SetupScreen.Waiting] = "--progress --pulsate med koden; tjenesten venter videre, hvis den lukkes",
        [SetupScreen.Rejected] = "--question \"Anmod igen\" med administratorens begrundelse",
        [SetupScreen.Profile] = "--list --radiolist (profil) og --checklist (tilvalg)",
        [SetupScreen.Installing] = "--progress --pulsate, mens pkexec/sudo spørger om adgangskoden",
        [SetupScreen.NoConnection] = "--info \"Næsten færdig\" – agenten prøver selv igen",
        [SetupScreen.Done] = "--info \"Alt er klar\" med server, navn og status-kommandoen",
        [SetupScreen.Installed] = "--question: allerede installeret – fortsæt for at forbinde igen",
        [SetupScreen.Enrolled] = "(kun Mac-appen: Fuld diskadgang) – Linux går direkte til Færdig",
    };

    /// <summary>"Fleetdeck – Trin 2 af 6 · Server": dialogernes titel følger trinene i forslag B (samme som Mac- og Windows-vinduet).</summary>
    private static string Title(SetupStep step) => $"Fleetdeck – {SetupTexts.StepLabel(Os, step)}";

    public static async Task<int> Run(string[] args)
    {
        var ui = PickUi();
        var os = Os;
        var flow = new SetupFlow(os, installed: File.Exists(AgentConfigPath));

        // 1) Velkommen (installeret: oversigten først)
        var welcome = SetupTexts.Page(SetupScreen.Welcome, new SetupContext(os));
        var intro = welcome.Text + "\n\n" + string.Join("\n", SetupTexts.Features(os).Select(f => $"• {f.Title} – {f.Text}")) +
                    $"\n\n{SetupTexts.PasteTitle} Indsæt det i næste felt, så udfyldes server og nøgle af sig selv.";
        if (flow.Installed)
            intro = SetupTexts.Page(SetupScreen.Installed, new SetupContext(os)).Text + " Fortsætter du, forbindes den igen – fx til en anden server eller profil.\n\n" + intro;
        if (!Confirm(ui, SetupStep.Welcome, welcome.Title, intro, SetupTexts.Continue)) return 1;

        // 2) Server: søg på netværket, vælg – eller skriv adressen. Fejl vises over feltet, og der spørges igen.
        flow.Start();
        flow.SearchStarted();
        List<Discovery.Found> found;
        using (Pulse(ui, SetupStep.Server, SetupTexts.SearchingRow)) found = await Discovery.Search(TimeSpan.FromSeconds(2.5));
        flow.SearchDone(found);
        string url = "", key = "";
        string? error = null;
        while (true)
        {
            string? server = null;
            if (flow.Screen == SetupScreen.Found && error is null)
            {
                var pg = flow.Page();
                var opts = found.Select(f => (Id: f.Url, Label: f.Name.Length > 0 ? f.Name : SetupTexts.HostPort(f.Url), Sub: SetupTexts.FoundLine(f))).ToList();
                opts.Add(("-", SetupTexts.OrTypeAddress, SetupTexts.WhereIsAddress));
                var pick = Choose(ui, SetupStep.Server, $"{pg.Title}\n{pg.Text}", opts);
                if (pick is null) return 1;
                if (pick != "-") server = pick;
            }
            if (server is null)
            {
                var pg = SetupTexts.Page(flow.Screen, flow.Context());
                var head = error ?? (flow.Screen == SetupScreen.NoServer
                    ? $"{SetupTexts.NoServerTitle}. {SetupTexts.NoServerText}\n\n" : "");
                var v = Ask(ui, SetupStep.Server, "Serverens adresse",
                    $"{head}{pg.Title}\nSkriv adressen ({SetupTexts.ServerPlaceholder}). {SetupTexts.WhereIsAddress}\nDu kan også indsætte hele kommandoen fra Fleetdeck.",
                    flow.Server.Length > 0 ? flow.Server : found.FirstOrDefault()?.Url ?? "", hidden: false);
                if (v is null) return 1;
                var (srv, k) = EnrollKey.Parse(v);
                if (srv is not null) { server = srv; key = k.Trim(); } else server = v;
            }
            url = Discovery.Normalize(server);
            flow.Server = url;
            SetupCheck c;
            using (Pulse(ui, SetupStep.Server, $"Tjekker {url}…")) c = await SetupProbe.Test(Http, url, null, os);
            flow.ServerTested(c);
            if (c.Ok) break;
            error = ErrorText(c);
        }

        // 3) Tilmelding: nøgle eller "Anmod om adgang"
        error = null;
        var chosen = key.Length > 0 ? "key" : null;
        while (true)
        {
            var pg = SetupTexts.Page(SetupScreen.Key, flow.Context());
            if (chosen is null)
            {
                chosen = Choose(ui, SetupStep.Enroll, $"{pg.Title}\n{pg.Text}",
                    [("key", SetupTexts.HaveKey, SetupTexts.HaveKeySub), ("ask", SetupTexts.AskAccess, SetupTexts.AskAccessSub)]);
                if (chosen is null) return 1;
                if (chosen == "ask") { flow.ChooseAsk(true); return await RequestAccess(ui, flow, found.FirstOrDefault(f => Discovery.Normalize(f.Url) == url)); }
            }
            if (key.Length == 0)
            {
                var v = Ask(ui, SetupStep.Enroll, SetupTexts.KeySection,
                    $"{error}{pg.Title}\nIndsæt tilmeldingsnøglen fra Fleetdeck under Maskiner → Tilføj maskine. Du kan også indsætte hele kommandoen derfra.", "", hidden: true);
                if (v is null) return 1;
                var (srv, k) = EnrollKey.Parse(v);
                if (srv is not null) { url = Discovery.Normalize(srv); flow.Server = url; }
                key = k.Trim();
                if (key.Length == 0) { error = ErrorText(SetupCheck.Bad(SetupField.Key, "Indsæt tilmeldingsnøglen.", SetupTexts.HaveKeySub + ".")); continue; }
            }
            SetupCheck c;
            using (Pulse(ui, SetupStep.Enroll, "Tjekker nøglen…")) c = await SetupProbe.Test(Http, url, key, os);
            flow.KeyTested(c);
            if (c.Ok) break;
            if (c.Field == SetupField.Server) { Show(ui, SetupStep.Server, false, ErrorText(c)); return 3; }
            error = ErrorText(c); key = "";
        }

        // 4) Profil og tilvalg – profilen skal altid vælges, så maskinen ikke får noget, den ikke skal have
        flow.ProfilesLoaded();
        var profile = await AskProfile(ui, url, key);
        if (profile is null) return 1;
        var extensions = AskExtensions(ui);

        // 5) Installation som root: kopiér agenten til /opt/fleetdeck, tilmeld og start tjenesten
        flow.InstallStarted();
        var installArgs = new List<string> { "install", "--server", url, "--key", key, "--service", "--profile", profile.Value.ToString(), "--offline-exit" };
        if (extensions.Count > 0) { installArgs.Add("--extensions"); installArgs.Add(string.Join(',', extensions)); }
        var ipg = SetupTexts.Page(SetupScreen.Installing, flow.Context());
        int exit; string output;
        using (Pulse(ui, SetupStep.Install, $"{ipg.Title}\n{ipg.Text}\n\n{SetupWords.For(os).Password}")) (exit, output) = await AsRoot(ui, installArgs);
        if (exit is 0 or 7)
        {
            // exit 7: installeret, men tilmeldingen kunne ikke nå serveren – tjenesten prøver selv igen (som "Ingen forbindelse efter
            // 2 minutter" i vinduerne). Exitkoden virker også i terminalen, hvor outputtet ikke samles op.
            flow.InstallFinished(connected: exit == 0);
            if (flow.Screen == SetupScreen.NoConnection)
            {
                var np = flow.Page();
                Show(ui, SetupStep.Install, true, $"{np.Title}\n\n{np.Text} Agenten prøver selv igen. Tjek, at {SetupWords.For(os).DeviceLower} er på samme netværk som serveren.");
                return 0;
            }
            Show(ui, SetupStep.Finish, true, DoneText(flow, os));
            return 0;
        }
        Show(ui, SetupStep.Install, false, exit switch
        {
            3 => ErrorText(SetupCheck.Bad(SetupField.Key, "Nøglen blev afvist.", "Hent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet.")),
            126 or 127 or -128 => "Installationen blev afbrudt (adgangskoden blev ikke givet).",
            _ => $"Installationen fejlede (kode {exit}). {SetupProbe.LastLine(output)}",
        });
        return exit;
    }

    /// <summary>"Alt er klar" med server, navn og hvor man ser status.</summary>
    private static string DoneText(SetupFlow flow, SetupOs os)
    {
        var pg = SetupTexts.Page(SetupScreen.Done, flow.Context());
        var (tt, tx) = SetupTexts.TrayTip(os);
        return $"{pg.Title}\n\n{pg.Text}\n\nServer: {flow.ServerName()}\nNavn: {Environment.MachineName}\n\n{tt}: {tx}";
    }

    /// <summary>Fejlen over feltet, der spørges om igen: "✖ Titel" + forklaring (+ tekniske detaljer).</summary>
    private static string ErrorText(SetupCheck c) =>
        $"✖ {c.Title}\n{c.Text}" + (c.Detail is { Length: > 0 } d ? $"\n({SetupTexts.Details}: {d})" : "") + "\n\n";

    /// <summary>
    /// "Anmod om adgang": installér uden nøgle, vis koden, og vent på administratoren. Tjenesten tilmelder sig selv,
    /// også hvis vinduet lukkes; "fleetdeck-agent status" viser, hvordan det går.
    /// </summary>
    private static async Task<int> RequestAccess(Ui ui, SetupFlow flow, Discovery.Found? found)
    {
        var os = Os;
        var url = flow.Server;
        if (found?.AccessRequests == false) { Show(ui, SetupStep.Enroll, false, "Serveren tager ikke imod anmodninger om adgang. Bed administratoren om en tilmeldingsnøgle."); return 3; }
        while (true)
        {
            var pg = SetupTexts.Page(SetupScreen.Request, flow.Context());
            var steps = string.Join("\n", SetupTexts.RequestFlow.Select((x, i) => $"{i + 1}. {x}"));
            var message = Ask(ui, SetupStep.Enroll, SetupTexts.MessageSection,
                $"{SetupTexts.AskAccess}\n{steps}\n\n{SetupTexts.MessageSection} {SetupTexts.Optional} – {SetupWords.For(os).Example}.", "", hidden: false);
            if (message is null) return 1;
            var installArgs = new List<string> { "install", "--server", url, "--request", "--service" };
            if (message.Trim().Length > 0) { installArgs.Add("--message"); installArgs.Add(message.Trim()); }
            int exit; string output;
            var sentAt = DateTime.UtcNow;
            using (Pulse(ui, SetupStep.Enroll, $"Installerer og sender anmodningen…\n\n{SetupWords.For(os).Password}")) (exit, output) = await AsRoot(ui, installArgs);
            if (exit != 0)
            {
                Show(ui, SetupStep.Enroll, false, exit switch
                {
                    126 or 127 or -128 => "Installationen blev afbrudt (adgangskoden blev ikke givet).",
                    _ => SetupProbe.LastLine(output) is { Length: > 0 } l ? l : $"Anmodningen fejlede (kode {exit}).",
                });
                return exit;
            }
            var code = AccessStatus.Read()?.AccessRequest?.Code is { Length: > 0 } c ? c
                : Regex.Match(output, "Kode: (" + AccessCode.Pattern + ")") is { Success: true } m ? m.Groups[1].Value : "?";
            flow.RequestSent(code, sentAt);
            var wp = flow.Page();
            var (rowTitle, rowText, _) = SetupTexts.WaitingRow(os, null);
            var waitText = $"{wp.Title} – kode {code}\n\n{wp.Text}\n\n{rowTitle}: {rowText}\nDu kan godt lukke vinduet – {SetupWords.For(os).DeviceLower} tilmelder sig selv, når anmodningen er godkendt.";
            Process? progress = null;
            switch (ui)
            {
                case Ui.Zenity:
                    var psi = new ProcessStartInfo("zenity") { UseShellExecute = false, RedirectStandardInput = true };
                    foreach (var a in new[] { "--progress", "--pulsate", "--auto-close", "--title", Title(SetupStep.Enroll), "--text", waitText, "--width", "480", "--cancel-label", SetupTexts.Close }) psi.ArgumentList.Add(a);
                    progress = Process.Start(psi);
                    break;
                case Ui.Terminal: Console.WriteLine($"\n{waitText}\n(Ctrl+C lukker kun dette vindue – maskinen venter videre.)"); break;
                default: Show(ui, SetupStep.Enroll, true, waitText + "\n\nDu får besked, når den er godkendt."); break;
            }
            var deadline = DateTime.UtcNow.AddHours(2);
            while (DateTime.UtcNow < deadline && flow.Screen == SetupScreen.Waiting)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (progress is { HasExited: true }) break;   // vinduet er lukket: tjenesten venter videre
                if (AccessStatus.Read() is { } st) flow.RequestUpdate(st);
            }
            try { if (progress is { HasExited: false }) { progress.StandardInput.WriteLine("100"); progress.StandardInput.Close(); progress.WaitForExit(2000); } }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
            if (flow.Screen is SetupScreen.Done or SetupScreen.Enrolled)
            {
                Notify(ui, "Godkendt! Maskinen er tilmeldt Fleetdeck.");
                Show(ui, SetupStep.Finish, true, DoneText(flow, os));
                return 0;
            }
            if (flow.Screen == SetupScreen.Rejected)
            {
                var rp = flow.Page();
                var text = $"{rp.Title}\n\n{rp.Text}" + (flow.Reason is { Length: > 0 } r ? $"\n\nBesked fra administratoren: “{r}”" : "") +
                           "\n\nSend en ny anmodning med en besked, så administratoren kan se, hvilken maskine det er.";
                Notify(ui, rp.Title);
                if (!Confirm(ui, SetupStep.Enroll, rp.Title, text, SetupTexts.AskAgain)) return 9;
                flow.AskAgain();
                continue;
            }
            Show(ui, SetupStep.Enroll, true, "Maskinen venter stadig på godkendelse og tilmelder sig selv bagefter. Se status med: fleetdeck-agent status");
            return 9;
        }
    }

    private static void Notify(Ui ui, string text)
    {
        if (ui != Ui.Terminal && Has("notify-send") && !Mac.Is) Shell.Run($"notify-send {Pkg.Q("Fleetdeck")} {Pkg.Q(text)}", TimeSpan.FromSeconds(5));
    }

    private const string OfflineMark = "fleetdeck-exit=7";

    /// <summary>Kører agenten selv som root (pkexec, osascript eller sudo). Output samles kun op uden for terminalen.</summary>
    private static async Task<(int, string)> AsRoot(Ui ui, List<string> agentArgs)
    {
        var self = Environment.ProcessPath!;
        var installArgs = new List<string> { self };
        installArgs.AddRange(agentArgs);
        ProcessStartInfo psi;
        if (Environment.UserName == "root") { psi = new ProcessStartInfo(self); installArgs.RemoveAt(0); }
        else if (ui == Ui.Osa)
        {
            // "do shell script" gør enhver exitkode ≠ 0 til en fejl: exit 7 (installeret, ikke tilmeldt endnu) sendes videre som en markør
            var shell = string.Join(' ', installArgs.Select(Pkg.Q)) + $"; e=$?; if [ $e -eq 7 ]; then echo {OfflineMark}; exit 0; fi; exit $e";
            psi = new ProcessStartInfo("osascript"); installArgs = ["-e", $"do shell script {AS(shell)} with administrator privileges"];
        }
        else if (ui != Ui.Terminal && Has("pkexec")) psi = new ProcessStartInfo("pkexec");
        else psi = new ProcessStartInfo("sudo");
        foreach (var a in installArgs) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        if (ui != Ui.Terminal) { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
        if (ui == Ui.Terminal) Console.WriteLine("\nInstallerer agenten (kræver administrator) …");
        using var p = Process.Start(psi)!;
        var output = ui != Ui.Terminal ? await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync() : "";
        await p.WaitForExitAsync();
        if (ui == Ui.Osa && p.ExitCode == 0 && output.Contains(OfflineMark, StringComparison.Ordinal)) return (7, output);
        // osascript: den egentlige exit-kode står i "(n)" til sidst, og brugeren skal kun se agentens egen besked
        if (ui == Ui.Osa) return (MacInstall.ScriptExitCode(p.ExitCode, output), MacInstall.CleanOsaOutput(output));
        return (p.ExitCode, output);
    }

    /// <summary>Installér som tjeneste (kaldes som root fra setup): kopiér agenten, skriv tjenesten for opstartssystemet og start den.</summary>
    /// <summary>Stopper den kørende agenttjeneste (launchd, Unraid-sløjfen eller opstartssystemet), før konfigurationen skrives om.</summary>
    public static void StopService()
    {
        if (Mac.Is) Shell.Run($"launchctl bootout system/{Mac.Label} 2>/dev/null || true", TimeSpan.FromSeconds(30));
        else if (Unraid.Is) Unraid.Stop();
        else Init.Stop();
    }

    public static int InstallService()
    {
        if (Mac.Is) return Mac.InstallService();
        if (Unraid.Is) return Unraid.InstallService();
        Directory.CreateDirectory(Init.Dir);
        Init.Stop();
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), Init.Exe, StringComparison.Ordinal))
        {
            File.Copy(Environment.ProcessPath!, Init.Exe + ".new", true);
            File.SetUnixFileMode(Init.Exe + ".new", (UnixFileMode)0b111_101_101);
            File.Move(Init.Exe + ".new", Init.Exe, true);
        }
        var r = Init.Install();
        Console.WriteLine(r.ExitCode == 0 ? $"Fleetdeck: færdig. Agenten kører som tjenesten fleetdeck-agent ({Init.Label})." : r.Output);
        return r.ExitCode == 0 ? 0 : 4;
    }

    private const string AgentConfigPath = "/etc/fleetdeck/agent.json";

    private enum Ui { Zenity, KDialog, Osa, Terminal }

    private static Ui PickUi()
    {
        if (Mac.Is) return Environment.GetEnvironmentVariable("SSH_CONNECTION") is { Length: > 0 } ? Ui.Terminal : Ui.Osa;
        var display = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        if (!display) return Ui.Terminal;
        var kde = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase);
        if (kde && Has("kdialog")) return Ui.KDialog;
        if (Has("zenity")) return Ui.Zenity;
        if (Has("kdialog")) return Ui.KDialog;
        return Ui.Terminal;
    }

    /// <summary>Spørgsmål med "Fortsæt"/"Annullér" (velkomst, "Anmod igen").</summary>
    private static bool Confirm(Ui ui, SetupStep step, string heading, string text, string ok)
    {
        switch (ui)
        {
            case Ui.Zenity:
                return Dialog("zenity", ["--question", "--title", Title(step), "--text", text, "--width", "520", "--ok-label", ok, "--cancel-label", "Annullér", "--icon-name", "computer"]) is not null;
            case Ui.KDialog:
                return Dialog("kdialog", ["--title", Title(step), "--yes-label", ok, "--no-label", "Annullér", "--yesno", text]) is not null;
            case Ui.Osa:
                return Osa($"display dialog {AS(text)} with title {AS(Title(step))} buttons {{\"Annullér\", {AS(ok)}}} default button 2 cancel button 1") is not null;
            default:
                Header(step, heading);
                Console.WriteLine(text);
                Console.Write($"\nTryk Enter = {ok} (Ctrl+C afbryder): ");
                return Console.ReadLine() is not null;
        }
    }

    /// <summary>Overskriften i terminalen: "── Trin 2 af 6 · Server ──".</summary>
    private static void Header(SetupStep step, string? heading = null)
    {
        Console.WriteLine($"\n── {SetupTexts.StepLabel(Os, step)} ──");
        if (heading is { Length: > 0 }) Console.WriteLine(heading);
    }

    private static string? Ask(Ui ui, SetupStep step, string label, string text, string value, bool hidden)
    {
        switch (ui)
        {
            case Ui.Zenity:
            {
                var a = new List<string> { "--entry", "--title", Title(step), "--text", text, "--width", "520", "--ok-label", SetupTexts.Continue, "--cancel-label", "Annullér" };
                if (hidden) a.Add("--hide-text"); else { a.Add("--entry-text"); a.Add(value); }
                return Dialog("zenity", a);
            }
            case Ui.KDialog:
                return Dialog("kdialog", hidden ? ["--title", Title(step), "--password", text] : ["--title", Title(step), "--inputbox", text, value]);
            case Ui.Osa:
            {
                var o = Osa($"display dialog {AS(text)} default answer {AS(value)}{(hidden ? " with hidden answer" : "")} with title {AS(Title(step))} buttons {{\"Annullér\", {AS(SetupTexts.Continue)}}} default button 2 cancel button 1");
                return o is null ? null : o[(o.IndexOf("text returned:", StringComparison.Ordinal) + "text returned:".Length)..].Trim();
            }
            default:
                Header(step);
                Console.WriteLine(text);
                Console.Write($"{label}{(value.Length > 0 ? $" [{value}]" : "")}: ");
                var line = hidden ? ReadHidden() : Console.ReadLine();
                if (line is null) return null;
                return line.Trim().Length == 0 ? value : line.Trim();
        }
    }

    /// <summary>
    /// Vælg én (radioliste): servere på netværket, nøgle eller anmodning, profil. Null = annulleret. preselect: den første er valgt på forhånd
    /// (og Enter i terminalen vælger den); uden skal der vælges aktivt – OK uden valg eller en tom linje spørger igen (profilen).
    /// </summary>
    private static string? Choose(Ui ui, SetupStep step, string text, List<(string Id, string Label, string Sub)> opts, bool preselect = true)
    {
        while (true)
        {
            switch (ui)
            {
                case Ui.Zenity:
                {
                    var a = new List<string> { "--list", "--radiolist", "--title", Title(step), "--text", text, "--column", "", "--column", "id", "--column", "Valg", "--column", "",
                        "--hide-column", "2", "--width", "600", "--height", "340", "--ok-label", SetupTexts.Continue, "--cancel-label", "Annullér" };
                    for (var i = 0; i < opts.Count; i++) { a.Add(i == 0 && preselect ? "TRUE" : "FALSE"); a.Add(opts[i].Id); a.Add(opts[i].Label); a.Add(opts[i].Sub); }
                    var r = Dialog("zenity", a);
                    if (r is null) return null;
                    if (r.Trim().Length > 0) return r.Trim();
                    break;   // OK uden valg: spørg igen
                }
                case Ui.KDialog:
                {
                    var a = new List<string> { "--title", Title(step), "--radiolist", text };
                    for (var i = 0; i < opts.Count; i++) { a.Add(opts[i].Id); a.Add($"{opts[i].Label} – {opts[i].Sub}"); a.Add(i == 0 && preselect ? "on" : "off"); }
                    var r = Dialog("kdialog", a);
                    if (r is null) return null;
                    if (r.Trim().Length > 0) return r.Trim();
                    break;
                }
                case Ui.Osa:
                {
                    var labels = opts.Select(o => $"{o.Label} – {o.Sub}").ToList();
                    var def = preselect ? $" default items {{{AS(labels[0])}}}" : "";
                    var o = Osa($"choose from list {{{string.Join(", ", labels.Select(AS))}}} with title {AS(Title(step))} with prompt {AS(text)}{def} OK button name {AS(SetupTexts.Continue)} cancel button name \"Annullér\"");
                    if (o is null || o == "false") return null;
                    var i = labels.IndexOf(o);
                    if (i >= 0) return opts[i].Id;
                    break;
                }
                default:
                    Header(step);
                    Console.WriteLine(text);
                    for (var i = 0; i < opts.Count; i++) Console.WriteLine($"  {i + 1}) {opts[i].Label} – {opts[i].Sub}");
                    while (true)
                    {
                        Console.Write(preselect ? "Skriv nummeret [1]: " : "Skriv nummeret: ");
                        var l = Console.ReadLine();
                        if (l is null) return null;
                        if (l.Trim().Length == 0 && preselect) return opts[0].Id;
                        if (int.TryParse(l.Trim(), out var n) && n >= 1 && n <= opts.Count) return opts[n - 1].Id;
                        Console.WriteLine(l.Trim().Length == 0 ? "Vælg et af numrene ovenfor – der er ikke valgt noget på forhånd." : "Skriv et af numrene ovenfor.");
                    }
            }
        }
    }

    /// <summary>Venten med en "pulserende" bjælke (zenity --progress --pulsate); i terminalen en linje. Lukkes med Dispose.</summary>
    private static IDisposable Pulse(Ui ui, SetupStep step, string text)
    {
        if (ui == Ui.Terminal) { Console.WriteLine($"… {text.Split('\n')[0]}"); return new Closer(null); }
        if (ui != Ui.Zenity) return new Closer(null);
        try
        {
            var psi = new ProcessStartInfo("zenity") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--progress", "--pulsate", "--auto-close", "--no-cancel", "--title", Title(step), "--text", text, "--width", "460" }) psi.ArgumentList.Add(a);
            return new Closer(Process.Start(psi));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return new Closer(null); }
    }

    private sealed class Closer(Process? p) : IDisposable
    {
        public void Dispose()
        {
            if (p is null) return;
            try { if (!p.HasExited) { p.StandardInput.WriteLine("100"); p.StandardInput.Close(); p.WaitForExit(2000); } if (!p.HasExited) p.Kill(); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }

    /// <summary>Profilen fra serverens liste. 0 = ingen profil, null = annulleret.</summary>
    private static async Task<int?> AskProfile(Ui ui, string url, string key)
    {
        List<ProfileOption> list;
        try { list = await Http.GetFromJsonAsync<List<ProfileOption>>($"{url}/api/agent/profiles?key={Uri.EscapeDataString(key)}", new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { list = []; }
        var opts = new List<(string Id, string Label, string Sub)> { ("0", SetupTexts.NoProfile, SetupTexts.NoProfileSub) };
        opts.AddRange(list.Select(p => (p.Id.ToString(), p.Name, SetupTexts.ProfileLine(p))));
        var pg = SetupTexts.Page(SetupScreen.Profile, new SetupContext(Os));
        var pick = Choose(ui, SetupStep.Profile, $"{pg.Title}\n{pg.Text}", opts, preselect: false);   // profilen skal altid vælges aktivt
        return int.TryParse(pick, out var v) ? v : null;
    }

    private static List<string> AskExtensions(Ui ui)
    {
        var list = Extensions.BuiltIn.Where(e => e.For(Platforms.Current) is not null).ToList();
        var q = $"{SetupTexts.ExtensionsSection} – kan også slås til senere i Fleetdeck:";
        switch (ui)
        {
            case Ui.Zenity:
            {
                var a = new List<string> { "--list", "--checklist", "--title", Title(SetupStep.Profile), "--text", q,
                    "--column", "", "--column", "id", "--column", "Tilvalg", "--column", "Giver", "--hide-column", "2", "--separator", ",",
                    "--width", "620", "--height", "330", "--ok-label", "Installér", "--cancel-label", "Spring over" };
                foreach (var e in list) { a.Add(Extensions.DefaultOn.Contains(e.Id) ? "TRUE" : "FALSE"); a.Add(e.Id); a.Add(e.Name); a.Add(e.Gives); }
                return (Dialog("zenity", a) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            case Ui.KDialog:
            {
                var a = new List<string> { "--title", Title(SetupStep.Profile), "--separate-output", "--checklist", q };
                foreach (var e in list) { a.Add(e.Id); a.Add($"{e.Name} – {e.Gives}"); a.Add(Extensions.DefaultOn.Contains(e.Id) ? "on" : "off"); }
                return (Dialog("kdialog", a) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            case Ui.Osa:
            {
                var names = list.Select(e => $"{e.Name} – {e.Gives}").ToList();
                var o = Osa($"choose from list {{{string.Join(", ", names.Select(AS))}}} with title {AS(Title(SetupStep.Profile))} with prompt {AS(q)} with multiple selections allowed and empty selection allowed OK button name \"Installér\" cancel button name \"Spring over\"");
                if (o is null || o == "false") return [];
                return list.Where((e, i) => o.Contains(names[i], StringComparison.Ordinal)).Select(e => e.Id).ToList();
            }
            default:
                Header(SetupStep.Profile, q);
                var res = new List<string>();
                foreach (var e in list)
                {
                    Console.Write($"  {e.Name} – {e.Gives}? [{(Extensions.DefaultOn.Contains(e.Id) ? "J/n" : "j/N")}] ");
                    var l = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                    if (l is "j" or "ja" or "y" || (l.Length == 0 && Extensions.DefaultOn.Contains(e.Id))) res.Add(e.Id);
                }
                return res;
        }
    }

    private static void Show(Ui ui, SetupStep step, bool ok, string text)
    {
        switch (ui)
        {
            case Ui.Zenity: Dialog("zenity", [ok ? "--info" : "--error", "--title", Title(step), "--text", text, "--width", "480"]); break;
            case Ui.KDialog: Dialog("kdialog", ["--title", Title(step), ok ? "--msgbox" : "--error", text]); break;
            case Ui.Osa: Osa($"display dialog {AS(text)} with title {AS(Title(step))} buttons {{\"OK\"}} default button 1 with icon {(ok ? "note" : "stop")}"); break;
            default: Header(step); Console.WriteLine((ok ? "✔ " : "✖ ") + text); break;
        }
    }

    private static string? Dialog(string exe, List<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? o.Trim() : null;
    }

    private static string? ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var s = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return s.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (s.Length > 0) s.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) s.Append(k.KeyChar);
        }
    }

    private static bool Has(string tool) => Shell.Out($"command -v {tool}").Length > 0;

    /// <summary>AppleScript-tekst i anførselstegn.</summary>
    private static string AS(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Osa(string script) => Dialog("osascript", ["-e", script]);
}
