using System.Net;
using System.Security.Authentication;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>
/// Opsætningsvinduet i forslag B: den fælles trin-/tilstandsmaskine (<see cref="SetupFlow"/>), teksterne, tjekket af server og nøgle
/// (<see cref="SetupProbe"/>), ikonerne (<see cref="SvgPath"/>) – og at Mac, Windows og Linux har alle skærmene.
/// </summary>
public class SetupFlowTests
{
    private static readonly SetupOs[] All = [SetupOs.Mac, SetupOs.Windows, SetupOs.Linux];
    private static readonly Discovery.Found Home = new("Fleetdeck · hjemme", "http://192.168.1.10:8585", "3.41.3", true);
    private static readonly Discovery.Found Shop = new("Fleetdeck · værksted", "http://192.168.1.24:8585", "3.40.1", true);
    private static SetupCheck Ok(string v = "3.41.3") => SetupCheck.Good("Forbindelse OK.", "x", v);

    // ---------------------------------------------------------------- trin og tekster

    [Fact]
    public void SixStepsWithPermissionsOnlyOnMac()
    {
        foreach (var os in All)
        {
            var steps = SetupTexts.Steps(os);
            Assert.Equal(Enum.GetValues<SetupStep>(), steps.Select(s => s.Step));
            Assert.Equal(["Velkommen", "Server", "Tilmelding", "Profil og tilvalg", "Installation"], steps.Take(5).Select(s => s.Title));
            Assert.Equal(os == SetupOs.Mac ? "Tilladelser" : "Færdig", steps[^1].Title);
            Assert.All(steps, s => Assert.True(SetupIcons.Paths.ContainsKey(s.Icon), s.Icon));
        }
        Assert.Equal("Trin 2 af 6 · Server", SetupTexts.StepLabel(SetupOs.Linux, SetupStep.Server));
        Assert.Equal("Trin 6 af 6 · Færdig", SetupTexts.StepLabel(SetupOs.Windows, SetupStep.Finish));
        Assert.Contains(SetupTexts.Sections(SetupOs.Mac), s => s.Id == "access");
        Assert.DoesNotContain(SetupTexts.Sections(SetupOs.Windows), s => s.Id == "access");
    }

    [Fact]
    public void EveryScreenHasAPageOnEveryPlatform()
    {
        foreach (var os in All)
            foreach (var screen in SetupFlow.AllScreens)
            {
                var p = SetupTexts.Page(screen, new SetupContext(os, "Fleetdeck · hjemme", 2, "7K4-Q2M", "PC1", "Hjemme"));
                Assert.False(string.IsNullOrWhiteSpace(p.Title), $"{os} {screen}");
                Assert.False(string.IsNullOrWhiteSpace(p.Text), $"{os} {screen}");
                Assert.NotNull(p.Primary);
                Assert.Equal(SetupFlow.StepOf(screen), p.Step);
                Assert.True(p.Icon == "logo" || SetupIcons.Paths.ContainsKey(p.Icon), p.Icon);
            }
    }

    [Fact]
    public void TheTextsAreTheSameOnAllPlatformsExceptTheDeviceWord()
    {
        foreach (var screen in SetupFlow.AllScreens)
        {
            string Norm(SetupOs os) => SetupTexts.Page(screen, new SetupContext(os, "srv", 2, "7K4-Q2M")).Title
                .Replace("Mac'en", "X").Replace("pc'en", "X").Replace("maskinen", "X");
            Assert.Equal(Norm(SetupOs.Mac), Norm(SetupOs.Windows));
            Assert.Equal(Norm(SetupOs.Windows), Norm(SetupOs.Linux));
        }
        Assert.Equal("Tilmeld Mac'en", SetupTexts.Page(SetupScreen.Key, new(SetupOs.Mac)).Title);
        Assert.Equal("Tilmeld pc'en", SetupTexts.Page(SetupScreen.Key, new(SetupOs.Windows)).Title);
        Assert.StartsWith("Fleetdeck · hjemme skal vide", SetupTexts.Page(SetupScreen.Key, new(SetupOs.Mac, "Fleetdeck · hjemme")).Text);
        Assert.StartsWith("Serveren skal vide", SetupTexts.Page(SetupScreen.Key, new(SetupOs.Mac)).Text);
        Assert.Contains("to servere", SetupTexts.Page(SetupScreen.Found, new(SetupOs.Mac, Found: 2)).Text);
        Assert.Contains("Genstart nu", SetupTexts.Page(SetupScreen.Done, new(SetupOs.Windows, Reboot: true)).Primary);
        Assert.Equal("Anmodningen er udløbet", SetupTexts.Page(SetupScreen.Rejected, new(SetupOs.Mac, Expired: true)).Title);
        Assert.Contains("notch", SetupTexts.HelpFor(SetupOs.Mac, SetupStep.Finish).Text);
        Assert.Equal(4, SetupTexts.InstallSteps(SetupOs.Mac).Count);
        Assert.Equal(3, SetupTexts.InstallSteps(SetupOs.Windows).Count);
        Assert.Equal("192.168.1.10:8585 · version 3.41.3", SetupTexts.FoundLine(Home));
        Assert.Equal("3 programmer · 1 maskiner", SetupTexts.ProfileLine(new ProfileOption(1, "A", 1, 3)));
    }

    // ---------------------------------------------------------------- overgange

    [Fact]
    public void TheHappyPathOnAMacEndsWithFullDiskAccessThenDone()
    {
        var f = new SetupFlow(SetupOs.Mac);
        Assert.Equal(SetupScreen.Welcome, f.Screen);
        Assert.Equal(SetupStepState.Current, f.StateOf(SetupStep.Welcome));
        f.Start(); f.SearchStarted();
        Assert.Equal(SetupScreen.Searching, f.Screen);
        Assert.False(f.CanContinue(hasAddress: false));
        f.SearchDone([Home]);
        Assert.Equal(SetupScreen.Found, f.Screen);
        Assert.Equal(Home.Url, f.Server);                       // én server: adressen udfyldes
        Assert.Equal("Fleetdeck · hjemme", f.ServerName());
        Assert.Equal(SetupStepState.Done, f.StateOf(SetupStep.Welcome));
        f.ServerTested(Ok());
        Assert.Equal(SetupScreen.Key, f.Screen);
        Assert.Equal(SetupStep.Enroll, f.Step);
        Assert.False(f.CanContinue(hasKey: false));
        f.KeyTested(SetupCheck.Good("Nøglen passer.", "…"));
        Assert.True(f.CanContinue());
        f.ProfilesLoaded();
        Assert.Equal(SetupScreen.Profile, f.Screen);
        Assert.False(f.CanContinue(profileChosen: false));
        Assert.True(f.CanContinue(profileChosen: true));
        f.InstallStarted();
        Assert.Equal(SetupScreen.Installing, f.Screen);
        Assert.False(f.CanContinue(true, true, true));
        f.InstallFinished(connected: true);
        Assert.Equal(SetupScreen.Enrolled, f.Screen);           // Mac: Fuld diskadgang
        Assert.False(f.CanContinue());
        f.AccessGranted();
        Assert.True(f.CanContinue());
        f.Finish();
        Assert.Equal(SetupScreen.Done, f.Screen);
        Assert.All(Enum.GetValues<SetupStep>(), s => Assert.Equal(SetupStepState.Done, f.StateOf(s)));
    }

    [Theory]
    [InlineData(SetupOs.Windows)]
    [InlineData(SetupOs.Linux)]
    public void WindowsAndLinuxGoStraightToDone(SetupOs os)
    {
        var f = new SetupFlow(os);
        f.Start(); f.SearchDone([]); f.Server = "192.168.1.10";
        Assert.Equal(SetupScreen.NoServer, f.Screen);
        f.ServerTested(Ok()); f.KeyTested(Ok()); f.ProfilesLoaded(); f.InstallStarted();
        f.InstallFinished(connected: true, reboot: true);
        Assert.Equal(SetupScreen.Done, f.Screen);
        Assert.True(f.Reboot);
        Assert.Equal(SetupStepState.Current, f.StateOf(SetupStep.Finish));   // genstart mangler: ikke "færdig" endnu
        Assert.Equal("Genstart nu", f.Page().Primary);
        Assert.DoesNotContain(SetupScreen.Enrolled, SetupFlow.ScreensFor(os));
    }

    [Fact]
    public void ErrorsBelongToTheirField()
    {
        var f = new SetupFlow(SetupOs.Mac);
        f.Start(); f.SearchDone([Home, Shop]);
        Assert.Equal("", f.Server);                             // flere servere: brugeren vælger
        var bad = SetupCheck.Bad(SetupField.Server, "Kan ikke nå x.", "…");
        f.ServerTested(bad);
        Assert.Equal(SetupScreen.ServerError, f.Screen);
        Assert.Same(bad, f.ErrorFor(SetupField.Server));
        Assert.Null(f.ErrorFor(SetupField.Key));
        f.ServerTested(bad);                                     // igen: bliver på skærmen
        f.ServerChanged();
        Assert.Null(f.ErrorFor(SetupField.Server));
        f.ServerTested(Ok());
        var wrong = SetupCheck.Bad(SetupField.Key, "Nøglen passer ikke.", "…");
        f.KeyTested(wrong);
        Assert.Equal(SetupScreen.KeyError, f.Screen);
        Assert.Same(wrong, f.ErrorFor(SetupField.Key));
        f.KeyChanged();                                          // nøglen rettes: fejlen forsvinder
        Assert.Equal(SetupScreen.Key, f.Screen);
        Assert.Null(f.ErrorFor(SetupField.Key));
        f.KeyTested(SetupCheck.Bad(SetupField.Server, "Kan ikke nå x.", "…"));   // nøgletesten kunne ikke nå serveren
        Assert.Equal(SetupScreen.ServerError, f.Screen);
    }

    [Fact]
    public void AFailedInstallGoesBackWithTheErrorAtTheButtonsOrTheKey()
    {
        var f = new SetupFlow(SetupOs.Mac);
        f.Start(); f.SearchDone([Home]); f.ServerTested(Ok()); f.KeyTested(Ok()); f.ProfilesLoaded(); f.InstallStarted();
        f.InstallFinished(false, error: SetupCheck.Bad(SetupField.None, "Installationen blev ikke gennemført.", "Afbrudt."));
        Assert.Equal(SetupScreen.Profile, f.Screen);
        Assert.Equal("Installationen blev ikke gennemført.", f.ActionError?.Title);
        f.InstallStarted();
        Assert.Null(f.ActionError);
        f.InstallFinished(false, error: SetupCheck.Bad(SetupField.Key, "Nøglen blev afvist.", "…"));
        Assert.Equal(SetupScreen.KeyError, f.Screen);
        f.KeyTested(Ok()); f.ProfilesLoaded(); f.InstallStarted();
        f.InstallFinished(connected: false);
        Assert.Equal(SetupScreen.NoConnection, f.Screen);
        Assert.Equal("Tjek igen", f.Page().Primary);
        f.Finish();
        Assert.Equal(SetupScreen.Done, f.Screen);
    }

    [Fact]
    public void RequestAccessWaitsShowsTheCodeAndHandlesApprovalRejectionAndExpiry()
    {
        var f = new SetupFlow(SetupOs.Windows);
        f.Start(); f.SearchDone([Home]); f.ServerTested(Ok());
        f.ChooseAsk(true);
        Assert.Equal(SetupScreen.Request, f.Screen);
        Assert.Equal("Vælges af administratoren", f.StepNote(SetupStep.Profile));
        f.ActionFailed(SetupCheck.Bad(SetupField.None, "Anmodningen blev ikke sendt.", "…"));
        Assert.Equal(SetupScreen.Request, f.Screen);
        Assert.NotNull(f.ActionError);
        f.RequestSent("7K4-Q2M");
        Assert.Equal(SetupScreen.Waiting, f.Screen);
        Assert.Equal("Venter på godkendelse", f.StepNote(SetupStep.Enroll));
        Assert.Contains("Fleetdeck · hjemme", f.Page().Text);
        var st = new TrayStatus { AccessRequest = new TrayAccessRequest { Code = "9AB-CDE", State = AccessStates.Pending } };
        f.RequestUpdate(st);
        Assert.Equal(SetupScreen.Waiting, f.Screen);
        Assert.Equal("9AB-CDE", f.Code);                        // ny kode fra tjenesten
        st.AccessRequest.State = AccessStates.Rejected; st.AccessRequest.Reason = "Ukendt maskine";
        f.RequestUpdate(st);
        Assert.Equal(SetupScreen.Rejected, f.Screen);
        Assert.Equal("Ukendt maskine", f.Reason);
        Assert.Equal("Afvist", f.StepNote(SetupStep.Enroll));
        Assert.Contains("9AB-CDE", f.Page().Text);
        f.AskAgain();
        Assert.Equal(SetupScreen.Request, f.Screen);
        f.RequestSent("7K4-Q2M");
        f.RequestUpdate(new TrayStatus { AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Expired } });
        Assert.True(f.Expired);
        Assert.Equal("Anmodningen er udløbet", f.Page().Title);
        f.AskAgain(); f.RequestSent("7K4-Q2M");
        f.RequestUpdate(new TrayStatus { Enrolled = true });
        Assert.Equal(SetupScreen.Done, f.Screen);               // Windows: ingen Fuld diskadgang
        var m = new SetupFlow(SetupOs.Mac); m.Start(); m.SearchDone([Home]); m.ServerTested(Ok()); m.ChooseAsk(true); m.RequestSent("7K4-Q2M");
        m.RequestUpdate(new TrayStatus { Connected = true });
        Assert.Equal(SetupScreen.Enrolled, m.Screen);
    }

    [Fact]
    public void ForcedRequestIgnoresStatusFromBeforeTheRequest()
    {
        // "Anmod igen" med --force på en tilmeldt pc: en gammel status.json (tilmeldt) må ikke springe til "Alt er klar" før godkendelsen
        var sent = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var f = new SetupFlow(SetupOs.Windows); f.Start(); f.SearchDone([Home]); f.ServerTested(Ok()); f.ChooseAsk(true);
        f.RequestSent("7K4-Q2M", sent);
        f.RequestUpdate(new TrayStatus { Enrolled = true, Connected = true, Written = sent.AddSeconds(-30) });
        Assert.Equal(SetupScreen.Waiting, f.Screen);
        f.RequestUpdate(new TrayStatus { AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Pending }, Written = sent.AddSeconds(2) });
        Assert.Equal(SetupScreen.Waiting, f.Screen);
        f.RequestUpdate(new TrayStatus { Enrolled = true, Written = sent.AddMinutes(1) });   // godkendt og tilmeldt efter anmodningen
        Assert.Equal(SetupScreen.Done, f.Screen);
    }

    [Fact]
    public void InstalledTestStaysInOverview()
    {
        // "Test forbindelse" i oversigten (Mac): resultatet står ved feltet, skærmen bliver Installed.
        var f = new SetupFlow(SetupOs.Mac, installed: true);
        f.KeyTested(Ok());
        Assert.Equal(SetupScreen.Installed, f.Screen);
        Assert.True(f.KeyCheck!.Ok);
        f.KeyTested(SetupCheck.Bad(SetupField.Key, "Nøglen passer ikke.", "…"));
        Assert.Equal(SetupScreen.Installed, f.Screen);
        Assert.NotNull(f.ErrorFor(SetupField.Key));
        f.KeyTested(SetupCheck.Bad(SetupField.Server, "Kan ikke nå x.", "…"));
        Assert.Equal(SetupScreen.Installed, f.Screen);
        Assert.NotNull(f.ErrorFor(SetupField.Server));
        Assert.Null(f.ErrorFor(SetupField.Key));
        f.ServerTested(SetupCheck.Bad(SetupField.Server, "Kan ikke nå x.", "…"));
        Assert.Equal(SetupScreen.Installed, f.Screen);
        f.Back();
        Assert.Equal(SetupScreen.Installed, f.Screen);
    }

    [Fact]
    public void BackAndInstalledOverview()
    {
        var f = new SetupFlow(SetupOs.Mac, installed: true);
        Assert.Equal(SetupScreen.Installed, f.Screen);
        Assert.Equal("Gem og forbind igen", f.Page().Primary);
        Assert.Equal(SetupTexts.ChangeServer, new SetupFlow(SetupOs.Windows, installed: true).Page().Primary);
        f.Start(); f.SearchDone([Home]);
        f.Back();
        Assert.Equal(SetupScreen.Installed, f.Screen);          // installeret: tilbage til oversigten
        f.Uninstalled();
        Assert.Equal(SetupScreen.Welcome, f.Screen);
        Assert.False(f.Installed);
        f.Start(); f.SearchDone([Home]); f.ServerTested(Ok());
        f.Back();
        Assert.Equal(SetupScreen.Found, f.Screen);
        f.Back();
        Assert.Equal(SetupScreen.Welcome, f.Screen);
        f.Start(); f.SearchDone([]); f.Server = "x"; f.ServerTested(Ok()); f.KeyTested(Ok()); f.ProfilesLoaded();
        f.Back();
        Assert.Equal(SetupScreen.Key, f.Screen);
        f.Back();
        Assert.Equal(SetupScreen.NoServer, f.Screen);
    }

    // ---------------------------------------------------------------- tjek af server og nøgle

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Urls.Add(r.RequestUri!.PathAndQuery);
            return Task.FromResult(answer(r));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };

    [Theory]
    [InlineData(SetupOs.Mac, "/install.sh")]
    [InlineData(SetupOs.Linux, "/install.sh")]
    [InlineData(SetupOs.Windows, "/install.ps1")]
    public async Task TheProbeChecksServerAndKeyLikeTheOldWindows(SetupOs os, string script)
    {
        var fake = new Fake(r => r.RequestUri!.AbsolutePath == "/api/health" ? Json("{\"ok\":true,\"version\":\"3.41.3\"}")
            : r.RequestUri.Query.Contains("good") ? Json("#!") : Json("", HttpStatusCode.Forbidden));
        using var http = new HttpClient(fake);
        var noKey = await SetupProbe.Test(http, "http://192.168.1.10:8585", null, os);
        Assert.True(noKey.Ok);
        Assert.Equal("3.41.3", noKey.Version);
        Assert.Single(fake.Urls);                               // uden nøgle: kun serveren
        var good = await SetupProbe.Test(http, "http://192.168.1.10:8585", "good", os);
        Assert.True(good.Ok);
        Assert.Equal("Nøglen passer.", good.Title);
        Assert.StartsWith(script + "?key=good", fake.Urls[^1]);
        var bad = await SetupProbe.Test(http, "http://192.168.1.10:8585", "bad", os);
        Assert.False(bad.Ok);
        Assert.Equal(SetupField.Key, bad.Field);
        Assert.Equal("Nøglen passer ikke.", bad.Title);
        var empty = await SetupProbe.Test(http, "", "good", os);
        Assert.Equal(SetupField.Server, empty.Field);
    }

    [Fact]
    public async Task TheProbeRecognisesOtherServersAndOldWinStyring()
    {
        using var other = new HttpClient(new Fake(_ => Json("<html>")));
        var r = await SetupProbe.Test(other, "http://x", "k", SetupOs.Mac);
        Assert.Equal(SetupField.Server, r.Field);
        Assert.Equal("Det er ikke en Fleetdeck-server.", r.Title);
        using var old = new HttpClient(new Fake(_ => Json("{\"ok\":true,\"version\":\"2.9.0\"}")));
        r = await SetupProbe.Test(old, "http://x", "k", SetupOs.Windows);
        Assert.Equal("Serveren skal opgraderes først.", r.Title);
        Assert.Contains("WinStyring 2.9.0", r.Text);
    }

    [Fact]
    public async Task CertificateErrorsGetADanishExplanationWithTheTechnicalTextFolded()
    {
        var e = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));
        using var http = new HttpClient(new Fake(_ => throw e));
        var r = await SetupProbe.Test(http, "https://fleetdeck.local:8586", null, SetupOs.Mac);
        Assert.False(r.Ok);
        Assert.Equal(SetupField.Server, r.Field);
        Assert.True(SetupProbe.IsCertificateText(r));
        Assert.Contains("Mac'en stoler ikke på serverens HTTPS-certifikat", r.Text);
        Assert.DoesNotContain("SSL", r.Text);                   // den engelske tekst står kun under "Tekniske detaljer"
        Assert.Contains("UntrustedRoot", r.Detail);
        Assert.True(SetupProbe.IsCertificate(new HttpRequestException("x", new AuthenticationException("y"))));
        Assert.Equal("Serveren svarer ikke.", SetupProbe.Unreachable("http://x", new TaskCanceledException(), SetupOs.Linux).Title);
        Assert.Equal("Navnet kan ikke findes.", SetupProbe.Unreachable("http://nope.local", new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)), SetupOs.Windows).Title);
        Assert.StartsWith("Kan ikke nå http://x", SetupProbe.Unreachable("http://x", new HttpRequestException("refused"), SetupOs.Windows).Title);
        Assert.Equal("sidste", SetupProbe.LastLine("første\nsidste\n"));
    }

    // ---------------------------------------------------------------- ikonerne

    [Fact]
    public void EveryIconParsesToBezierFiguresInsideTheBox()
    {
        foreach (var (name, parts) in SetupIcons.Paths)
            foreach (var part in parts)
            {
                var figs = SvgPath.Parse(part.Length > 1 && part[1] == ':' ? part[2..] : part);
                Assert.NotEmpty(figs);
                foreach (var f in figs)
                {
                    Assert.Equal(1, f.Points.Count % 3);        // start + tripler
                    Assert.All(f.Points, p => Assert.InRange(p.X, -1, 25));
                    Assert.All(f.Points, p => Assert.InRange(p.Y, -1, 25));
                }
                Assert.Contains("<path", SetupIcons.Svg(name));
            }
        // en hel cirkel (to halve buer) ender, hvor den startede
        var c = SvgPath.Parse("M20.5 12a8.5 8.5 0 1 1-17 0a8.5 8.5 0 1 1 17 0Z")[0];
        Assert.True(c.Closed);
        Assert.Equal(20.5, c.Points[^1].X, 3); Assert.Equal(12, c.Points[^1].Y, 3);
        Assert.Contains(c.Points, p => Math.Abs(p.Y - 3.5) < 0.6 || Math.Abs(p.Y - 20.5) < 0.6);
        var h = SvgPath.Parse("M5 7h12v3H5Z")[0];
        Assert.Equal((17d, 10d), h.Points[6]);
    }

    // ---------------------------------------------------------------- alle tre platforme har alle skærme

    [Theory]
    [InlineData(SetupOs.Mac, "src/Fleetdeck.MacApp/SetupWindow.cs")]
    [InlineData(SetupOs.Windows, "src/Fleetdeck.Agent.Windows/SetupForm.cs")]
    [InlineData(SetupOs.Linux, "src/Fleetdeck.Agent.Unix/Setup.cs")]
    public void EveryPlatformShowsEveryScreen(SetupOs os, string file)
    {
        var src = File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, file));
        foreach (var s in SetupFlow.ScreensFor(os)) Assert.Contains($"SetupScreen.{s}", src);
        Assert.Contains("SetupTexts.", src);                    // teksterne kommer fra den fælles model
        Assert.Contains($"SetupOs.{os}", src);
    }

    [Fact]
    public void TheOldCopiesOfTheServerTestAreGone()
    {
        string R(string f) => File.ReadAllText(Path.Combine(MacPackTests.RepoRoot, f));
        Assert.DoesNotContain("Test(string url, string key)", R("src/Fleetdeck.Agent.Unix/Setup.cs"));
        Assert.DoesNotContain("Test(string url, string key)", R("src/Fleetdeck.Agent.Windows/SetupForm.cs"));
        Assert.Contains("SetupProbe.Test", R("src/Fleetdeck.MacApp/SetupWindow.cs"));
        // Mac: indholdet op under trafiklysene og vibrancy (forslag B)
        var mac = R("src/Fleetdeck.MacApp/SetupWindow.cs");
        Assert.Contains("ExtendClientAreaToDecorationsHint = true", mac);
        Assert.Contains("WindowTransparencyLevel.AcrylicBlur", mac);
        Assert.Contains("DwmSystemBackdropType", R("src/Fleetdeck.Agent.Windows/SetupParts.cs"));
    }
}
