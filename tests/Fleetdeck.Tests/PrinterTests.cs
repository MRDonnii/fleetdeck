using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services.Printers;
using Fleetdeck.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Printere: adresser, plan og kommandoer pr. platform, fortolkning, IPP, Bonjour, tildelinger og Home Assistant.</summary>
public class PrinterTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 8, 12, 0, DateTimeKind.Utc);

    private static PrinterSpec Spec(int id, string name, string uri = "ipp://192.0.2.40/ipp/print", bool def = false, bool keep = true, string? driver = null) =>
        new() { Key = $"p{id}", Name = name, Uri = uri, Location = "Kontoret", MakeDefault = def, Keep = keep, Driver = driver };

    private static OwnedPrinter Own(PrinterSpec s, bool cups = true, string? uri = null) =>
        new() { Name = PrinterNames.Local(s.Name, cups), Uri = uri ?? PrinterUri.Normalize(s.Uri)!, Driver = s.Driver, Settings = PrinterPlan.Settings(s), AddedAt = Now.AddDays(-3) };

    private static LocalPrinter Here(string name, bool def = false, string? uri = null) =>
        new() { Name = name, IsDefault = def, Uri = uri, Virtual = PrinterNames.IsVirtual(name, uri) };

    // ------------------------------------------------------------ adresser og navne

    [Theory]
    [InlineData("IPP://192.0.2.40:631/ipp/print/", "ipp://192.0.2.40/ipp/print")]
    [InlineData("ipps://Printer.local/ipp/print", "ipps://printer.local/ipp/print")]
    [InlineData("ipp://192.0.2.40:8631/ipp/print", "ipp://192.0.2.40:8631/ipp/print")]
    [InlineData("socket://192.0.2.45:9100", "socket://192.0.2.45:9100")]
    [InlineData("lpd://192.0.2.45/queue", "lpd://192.0.2.45/queue")]
    [InlineData("http://192.0.2.40:631/ipp/print", "http://192.0.2.40:631/ipp/print")]
    public void Uri_is_normalized(string input, string expected) => Assert.Equal(expected, PrinterUri.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("ftp://192.0.2.40/x")]
    [InlineData("ipp://user:pass@192.0.2.40/ipp/print")]
    [InlineData("ipp://192.0.2.40/ipp/print'; rm -rf /")]
    [InlineData("ipp://192.0.2.40/$(reboot)")]
    [InlineData("ipp://192.0.2.40/a b")]
    [InlineData("192.0.2.40")]
    public void Bad_uris_are_refused(string input) => Assert.Null(PrinterUri.Normalize(input));

    [Theory]
    [InlineData("ipp://192.0.2.40/ipp/print", "http://192.0.2.40:631/ipp/print")]
    [InlineData("ipps://192.0.2.40:443/ipp/print", "https://192.0.2.40:443/ipp/print")]
    [InlineData("http://192.0.2.40:631/ipp/print", "http://192.0.2.40:631/ipp/print")]
    public void Ipp_request_goes_to_http(string uri, string http) => Assert.Equal(http, PrinterUri.ToHttp(uri));

    [Fact]
    public void Socket_has_no_ipp_status() => Assert.Null(PrinterUri.ToHttp("socket://192.0.2.45:9100"));

    [Theory]
    [InlineData("Farveprinter stue", true, "Farveprinter_stue")]
    [InlineData("Køkken/printer #2", true, "Koekken_printer_2")]
    [InlineData("Åben ø", true, "Aaben_oe")]
    [InlineData("A\\B,C", false, "A-B-C")]
    [InlineData("Kontorprinter", false, "Kontorprinter")]
    [InlineData("  ", true, "Printer")]
    public void Local_names_are_safe(string name, bool cups, string expected) => Assert.Equal(expected, PrinterNames.Local(name, cups));

    [Theory]
    [InlineData("Microsoft Print to PDF", "PORTPROMPT:", true)]
    [InlineData("OneNote (Desktop)", null, true)]
    [InlineData("Fax", "SHRFAX:", true)]
    [InlineData("PDF", "cups-pdf:/", true)]
    [InlineData("Kontorprinter", "IP_192.0.2.40", false)]
    public void Virtual_printers_are_recognized(string name, string? port, bool v) => Assert.Equal(v, PrinterNames.IsVirtual(name, port));

    // ------------------------------------------------------------ plan

    [Fact]
    public void Missing_printer_is_added()
    {
        var s = Spec(1, "Kontorprinter");
        var steps = PrinterPlan.Make([s], [], new Dictionary<string, OwnedPrinter>(), cups: true);
        Assert.Equal(PrinterOp.Add, Assert.Single(steps).Op);
    }

    [Fact]
    public void Removed_printer_is_readded_when_kept()
    {
        var s = Spec(1, "Kontorprinter");
        var steps = PrinterPlan.Make([s], [], new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s) }, cups: true);
        Assert.Equal(PrinterOp.Readd, Assert.Single(steps).Op);
    }

    [Fact]
    public void Removed_printer_is_left_alone_when_not_kept()
    {
        var s = Spec(1, "Kontorprinter", keep: false);
        var steps = PrinterPlan.Make([s], [], new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s) }, cups: true);
        Assert.Equal(PrinterOp.Forget, Assert.Single(steps).Op);
        var dropped = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s) };
        dropped["p1"].Dropped = true;
        Assert.Empty(PrinterPlan.Make([s], [], dropped, cups: true));
        // Hold på slået til igen: lægges på igen
        s.Keep = true;
        Assert.Equal(PrinterOp.Readd, Assert.Single(PrinterPlan.Make([s], [], dropped, cups: true)).Op);
    }

    [Fact]
    public void Printer_fleetdeck_did_not_add_is_never_touched()
    {
        var s = Spec(1, "Kontorprinter");
        var steps = PrinterPlan.Make([s], [Here("Kontorprinter", uri: "ipp://192.0.2.99/other")], new Dictionary<string, OwnedPrinter>(), cups: true);
        Assert.Empty(steps);
        // og en fremmed printer, der ikke ønskes, fjernes aldrig
        Assert.Empty(PrinterPlan.Make([], [Here("Brugerens egen")], new Dictionary<string, OwnedPrinter>(), cups: true));
    }

    [Fact]
    public void New_address_replaces_and_settings_reconfigure()
    {
        var s = Spec(1, "Kontorprinter");
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s, uri: "ipp://192.0.2.39/ipp/print") };
        var st = Assert.Single(PrinterPlan.Make([s], [Here("Kontorprinter")], owned, cups: true));
        Assert.Equal((PrinterOp.Replace, "Kontorprinter"), (st.Op, st.OldName));

        owned["p1"] = Own(s);
        s.Duplex = PrinterDuplex.None;
        Assert.Equal(PrinterOp.Configure, Assert.Single(PrinterPlan.Make([s], [Here("Kontorprinter")], owned, cups: true)).Op);
        s.Duplex = PrinterDuplex.Long;
        Assert.Empty(PrinterPlan.Make([s], [Here("Kontorprinter")], owned, cups: true));
    }

    [Fact]
    public void Renamed_printer_replaces_the_old_queue()
    {
        var old = Spec(1, "Printer gang");
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(old) };
        var renamed = Spec(1, "Kontorprinter");
        var st = Assert.Single(PrinterPlan.Make([renamed], [Here("Printer_gang")], owned, cups: true));
        Assert.Equal((PrinterOp.Replace, "Kontorprinter", "Printer_gang"), (st.Op, st.LocalName, st.OldName));
    }

    [Fact]
    public void Unwanted_owned_printer_is_removed_or_forgotten()
    {
        var s = Spec(1, "Kontorprinter");
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s) };
        Assert.Equal(PrinterOp.Remove, Assert.Single(PrinterPlan.Make([], [Here("Kontorprinter")], owned, cups: true)).Op);
        Assert.Equal(PrinterOp.Forget, Assert.Single(PrinterPlan.Make([], [], owned, cups: true)).Op);
    }

    [Fact]
    public void Default_only_when_machine_has_no_real_default()
    {
        var s = Spec(1, "Kontorprinter", def: true);
        var none = new Dictionary<string, OwnedPrinter>();
        Assert.Contains(PrinterPlan.Make([s], [], none, cups: false), x => x.Op == PrinterOp.SetDefault);
        // PDF som standard tæller ikke
        Assert.Contains(PrinterPlan.Make([s], [Here("Microsoft Print to PDF", def: true, uri: "PORTPROMPT:")], none, cups: false), x => x.Op == PrinterOp.SetDefault);
        // en rigtig standardprinter: lad den være
        Assert.DoesNotContain(PrinterPlan.Make([s], [Here("Brugerens egen", def: true)], none, cups: false), x => x.Op == PrinterOp.SetDefault);
        // ikke valgt: ingen standard
        Assert.DoesNotContain(PrinterPlan.Make([Spec(1, "Kontorprinter")], [], none, cups: false), x => x.Op == PrinterOp.SetDefault);
        // allerede lagt på af Fleetdeck, men ingen standard: sættes
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(s, cups: false) };
        Assert.Equal(PrinterOp.SetDefault, Assert.Single(PrinterPlan.Make([s], [Here("Kontorprinter")], owned, cups: false)).Op);
        // en fremmed printer med samme navn bliver ikke standard
        Assert.Empty(PrinterPlan.Make([s], [Here("Kontorprinter")], none, cups: false));
    }

    [Fact]
    public void Invalid_and_duplicate_specs_are_skipped()
    {
        var steps = PrinterPlan.Make([Spec(1, "A", uri: "ftp://x"), new PrinterSpec { Key = "x; rm", Name = "B", Uri = "ipp://192.0.2.1/ipp/print" },
            Spec(2, "C"), Spec(3, "C")], [], new Dictionary<string, OwnedPrinter>(), cups: true);
        Assert.Equal("p2", Assert.Single(steps).Key);
    }

    [Fact]
    public void Commit_records_only_what_succeeded()
    {
        var a = Spec(1, "Kontorprinter");
        var b = Spec(2, "Farveprinter stue", uri: "ipp://192.0.2.41/ipp/print");
        var owned = new Dictionary<string, OwnedPrinter> { ["p2"] = Own(b) };
        var steps = PrinterPlan.Make([a, b], [], owned, cups: true);   // p1 lægges på, p2 lægges på igen
        var ok = new Dictionary<string, PrinterResult> { ["p1"] = new(false, "printeren svarer ikke"), ["p2"] = new(true, null) };
        var next = PrinterPlan.Commit(owned, steps, ok, Now);
        Assert.False(next.ContainsKey("p1"));
        Assert.Equal(Now, next["p2"].ReaddedAt);
        Assert.Equal(Now.AddDays(-3), next["p2"].AddedAt);      // første gang huskes

        var removed = PrinterPlan.Commit(next, PrinterPlan.Make([], [Here("Farveprinter_stue")], next, cups: true), new Dictionary<string, PrinterResult> { ["p2"] = new(true, null) }, Now);
        Assert.Empty(removed);
    }

    [Fact]
    public void Report_shows_state_per_printer()
    {
        var a = Spec(1, "Kontorprinter");
        var b = Spec(2, "Farveprinter stue", keep: false);
        var c = Spec(3, "Labelprinter");
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(a), ["p2"] = Own(b) };
        owned["p1"].ReaddedAt = Now;
        owned["p2"].Dropped = true;
        var r = PrinterPlan.Report([a, b, c], [Here("Kontorprinter", def: true), Here("Labelprinter")], owned,
            new Dictionary<string, PrinterResult>(), cups: true);
        Assert.True(r[0].Ok); Assert.True(r[0].IsDefault); Assert.Equal(Now, r[0].ReaddedAt);
        Assert.True(r[1].Dropped); Assert.False(r[1].Ok); Assert.Null(r[1].Error);
        Assert.True(r[2].Foreign); Assert.True(r[2].Ok);

        var failed = PrinterPlan.Report([a], [], new Dictionary<string, OwnedPrinter>(), new Dictionary<string, PrinterResult> { ["p1"] = new(false, "lpadmin: Bad device-uri") }, cups: true);
        Assert.Equal("lpadmin: Bad device-uri", Assert.Single(failed).Error);
    }

    // ------------------------------------------------------------ kommandoer

    [Fact]
    public void PowerShell_quoting_doubles_all_single_quotes()
    {
        Assert.Equal("'Peters printer'", PrinterCommands.Ps("Peters printer"));
        Assert.Equal("'Peter''s'", PrinterCommands.Ps("Peter's"));
        Assert.Equal("'Peter\u2019\u2019s'", PrinterCommands.Ps("Peter\u2019s"));   // PowerShell regner også ’ som anførselstegn
        Assert.Equal("'ab'", PrinterCommands.Ps("a\r\nb"));
    }

    [Fact]
    public void Shell_quoting_is_safe()
    {
        Assert.Equal("'a'\\''b'", PrinterCommands.Sh("a'b"));
        Assert.Equal("'$(reboot)'", PrinterCommands.Sh("$(reboot)"));
    }

    [Fact]
    public void Windows_script_adds_without_driver_and_quotes_names()
    {
        var s = Spec(1, "Kontor'; Remove-Item C:\\ -Recurse; '");
        var steps = PrinterPlan.Make([s], [], new Dictionary<string, OwnedPrinter>(), cups: false);
        var ps = PrinterCommands.Windows(steps);
        Assert.Contains("Put 'Kontor''; Remove-Item C:- -Recurse; ''' 'ipp://192.0.2.40/ipp/print' '192.0.2.40' '' 'Kontoret'", ps);
        Assert.Contains("-IppURL $u", ps);
        Assert.Contains("Conf 'Kontor''; Remove-Item C:- -Recurse; ''' 'A4' 'TwoSidedLongEdge' $true", ps);
        Assert.Contains("R 'p1' $true ''", ps);
        Assert.DoesNotContain("Remove-Printer -Name", ps.Split("try {")[^1]);
    }

    [Fact]
    public void Windows_script_replaces_and_removes()
    {
        var s = Spec(1, "Kontorprinter", driver: "HP Universal Printing PCL 6");
        s.Color = false; s.Duplex = PrinterDuplex.None; s.Paper = "Letter";
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(Spec(1, "Kontorprinter"), cups: false), ["p2"] = Own(Spec(2, "Gammel"), cups: false) };
        var ps = PrinterCommands.Windows(PrinterPlan.Make([s], [Here("Kontorprinter"), Here("Gammel")], owned, cups: false));
        Assert.Contains("Remove-Printer -Name 'Kontorprinter'", ps);
        Assert.Contains("'HP Universal Printing PCL 6'", ps);
        Assert.Contains("Conf 'Kontorprinter' 'Letter' 'OneSided' $false", ps);
        Assert.Contains("Remove-Printer -Name 'Gammel'", ps);
        Assert.Contains("R 'p2' $true ''", ps);
    }

    [Fact]
    public void Windows_default_runs_as_user_script_and_respects_existing_default()
    {
        var st = new PrinterStep(PrinterOp.SetDefault, "p1", "Kontorprinter", Spec(1, "Kontorprinter", def: true));
        var ps = PrinterCommands.WindowsDefault(st);
        Assert.Contains("LegacyDefaultPrinterMode", ps);
        Assert.Contains("$_.Name -eq 'Kontorprinter'", ps);
        Assert.Contains("'p1:d'", ps);
        Assert.Contains("-notmatch 'PDF|XPS|OneNote|Fax'", ps);
        Assert.Contains("' skip'", ps);   // brugeren har en anden rigtig standardprinter: lad den være
    }

    [Fact]
    public void Cups_script_uses_everywhere_and_options()
    {
        var s = Spec(1, "Farveprinter stue");
        s.MakeDefault = true;
        var steps = PrinterPlan.Make([s], [], new Dictionary<string, OwnedPrinter>(), cups: true);
        var sh = PrinterCommands.Cups(steps, "apt-get install -y 'cups'");
        Assert.Contains("lpadmin -p 'Farveprinter_stue' -E -v 'ipp://192.0.2.40/ipp/print' -m everywhere -D 'Farveprinter stue' -L 'Kontoret' -o printer-is-shared=false", sh);
        Assert.Contains("-o 'media=iso_a4_210x297mm' -o 'sides=two-sided-long-edge' -o 'print-color-mode=color'", sh);
        Assert.Contains("lpadmin -d 'Farveprinter_stue'", sh);
        Assert.Contains("r 'p1:d' $?", sh);
        Assert.Contains("command -v lpadmin", sh);
        Assert.Contains("apt-get install -y 'cups'", sh);
    }

    [Fact]
    public void Cups_script_with_driver_and_removal()
    {
        Assert.Equal("-P '/usr/share/ppd/x.ppd'", PrinterCommands.CupsModel(Spec(1, "A", driver: "/usr/share/ppd/x.ppd")));
        Assert.Equal("-m 'drv:///sample.drv/generic.ppd'", PrinterCommands.CupsModel(Spec(1, "A", driver: "drv:///sample.drv/generic.ppd")));
        var owned = new Dictionary<string, OwnedPrinter> { ["p1"] = Own(Spec(1, "Kontorprinter")) };
        var sh = PrinterCommands.Cups(PrinterPlan.Make([], [Here("Kontorprinter")], owned, cups: true), "apt-get install -y 'cups'");
        Assert.Contains("lpadmin -x 'Kontorprinter'", sh);
        Assert.DoesNotContain("command -v lpadmin", sh);   // fjernelse lægger aldrig CUPS på
    }

    [Fact]
    public void Cups_script_runs_with_a_fake_lpadmin()
    {
        if (!File.Exists("/bin/sh") || OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("fd-lpadmin-");
        try
        {
            // falsk lpadmin: skriver argumenterne én pr. linje og fejler for køen "Fejler"
            var fake = Path.Combine(dir.FullName, "lpadmin");
            File.WriteAllText(fake, "#!/bin/sh\nfor a in \"$@\"; do echo \"$a\" >> \"$(dirname \"$0\")/args\"; done\nfor a in \"$@\"; do [ \"$a\" = Fejler ] && { echo 'lpadmin: Unable to connect' >&2; exit 1; }; done\nexit 0\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var ok = Spec(1, "Peter's $(touch /tmp/fd-ond) printer");
            ok.MakeDefault = true;
            var bad = Spec(2, "Fejler");
            var steps = PrinterPlan.Make([ok, bad], [], new Dictionary<string, OwnedPrinter>(), cups: true);
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["PATH"] = dir.FullName + ":/usr/bin:/bin";
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardInput.Write(PrinterCommands.Cups(steps));
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            var r = PrinterCommands.Results(output);
            Assert.True(r["p1"].Ok);
            Assert.True(r["p1:d"].Ok);
            Assert.Equal("lpadmin: Unable to connect", r["p2"].Error);
            var args = File.ReadAllLines(Path.Combine(dir.FullName, "args"));
            Assert.Contains("Peter_s_touch_tmp_fd-ond_printer", args);      // kønavnet
            Assert.Contains("Peter's $(touch /tmp/fd-ond) printer", args);   // beskrivelsen, ordret og uden at blive kørt
            Assert.Contains("media=iso_a4_210x297mm", args);
            Assert.False(File.Exists("/tmp/fd-ond"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Results_are_parsed_per_step()
    {
        var r = PrinterCommands.Results("noget\n::p p1 ok\r\n::p p2 err lpadmin: Unable to connect\n::p p2:d ok\n::p x; err nej\n::p p3 err \n::p p1:d skip\n");
        Assert.True(r["p1:d"] is { Ok: true, Skipped: true });
        Assert.True(r["p1"].Ok);
        Assert.Equal("lpadmin: Unable to connect", r["p2"].Error);
        Assert.True(r["p2:d"].Ok);
        Assert.Equal("ukendt fejl", r["p3"].Error);
        Assert.False(r.ContainsKey("x;"));
    }

    [Fact]
    public void Lpstat_is_parsed()
    {
        var l = PrinterCommands.ParseCups("""
            device for Kontorprinter: ipp://192.0.2.40/ipp/print
            device for PDF: cups-pdf:/
            device for Label: socket://192.0.2.42:9100
            ::default
            system default destination: Kontorprinter
            """);
        Assert.Equal(["Kontorprinter", "PDF", "Label"], l.Select(p => p.Name));
        Assert.True(l[0].IsDefault);
        Assert.True(l[1].Virtual);
        Assert.Equal("socket://192.0.2.42:9100", l[2].Uri);
        Assert.Empty(PrinterCommands.ParseCups("lpstat: No destinations added.\n::default\nno system default destination"));
    }

    [Fact]
    public void Windows_list_is_parsed_as_object_or_array()
    {
        var one = PrinterCommands.ParseWindows("""{"name":"Microsoft Print to PDF","port":"PORTPROMPT:","driver":"Microsoft Print To PDF","uri":null,"isDefault":true}""");
        Assert.True(Assert.Single(one).Virtual);
        var many = PrinterCommands.ParseWindows("""
            [{"name":"Kontorprinter","port":"IP_192.0.2.40","driver":"Microsoft IPP Class Driver","uri":"socket://192.0.2.40:9100","isDefault":false},
             {"name":"Fax","port":"SHRFAX:","driver":"Microsoft Shared Fax Driver","uri":null,"isDefault":false}]
            """);
        Assert.Equal(2, many.Count);
        Assert.Equal("socket://192.0.2.40:9100", many[0].Uri);
        Assert.False(many[0].Virtual);
        Assert.Empty(PrinterCommands.ParseWindows("fejl"));
    }

    // ------------------------------------------------------------ IPP

    private sealed class IppBuilder
    {
        private readonly MemoryStream _ms = new();
        public IppBuilder(int status = 0) { _ms.Write([2, 0, (byte)(status >> 8), (byte)status, 0, 0, 0, 1]); }
        public IppBuilder Group(byte g) { _ms.WriteByte(g); return this; }
        private IppBuilder Raw(byte tag, string name, byte[] v)
        {
            var n = Encoding.UTF8.GetBytes(name);
            _ms.WriteByte(tag); _ms.WriteByte((byte)(n.Length >> 8)); _ms.WriteByte((byte)n.Length); _ms.Write(n);
            _ms.WriteByte((byte)(v.Length >> 8)); _ms.WriteByte((byte)v.Length); _ms.Write(v);
            return this;
        }
        public IppBuilder Text(byte tag, string name, params string[] values)
        {
            for (var i = 0; i < values.Length; i++) Raw(tag, i == 0 ? name : "", Encoding.UTF8.GetBytes(values[i]));
            return this;
        }
        public IppBuilder Int(byte tag, string name, params int[] values)
        {
            for (var i = 0; i < values.Length; i++) Raw(tag, i == 0 ? name : "", [(byte)(values[i] >> 24), (byte)(values[i] >> 16), (byte)(values[i] >> 8), (byte)values[i]]);
            return this;
        }
        public IppBuilder Bool(string name, bool v) => Raw(0x22, name, [(byte)(v ? 1 : 0)]);
        public byte[] Done() { _ms.WriteByte(3); return _ms.ToArray(); }
    }

    private static byte[] SampleResponse(int black = 12, string reasons = "none") => new IppBuilder()
        .Group(1).Text(0x47, "attributes-charset", "utf-8").Text(0x48, "attributes-natural-language", "en")
        .Group(4)
        .Text(0x41, "printer-make-and-model", "Brother HL-L3270CDW series")
        .Text(0x41, "printer-location", "Kontoret")
        .Int(0x23, "printer-state", 3)
        .Text(0x44, "printer-state-reasons", reasons.Split(','))
        .Bool("color-supported", true)
        .Text(0x44, "sides-supported", "one-sided", "two-sided-long-edge", "two-sided-short-edge")
        .Text(0x49, "document-format-supported", "application/octet-stream", "image/urf", "image/pwg-raster")
        .Text(0x44, "media-supported", "iso_a4_210x297mm", "na_letter_8.5x11in")
        .Text(0x42, "marker-names", "Black Toner", "Cyan Toner", "Magenta Toner", "Yellow Toner")
        .Int(0x21, "marker-levels", black, 80, -3, 55)
        .Text(0x42, "marker-colors", "#000000", "#00FFFF", "#FF00FF", "#FFFF00")
        .Text(0x44, "marker-types", "toner", "toner", "toner", "toner")
        .Done();

    [Fact]
    public void Get_printer_attributes_request_is_encoded()
    {
        var b = Ipp.GetPrinterAttributes("ipp://192.0.2.40/ipp/print", 7);
        Assert.Equal(new byte[] { 2, 0, 0, 0x0B, 0, 0, 0, 7, 1 }, b[..9]);
        Assert.Equal(3, b[^1]);
        var s = Encoding.UTF8.GetString(b);
        Assert.Contains("printer-uri", s);
        Assert.Contains("ipp://192.0.2.40/ipp/print", s);
        Assert.Contains("marker-levels", s);
        // kan læses af vores egen fortolker (samme format)
        var r = Ipp.Parse(b);
        Assert.Equal("ipp://192.0.2.40/ipp/print", r.Operation["printer-uri"].Single());
        Assert.Equal(Ipp.Requested.Length, r.Operation["requested-attributes"].Count);
    }

    [Fact]
    public void Printer_attributes_are_read()
    {
        var r = Ipp.Parse(SampleResponse());
        Assert.True(r.Ok);
        var i = PrinterInfoReader.From(r);
        Assert.Equal("Brother HL-L3270CDW series", i.MakeModel);
        Assert.Equal("Kontoret", i.Location);
        Assert.True(i.Color); Assert.True(i.Duplex); Assert.True(i.Driverless);
        Assert.Equal("idle", i.State);
        Assert.Empty(i.Reasons);
        Assert.Equal(4, i.Markers.Count);
        Assert.Equal(12, i.Markers[0].Level);
        Assert.Null(i.Markers[2].Level);           // -3: "noget tilbage" = ukendt niveau
        Assert.Equal("sort", PrinterStatus.ColorName(i.Markers[0]));
        Assert.Equal("gul", PrinterStatus.ColorName(i.Markers[3]));
        Assert.Equal(("Lav på sort", "warn"), PrinterStatus.Describe(i));
        Assert.Equal(("Klar", "ok"), PrinterStatus.Describe(PrinterInfoReader.From(Ipp.Parse(SampleResponse(black: 60)))));
    }

    [Fact]
    public void Errors_are_described_in_danish()
    {
        var jam = PrinterInfoReader.From(Ipp.Parse(SampleResponse(black: 60, reasons: "media-jam-error,cover-open-report")));
        Assert.Equal(["media-jam", "cover-open"], jam.Reasons);
        Assert.Equal(("Papirstop, Låg åbent", "error"), PrinterStatus.Describe(jam));
        Assert.Equal(("Slukket", "off"), PrinterStatus.Describe(new PrinterInfo { State = "off", Error = "svarer ikke" }));
        Assert.Equal("off", PrinterStatus.Describe(null).Level);
    }

    [Fact]
    public void Broken_ipp_is_refused()
    {
        var ok = SampleResponse();
        Assert.Throws<FormatException>(() => Ipp.Parse(ok[..40]));
        Assert.Throws<FormatException>(() => Ipp.Parse([0x48, 0x54, 0x54, 0x50, 0x2F, 0x31, 0x2E, 0x31, 0x20]));   // "HTTP/1.1 "
        Assert.False(Ipp.Parse(new IppBuilder(0x0400).Group(1).Done()).Ok);
    }

    [Fact]
    public void Warnings_only_when_status_gets_worse()
    {
        Assert.Equal("Lav på sort", PrinterMonitor.Warn(("Klar", "ok"), ("Lav på sort", "warn")));
        Assert.Equal("Papirstop", PrinterMonitor.Warn(("Lav på sort", "warn"), ("Papirstop", "error")));
        Assert.Null(PrinterMonitor.Warn(("Lav på sort", "warn"), ("Lav på sort", "warn")));
        Assert.Null(PrinterMonitor.Warn(("Papirstop", "error"), ("Lav på sort", "warn")));
        Assert.Null(PrinterMonitor.Warn(("Klar", "ok"), ("Slukket", "off")));
    }

    // ------------------------------------------------------------ Bonjour

    private sealed class DnsBuilder
    {
        private readonly List<byte> _b = [];
        private readonly Dictionary<string, int> _names = [];
        private int _count;
        public DnsBuilder() { _b.AddRange(new byte[12]); _b[2] = 0x84; }

        private void Name(string name)
        {
            var labels = name.Split('.');
            for (var i = 0; i < labels.Length; i++)
            {
                var rest = string.Join('.', labels[i..]);
                if (_names.TryGetValue(rest, out var at)) { _b.Add((byte)(0xC0 | (at >> 8))); _b.Add((byte)at); return; }
                _names[rest] = _b.Count;
                var l = Encoding.UTF8.GetBytes(labels[i]);
                _b.Add((byte)l.Length); _b.AddRange(l);
            }
            _b.Add(0);
        }

        private DnsBuilder Rec(string name, ushort type, Action data)
        {
            Name(name);
            _b.AddRange([(byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0x11, 0x94]);
            var lenAt = _b.Count; _b.AddRange([0, 0]);
            var start = _b.Count;
            data();
            var len = _b.Count - start;
            _b[lenAt] = (byte)(len >> 8); _b[lenAt + 1] = (byte)len;
            _count++;
            return this;
        }

        public DnsBuilder Ptr(string svc, string inst) => Rec(svc, Mdns.Ptr, () => Name(inst));
        public DnsBuilder Srv(string inst, ushort port, string host) => Rec(inst, Mdns.Srv, () => { _b.AddRange([0, 0, 0, 0, (byte)(port >> 8), (byte)port]); Name(host); });
        public DnsBuilder Txt(string inst, params string[] kv) => Rec(inst, Mdns.Txt, () => { foreach (var s in kv) { var x = Encoding.UTF8.GetBytes(s); _b.Add((byte)x.Length); _b.AddRange(x); } });
        public DnsBuilder A(string host, string ip) => Rec(host, Mdns.A, () => _b.AddRange(IPAddress.Parse(ip).GetAddressBytes()));
        public byte[] Done() { _b[6] = (byte)(_count >> 8); _b[7] = (byte)_count; return [.. _b]; }
    }

    [Fact]
    public void Mdns_query_asks_for_all_printer_services()
    {
        var q = Mdns.Query(Mdns.PrinterServices);
        Assert.Equal(4, q[5]);
        Assert.Contains("_pdl-datastream", Encoding.ASCII.GetString(q));
    }

    [Fact]
    public void Bonjour_answers_become_printers()
    {
        var pkt = new DnsBuilder()
            .Ptr("_ipp._tcp.local", "Canon TS8350._ipp._tcp.local")
            .Ptr("_ipps._tcp.local", "Canon TS8350._ipps._tcp.local")
            .Srv("Canon TS8350._ipp._tcp.local", 631, "canon-ts8350.local")
            .Srv("Canon TS8350._ipps._tcp.local", 443, "canon-ts8350.local")
            .Txt("Canon TS8350._ipp._tcp.local", "txtvers=1", "rp=ipp/print", "ty=Canon TS8300 series", "URF=W8,SRGB24", "Color=T", "Duplex=T", "note=Stuen")
            .A("canon-ts8350.local", "192.0.2.44")
            .Ptr("_pdl-datastream._tcp.local", "EPSON ET-2850._pdl-datastream._tcp.local")
            .Srv("EPSON ET-2850._pdl-datastream._tcp.local", 9100, "epson.local")
            .A("epson.local", "192.0.2.45")
            .Done();
        var services = Mdns.Resolve(Mdns.Parse(pkt));
        Assert.Equal(3, services.Count);
        var found = PrinterFinds.FromMdns(services);
        Assert.Equal(2, found.Count);
        var canon = found.Single(f => f.Host == "192.0.2.44");
        Assert.Equal(("Canon TS8350", "ipp://192.0.2.44/ipp/print", "Canon TS8300 series"), (canon.Name, canon.Uri, canon.MakeModel));
        Assert.True(canon.Driverless); Assert.True(canon.Color); Assert.Equal("Stuen", canon.Location);
        var epson = found.Single(f => f.Host == "192.0.2.45");
        Assert.Equal("socket://192.0.2.45:9100", epson.Uri);
        Assert.False(epson.Driverless);
    }

    [Fact]
    public void Broken_dns_is_refused()
    {
        var pkt = new DnsBuilder().Ptr("_ipp._tcp.local", "X._ipp._tcp.local").Done();
        Assert.Throws<FormatException>(() => Mdns.Parse(pkt[..^3]));
        // navnehop i ring
        var loop = new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12 };
        Assert.Throws<FormatException>(() => Mdns.Parse(loop));
    }

    [Theory]
    [InlineData("192.0.2.0/24", 254)]
    [InlineData("192.0.2.17/30", 2)]
    [InlineData("192.0.2.9/32", 1)]
    [InlineData("172.16.0.0/22", 1022)]
    public void Scan_ranges_are_expanded(string cidr, int n) => Assert.Equal(n, PrinterFinds.Hosts(cidr)!.Count);

    [Theory]
    [InlineData("172.16.0.0/21")]
    [InlineData("192.0.2.0")]
    [InlineData("2001:db8::/120")]
    [InlineData("nej/24")]
    public void Bad_scan_ranges_are_refused(string cidr) => Assert.Null(PrinterFinds.Hosts(cidr));

    [Fact]
    public void Finds_are_merged_and_known_ones_recognized()
    {
        var bonjour = new FoundPrinter { Name = "Canon TS8350", Host = "192.0.2.44", Uri = "ipp://192.0.2.44/ipp/print", Via = "bonjour" };
        var scan = new FoundPrinter { Name = "192.0.2.44", Host = "192.0.2.44", Uri = "ipp://192.0.2.44/ipp/print", Via = "scan", MakeModel = "Canon TS8300" };
        var agent = PrinterFinds.FromAgent("ARBEJDS-PC", [
            new LocalPrinter { Name = "Brother", Uri = "socket://192.0.2.46:9100", Driver = "Brother HL-L2350DW" },
            new LocalPrinter { Name = "Microsoft Print to PDF", Uri = "PORTPROMPT:", Virtual = true },
            new LocalPrinter { Name = "Lokal", Uri = "ipp://localhost/printers/x" },
        ]).ToList();
        Assert.Equal("ARBEJDS-PC", Assert.Single(agent).Machine);
        var all = PrinterFinds.Merge([bonjour], [scan], agent);
        Assert.Equal(2, all.Count);
        var c = all.Single(f => f.Host == "192.0.2.44");
        Assert.Equal(("bonjour", "Canon TS8300"), (c.Via, c.MakeModel));
        Assert.True(PrinterFinds.Known(c, ["ipp://192.0.2.44/ipp/print"]));
        Assert.False(PrinterFinds.Known(c, ["ipp://192.0.2.40/ipp/print"]));
    }

    // ------------------------------------------------------------ find selv: dubletter, subnet, agenternes fund

    [Fact]
    public void Same_printer_from_many_sources_becomes_one()
    {
        var wsd = PrinterFinds.FromAgent("STUE-PC", [], [new SeenPrinter { Name = "192.168.77.44", Host = "192.168.77.44", Uri = "ipp://192.168.77.44/ipp/print", Via = "wsd" }]);
        var bonjourPc = PrinterFinds.FromAgent("ARBEJDS-PC", [], [new SeenPrinter { Name = "Canon TS8350", Host = "192.168.77.44", Uri = "IPP://192.168.77.44:631/ipp/print/", Via = "bonjour", MakeModel = "Canon TS8350 series", Driverless = true }]);
        var installed = PrinterFinds.FromAgent("ARBEJDS-PC", [new LocalPrinter { Name = "Canon (kontor)", Uri = "socket://192.168.77.44:9100", Driver = "Canon Generic Plus" }]);
        var scan = new FoundPrinter { Name = "192.168.77.44", Host = "192.168.77.44", Uri = "ipp://192.168.77.44/ipp/print", Via = "scan", Sources = ["scan"] };
        var other = new FoundPrinter { Name = "Brother", Host = "192.168.77.46", Uri = "socket://192.168.77.46:9100", Via = "scan", Sources = ["scan"] };

        // agenterne melder kun private adresser (her: et hjemmenet)
        var all = PrinterFinds.Merge(wsd, installed, [scan, other], bonjourPc);
        Assert.Equal(2, all.Count);
        var c = all.Single(f => f.Host == "192.168.77.44");
        Assert.Equal("Canon TS8350", c.Name);                          // et rigtigt navn i stedet for IP'en
        Assert.Equal("ipp://192.168.77.44/ipp/print", c.Uri);            // Bonjour fra en pc slår scanning, installeret og WSD
        Assert.Equal(["bonjour", "scan", "wsd", "installed"], c.Sources);
        Assert.Equal(["ARBEJDS-PC", "STUE-PC"], c.Machines);
        Assert.Equal(("Canon TS8350 series", true), (c.MakeModel, c.Driverless));
        // serverens egen Bonjour vinder over alt
        var server = PrinterFinds.FromMdns([new Mdns.Service("Canon TS8350._ipp._tcp.local", "_ipp._tcp.local", "canon.local", 631,
            new Dictionary<string, string> { ["rp"] = "ipp/print", ["URF"] = "W8" }, [IPAddress.Parse("192.168.77.44")])]);
        Assert.Equal("bonjour", PrinterFinds.Merge(bonjourPc, server).Single().Via);
    }

    [Fact]
    public void Own_subnet_is_private_24_only()
    {
        var nets = PrinterFinds.OwnSubnets([
            ("eth0", IPAddress.Parse("192.168.77.20")),
            ("eth1", IPAddress.Parse("10.20.30.40")),
            ("docker0", IPAddress.Parse("172.17.0.1")),
            ("br-1a2b", IPAddress.Parse("172.18.0.1")),
            ("tailscale0", IPAddress.Parse("100.101.102.103")),
            ("wan", IPAddress.Parse("192.0.2.17")),      // offentlig/dokumentation: aldrig
            ("eth0", IPAddress.Parse("192.168.77.21")),  // samme net én gang
        ]);
        Assert.Equal(["192.168.77.0/24", "10.20.30.0/24"], nets);
        Assert.Equal(254, PrinterFinds.Hosts(nets[1])!.Count);
        Assert.True(PrinterFinds.IsPrivateRange("172.16.4.0/22"));
        Assert.False(PrinterFinds.IsPrivateRange("192.0.2.0/24"));
        Assert.False(PrinterFinds.IsPrivateRange("172.32.0.0/24"));
    }

    [Fact]
    public void Agent_report_keeps_only_private_network_printers()
    {
        var finds = PrinterFinds.FromAgent("ARBEJDS-PC", [
                new LocalPrinter { Name = "Kontorprinter", Uri = "ipp://192.0.2.40/ipp/print", Fleetdeck = true },   // lagt på af Fleetdeck
                new LocalPrinter { Name = "Brother", Uri = "socket://192.168.77.46:9100", Driver = "Brother HL-L2350DW" },
            ],
            [
                new SeenPrinter { Name = "HP", Host = "192.168.77.45", Uri = "ipp://192.168.77.45/ipp/print", Via = "bonjour" },
                new SeenPrinter { Name = "Ude", Host = "198.51.100.9", Uri = "ipp://198.51.100.9/ipp/print", Via = "wsd" },
                new SeenPrinter { Name = "Lokal", Host = "localhost", Uri = "ipp://localhost/ipp/print", Via = "bonjour" },
                new SeenPrinter { Name = "Ond", Host = "192.168.77.47", Uri = "ipp://192.168.77.47/x';rm -rf /", Via = "bonjour" },
            ], new DateTime(2026, 11, 19, 8, 12, 0, DateTimeKind.Utc)).ToList();
        Assert.Equal(["192.168.77.46", "192.168.77.45"], finds.Select(f => f.Host));
        Assert.All(finds, f => Assert.Equal(("agent", "ARBEJDS-PC"), (f.Via, f.Machine)));
        Assert.Equal(["installed"], finds[0].Sources);
        Assert.Equal(["bonjour"], finds[1].Sources);
    }

    [Fact]
    public void Wsd_probe_and_answers()
    {
        var probe = Encoding.UTF8.GetString(Wsd.Probe(Guid.Empty));
        Assert.Contains("wprt:PrintDeviceType", probe);
        Assert.Contains("urn:uuid:00000000-0000-0000-0000-000000000000", probe);
        var answer = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:wsd="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <soap:Body><wsd:ProbeMatches>
                <wsd:ProbeMatch><wsd:Types>wsdp:Device wprt:PrintDeviceType</wsd:Types><wsd:XAddrs>http://192.0.2.44:3911/ http://[fe80::1]:3911/</wsd:XAddrs></wsd:ProbeMatch>
                <wsd:ProbeMatch><wsd:Types>wsdp:Device scan:ScanDeviceType</wsd:Types><wsd:XAddrs>http://192.0.2.50/</wsd:XAddrs></wsd:ProbeMatch>
              </wsd:ProbeMatches></soap:Body>
            </soap:Envelope>
            """);
        var hosts = Wsd.Parse(answer);
        Assert.Equal("192.0.2.44", Assert.Single(hosts).ToString());
        Assert.Empty(Wsd.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a 'b'>]><x>&a;</x>")));   // DTD afvises
        Assert.Empty(Wsd.Parse(Encoding.UTF8.GetBytes("ikke xml")));
        var seen = PrinterSeen.Combine(PrinterSeen.FromWsd(hosts), [new SeenPrinter { Name = "Canon", Host = "192.0.2.44", Uri = "ipp://192.0.2.44/ipp/print", Via = "bonjour" }]);
        Assert.Equal("bonjour", Assert.Single(seen).Via);
    }

    [Fact]
    public void Avahi_browse_output_becomes_printers()
    {
        const string output = """
            +;eth0;IPv4;HP\032LaserJet\032M110w;_ipp._tcp;local
            =;eth0;IPv4;HP\032LaserJet\032M110w;_ipp._tcp;local;HP1234.local;192.0.2.45;631;"txtvers=1" "ty=HP LaserJet M110w" "rp=ipp/print" "pdl=application/pdf,image/urf" "URF=W8"
            =;eth0;IPv6;HP\032LaserJet\032M110w;_ipp._tcp;local;HP1234.local;fe80::1;631;"ty=HP LaserJet M110w"
            =;eth0;IPv4;Brother\032HL;_pdl-datastream._tcp;local;brother.local;192.0.2.46;9100;"ty=Brother HL-L2350DW"
            """;
        Assert.Equal("Kontor æ.1", AvahiBrowse.Unescape(@"Kontor\032\195\166\.1"));
        var services = AvahiBrowse.Parse(output);
        Assert.Equal(2, services.Count);
        Assert.Equal("HP LaserJet M110w", services[0].Label);
        var seen = PrinterSeen.FromMdns(services);
        var hp = seen.Single(p => p.Host == "192.0.2.45");
        Assert.Equal(("ipp://192.0.2.45/ipp/print", "HP LaserJet M110w", true), (hp.Uri, hp.MakeModel, hp.Driverless));
        Assert.Equal(("socket://192.0.2.46:9100", false), (seen.Single(p => p.Host == "192.0.2.46").Uri, seen.Single(p => p.Host == "192.0.2.46").Driverless));
    }

    [Fact]
    public void Automatic_search_runs_when_due()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(PrinterFinds.Due(null, 6, now));
        Assert.False(PrinterFinds.Due(now.AddHours(-5), 6, now));
        Assert.True(PrinterFinds.Due(now.AddHours(-6), 6, now));
        Assert.True(PrinterFinds.Due(now.AddHours(-2), 0, now));   // mindst 1 time
    }

    [Fact]
    public async Task Found_printer_is_set_up_on_chosen_machines()
    {
        using var conn = new SqliteConnection("Data Source=:memory:"); conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();
        var prof = new Profile { Name = "Hjemmekontor" };
        db.Profiles.Add(prof);
        var health = new HealthReport { NetPrinters = [new SeenPrinter { Name = "Canon TS8350", Host = "192.168.77.44", Uri = "ipp://192.168.77.44/ipp/print", Via = "bonjour", Driverless = true }], NetPrintersAt = DateTime.UtcNow };
        var pc = new Machine { Name = "ARBEJDS-PC", Platform = Platforms.Windows, HealthJson = Fleetdeck.Server.Services.Json.Ser(health) };
        var mac = new Machine { Name = "minmac", Platform = Platforms.Mac };
        var ipad = new Machine { Name = "iPad", Platform = "ios" };
        var unraid = new Machine { Name = "minserver", Platform = Platforms.Linux, HealthJson = """{"unraid":{}}""" };
        db.Machines.AddRange(pc, mac, ipad, unraid);
        await db.SaveChangesAsync();

        // agenten har meldt printeren: den står under "Fundne printere"
        var found = Assert.Single(PrinterFinds.Merge(await PrinterDiscovery.AgentFinds(db)));
        Assert.Equal(("192.168.77.44", "ARBEJDS-PC"), (found.Host, found.Machine));

        // "Opsæt på…": pc, Mac og profilen; iPad og Unraid springes over, og to gange giver ingen dubletter
        var p = new Printer { Name = found.Name, Uri = found.Uri, Source = "agent" };
        db.Printers.Add(p);
        await db.SaveChangesAsync();
        Assert.Equal(3, await PrinterService.Place(db, p.Id, [prof.Id], [pc.Id, mac.Id, ipad.Id, unraid.Id, 9999], makeDefault: true));
        Assert.Equal(0, await PrinterService.Place(db, p.Id, [prof.Id], [pc.Id], makeDefault: true));
        Assert.Equal($"p{p.Id}", Assert.Single((await PrinterService.Specs(db, pc))!).Key);
        Assert.True(Assert.Single((await PrinterService.Specs(db, mac))!).MakeDefault);
        Assert.Null(await PrinterService.Specs(db, ipad));
        // nu er den tilføjet og står ikke længere blandt de fundne
        Assert.True(PrinterFinds.Known(found, await db.Printers.Select(x => x.Uri).ToListAsync()));
    }

    // ------------------------------------------------------------ tildelinger og check-in

    [Fact]
    public void Machine_assignment_wins_over_profile()
    {
        var printers = new Dictionary<int, Printer>
        {
            [1] = new() { Id = 1, Name = "Kontorprinter", Uri = "ipp://192.0.2.40/ipp/print" },
            [2] = new() { Id = 2, Name = "Labelprinter", Uri = "socket://192.0.2.42:9100", NeedsDriver = true, Driver = " Brother QL-820NWB " },
        };
        var own = new[] { new PrinterAssignment { Id = 10, PrinterId = 1, MachineId = 5, MakeDefault = true } };
        var prof = new[] { new PrinterAssignment { Id = 11, PrinterId = 1, ProfileId = 3 }, new PrinterAssignment { Id = 12, PrinterId = 2, ProfileId = 3, Keep = false }, new PrinterAssignment { Id = 13, PrinterId = 99, ProfileId = 3 } };
        var r = PrinterService.Resolve(own, prof, printers);
        Assert.Equal(2, r.Count);
        Assert.Equal((10, false), (r[0].Assignment.Id, r[0].FromProfile));
        var spec = PrinterService.Spec(r[0]);
        Assert.Equal(("p1", true, true, null), (spec.Key, spec.MakeDefault, spec.Keep, spec.Driver));
        var label = PrinterService.Spec(r[1]);
        Assert.Equal(("Brother QL-820NWB", false), (label.Driver, label.Keep));
    }

    [Fact]
    public async Task Printers_for_a_machine_come_from_the_database()
    {
        using var conn = new SqliteConnection("Data Source=:memory:"); conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();
        var prof = new Profile { Name = "Hjemmekontor" };
        db.Profiles.Add(prof);
        await db.SaveChangesAsync();
        var m = new Machine { Name = "ARBEJDS-PC", Platform = Platforms.Windows, ProfileId = prof.Id };
        var unraid = new Machine { Name = "minserver", Platform = Platforms.Linux, HealthJson = """{"unraid":{}}""" };
        db.Machines.AddRange(m, unraid);
        var p = new Printer { Name = "Kontorprinter", Uri = "ipp://192.0.2.40/ipp/print" };
        db.Printers.Add(p);
        await db.SaveChangesAsync();
        db.PrinterAssignments.Add(new PrinterAssignment { PrinterId = p.Id, ProfileId = prof.Id, MakeDefault = true });
        db.PrinterAssignments.Add(new PrinterAssignment { PrinterId = p.Id, MachineId = unraid.Id });
        await db.SaveChangesAsync();

        var specs = await PrinterService.Specs(db, m);
        Assert.Equal($"p{p.Id}", Assert.Single(specs!).Key);
        Assert.Null(await PrinterService.Specs(db, unraid));   // Unraid: rør ingenting

        // slettes printeren, forsvinder tildelingerne også (agenten fjerner den så igen)
        db.Printers.Remove(p);
        await db.SaveChangesAsync();
        Assert.Empty(await db.PrinterAssignments.ToListAsync());
        Assert.Empty((await PrinterService.Specs(db, m))!);
    }

    [Fact]
    public void Signed_checkin_carries_printers_and_rejected_ones_touch_nothing()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = k.ExportSubjectPublicKeyInfoPem();
        const string nonce = "n-0123456789abcdef";
        var signed = CommandSigning.Sign(k, pem, new SignedCommands
        {
            MachineId = 7, Nonce = nonce, Issued = Now, Expires = Now + CommandSigning.Lifetime, Printers = [Spec(1, "Kontorprinter")],
        });
        var store = new TrustStore { JobKey = pem };
        var r = new CheckinResponse { Signed = signed, Printers = [Spec(9, "Ond", uri: "ipp://192.0.2.66/x")] };
        Assert.True(CommandGate.Apply(r, store, 7, nonce, Now).Trusted);
        Assert.Equal("p1", Assert.Single(r.Printers!).Key);

        var bad = new CheckinResponse { Signed = null, Printers = [Spec(9, "Ond")] };
        Assert.False(CommandGate.Apply(bad, store, 7, nonce, Now).Trusted);
        Assert.Null(bad.Printers);
    }

    private sealed class TrustStore : ITrustStore
    {
        public string? JobKey { get; set; }
        public string? TlsPin { get; set; }
    }

    [Fact]
    public void Machine_rows_show_fleetdeck_and_own_printers()
    {
        var p = new Printer { Id = 1, Name = "Farveprinter stue", Uri = "ipp://192.0.2.41/ipp/print" };
        var want = new List<MachinePrinter> { new(p, new PrinterAssignment { Id = 4, PrinterId = 1, ProfileId = 3 }, true) };
        var h = new HealthReport
        {
            Printers = [new PrinterState { Key = "p1", Name = "Farveprinter stue", Ok = true, ReaddedAt = Now }],
            LocalPrinters = [new LocalPrinter { Name = "Farveprinter stue", Fleetdeck = true }, new LocalPrinter { Name = "PDF (Microsoft Print to PDF)", Virtual = true }],
        };
        var json = JsonSerializer.Serialize(PrinterService.MachineRows(want, h, cups: false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].GetProperty("fleetdeck").GetBoolean());
        Assert.Equal(Now, rows[0].GetProperty("state").GetProperty("readdedAt").GetDateTime());
        Assert.Equal("PDF (Microsoft Print to PDF)", rows[1].GetProperty("name").GetString());
        Assert.False(rows[1].GetProperty("fleetdeck").GetBoolean());
    }

    [Theory]
    [InlineData("Kontorprinter", "ipp://192.0.2.40/ipp/print", "A4", "long", null)]
    [InlineData("", "ipp://192.0.2.40/ipp/print", "A4", "long", "Skriv et navn")]
    [InlineData("X", "printer", "A4", "long", "Skriv adressen")]
    [InlineData("X", "ipp://192.0.2.40/ipp/print", "B5", "long", "Ukendt papirstørrelse")]
    [InlineData("X", "ipp://192.0.2.40/ipp/print", "A4", "begge", "Vælg tosidet")]
    public void Input_is_validated(string name, string uri, string paper, string duplex, string? error)
    {
        var e = PrinterService.Validate(name, uri, paper, duplex);
        if (error is null) Assert.Null(e); else Assert.StartsWith(error, e);
    }

    // ------------------------------------------------------------ Home Assistant

    [Fact]
    public void Home_assistant_sensors_per_printer()
    {
        var p = new Printer { Id = 3, Name = "Kontorprinter", Uri = "ipp://192.0.2.40/ipp/print", MakeModel = "Brother HL-L3270CDW", Location = "Kontoret", LastPolled = Now };
        var info = PrinterInfoReader.From(Ipp.Parse(SampleResponse()));
        var conf = PrinterHa.Configs("homeassistant", "fleetdeck", p, info);
        Assert.Equal(PrinterHa.ConfigTopics("homeassistant", "fleetdeck", 3), conf.Select(c => c.Topic));
        Assert.Equal(4, conf.Count(c => c.Topic.Contains("/m") && c.Payload.Length > 0));
        Assert.Equal(PrinterHa.MaxMarkers - 4, conf.Count(c => c.Payload.Length == 0));   // patroner, der ikke findes, tømmes
        using var status = JsonDocument.Parse(conf[0].Payload);
        Assert.Equal("fleetdeck/printer/3/state", status.RootElement.GetProperty("state_topic").GetString());
        Assert.Equal("Kontoret", status.RootElement.GetProperty("device").GetProperty("suggested_area").GetString());
        using var m0 = JsonDocument.Parse(conf.First(c => c.Topic.EndsWith("/m0/config")).Payload);
        Assert.Equal(("Sort", "%"), (m0.RootElement.GetProperty("name").GetString(), m0.RootElement.GetProperty("unit_of_measurement").GetString()));

        using var st = JsonDocument.Parse(PrinterHa.State(p, info));
        Assert.Equal(("Lav på sort", "ON", "OFF", 12), (st.RootElement.GetProperty("status").GetString(), st.RootElement.GetProperty("low").GetString(),
            st.RootElement.GetProperty("problem").GetString(), st.RootElement.GetProperty("m0").GetInt32()));
    }
}
