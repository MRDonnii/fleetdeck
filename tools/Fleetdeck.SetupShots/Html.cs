using System.Net;
using System.Text;
using Fleetdeck.Shared;

/// <summary>
/// Tro HTML-gengivelser af opsætningen på Windows (WinForms kan ikke køre på Linux) og Linux (zenity-dialoger og terminalen).
/// Teksterne kommer fra den fælles model (<see cref="SetupTexts"/>/<see cref="SetupFlow"/>), mål og farver fra koden:
/// Windows = SetupForm.cs/SetupParts.cs (820×600, navigation 240, indhold 480, Theme-farverne, kort radius 8, kontroller radius 5),
/// Linux = Setup.cs (titlen "Fleetdeck – Trin n af 6 · …", --entry/--list/--progress). Data er opdigtede.
/// </summary>
internal static class Html
{
    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static readonly Discovery.Found Home = new("Fleetdeck · hjemme", "http://192.168.1.10:8585", "3.41.3", true);
    private static readonly Discovery.Found Shop = new("Fleetdeck · værksted", "http://192.168.1.24:8585", "3.40.1", true);
    private const string Url = "http://192.168.1.10:8585";

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var n = 0;
        var index = new StringBuilder("<!doctype html><meta charset=utf-8><title>Fleetdeck opsætning B – Windows og Linux</title><body style='font-family:sans-serif'><h1>Windows</h1><ul>");
        foreach (var (name, flow, extra) in WindowsStates())
        {
            n++;
            foreach (var dark in new[] { false, true })
            {
                var file = $"windows-{n:00}-{name}-{(dark ? "moerk" : "lys")}.html";
                File.WriteAllText(Path.Combine(dir, file), Windows(flow, extra, dark));
                index.Append($"<li><a href='{file}'>{file}</a>");
            }
        }
        index.Append("</ul><h1>Linux</h1><ul>");
        n = 0;
        foreach (var (name, html) in LinuxDialogs())
        {
            n++;
            var file = $"linux-{n:00}-{name}.html";
            File.WriteAllText(Path.Combine(dir, file), html);
            index.Append($"<li><a href='{file}'>{file}</a>");
        }
        File.WriteAllText(Path.Combine(dir, "index.html"), index + "</ul>");
    }

    // ================================================================ Windows

    private sealed record Extra(List<ProfileOption>? Profiles = null, int? ProfileId = null, int InstallAt = 0, TrayStatus? Status = null, string? Section = null,
        string Server = "", string Key = "", bool Details = false);

    private static SetupFlow AtKey(SetupOs os)
    {
        var f = new SetupFlow(os); f.Start(); f.SearchDone([Home, Shop]); f.Server = Url; f.ServerTested(SetupCheck.Good("Forbindelse OK.", Url, "3.41.3"));
        return f;
    }

    private static TrayStatus St(bool enrolled = true) => new()
    {
        Machine = "STUE-PC", Server = Url, Connected = enrolled, Enrolled = enrolled, Profile = "Hjemme-pc", LastContact = enrolled ? DateTime.UtcNow.AddSeconds(-3) : null,
        Written = DateTime.UtcNow, Version = "3.41.3",
    };

    private static IEnumerable<(string, SetupFlow, Extra)> WindowsStates()
    {
        const SetupOs os = SetupOs.Windows;
        List<ProfileOption> profiles = [new(3, "Hjemme-pc", 3, 14), new(5, "Kontor", 5, 18)];
        SetupFlow F(Action<SetupFlow> a) { var f = new SetupFlow(os); a(f); return f; }
        SetupFlow K(Action<SetupFlow> a) { var f = AtKey(os); a(f); return f; }
        yield return ("velkomst", new SetupFlow(os), new());
        yield return ("soeger", F(f => { f.Start(); f.SearchStarted(); }), new());
        yield return ("fundet", F(f => { f.Start(); f.SearchDone([Home, Shop]); f.Server = Url; }), new(Server: Url));
        yield return ("fejl-ingen-server", F(f => { f.Start(); f.SearchDone([]); }), new(Server: "10.0.0."));
        yield return ("fejl-certifikat", F(f =>
        {
            f.Start(); f.SearchDone([]); f.Server = "https://fleetdeck.local:8586";
            f.ServerTested(SetupProbe.Unreachable(f.Server, new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
                new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")), os));
        }), new(Server: "https://fleetdeck.local:8586", Details: true));
        yield return ("noegle-ok", K(f => f.KeyTested(SetupCheck.Good("Nøglen passer.", $"Forbundet til {Url}, version 3.41.3."))), new(Server: Url, Key: "fd_9d3c1b7e2a8f4c60"));
        yield return ("fejl-noegle", K(f => f.KeyTested(SetupCheck.Bad(SetupField.Key, "Nøglen passer ikke.", "Hent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet."))), new(Server: Url, Key: "fd_0000"));
        yield return ("profil", K(f => { f.KeyTested(SetupCheck.Good("Nøglen passer.", "")); f.ProfilesLoaded(); }), new(profiles, 3, Server: Url));
        yield return ("installerer", K(f => { f.KeyTested(SetupCheck.Good("", "")); f.ProfilesLoaded(); f.InstallStarted(); }), new(profiles, 3, InstallAt: 1, Server: Url));
        yield return ("installeret-uden-forbindelse", K(f => { f.KeyTested(SetupCheck.Good("", "")); f.ProfilesLoaded(); f.InstallStarted(); f.InstallFinished(false); }), new(profiles, 3, Server: Url));
        yield return ("anmod", K(f => f.ChooseAsk(true)), new(Server: Url));
        yield return ("venter", K(f => { f.ChooseAsk(true); f.RequestSent("7K4-Q2M"); }), new(Server: Url));
        yield return ("afvist", K(f =>
        {
            f.ChooseAsk(true); f.RequestSent("7K4-Q2M");
            f.RequestUpdate(new TrayStatus { AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Rejected, Reason = "Ukendt maskine – skriv lige, hvis det er din." } });
        }), new(Server: Url));
        yield return ("faerdig", K(f => { f.KeyTested(SetupCheck.Good("", "")); f.ProfilesLoaded(); f.InstallStarted(); f.InstallFinished(true); }), new(profiles, 3, Status: St(), Server: Url));
        yield return ("faerdig-genstart", K(f => { f.KeyTested(SetupCheck.Good("", "")); f.ProfilesLoaded(); f.InstallStarted(); f.InstallFinished(true, reboot: true); }), new(profiles, 3, Status: St(), Server: Url));
        yield return ("installeret", new SetupFlow(os, installed: true), new(Status: St(), Section: "overview"));
        yield return ("installeret-server", new SetupFlow(os, installed: true), new(Status: St(), Section: "server"));
    }

    private sealed record WinTheme(string Bg, string Card, string Text, string Muted, string Border, string Accent, string Ok, string Warn, string Bad, string Field, string Side);
    private static readonly WinTheme Light = new("#f3f3f3", "#ffffff", "#1b1b1b", "#5e5e5e", "#e3e3e3", "#0f6cbd", "#0f7b0f", "#8a5300", "#c42b1c", "#fbfbfb", "#ebeef3");
    private static readonly WinTheme Dark = new("#1f2023", "#2a2b2f", "#f1f1f1", "#a3a6ad", "#3a3c42", "#4c9bff", "#6ccb5f", "#f5c35a", "#ff8a80", "#24252a", "#1a1b1e");

    private static string Icon(string name, string color, int size, double stroke = 1.7) =>
        SetupIcons.Svg(name, color, stroke).Replace("<svg ", $"<svg width='{size}' height='{size}' ");

    private static string Tile(string icon, string color, int size) =>
        $"<span class='tile' style='width:{size}px;height:{size}px;border-radius:{(size >= 46 ? size / 4.0 : size >= 28 ? 7 : 6)}px;background:linear-gradient(180deg,color-mix(in srgb,{color} 82%,#fff),{color})'>" +
        Icon(icon, "#fff", (int)(size * (size >= 46 ? 0.58 : 0.62)), size >= 46 ? 1.9 : 2) + "</span>";

    private static string Windows(SetupFlow f, Extra x, bool dark)
    {
        var t = dark ? Dark : Light;
        const SetupOs os = SetupOs.Windows;
        var pg = f.Page(x.Status?.Machine, x.Status?.Profile);
        var side = new StringBuilder();
        var installed = f.Screen == SetupScreen.Installed;
        var sub = installed ? $"<span style='color:{t.Ok}'>● Forbundet</span>" : E(SetupTexts.Subtitle(os));
        side.Append($"<div class='ident'><img src='fleetdeck.png'><div><b>Fleetdeck</b><span>{sub}</span></div></div>");
        if (installed)
            foreach (var s in SetupTexts.Sections(os)) side.Append(Nav(s.Icon, s.Color, s.Title, null, (x.Section ?? "overview") == s.Id ? SetupStepState.Current : SetupStepState.Todo, true));
        else
            foreach (var st in SetupTexts.Steps(os)) side.Append(Nav(st.Icon, st.Color, st.Title, f.StepNote(st.Step), f.StateOf(st.Step), false));
        if (!installed && f.Screen != SetupScreen.Welcome && f.Step > SetupStep.Server && f.ServerName() is { } srv)
            side.Append($"<div class='side-srv' style='color:{(f.Screen is SetupScreen.Waiting or SetupScreen.NoConnection ? t.Warn : t.Ok)}'>● {E(srv)}</div><div class='ver2'>Fleetdeck 3.41.3</div>");
        else side.Append("<div class='ver'>Fleetdeck 3.41.3</div>");

        var b = new StringBuilder();
        string Hero(string? icon = null, bool big = false) =>
            $"<div class='hero'>{icon ?? Tile(pg.Icon, pg.Color, 52)}<h1 class='{(big ? "big" : "")}'>{E(pg.Title)}</h1><p>{E(pg.Text)}</p></div>";
        string Msg(SetupTone tone, string title, string? text) =>
            $"<div class='msg {(tone == SetupTone.Bad ? "bad" : "")}'>{Icon(tone switch { SetupTone.Ok => "checkc", SetupTone.Warn => "warn", SetupTone.Bad => "xc", _ => "info" }, tone switch { SetupTone.Ok => t.Ok, SetupTone.Warn => t.Warn, SetupTone.Bad => t.Bad, _ => t.Accent }, 16)}<div><b>{E(title)}</b>{(text is { Length: > 0 } ? $"<span>{E(text)}</span>" : "")}</div></div>";
        string Field(string value, string ph, bool err = false, bool pw = false) =>
            $"<div class='field {(err ? "err" : "")}'>{(value.Length > 0 ? (pw ? new string('●', Math.Min(value.Length, 22)) : E(value)) : $"<span class='ph'>{E(ph)}</span>")}</div>";
        string Line(string? lead, string title, string? s = null, string? right = null, bool dim = false) =>
            $"<div class='line'>{lead}<div class='t {(dim ? "dim" : "")}'>{E(title)}{(s is { Length: > 0 } ? $"<small>{E(s)}</small>" : "")}</div>{right}</div>";
        string Choice(string title, string? s, bool on, string? tile = null, string? color = null) =>
            $"<div class='line choice {(on ? "on" : "")}'>{(tile is null ? Radio(on) : Tile(tile, color!, 28))}<div class='t'>{(on ? $"<b>{E(title)}</b>" : E(title))}{(s is null ? "" : $"<small>{E(s)}</small>")}</div>{(tile is null ? "" : Radio(on))}</div>";
        string Radio(bool on) => $"<span class='radio {(on ? "on" : "")}'></span>";
        string Ring() => "<span class='ring'></span>";
        string Btn(string text, bool primary = false, bool dis = false) => $"<span class='btn {(primary ? "pri" : "")} {(dis ? "dis" : "")}'>{E(text)}</span>";

        switch (f.Screen)
        {
            case SetupScreen.Welcome:
                b.Append(Hero("<img class='logo' src='fleetdeck.png'>", true));
                foreach (var (ic, ti, tx) in SetupTexts.Features(os)) b.Append($"<div class='feat'>{Icon(ic, t.Accent, 22)}<div><b>{E(ti)}</b><span>{E(tx)}</span></div></div>");
                b.Append($"<div class='card tip'>{Line(Icon("clip", t.Accent, 20), SetupTexts.PasteTitle, SetupTexts.PasteText, Btn(SetupTexts.Paste))}</div>");
                break;
            case SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer:
                b.Append(Hero());
                b.Append($"<div class='sec'>{E(SetupTexts.OnNetwork)}{(f.Screen == SetupScreen.Found ? $"<a>⟳ {E(SetupTexts.SearchAgain)}</a>" : "")}</div>");
                if (f.Screen == SetupScreen.Searching) b.Append($"<div class='card center'>{Ring()}<span class='mut'>{E(SetupTexts.SearchingRow)}</span></div>");
                else if (f.Screen == SetupScreen.Found)
                    b.Append("<div class='card list'>" + string.Concat(f.Found.Select(s => Choice(s.Name, SetupTexts.FoundLine(s), s.Url == x.Server, "server", s.Url == x.Server ? SetupColors.Blue : SetupColors.Gray))) + "</div>");
                else b.Append($"<div class='card col'>{Icon("wifix", t.Muted, 30)}<b>Fandt ingen server</b><span class='mut'>Pc'en og serveren skal være på samme netværk. Du kan også skrive adressen herunder.</span>{Btn(SetupTexts.SearchAgain)}</div>");
                b.Append($"<div class='sec'>{E(SetupTexts.OrTypeAddress)}</div><div class='card pad'>{Field(x.Server, SetupTexts.ServerPlaceholder)}</div><div class='note'>{E(SetupTexts.WhereIsAddress)}</div>");
                break;
            case SetupScreen.ServerError:
            {
                b.Append(Hero());
                var e = f.ErrorFor(SetupField.Server)!;
                b.Append($"<div class='sec'>Adresse</div><div class='card pad'>{Field(x.Server, "", err: true)}</div>");
                b.Append(Msg(SetupProbe.IsCertificateText(e) ? SetupTone.Warn : SetupTone.Bad, e.Title, e.Text));
                if (e.Detail is { } d) b.Append($"<div class='det'>{(x.Details ? "▾" : "▸")} {E(SetupTexts.Details)}</div>" + (x.Details ? $"<div class='mono'>{E(d)}</div>" : ""));
                break;
            }
            case SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request:
                b.Append(Hero());
                b.Append($"<div class='choices'><div class='card ch {(f.Ask ? "" : "on")}'>{Choice(SetupTexts.HaveKey, SetupTexts.HaveKeySub, !f.Ask, "key", SetupColors.Gray)}</div><div class='card ch {(f.Ask ? "on" : "")}'>{Choice(SetupTexts.AskAccess, SetupTexts.AskAccessSub, f.Ask, "plane", SetupColors.Teal)}</div></div>");
                if (f.Screen == SetupScreen.Request)
                {
                    b.Append("<div class='flow'>" + string.Concat(SetupTexts.RequestFlow.Select((s, i) => $"<div>{i + 1}.&nbsp; {E(s)}</div>")) + "</div>");
                    b.Append($"<div class='sec'>Pc'ens navn</div><div class='card pad'>{Field("STUE-PC", "")}</div>");
                    b.Append($"<div class='sec'>{E(SetupTexts.MessageSection)} <span class='light'>{E(SetupTexts.Optional)}</span></div><div class='card pad'><div class='field ta'><span class='ph'>{E(SetupWords.For(os).Example)}</span></div></div>");
                }
                else
                {
                    var err = f.ErrorFor(SetupField.Key);
                    b.Append($"<div class='sec'>{E(SetupTexts.KeySection)}</div><div class='card pad row'>{Field(x.Key, SetupTexts.KeyPlaceholder, err is not null, true)}{Btn(SetupTexts.Paste)}</div>");
                    if (f.KeyCheck is { Ok: true } ok) b.Append(Msg(SetupTone.Ok, ok.Title, ok.Text));
                    else if (err is not null) b.Append(Msg(SetupTone.Bad, err.Title, err.Text));
                }
                break;
            case SetupScreen.Waiting:
            {
                b.Append(Hero());
                b.Append("<div class='code'>" + string.Concat((f.Code ?? "").Select(c => c == '-' ? "<i></i>" : $"<span>{c}</span>")) + "</div>");
                b.Append($"<div class='c'>{Btn(SetupTexts.CopyCode)}</div>");
                var (wt, ws, _) = SetupTexts.WaitingRow(os, null);
                b.Append($"<div class='card list'>{Line(Ring(), wt, ws)}{Line(Icon("laptop", t.Muted, 20), "Du kan godt lukke vinduet", "Pc'en tilmelder sig selv, når anmodningen er godkendt.")}</div>");
                break;
            }
            case SetupScreen.Rejected:
                b.Append(Hero());
                if (f.Reason is { } r) b.Append($"<div class='sec'>Besked fra administratoren</div><div class='card pad'><i class='mut'>“{E(r)}”</i></div>");
                b.Append("<div class='note'>Send en ny anmodning med en besked, så administratoren kan se, hvilken pc det er.</div>");
                break;
            case SetupScreen.Profile:
                b.Append(Hero());
                b.Append("<div class='card list'>" + Choice(SetupTexts.NoProfile, SetupTexts.NoProfileSub, x.ProfileId == 0) +
                         string.Concat((x.Profiles ?? []).Select(p => Choice(p.Name, SetupTexts.ProfileLine(p), x.ProfileId == p.Id))) + "</div>");
                b.Append($"<div class='sec'>{E(SetupTexts.ExtensionsSection)}</div><div class='card list'>");
                foreach (var e in Extensions.BuiltIn.Where(e => e.Windows is not null && e.Id != "winget"))
                    b.Append(Line(null, e.Name, e.Gives, $"<span class='tg {(Extensions.DefaultOn.Contains(e.Id) ? "on" : "")}'></span>"));
                b.Append(Line(null, "Nyinstallation bagefter", "Kun en pc, der lige er sat op fra bunden", "<span class='tg'></span>") + "</div>");
                break;
            case SetupScreen.Installing or SetupScreen.NoConnection:
            {
                b.Append(Hero());
                var stuck = f.Screen == SetupScreen.NoConnection;
                var steps = SetupTexts.InstallSteps(os);
                b.Append($"<div class='bar'><i style='width:{(stuck ? 92 : (int)((x.InstallAt + 0.5) * 100 / steps.Count))}%;{(stuck ? $"background:{t.Warn}" : "")}'></i></div><div class='card list'>");
                for (var i = 0; i < steps.Count; i++)
                {
                    if (stuck && i == steps.Count - 1) { b.Append(Line(Icon("warn", t.Warn, 18), "Ingen forbindelse efter 2 minutter", "Agenten prøver selv igen. Tjek, at pc'en er på samme netværk som serveren.")); continue; }
                    var done = stuck || i < x.InstallAt;
                    b.Append(Line(done ? Icon("checkc", t.Ok, 18) : i == x.InstallAt ? Ring() : Icon("info", t.Border, 18), steps[i].Title, i == x.InstallAt && !stuck ? steps[i].Sub : null, dim: !done && i > x.InstallAt));
                }
                b.Append("</div>");
                break;
            }
            case SetupScreen.Done:
            {
                b.Append(Hero(Icon(f.Reboot ? "warn" : "checkc", f.Reboot ? t.Warn : t.Ok, 72), true));
                b.Append($"<div class='card list'>{Line(null, "Server", null, $"<span class='mut'>{E(f.ServerName() ?? "–")}</span>")}{Line(null, "Pc'ens navn", null, $"<span class='mut'>{E(x.Status?.Machine ?? "")}</span>")}{Line(null, "Profil", null, $"<span class='mut'>{E(x.Status?.Profile ?? "")}</span>")}</div>");
                var (tt, tx) = SetupTexts.TrayTip(os);
                b.Append($"<div class='card tip'>{Line(Icon("info", t.Accent, 20), tt, tx)}</div>");
                break;
            }
            case SetupScreen.Installed:
            {
                var sec = SetupTexts.Sections(os).First(s => s.Id == (x.Section ?? "overview"));
                b.Append($"<h2>{E(sec.Title)}</h2>");
                if (sec.Id == "overview")
                    b.Append("<div class='card list'>" + string.Concat(MacSetup.Summary(x.Status, DateTime.UtcNow).Where(r => r.Label != "Fuld diskadgang").Select(r => Line(null, r.Label, null, $"<span class='mut'>{E(r.Value)}</span>"))) + "</div>");
                else
                {
                    b.Append($"<div class='card list'>{Line(Tile("server", SetupColors.Blue, 28), SetupTexts.HostPort(Url), "Tilmeldt – pc'en har sit eget login. Nøglen skal kun bruges, hvis du skifter server eller profil.")}</div>");
                    b.Append("<div class='note'>\"Skift server eller profil…\" fører dig gennem trinene igen. Fleetdeck installeres forfra, og pc'en tilmelder sig med den nye nøgle.</div>");
                }
                break;
            }
        }

        var foot = new StringBuilder("<span class='btn help'>?</span>");
        if (installed) foot.Append($"<a class='rm'>Fjern Fleetdeck</a>");
        foot.Append("<span class='sp'></span>");
        if (pg.Secondary is { } s2) foot.Append(Btn(s2));
        var canGo = f.Screen == SetupScreen.Installed || f.CanContinue(x.Server.Length > 0, x.Key.Length > 0, x.ProfileId is not null);
        if (pg.Primary is { } p1) foot.Append(Btn(p1, true, !canGo));

        return $$"""
<!doctype html><html lang="da"><head><meta charset="utf-8"><title>Fleetdeck – Windows – {{E(pg.Title)}}</title>
<style>
@font-face { font-family: "UI"; src: url(inter-latin-wght-normal.woff2) format("woff2"); font-weight: 100 900; }
@font-face { font-family: "UI"; src: url(inter-latin-ext-wght-normal.woff2) format("woff2"); font-weight: 100 900; unicode-range: U+0100-024F; }
* { box-sizing: border-box; } body { margin: 0; font-family: "Segoe UI Variable Text", "Segoe UI", "UI", sans-serif; font-size: 13.3px; color: {{t.Text}}; }
.desk { width: 960px; height: 720px; background: {{(dark ? "radial-gradient(70% 70% at 30% 30%,#1b3a6b,#0b1020)" : "radial-gradient(70% 70% at 30% 30%,#cfe3ff,#8fb5e8)")}}; position: relative; overflow: hidden; }
.win { position: absolute; left: 70px; top: 44px; width: 820px; height: 632px; border-radius: 8px; overflow: hidden; background: {{t.Bg}};
  box-shadow: 0 0 0 1px {{(dark ? "#3a3a3a" : "rgba(0,0,0,.18)")}}, 0 18px 50px rgba(0,0,0,.35); display: flex; flex-direction: column; }
.tb { height: 32px; display: flex; align-items: center; gap: 8px; padding-left: 12px; background: {{(dark ? "#202020" : "#eef1f6")}}; font-size: 12px; }
.tb img { width: 16px; } .tb .sp { flex: 1; } .tb .cap { width: 46px; text-align: center; font-size: 14px; color: {{t.Muted}}; } .tb .cap.dis { opacity: .35; }
.body { flex: 1; display: flex; min-height: 0; }
.side { width: 240px; background: {{t.Side}}; border-right: 1px solid {{t.Border}}; padding: 14px 10px 12px; display: flex; flex-direction: column; }
.ident { display: flex; gap: 10px; align-items: center; padding: 6px 8px 18px; } .ident img { width: 36px; } .ident b { display: block; font-size: 14px; } .ident span { font-size: 11.3px; color: {{t.Muted}}; }
.nav { position: relative; display: flex; align-items: center; gap: 10px; height: 36px; padding: 0 12px 0 10px; border-radius: 5px; margin-bottom: 2px; }
.nav.two { height: 46px; } .nav.cur { background: {{(dark ? "rgba(255,255,255,.086)" : "rgba(0,0,0,.055)")}}; font-weight: 600; }
.nav.cur::before { content: ""; position: absolute; left: 0; top: calc(50% - 8px); width: 3px; height: 16px; border-radius: 2px; background: {{t.Accent}}; }
.nav .l { flex: 1; } .nav small { display: block; font-size: 11px; font-weight: 400; color: {{t.Muted}}; } .nav.todo { color: {{t.Muted}}; } .nav.todo .tile { opacity: .45; }
.ver, .ver2 { margin-top: auto; padding: 0 10px; font-size: 11.3px; color: {{t.Muted}}; } .ver2 { margin-top: 2px; } .side-srv { margin-top: auto; padding: 0 10px; font-size: 11.3px; }
.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.content { flex: 1; overflow: hidden; padding: 28px 0 12px; display: flex; flex-direction: column; align-items: center; }
.content > * { width: 480px; }
.foot { height: 64px; border-top: 1px solid {{t.Border}}; display: flex; align-items: center; gap: 8px; padding: 0 20px; } .foot .sp { flex: 1; }
.rm { color: {{t.Bad}}; font-size: 12.6px; margin-left: 14px; }
.tile { display: inline-grid; place-items: center; flex: none; } .tile svg { display: block; }
.hero { display: flex; flex-direction: column; align-items: center; text-align: center; margin-bottom: 16px; }
.hero > :first-child { margin-bottom: 12px; } .hero h1 { font-size: 21px; margin: 0 0 4px; font-weight: 700; } .hero h1.big { font-size: 25px; }
.hero p { margin: 0; color: {{t.Muted}}; max-width: 420px; line-height: 1.4; } .logo { width: 88px; height: 88px; }
h2 { font-size: 21px; margin: 0 0 12px; }
.feat { display: flex; gap: 12px; padding: 4px 18px; } .feat b { display: block; } .feat span { color: {{t.Muted}}; font-size: 11.6px; }
.card { background: {{t.Card}}; border: 1px solid {{t.Border}}; border-radius: 8px; }
.card.pad { padding: 12px 16px; } .card.row { display: flex; gap: 8px; align-items: center; } .card.tip { margin-top: 14px; padding: 4px 2px; }
.card.center { display: flex; gap: 8px; justify-content: center; align-items: center; padding: 30px; }
.card.col { display: flex; flex-direction: column; gap: 6px; align-items: center; text-align: center; padding: 18px 60px; }
.card.list { padding: 4px 1px; } .card.list .line + .line { border-top: 1px solid {{t.Border}}; }
.line { display: flex; gap: 12px; align-items: center; padding: 8px 14px; min-height: 40px; } .line .t { flex: 1; } .line .t.dim { color: {{t.Muted}}; }
.line small { display: block; color: {{t.Muted}}; font-size: 11.6px; }
.choices { display: flex; gap: 12px; } .choices .card { flex: 1; } .card.ch.on { outline: 2px solid {{t.Accent}}; outline-offset: -2px; } .card.ch .line { min-height: 62px; }
.radio { width: 18px; height: 18px; border-radius: 50%; border: 1.2px solid {{t.Muted}}; flex: none; }
.radio.on { border: 0; background: radial-gradient(circle, #fff 4px, {{t.Accent}} 4.5px); }
.sec { font-weight: 700; margin: 14px 2px 6px; display: flex; } .sec a { margin-left: auto; color: {{t.Accent}}; font-weight: 400; } .sec .light { font-weight: 400; color: {{t.Muted}}; margin-left: 6px; }
.note { color: {{t.Muted}}; font-size: 11.6px; margin: 6px 2px 0; } .mut { color: {{t.Muted}}; }
.field { flex: 1; height: 34px; border-radius: 5px; background: {{t.Field}}; border: 1px solid {{t.Border}}; border-bottom: 1px solid {{t.Muted}}; display: flex; align-items: center; padding: 0 10px; }
.field.err { background: color-mix(in srgb, {{t.Field}} 92%, {{t.Bad}}); border-bottom: 2px solid {{t.Bad}}; } .field.ta { height: 58px; align-items: flex-start; padding-top: 7px; }
.ph { color: {{t.Muted}}; }
.btn { display: inline-flex; align-items: center; justify-content: center; min-width: 80px; height: 34px; padding: 0 14px; border-radius: 5px; background: {{(dark ? "#2f3034" : t.Field)}};
  border: 1px solid {{t.Border}}; white-space: nowrap; } .btn.pri { background: {{t.Accent}}; color: #fff; border: 0; font-weight: 600; }
.btn.pri.dis { background: {{(dark ? "#45474d" : "#c5c5c5")}}; color: {{(dark ? "#8b8d93" : "#fff")}}; } .btn.help { min-width: 34px; padding: 0; }
.msg { display: flex; gap: 8px; margin: 8px 2px 0; font-size: 12.6px; } .msg svg { flex: none; margin-top: 1px; } .msg b { display: block; font-weight: 400; } .msg span { color: {{t.Muted}}; }
.msg.bad b { color: {{t.Bad}}; font-weight: 700; }
.det { margin: 4px 0 0 26px; color: {{t.Muted}}; font-size: 11.6px; } .mono { margin: 2px 0 0 26px; font-family: "Cascadia Mono", monospace; font-size: 11.3px; color: {{t.Muted}}; }
.ring { width: 18px; height: 18px; border-radius: 50%; border: 2.2px solid transparent; border-top-color: {{t.Accent}}; border-right-color: {{t.Accent}}; flex: none; }
.bar { height: 6px; border-radius: 3px; background: {{t.Border}}; margin-bottom: 16px; } .bar i { display: block; height: 100%; border-radius: 3px; background: {{t.Accent}}; }
.code { display: flex; gap: 6px; justify-content: center; margin-bottom: 8px; } .code span { width: 38px; height: 48px; border-radius: 7px; border: 1px solid {{t.Border}}; background: {{t.Card}};
  display: grid; place-items: center; font: 700 26px "Cascadia Mono", monospace; } .code i { width: 10px; height: 2.4px; background: {{t.Muted}}; align-self: center; }
.c { display: flex; justify-content: center; margin-bottom: 16px; } .flow { margin: 14px 4px 0; display: grid; gap: 4px; font-size: 12.6px; }
.tg { width: 40px; height: 20px; border-radius: 10px; border: 1px solid {{t.Muted}}; position: relative; flex: none; }
.tg::after { content: ""; position: absolute; left: 4px; top: 4px; width: 10px; height: 10px; border-radius: 50%; background: {{t.Muted}}; }
.tg.on { background: {{t.Accent}}; border-color: {{t.Accent}}; } .tg.on::after { left: 24px; background: #fff; }
</style></head><body><div class="desk"><div class="win">
<div class="tb"><img src="fleetdeck.png">Fleetdeck<span class="sp"></span><span class="cap">—</span><span class="cap dis">☐</span><span class="cap">✕</span></div>
<div class="body"><aside class="side">{{side}}</aside><div class="main"><div class="content">{{b}}</div><div class="foot">{{foot}}</div></div></div>
</div></div></body></html>
""";
    }

    private static string Nav(string icon, string color, string title, string? note, SetupStepState st, bool plain) =>
        $"<div class='nav {(note is null ? "" : "two")} {(st == SetupStepState.Current ? "cur" : st == SetupStepState.Todo && !plain ? "todo" : "")}'>{Tile(icon, color, 22)}" +
        $"<span class='l'>{E(title)}{(note is null ? "" : $"<small>{E(note)}</small>")}</span>" +
        (st == SetupStepState.Done && !plain ? Icon("check", "currentColor", 13, 2.2).Replace("<svg ", "<svg style='opacity:.6' ") : "") + "</div>";

    // ================================================================ Linux (zenity – GTK4/libadwaita-udseende – og terminalen)

    private static IEnumerable<(string, string)> LinuxDialogs()
    {
        const SetupOs os = SetupOs.Linux;
        string T(SetupStep s) => $"Fleetdeck – {SetupTexts.StepLabel(os, s)}";
        var w = SetupWords.For(os);
        var welcome = SetupTexts.Page(SetupScreen.Welcome, new SetupContext(os));
        var intro = welcome.Text + "\n\n" + string.Join("\n", SetupTexts.Features(os).Select(f => $"• {f.Title} – {f.Text}")) +
                    $"\n\n{SetupTexts.PasteTitle} Indsæt det i næste felt, så udfyldes server og nøgle af sig selv.";
        yield return ("velkommen", Zenity(T(SetupStep.Welcome), intro, null, ["Annullér", SetupTexts.Continue], icon: "question"));
        yield return ("soeger", Zenity(T(SetupStep.Server), SetupTexts.SearchingRow, "<div class='pbar'><i></i></div>", []));
        var flow = new SetupFlow(os); flow.Start(); flow.SearchDone([Home, Shop]);
        var fp = flow.Page();
        yield return ("fundet", Zenity(T(SetupStep.Server), $"{fp.Title}\n{fp.Text}",
            List([("TRUE", Home.Name, SetupTexts.FoundLine(Home)), ("FALSE", Shop.Name, SetupTexts.FoundLine(Shop)), ("FALSE", SetupTexts.OrTypeAddress, SetupTexts.WhereIsAddress)], radio: true),
            ["Annullér", SetupTexts.Continue]));
        var cert = SetupProbe.Unreachable("https://fleetdeck.local:8586", new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")), os);
        var nf = new SetupFlow(os); nf.Start(); nf.SearchDone([]);
        var np = SetupTexts.Page(SetupScreen.NoServer, nf.Context());
        yield return ("fejl-ingen-server", Zenity(T(SetupStep.Server),
            $"Fandt ingen server på netværket. {w.Device} og serveren skal være på samme netværk.\n\n{np.Title}\nSkriv adressen ({SetupTexts.ServerPlaceholder}). {SetupTexts.WhereIsAddress}\nDu kan også indsætte hele kommandoen fra Fleetdeck.",
            Entry("10.0.0."), ["Annullér", SetupTexts.Continue]));
        yield return ("fejl-certifikat", Zenity(T(SetupStep.Server),
            $"✖ {cert.Title}\n{cert.Text}\n({SetupTexts.Details}: {cert.Detail})\n\nFind din Fleetdeck-server\nSkriv adressen ({SetupTexts.ServerPlaceholder}). {SetupTexts.WhereIsAddress}\nDu kan også indsætte hele kommandoen fra Fleetdeck.",
            Entry("https://fleetdeck.local:8586"), ["Annullér", SetupTexts.Continue]));
        var kp = SetupTexts.Page(SetupScreen.Key, new SetupContext(os, Home.Name));
        yield return ("tilmelding", Zenity(T(SetupStep.Enroll), $"{kp.Title}\n{kp.Text}",
            List([("TRUE", SetupTexts.HaveKey, SetupTexts.HaveKeySub), ("FALSE", SetupTexts.AskAccess, SetupTexts.AskAccessSub)], radio: true), ["Annullér", SetupTexts.Continue]));
        yield return ("fejl-noegle", Zenity(T(SetupStep.Enroll),
            $"✖ Nøglen passer ikke.\nHent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet.\n\n{kp.Title}\nIndsæt tilmeldingsnøglen fra Fleetdeck under Maskiner → Tilføj maskine. Du kan også indsætte hele kommandoen derfra.",
            Entry("", password: true), ["Annullér", SetupTexts.Continue]));
        var pp = SetupTexts.Page(SetupScreen.Profile, new SetupContext(os));
        yield return ("profil", Zenity(T(SetupStep.Profile), $"{pp.Title}\n{pp.Text}",
            List([("FALSE", SetupTexts.NoProfile, SetupTexts.NoProfileSub), ("TRUE", "Servere", "4 programmer · 6 maskiner"), ("FALSE", "Kontor", "18 programmer · 5 maskiner")], radio: true),
            ["Annullér", SetupTexts.Continue]));
        yield return ("tilvalg", Zenity(T(SetupStep.Profile), $"{SetupTexts.ExtensionsSection} – kan også slås til senere i Fleetdeck:",
            List([.. Extensions.BuiltIn.Where(e => e.Linux is not null).Select(e => (Extensions.DefaultOn.Contains(e.Id) ? "TRUE" : "FALSE", e.Name, e.Gives))], radio: false),
            ["Spring over", "Installér"]));
        var ip = SetupTexts.Page(SetupScreen.Installing, new SetupContext(os));
        yield return ("installerer", Zenity(T(SetupStep.Install), $"{ip.Title}\n{ip.Text}\n\n{w.Password}", "<div class='pbar'><i></i></div>", []));
        var rp = SetupTexts.Page(SetupScreen.Request, new SetupContext(os, Home.Name));
        yield return ("anmod", Zenity(T(SetupStep.Enroll),
            $"{SetupTexts.AskAccess}\n{string.Join("\n", SetupTexts.RequestFlow.Select((x, i) => $"{i + 1}. {x}"))}\n\n{SetupTexts.MessageSection} {SetupTexts.Optional} – {w.Example}.",
            Entry(""), ["Annullér", SetupTexts.Continue]));
        _ = rp;
        var wf = new SetupFlow(os); wf.Start(); wf.SearchDone([Home]); wf.ServerTested(SetupCheck.Good("", "")); wf.ChooseAsk(true); wf.RequestSent("7K4-Q2M");
        var wp = wf.Page(); var (rt, rx, _) = SetupTexts.WaitingRow(os, null);
        yield return ("venter", Zenity(T(SetupStep.Enroll),
            $"{wp.Title} – kode 7K4-Q2M\n\n{wp.Text}\n\n{rt}: {rx}\nDu kan godt lukke vinduet – {w.DeviceLower} tilmelder sig selv, når anmodningen er godkendt.",
            "<div class='pbar'><i></i></div>", [SetupTexts.Close]));
        wf.RequestUpdate(new TrayStatus { AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Rejected, Reason = "Ukendt maskine – skriv lige, hvis det er din." } });
        var rj = wf.Page();
        yield return ("afvist", Zenity(T(SetupStep.Enroll),
            $"{rj.Title}\n\n{rj.Text}\n\nBesked fra administratoren: “{wf.Reason}”\n\nSend en ny anmodning med en besked, så administratoren kan se, hvilken maskine det er.",
            null, ["Annullér", SetupTexts.AskAgain], icon: "question"));
        var nc = SetupTexts.Page(SetupScreen.NoConnection, new SetupContext(os));
        yield return ("ingen-forbindelse", Zenity(T(SetupStep.Install), $"{nc.Title}\n\n{nc.Text} Agenten prøver selv igen. Tjek, at {w.DeviceLower} er på samme netværk som serveren.", null, ["OK"], icon: "info"));
        var df = new SetupFlow(os); df.Start(); df.SearchDone([Home]); df.ServerTested(SetupCheck.Good("", "")); df.KeyTested(SetupCheck.Good("", "")); df.ProfilesLoaded(); df.InstallStarted(); df.InstallFinished(true);
        var dp = df.Page(); var (tt, tx) = SetupTexts.TrayTip(os);
        yield return ("faerdig", Zenity(T(SetupStep.Finish), $"{dp.Title}\n\n{dp.Text}\n\nServer: {df.ServerName()}\nNavn: kaelder-server\n\n{tt}: {tx}", null, ["OK"], icon: "info"));
        yield return ("terminal", Terminal());
    }

    private static string Entry(string value, bool password = false) =>
        $"<div class='entry'>{(password ? "<span class='ph'>••••••••••••</span>" : value.Length > 0 ? E(value) : "")}<span class='caret'></span></div>";

    private static string List(IEnumerable<(string On, string A, string B)> rows, bool radio) =>
        "<table class='lst'><tr><th></th><th>" + (radio ? "Valg" : "Tilvalg") + "</th><th>" + (radio ? "" : "Giver") + "</th></tr>" +
        string.Concat(rows.Select(r => $"<tr class='{(r.On == "TRUE" ? "sel" : "")}'><td><span class='{(radio ? "rb" : "cb")} {(r.On == "TRUE" ? "on" : "")}'></span></td><td>{E(r.A)}</td><td class='mut'>{E(r.B)}</td></tr>")) +
        "</table>";

    /// <summary>En zenity 4-dialog (GTK4/libadwaita): titellinje, tekst, evt. felt/liste/bjælke og knapper i bunden.</summary>
    private static string Zenity(string title, string text, string? body, string[] buttons, string? icon = null) => $$"""
<!doctype html><html lang="da"><head><meta charset="utf-8"><title>{{E(title)}}</title><style>
@font-face { font-family: "UI"; src: url(inter-latin-wght-normal.woff2) format("woff2"); font-weight: 100 900; }
@font-face { font-family: "UI"; src: url(inter-latin-ext-wght-normal.woff2) format("woff2"); font-weight: 100 900; unicode-range: U+0100-024F; }
* { box-sizing: border-box; } body { margin: 0; font-family: "Cantarell", "UI", sans-serif; font-size: 14px; color: #1e1e1e; }
.desk { width: 960px; min-height: 640px; background: linear-gradient(160deg,#3d6fb6,#7aa3d8); display: grid; place-items: center; padding: 40px; }
.dlg { width: 600px; background: #fafafa; border-radius: 12px; box-shadow: 0 0 0 1px rgba(0,0,0,.12), 0 16px 40px rgba(0,0,0,.35); overflow: hidden; }
.hb { height: 46px; display: flex; align-items: center; justify-content: center; font-weight: 700; background: #ebebeb; position: relative; }
.hb .x { position: absolute; right: 10px; width: 24px; height: 24px; border-radius: 50%; background: #d6d6d6; display: grid; place-items: center; font-size: 12px; }
.in { padding: 18px 22px 8px; display: flex; gap: 14px; } .ic { width: 48px; height: 48px; border-radius: 50%; flex: none; display: grid; place-items: center; color: #fff; font-weight: 700; font-size: 24px; }
.ic.question { background: #3584e4; } .ic.info { background: #3584e4; } .txt { white-space: pre-wrap; line-height: 1.42; flex: 1; }
.extra { padding: 4px 22px 14px; }
.entry { height: 36px; border-radius: 6px; background: #fff; box-shadow: inset 0 0 0 2px #3584e4; display: flex; align-items: center; padding: 0 10px; }
.caret { width: 1px; height: 18px; background: #1e1e1e; margin-left: 1px; } .ph { letter-spacing: 2px; }
.pbar { height: 8px; border-radius: 4px; background: #dcdcdc; overflow: hidden; } .pbar i { display: block; width: 25%; height: 100%; margin-left: 38%; background: #3584e4; border-radius: 4px; }
.lst { width: 100%; border-collapse: collapse; background: #fff; border-radius: 8px; overflow: hidden; box-shadow: 0 0 0 1px #d8d8d8; }
.lst th { text-align: left; font-weight: 700; font-size: 13px; padding: 6px 8px; border-bottom: 1px solid #e0e0e0; } .lst td { padding: 7px 8px; border-bottom: 1px solid #f0f0f0; vertical-align: top; }
.lst tr.sel td { background: #e8f0fc; } .mut { color: #6b6b6b; font-size: 13px; }
.rb, .cb { display: inline-block; width: 16px; height: 16px; border: 2px solid #9a9a9a; border-radius: 50%; } .cb { border-radius: 4px; }
.rb.on { border: 5px solid #3584e4; } .cb.on { background: #3584e4; border-color: #3584e4; }
.btns { display: flex; border-top: 1px solid #ddd; } .btns span { flex: 1; text-align: center; padding: 12px; } .btns span + span { border-left: 1px solid #ddd; }
.btns span:last-child { color: #1c71d8; font-weight: 700; }
</style></head><body><div class="desk"><div class="dlg"><div class="hb">{{E(title)}}<span class="x">✕</span></div>
<div class="in">{{(icon is null ? "" : $"<span class='ic {icon}'>{(icon == "question" ? "?" : "i")}</span>")}}<div class="txt">{{E(text)}}</div></div>
{{(body is null ? "" : $"<div class='extra'>{body}</div>")}}
{{(buttons.Length == 0 ? "" : "<div class='btns'>" + string.Concat(buttons.Select(b => $"<span>{E(b)}</span>")) + "</div>")}}
</div></div></body></html>
""";

    private static string Terminal()
    {
        const SetupOs os = SetupOs.Linux;
        string H(SetupStep s) => $"── {SetupTexts.StepLabel(os, s)} ──";
        var welcome = SetupTexts.Page(SetupScreen.Welcome, new SetupContext(os));
        var f = new SetupFlow(os); f.Start(); f.SearchDone([Home]);
        var kp = SetupTexts.Page(SetupScreen.Key, new SetupContext(os, Home.Name));
        var pp = SetupTexts.Page(SetupScreen.Profile, new SetupContext(os));
        var lines = $"""
$ sudo fleetdeck-agent setup

{H(SetupStep.Welcome)}
{welcome.Title}
{welcome.Text}

{string.Join("\n", SetupTexts.Features(os).Select(x => $"• {x.Title} – {x.Text}"))}

{SetupTexts.PasteTitle} Indsæt det i næste felt, så udfyldes server og nøgle af sig selv.

Tryk Enter = Fortsæt (Ctrl+C afbryder):
… {SetupTexts.SearchingRow}

{H(SetupStep.Server)}
{f.Page().Title}
{f.Page().Text}
  1) {Home.Name} – {SetupTexts.FoundLine(Home)}
  2) {SetupTexts.OrTypeAddress} – {SetupTexts.WhereIsAddress}
Skriv nummeret [1]: 1
… Tjekker http://192.168.1.10:8585…

{H(SetupStep.Enroll)}
{kp.Title}
{kp.Text}
  1) {SetupTexts.HaveKey} – {SetupTexts.HaveKeySub}
  2) {SetupTexts.AskAccess} – {SetupTexts.AskAccessSub}
Skriv nummeret [1]: 1

{H(SetupStep.Enroll)}
{kp.Title}
Indsæt tilmeldingsnøglen fra Fleetdeck under Maskiner → Tilføj maskine. Du kan også indsætte hele kommandoen derfra.
{SetupTexts.KeySection}:
… Tjekker nøglen…

{H(SetupStep.Profile)}
{pp.Title}
{pp.Text}
  1) {SetupTexts.NoProfile} – {SetupTexts.NoProfileSub}
  2) Servere – 4 programmer · 6 maskiner
Skriv nummeret: 
Vælg et af numrene ovenfor – der er ikke valgt noget på forhånd.
Skriv nummeret: 2

{H(SetupStep.Profile)}
{SetupTexts.ExtensionsSection} – kan også slås til senere i Fleetdeck:
  Diskhelbred (SMART) – … ? [J/n]
… Installerer Fleetdeck

Installerer agenten (kræver administrator) …

{H(SetupStep.Finish)}
✔ Alt er klar

Fleetdeck passer nu på denne maskine. Du finder den som tjenesten fleetdeck-agent.
""";
        return $"<!doctype html><html lang='da'><head><meta charset='utf-8'><title>Terminal</title><style>body{{margin:0;background:#2b2b2b}}pre{{margin:30px;padding:18px 22px;width:900px;background:#1d1f21;color:#e6e6e6;font:13px/1.45 'DejaVu Sans Mono',monospace;border-radius:10px;white-space:pre-wrap}}</style></head><body><pre>{E(lines)}</pre></body></html>";
    }
}
