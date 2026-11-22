using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Bios;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Et falsk /sys-træ i en midlertidig mappe. Skrivninger optages, og kernens svar efterlignes (EINVAL, EACCES, pending_reboot).</summary>
internal sealed class FakeSysfs : FirmwareFs, IDisposable
{
    public readonly List<(string Path, string Value)> Writes = [];
    /// <summary>Indstillinger, kernen afviser med EINVAL.</summary>
    public readonly HashSet<string> RejectNames = [];
    public readonly HashSet<string> RejectAfterFirstWrite = [];
    /// <summary>Indstillinger, hvis current_value giver EINVAL ved læsning (tom værdi i BIOS'en).</summary>
    public readonly HashSet<string> Unreadable = [];

    public override string? Read(string rel) =>
        rel.Split('/') is [.., var n, "current_value"] && Unreadable.Contains(n) ? null : base.Read(rel);
    private readonly HashSet<string> written = [];
    /// <summary>Den rigtige adgangskode (null = ingen). Forkert eller manglende kode giver EACCES ved skrivning af en værdi.</summary>
    public string? AdminPassword;
    private string currentPassword = "";
    /// <summary>Den kode, kernen stadig holder (tom = ryddet).</summary>
    public string KernelPassword => currentPassword;
    public bool FailPasswordClear { get; set; }

    public FakeSysfs(string driver = BiosSettingsDrivers.ThinkLmi) : base(Directory.CreateTempSubdirectory("fd-sysfs-").FullName)
    {
        Driver = driver;
        Directory.CreateDirectory(Full($"{Dir}/attributes"));
    }

    public string Driver { get; }
    public string Dir => $"{FirmwareAttributes.Base}/{Driver}";

    public FakeSysfs Attr(string name, string value, string? possible = null, string? type = null, string? display = null, long? min = null, long? max = null)
    {
        var d = Full($"{Dir}/attributes/{name}");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "current_value"), value + "\n");
        if (possible is not null) File.WriteAllText(Path.Combine(d, "possible_values"), possible + "\n");
        if (type is not null) File.WriteAllText(Path.Combine(d, "type"), type + "\n");
        if (display is not null) File.WriteAllText(Path.Combine(d, "display_name"), display + "\n");
        if (min is not null) File.WriteAllText(Path.Combine(d, "min_value"), min + "\n");
        if (max is not null) File.WriteAllText(Path.Combine(d, "max_value"), max + "\n");
        return this;
    }

    public FakeSysfs File_(string rel, string value)
    {
        var p = Full($"{Dir}/{rel}");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, value + "\n");
        return this;
    }

    public FakeSysfs Admin(string password)
    {
        AdminPassword = password;
        File_("authentication/Admin/is_enabled", "1");
        File_("authentication/Admin/current_password", "");
        return this;
    }

    public string? Value(string name) => Read($"{Dir}/attributes/{name}/current_value");

    public override void Write(string rel, string value)
    {
        // sysfs: en tom skrivning sender ingen write(), så kernen ser den aldrig
        if (value.Length == 0) return;
        Writes.Add((rel, value));
        var name = rel.Split('/') is [.., var n, "current_value"] ? n : null;
        if (rel.EndsWith("authentication/Admin/current_password", StringComparison.Ordinal))
        {
            // som think-lmi: længden tjekkes (0 er tilladt), derefter bliver linjeskift til 0-tegn
            var min = int.TryParse(Read($"{Dir}/authentication/Admin/min_password_length"), out var mn) ? mn : 1;
            if (FailPasswordClear && value.Trim('\n').Length == 0) throw new IOException("Invalid argument", 22);
            if (value.Length < min) throw new IOException("Invalid argument", 22);
            var nl = value.IndexOf('\n');
            currentPassword = nl < 0 ? value : value[..nl];
            return;
        }
        if (rel.EndsWith("attributes/save_settings", StringComparison.Ordinal))
        {
            if (value == "save") base.Write($"{Dir}/attributes/pending_reboot", "1");
            else base.Write(rel, value);
            return;
        }
        if (name is not null)
        {
            if (AdminPassword is not null && currentPassword != AdminPassword) throw new UnauthorizedAccessException("EACCES");
            if (RejectNames.Contains(name)) throw new IOException("Invalid argument", 22);
            if (RejectAfterFirstWrite.Contains(name) && !written.Add(name)) throw new IOException("Invalid argument", 22);
            // uden save_settings (single) gemmer kernen med det samme og melder genstart
            if (!FileExists($"{Dir}/attributes/save_settings")) base.Write($"{Dir}/attributes/pending_reboot", "1");
        }
        base.Write(rel, value);
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

/// <summary>Linux: firmware-attributes (think-lmi, dell-wmi-sysman, hp-bioscfg) mod et falsk sysfs-træ.</summary>
public class FirmwareAttributesTests
{
    private static FakeSysfs ThinkLmi() => new FakeSysfs()
        .Attr("WakeOnLAN", "Automatic", "Disabled;Automatic;Primary", "enumeration")
        .Attr("AfterPowerLoss", "Power Off;[Optional:ShowHidden]", "Power On;Power Off;Last State")
        .Attr("SecureBoot", "Enabled", "Disabled;Enabled")
        .Attr("PrimaryBootSequence", "NVMe0:USBHDD:PXEBOOT", "NVMe0;USBHDD;USBCD;PXEBOOT")
        .Attr("AlarmDate", "01/01/2026")
        .File_("attributes/save_settings", "single")
        .File_("attributes/pending_reboot", "0")
        .File_("attributes/debug_cmd", "")
        .File_("authentication/Admin/is_enabled", "0");

    private static BiosSettingChange Ch(string name, string value, string old) => new() { Name = name, Value = value, ExpectOld = old };

    [Fact]
    public void Reads_all_settings_and_skips_control_files()
    {
        using var fs = ThinkLmi();
        var fa = FirmwareAttributes.Detect(fs)!;
        Assert.Equal(BiosSettingsDrivers.ThinkLmi, fa.Driver);
        var r = fa.Read();
        Assert.Equal(5, r.Items.Count);
        Assert.DoesNotContain(r.Items, i => i.Name is "save_settings" or "pending_reboot" or "debug_cmd");
        var apl = r.Items.Single(i => i.Name == "AfterPowerLoss");
        Assert.Equal("Power Off", apl.Value);                       // ";[Optional:...]" fjernet
        Assert.Equal(["Power On", "Power Off", "Last State"], apl.Possible);
        Assert.Equal("enumeration", apl.Type);                       // ingen type-fil: udledt af possible_values
        Assert.Equal("string", r.Items.Single(i => i.Name == "AlarmDate").Type);
        Assert.False(r.AdminPassword);
        Assert.Equal("single", r.SaveMode);
        Assert.False(r.PendingReboot);
        Assert.True(r.Writable);
        var info = fa.Info();
        Assert.Equal(5, info.Count);
        Assert.Equal(BiosSettingsDrivers.ThinkLmi, info.Driver);
    }

    [Fact]
    public void No_firmware_attributes_gives_null()
    {
        var root = Directory.CreateTempSubdirectory("fd-sysfs-empty-").FullName;
        try { Assert.Null(FirmwareAttributes.Detect(new FirmwareFs(root))); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dell_and_hp_are_read_only_through_the_same_code()
    {
        using var dell = new FakeSysfs(BiosSettingsDrivers.DellSysman)
            .Attr("FanSpeed", "Auto", "Auto;High;Low", "enumeration", "Fan Speed")
            .Attr("AutoOnHr", "7", type: "integer", min: 0, max: 23)
            .File_("authentication/Admin/is_enabled", "1");
        var fa = FirmwareAttributes.Detect(dell)!;
        Assert.Equal(BiosSettingsDrivers.DellSysman, fa.Driver);
        Assert.False(fa.CanWrite);
        var r = fa.Read();
        Assert.True(r.AdminPassword);
        Assert.Equal("Fan Speed", r.Items.Single(i => i.Name == "FanSpeed").Display);
        var hr = r.Items.Single(i => i.Name == "AutoOnHr");
        Assert.Equal((0L, 23L), (hr.Min!.Value, hr.Max!.Value));
        var res = fa.Apply([Ch("FanSpeed", "High", "Auto")], "pw");
        Assert.False(res.Ok);
        Assert.Empty(dell.Writes);

        using var hp = new FakeSysfs(BiosSettingsDrivers.HpBioscfg)
            .Attr("Wake On LAN", "Boot to Hard Drive", "Disable;Boot to Network;Boot to Hard Drive", "enumeration")
            .Attr("Setup Password", "", type: "password")
            .File_("authentication/Setup Password/is_enabled", "1");
        var h = FirmwareAttributes.Detect(hp)!;
        var hr2 = h.Read();
        Assert.Single(hr2.Items);                                    // passwordfelter læses aldrig
        Assert.True(hr2.AdminPassword);
        Assert.False(h.CanWrite);
    }

    [Fact]
    public void Apply_writes_bulk_then_values_then_one_save()
    {
        using var fs = ThinkLmi();
        var res = FirmwareAttributes.Detect(fs)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Power Off")], null);
        Assert.True(res.Ok, res.Message);
        Assert.True(res.Saved);
        Assert.True(res.PendingReboot);
        Assert.Contains("næste genstart", res.Message);
        var seq = fs.Writes.Select(w => (w.Path[(fs.Dir.Length + 1)..], w.Value)).ToList();
        Assert.Equal([("attributes/save_settings", "bulk"), ("attributes/WakeOnLAN/current_value", "Disabled"),
            ("attributes/AfterPowerLoss/current_value", "Power On"), ("attributes/save_settings", "save"), ("attributes/save_settings", "single")], seq);
        Assert.All(res.Lines, l => Assert.Equal(BiosApplyStatus.Written, l.Status));
    }

    [Fact]
    public void Apply_stops_when_changes_already_wait_for_a_reboot()
    {
        using var fs = ThinkLmi().File_("attributes/pending_reboot", "1");
        var res = FirmwareAttributes.Detect(fs)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic")], null);
        Assert.False(res.Ok);
        Assert.Equal("Der venter allerede BIOS-ændringer. Genstart maskinen først.", res.Message);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Apply_writes_nothing_when_a_value_changed_or_is_invalid()
    {
        using var fs = ThinkLmi();
        var fa = FirmwareAttributes.Detect(fs)!;
        var res = fa.Apply([Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Last State")], null);
        Assert.False(res.Ok);
        Assert.Equal(BiosApplyStatus.Changed, res.Lines[1].Status);
        Assert.Empty(fs.Writes);

        res = fa.Apply([Ch("WakeOnLAN", "Sometimes", "Automatic")], null);
        Assert.Equal(BiosApplyStatus.Invalid, res.Lines[0].Status);
        res = fa.Apply([Ch("NoSuchThing", "On", "Off")], null);
        Assert.Equal(BiosApplyStatus.Missing, res.Lines[0].Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void Apply_stops_at_first_rejection_without_saving()
    {
        using var fs = ThinkLmi();
        fs.RejectNames.Add("AfterPowerLoss");
        var res = FirmwareAttributes.Detect(fs)!.Apply(
            [Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Power Off"), Ch("SecureBoot", "Disabled", "Enabled")], null);
        Assert.False(res.Ok);
        Assert.False(res.Saved);
        Assert.Contains("uden at gemme", res.Message);
        Assert.Equal([BiosApplyStatus.Written, BiosApplyStatus.Rejected, BiosApplyStatus.NotWritten], res.Lines.Select(l => l.Status));
        Assert.Equal("Skrevet og trukket tilbage. Intet er gemt.", res.Lines[0].Detail);
        Assert.DoesNotContain(fs.Writes, w => w.Value == "save");
        Assert.DoesNotContain(fs.Writes, w => w.Path.Contains("SecureBoot"));
        // det klargjorte WakeOnLAN er skrevet tilbage, og driveren er i single-tilstand igen
        Assert.Equal("Automatic", fs.Value("WakeOnLAN"));
        Assert.Equal(("attributes/save_settings", "single"), (fs.Writes[^1].Path[(fs.Dir.Length + 1)..], fs.Writes[^1].Value));
        Assert.NotEqual(true, res.PendingReboot);
    }

    [Fact]
    public void Aborted_bulk_that_cannot_be_reverted_demands_a_reboot()
    {
        using var fs = ThinkLmi();
        fs.RejectNames.Add("AfterPowerLoss");
        var fa = FirmwareAttributes.Detect(fs)!;
        // WakeOnLAN skrives, AfterPowerLoss afvises – og tilbageskrivningen af WakeOnLAN afvises også
        fs.RejectAfterFirstWrite.Add("WakeOnLAN");
        var res = fa.Apply([Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Power Off")], null);
        Assert.False(res.Ok);
        Assert.True(res.PendingReboot);
        Assert.Contains("Genstart maskinen før næste BIOS-ændring", res.Message);
        Assert.Contains("Kunne ikke trækkes tilbage", res.Lines[0].Detail);
    }

    [Fact]
    public void Password_clear_respects_min_length_and_reports_failure()
    {
        using var fs = ThinkLmi().Admin("hemmelig-123").File_("authentication/Admin/min_password_length", "4");
        var ok = FirmwareAttributes.Detect(fs)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic")], "hemmelig-123");
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("\n\n\n\n", fs.Writes.Last(w => w.Path.EndsWith("current_password", StringComparison.Ordinal)).Value);
        Assert.Equal("", fs.KernelPassword);

        using var fs2 = ThinkLmi().Admin("hemmelig-123");
        fs2.FailPasswordClear = true;
        var res = FirmwareAttributes.Detect(fs2)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic")], "hemmelig-123");
        Assert.True(res.Ok);
        Assert.True(res.PasswordNotCleared);
        Assert.Contains("indtil næste genstart", res.Message);
        Assert.StartsWith("Gemt", res.Message);
    }

    [Fact]
    public void Setting_with_an_empty_value_stays_in_the_backup_as_unreadable()
    {
        using var fs = ThinkLmi().Attr("AssetTag", "x", type: "string");
        fs.Unreadable.Add("AssetTag");   // think-lmi: current_value giver EINVAL, når BIOS'en melder "AssetTag,"
        var fa = FirmwareAttributes.Detect(fs)!;
        var r = fa.Read();
        var it = Assert.Single(r.Items, i => i.Name == "AssetTag");
        Assert.True(it.Unreadable);
        Assert.Equal("", it.Value);
        Assert.Equal(fa.Info().Count, r.Items.Count);
    }

    [Fact]
    public void Admin_password_is_required_used_and_cleared()
    {
        using var fs = ThinkLmi().Admin("hemmelig-123");
        var fa = FirmwareAttributes.Detect(fs)!;
        Assert.True(fa.Read().AdminPassword);

        var none = fa.Apply([Ch("WakeOnLAN", "Disabled", "Automatic")], null);
        Assert.False(none.Ok);
        Assert.Contains("administratoradgangskode", none.Message);
        Assert.Empty(fs.Writes);

        var ok = fa.Apply([Ch("WakeOnLAN", "Disabled", "Automatic")], "hemmelig-123");
        Assert.True(ok.Ok, ok.Message);
        var pw = fs.Writes.Where(w => w.Path.EndsWith("current_password", StringComparison.Ordinal)).Select(w => w.Value).ToList();
        Assert.Equal(["hemmelig-123", "\n"], pw);                   // skrevet først, ryddet til sidst
        Assert.Equal("hemmelig-123", fs.Writes[0].Value);
        Assert.Equal("", fs.KernelPassword);                        // en tom skrivning ville ikke nå kernen
        Assert.False(ok.PasswordNotCleared);
    }

    [Fact]
    public void Wrong_password_stops_immediately_and_is_never_shown()
    {
        using var fs = ThinkLmi().Admin("rigtig");
        var res = FirmwareAttributes.Detect(fs)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Power Off")], "forkert-kode");
        Assert.False(res.Ok);
        Assert.Equal(BiosApplyStatus.Denied, res.Lines[0].Status);
        Assert.Equal(BiosApplyStatus.NotWritten, res.Lines[1].Status);
        Assert.Equal(1, fs.Writes.Count(w => w.Path.Contains("/current_value")));   // kun ét forsøg
        Assert.DoesNotContain(fs.Writes, w => w.Value == "save");
        Assert.Equal("", fs.KernelPassword);                        // koden er ryddet i kernen
        Assert.DoesNotContain("forkert-kode", res.Message + string.Join(" ", res.Lines.Select(l => l.Detail)));
    }

    [Fact]
    public void Without_save_settings_each_value_is_saved_by_itself()
    {
        using var fs = new FakeSysfs()
            .Attr("WakeOnLAN", "Automatic", "Disabled;Automatic")
            .Attr("AfterPowerLoss", "Power Off", "Power On;Power Off")
            .File_("attributes/pending_reboot", "0");
        fs.RejectNames.Add("AfterPowerLoss");
        var res = FirmwareAttributes.Detect(fs)!.Apply([Ch("WakeOnLAN", "Disabled", "Automatic"), Ch("AfterPowerLoss", "Power On", "Power Off")], null);
        Assert.False(res.Ok);
        Assert.Contains("allerede er skrevet, er gemt", res.Message);
        Assert.Equal("Gemt.", res.Lines[0].Detail);
        Assert.DoesNotContain(fs.Writes, w => w.Path.EndsWith("save_settings", StringComparison.Ordinal));
    }
}

/// <summary>Windows: Lenovo WMI bag et falsk WMI-lag.</summary>
public class LenovoWmiSettingsTests
{
    private sealed class FakeWmi : ILenovoWmi
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal)
        {
            ["WakeOnLAN"] = "Automatic", ["After Power Loss"] = "Power Off", ["SecureBoot"] = "Enable", ["BootOrder"] = "NVMe0:USBHDD",
        };
        public readonly Dictionary<string, string> Choices = new(StringComparer.Ordinal)
        {
            ["WakeOnLAN"] = "Disable,Automatic,Primary", ["After Power Loss"] = "Power On,Power Off,Last State", ["SecureBoot"] = "Disable,Enable",
            ["BootOrder"] = "NVMe0:USBHDD:USBCD:PXEBOOT",
        };
        public readonly List<string> Calls = [];
        public int? State;
        public bool Opcodes = true;
        public string? Password;
        public string? SetAnswer;
        public string SaveAnswer = LenovoWmiSettings.Success;
        private bool unlocked;

        public IReadOnlyList<string> CurrentSettings() => [.. Values.Select(kv => $"{kv.Key},{kv.Value}" + (kv.Key == "SecureBoot" ? ";[Optional:Disable,Enable]" : "")), "", "Junk"];
        public string? Selections(string name) => Choices.GetValueOrDefault(name);
        public int? PasswordState() => State;
        public bool HasOpcode => Opcodes;
        public string Opcode(string p) { Calls.Add("opcode"); unlocked = p == "WmiOpcodePasswordAdmin:" + Password; return unlocked ? LenovoWmiSettings.Success : LenovoWmiSettings.AccessDenied; }
        public string Set(string p)
        {
            Calls.Add("set:" + p);
            if (Password is not null && !unlocked && !p.EndsWith($",{Password},ascii,us", StringComparison.Ordinal)) return LenovoWmiSettings.AccessDenied;
            return SetAnswer ?? LenovoWmiSettings.Success;
        }
        public string Save(string? p) { Calls.Add("save:" + (p ?? "")); return SaveAnswer; }
        public string? Discard(string? p) { Calls.Add("discard"); return LenovoWmiSettings.Success; }
    }

    private static BiosSettingChange Ch(string name, string value, string old) => new() { Name = name, Value = value, ExpectOld = old };

    [Fact]
    public void Reads_settings_with_selections()
    {
        var r = new LenovoWmiSettings(new FakeWmi()).Read();
        Assert.Equal(BiosSettingsDrivers.LenovoWmi, r.Driver);
        Assert.Equal(4, r.Items.Count);
        Assert.Equal("Enable", r.Items.Single(i => i.Name == "SecureBoot").Value);
        Assert.Equal(["NVMe0", "USBHDD", "USBCD", "PXEBOOT"], r.Items.Single(i => i.Name == "BootOrder").Possible);
        Assert.Equal(["Power On", "Power Off", "Last State"], r.Items.Single(i => i.Name == "After Power Loss").Possible);
        Assert.False(r.AdminPassword);
    }

    [Fact]
    public void Apply_sets_each_value_and_saves_once()
    {
        var w = new FakeWmi();
        var res = new LenovoWmiSettings(w).Apply([Ch("WakeOnLAN", "Disable", "Automatic"), Ch("BootOrder", "USBHDD:NVMe0", "NVMe0:USBHDD")], null);
        Assert.True(res.Ok, res.Message);
        Assert.Equal(["set:WakeOnLAN,Disable", "set:BootOrder,USBHDD:NVMe0", "save:"], w.Calls);
        Assert.True(res.PendingReboot);
    }

    [Fact]
    public void Access_denied_stops_at_once_and_discards()
    {
        var w = new FakeWmi { SetAnswer = LenovoWmiSettings.AccessDenied };
        var res = new LenovoWmiSettings(w).Apply([Ch("WakeOnLAN", "Disable", "Automatic"), Ch("SecureBoot", "Disable", "Enable")], null);
        Assert.False(res.Ok);
        Assert.Equal(["set:WakeOnLAN,Disable", "discard"], w.Calls);
        Assert.Equal(BiosApplyStatus.Denied, res.Lines[0].Status);
    }

    [Fact]
    public void System_busy_means_reboot_first()
    {
        var w = new FakeWmi { SetAnswer = LenovoWmiSettings.SystemBusy };
        var res = new LenovoWmiSettings(w).Apply([Ch("WakeOnLAN", "Disable", "Automatic")], null);
        Assert.Equal("Der venter allerede ændringer. Genstart først. Intet er gemt.", res.Message);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("save", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_parameter_and_failed_save_save_nothing()
    {
        var w = new FakeWmi { SetAnswer = LenovoWmiSettings.InvalidParameter };
        var res = new LenovoWmiSettings(w).Apply([Ch("WakeOnLAN", "Disable", "Automatic")], null);
        Assert.Equal(BiosApplyStatus.Rejected, res.Lines[0].Status);
        Assert.Contains("Invalid Parameter", res.Message);

        var w2 = new FakeWmi { SaveAnswer = LenovoWmiSettings.NotSupported };
        var r2 = new LenovoWmiSettings(w2).Apply([Ch("WakeOnLAN", "Disable", "Automatic")], null);
        Assert.False(r2.Ok);
        Assert.False(r2.Saved);
        Assert.Contains("discard", w2.Calls);
    }

    [Fact]
    public void Admin_password_via_opcode_or_legacy_suffix_never_leaks()
    {
        var w = new FakeWmi { State = 2, Password = "pw-9" };
        var s = new LenovoWmiSettings(w);
        Assert.True(s.Read().AdminPassword);
        Assert.Contains("administratoradgangskode", s.Apply([Ch("WakeOnLAN", "Disable", "Automatic")], null).Message);
        Assert.Empty(w.Calls);

        var ok = s.Apply([Ch("WakeOnLAN", "Disable", "Automatic")], "pw-9");
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(["opcode", "set:WakeOnLAN,Disable", "save:"], w.Calls);

        var bad = new FakeWmi { State = 2, Password = "pw-9" };
        var r = new LenovoWmiSettings(bad).Apply([Ch("WakeOnLAN", "Disable", "Automatic")], "gæt");
        Assert.False(r.Ok);
        Assert.DoesNotContain(bad.Calls, c => c.StartsWith("set", StringComparison.Ordinal));
        Assert.DoesNotContain("gæt", r.Message);

        var legacy = new FakeWmi { State = 2, Password = "pw-9", Opcodes = false };
        var l = new LenovoWmiSettings(legacy).Apply([Ch("WakeOnLAN", "Disable", "Automatic")], "pw-9");
        Assert.True(l.Ok, l.Message);
        Assert.Equal(["set:WakeOnLAN,Disable,pw-9,ascii,us", "save:pw-9,ascii,us"], legacy.Calls);
        Assert.DoesNotContain("pw-9", l.Message + string.Join(" ", l.Lines.Select(x => x.Value + x.Detail)));
    }
}

/// <summary>Jobbene på agenten (fælles for Linux og Windows).</summary>
public class BiosSettingsJobTests
{
    private sealed class FakeBackend(bool canWrite = true) : IBiosSettingsBackend
    {
        public string Driver => BiosSettingsDrivers.ThinkLmi;
        public bool CanWrite => canWrite;
        public string? GotPassword;
        public int Applies;
        public BiosSettingsInfo Info() => new() { Driver = Driver, Count = 1 };
        public BiosSettingsReport Read() => new() { Driver = Driver, Items = [new() { Name = "WakeOnLAN", Value = "Automatic", Possible = ["Disabled", "Automatic"] }] };
        public BiosApplyResult Apply(IReadOnlyList<BiosSettingChange> changes, string? password)
        {
            Applies++; GotPassword = password;
            return new() { Ok = true, Saved = true, PendingReboot = true, Message = "Gemt (1 indstillinger). Træder i kraft ved næste genstart.",
                Lines = [.. changes.Select(c => new BiosApplyLine { Name = c.Name, Value = c.Value, Status = BiosApplyStatus.Written })] };
        }
    }

    private static AgentJob Job(string type, JobParams? p = null) => new() { Id = 7, Type = type, Title = "t", Params = p ?? new JobParams() };
    private static readonly List<BiosSettingChange> One = [new() { Name = "WakeOnLAN", Value = "Disabled", ExpectOld = "Automatic" }];

    [Fact]
    public async Task Read_returns_the_report()
    {
        var r = await BiosSettingsJob.Run(Job(JobTypes.BiosSettingsRead), new FakeBackend(), (_, _) => Task.FromResult<BiosSecretResponse?>(null), default);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Single(r.BiosSettings!.Items);
        Assert.Contains("Læste 1 BIOS-indstillinger", r.Output);
    }

    [Fact]
    public async Task No_backend_fails_with_an_explanation()
    {
        var r = await BiosSettingsJob.Run(Job(JobTypes.BiosSettingsRead), null, (_, _) => Task.FromResult<BiosSecretResponse?>(null), default);
        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(BiosSettingsJob.NotSupported, r.Output);
    }

    [Fact]
    public async Task Apply_takes_a_copy_first_and_fetches_the_password_with_the_ticket()
    {
        var b = new FakeBackend();
        BiosSecretRequest? asked = null;
        var r = await BiosSettingsJob.Run(Job(JobTypes.BiosSettingsApply, new JobParams { BiosSettings = One, Ticket = "tkt" }), b,
            (req, _) => { asked = req; return Task.FromResult<BiosSecretResponse?>(new() { Value = "kode" }); }, default);
        Assert.Equal(JobStatus.Success, r.Status);
        Assert.Equal((7, "tkt"), (asked!.JobId, asked.Ticket));
        Assert.Equal("kode", b.GotPassword);
        Assert.NotNull(r.BiosSettingsBefore);
        Assert.NotNull(r.BiosApply);
        Assert.DoesNotContain("kode", r.Output);
    }

    [Fact]
    public async Task Apply_without_the_password_writes_nothing()
    {
        var b = new FakeBackend();
        var r = await BiosSettingsJob.Run(Job(JobTypes.BiosSettingsApply, new JobParams { BiosSettings = One, Ticket = "tkt" }), b,
            (_, _) => Task.FromResult<BiosSecretResponse?>(null), default);
        Assert.Equal(JobStatus.Failed, r.Status);
        Assert.Equal(0, b.Applies);
        Assert.Contains("Intet er skrevet", r.Output);

        var ro = await BiosSettingsJob.Run(Job(JobTypes.BiosSettingsApply, new JobParams { BiosSettings = One }), new FakeBackend(canWrite: false),
            (_, _) => Task.FromResult<BiosSecretResponse?>(null), default);
        Assert.Equal(JobStatus.Failed, ro.Status);
    }
}

/// <summary>Regelfilen og gendannelsesplanlæggeren.</summary>
public class BiosRestorePlannerTests
{
    private static BiosSettingItem I(string name, string value, string? possible = null, string? display = null, string type = "enumeration") =>
        new() { Name = name, Value = value, Display = display, Type = type, Possible = possible?.Split(';').ToList() };

    [Theory]
    [InlineData("SecureBoot", null, BiosRuleClasses.Ask)]
    [InlineData("Secure Boot", null, BiosRuleClasses.Ask)]
    [InlineData("PrimaryBootSequence", "Primary Boot Sequence", BiosRuleClasses.Ask)]
    [InlineData("SecurityChip", null, BiosRuleClasses.Ask)]
    [InlineData("ConfigureSATAas", null, BiosRuleClasses.Ask)]
    [InlineData("OnboardEthernetController", null, BiosRuleClasses.Ask)]
    [InlineData("AlarmDate", null, BiosRuleClasses.Never)]
    [InlineData("UserDefinedAlarmSunday", null, BiosRuleClasses.Never)]
    [InlineData("ComputraceModuleActivation", null, BiosRuleClasses.Never)]
    [InlineData("RequireAdminPasswordForF12", null, BiosRuleClasses.Never)]
    [InlineData("WakeOnLAN", "Wake on LAN", BiosRuleClasses.Common)]
    [InlineData("AfterPowerLoss", "After Power Loss", BiosRuleClasses.Common)]
    [InlineData("IntelVirtualizationTechnology", null, BiosRuleClasses.Common)]
    public void Rule_file_classifies_by_name(string name, string? display, string expected) =>
        Assert.Equal(expected, BiosSettingsRules.Default.Classify(BiosSettingsDrivers.ThinkLmi, name, display).Class);

    [Fact]
    public void Never_wins_and_driver_filter_works()
    {
        var rules = BiosSettingsRules.Parse("""
            {"rules":[{"class":"ask","match":["fan"]},{"class":"never","match":["fanlock"]},{"class":"ask","match":["wake"],"drivers":["hp-bioscfg"]}]}
            """);
        Assert.Equal(BiosRuleClasses.Never, rules.Classify("thinklmi", "FanLock").Class);
        Assert.Equal(BiosRuleClasses.Ask, rules.Classify("thinklmi", "FanSpeed").Class);
        Assert.Equal(BiosRuleClasses.Common, rules.Classify("thinklmi", "WakeOnLAN").Class);
        Assert.Equal(BiosRuleClasses.Ask, rules.Classify("hp-bioscfg", "WakeOnLAN").Class);
        Assert.Throws<InvalidDataException>(() => BiosSettingsRules.Parse("""{"rules":[{"class":"maybe","match":["x"]}]}"""));
    }

    private static readonly List<BiosSettingItem> Before =
    [
        I("AfterPowerLoss", "Power On", "Power On;Power Off;Last State"),
        I("WakeOnLAN", "Automatic", "Disabled;Automatic"),
        I("SecureBoot", "Enabled", "Disabled;Enabled"),
        I("PrimaryBootSequence", "NVMe0:USBHDD:LEGACYNET", "NVMe0;USBHDD;LEGACYNET"),
        I("AlarmDate", "01/01/2026", type: "string"),
        I("FanMode", "Quiet", "Quiet;Normal"),
        I("OldThing", "On", "On;Off"),
        I("Same", "X", "X;Y"),
    ];

    private static readonly List<BiosSettingItem> Now =
    [
        I("AfterPowerLoss", "Power Off", "Power On;Power Off;Last State"),
        I("WakeOnLAN", "Disabled", "Disabled;Automatic"),
        I("SecureBoot", "Disabled", "Disabled;Enabled"),
        I("PrimaryBootSequence", "USBHDD:NVMe0", "NVMe0;USBHDD;USBCD"),
        I("AlarmDate", "01/01/2019", type: "string"),
        I("FanMode", "Normal", "Normal;Performance"),
        I("NewThing", "On", "On;Off"),
        I("Same", "X", "X;Y"),
    ];

    [Fact]
    public void Diff_classifies_every_row()
    {
        var rows = BiosRestorePlanner.Diff(Before, Now, BiosSettingsDrivers.ThinkLmi).ToDictionary(r => r.Name);
        Assert.True(rows["AfterPowerLoss"] is { State: BiosDiffStates.Changed, Rule: BiosRuleClasses.Common, Selectable: true, Preselected: true });
        Assert.True(rows["WakeOnLAN"].Preselected);
        Assert.True(rows["SecureBoot"] is { Rule: BiosRuleClasses.Ask, Selectable: true, Preselected: false, Warn: not null });
        Assert.True(rows["PrimaryBootSequence"] is { Valid: false, Selectable: false });   // LEGACYNET findes ikke mere
        Assert.Contains("elementer", rows["PrimaryBootSequence"].Why);
        Assert.True(rows["AlarmDate"] is { Rule: BiosRuleClasses.Never, Selectable: false });
        Assert.True(rows["FanMode"] is { Valid: false, Selectable: false });                // "Quiet" er ikke gyldig længere
        Assert.Equal(BiosDiffStates.New, rows["NewThing"].State);
        Assert.False(rows["NewThing"].Selectable);
        Assert.Equal(BiosDiffStates.Gone, rows["OldThing"].State);
        Assert.Equal(BiosDiffStates.Same, rows["Same"].State);
        Assert.False(rows["Same"].Selectable);
    }

    [Fact]
    public void Boot_list_without_possible_values_is_checked_against_the_current_list()
    {
        // think-lmi på M720q: bootlisterne er type string uden possible_values
        var now = new List<BiosSettingItem> { I("PrimaryBootSequence", "M.2 Drive 1:USB HDD:PCI LAN", type: "string", display: "Primary Boot Sequence") };
        var ok = BiosRestorePlanner.Diff([I("PrimaryBootSequence", "USB HDD:M.2 Drive 1:PCI LAN", type: "string")], now, BiosSettingsDrivers.ThinkLmi).Single();
        Assert.True(ok is { Valid: true, Selectable: true });
        // et element, der ikke findes mere (omdøbt i den nye BIOS), og dubletter: ikke gyldig
        foreach (var bad in new[] { "SATA 1:M.2 Drive 1:PCI LAN", "USB HDD:USB HDD:PCI LAN" })
        {
            var row = BiosRestorePlanner.Diff([I("PrimaryBootSequence", bad, type: "string")], now, BiosSettingsDrivers.ThinkLmi).Single();
            Assert.True(row is { Valid: false, Selectable: false });
            Assert.Equal("Listen har elementer, der ikke findes i den nye BIOS.", row.Why);
        }
        // en almindelig tekstindstilling er ikke en bootliste
        var plain = BiosRestorePlanner.Diff([I("AssetTag", "A:B", type: "string")], [I("AssetTag", "C", type: "string")], BiosSettingsDrivers.ThinkLmi).Single();
        Assert.True(plain.Valid);
    }

    [Fact]
    public void Dependent_settings_are_written_in_a_safe_order()
    {
        var diff = BiosRestorePlanner.Diff(
            [I("CSM", "Enabled", "Disabled;Enabled"), I("SecureBoot", "Disabled", "Disabled;Enabled"), I("WakeOnLAN", "Primary", "Disabled;Primary"), I("OSOptimizedDefaults", "Disabled", "Disabled;Enabled")],
            [I("CSM", "Disabled", "Disabled;Enabled"), I("SecureBoot", "Enabled", "Disabled;Enabled"), I("WakeOnLAN", "Disabled", "Disabled;Primary"), I("OSOptimizedDefaults", "Enabled", "Disabled;Enabled")],
            BiosSettingsDrivers.ThinkLmi);
        var all = new[] { "CSM", "SecureBoot", "WakeOnLAN", "OSOptimizedDefaults" };
        var (changes, errors) = BiosRestorePlanner.Plan(diff, all, all);
        Assert.Empty(errors);
        Assert.Equal(["OSOptimizedDefaults", "SecureBoot", "CSM", "WakeOnLAN"], changes.Select(c => c.Name));
        // modsat retning: slå sikkerhed til til sidst
        var back = BiosRestorePlanner.Diff(
            [I("CSM", "Disabled", "Disabled;Enabled"), I("SecureBoot", "Enabled", "Disabled;Enabled")],
            [I("CSM", "Enabled", "Disabled;Enabled"), I("SecureBoot", "Disabled", "Disabled;Enabled")], BiosSettingsDrivers.ThinkLmi);
        Assert.Equal(["CSM", "SecureBoot"], BiosRestorePlanner.Plan(back, ["SecureBoot", "CSM"], ["SecureBoot", "CSM"]).Changes.Select(c => c.Name));
    }

    [Fact]
    public void Unreadable_value_is_shown_but_never_restored()
    {
        var row = BiosRestorePlanner.Diff([I("AssetTag", "PC-1", type: "string")], [new() { Name = "AssetTag", Type = "string", Value = "", Unreadable = true }],
            BiosSettingsDrivers.ThinkLmi).Single();
        Assert.Equal(BiosDiffStates.Changed, row.State);
        Assert.False(row.Selectable);
        Assert.Contains("kunne ikke læses", row.Why);
    }

    [Fact]
    public void Boot_list_is_restorable_when_all_elements_exist()
    {
        var now = new List<BiosSettingItem> { I("PrimaryBootSequence", "USBHDD:NVMe0", "NVMe0;USBHDD;USBCD") };
        var before = new List<BiosSettingItem> { I("PrimaryBootSequence", "NVMe0:USBHDD", "NVMe0;USBHDD;USBCD") };
        var row = BiosRestorePlanner.Diff(before, now, BiosSettingsDrivers.ThinkLmi).Single();
        Assert.True(row is { Valid: true, Selectable: true, Rule: BiosRuleClasses.Ask, Preselected: false });
        Assert.False(BiosValues.IsValid(now[0], "NVMe0:NVMe0"));   // dubletter afvises
    }

    [Fact]
    public void Plan_needs_explicit_acknowledgement_and_refuses_never()
    {
        var diff = BiosRestorePlanner.Diff(Before, Now, BiosSettingsDrivers.ThinkLmi);
        var (ok, errors) = BiosRestorePlanner.Plan(diff, ["AfterPowerLoss", "WakeOnLAN"], null);
        Assert.Empty(errors);
        Assert.Equal([("AfterPowerLoss", "Power On", "Power Off"), ("WakeOnLAN", "Automatic", "Disabled")], ok.Select(c => (c.Name, c.Value, c.ExpectOld)));

        (_, errors) = BiosRestorePlanner.Plan(diff, ["SecureBoot"], null);
        Assert.Contains(errors, e => e.StartsWith("SecureBoot: kræver", StringComparison.Ordinal));
        var (sb, e2) = BiosRestorePlanner.Plan(diff, ["SecureBoot"], ["SecureBoot"]);
        Assert.Empty(e2);
        Assert.Equal("Enabled", sb.Single().Value);

        foreach (var bad in new[] { "AlarmDate", "PrimaryBootSequence", "NewThing", "OldThing", "Same", "Nope" })
            Assert.NotEmpty(BiosRestorePlanner.Plan(diff, [bad], [bad]).Errors);
        Assert.NotEmpty(BiosRestorePlanner.Plan(diff, [], null).Errors);
    }

    [Fact]
    public void Check_gives_green_yellow_and_red()
    {
        List<BiosSettingChange> expected =
        [
            new() { Name = "A", Value = "Power On", ExpectOld = "Power Off" },
            new() { Name = "B", Value = "Enabled", ExpectOld = "Disabled" },
            new() { Name = "C", Value = "On", ExpectOld = "Off" },
            new() { Name = "D", Value = "On", ExpectOld = "Off" },
            new() { Name = "E", Value = "On", ExpectOld = "Off" },
        ];
        List<BiosSettingItem> after = [I("A", "Power On"), I("B", "enabled"), I("C", "Off"), I("E", "On")];
        var apply = new BiosApplyResult { Lines = [new() { Name = "E", Status = BiosApplyStatus.Written }] };
        var rows = BiosRestorePlanner.Check(expected, after, apply).ToDictionary(r => r.Name);
        Assert.Equal(BiosCheckColors.Green, rows["A"].Color);
        Assert.Equal(BiosCheckColors.Yellow, rows["B"].Color);
        Assert.Equal(BiosCheckColors.Red, rows["C"].Color);
        Assert.Equal("BIOS'en beholdt den gamle værdi.", rows["C"].Why);
        Assert.Equal(BiosCheckColors.Red, rows["D"].Color);
        Assert.Equal(BiosCheckColors.Green, rows["E"].Color);
    }
}

/// <summary>Adgangskoden ligger kun i serverens hukommelse og kan hentes én gang.</summary>
public class BiosSecretsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Ticket_gives_the_password_once()
    {
        var s = new BiosSecrets();
        var t = s.Put(5, "kode");
        Assert.Null(s.Take(5, "forkert"));
        Assert.Null(s.Take(6, t));
        Assert.Equal("kode", s.Take(5, t));
        Assert.Null(s.Take(5, t));
    }

    [Fact]
    public void Password_expires()
    {
        var c = new Clock();
        var s = new BiosSecrets(c);
        var t = s.Put(5, "kode");
        c.Now += BiosSecrets.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(s.Take(5, t));
        Assert.False(s.Has(5));
    }
}

/// <summary>Snapshots, gendannelse og automatiske kopier mod en rigtig (SQLite) database med migrationerne.</summary>
public sealed class BiosSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly JobService jobs;
    private readonly BiosSecrets secrets = new();
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public BiosSettingsServiceTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();   // kontrollerer også, at AddBios-migrationen passer til modellen
        jobs = new JobService(db, new Live(new NoHub<UiHub>(), new NoHub<AgentHub>()));
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private Machine AddMachine(bool admin = false, string driver = BiosSettingsDrivers.ThinkLmi, bool? pending = false, string bios = "Q9ZKT75A")
    {
        var f = new SystemFacts
        {
            Manufacturer = "LENOVO", BiosVendor = "LENOVO", BiosVersion = bios, ProductVersion = "ThinkCentre M999q", IsVirtual = false,
            BiosSettings = new BiosSettingsInfo { Driver = driver, Count = 3, AdminPassword = admin, PendingReboot = pending, Writable = BiosSettingsDrivers.Writable(driver) },
        };
        var m = new Machine { Name = "pc-" + Guid.NewGuid().ToString("N")[..6], Platform = Platforms.Linux, FactsJson = Json.Ser(f) };
        db.Machines.Add(m);
        db.SaveChanges();
        return m;
    }

    private static BiosSettingsReport Rep(string apl, string wol, string sb = "Enabled", bool admin = false, string driver = BiosSettingsDrivers.ThinkLmi) => new()
    {
        Driver = driver, AdminPassword = admin, PendingReboot = false, Writable = true,
        Items =
        [
            new() { Name = "AfterPowerLoss", Value = apl, Possible = ["Power On", "Power Off", "Last State"] },
            new() { Name = "WakeOnLAN", Value = wol, Possible = ["Disabled", "Automatic"] },
            new() { Name = "SecureBoot", Value = sb, Possible = ["Disabled", "Enabled"] },
            new() { Name = "AdminPwd", Value = "x", Type = "password" },
        ],
    };

    [Fact]
    public async Task Store_keeps_rules_skips_passwords_and_prunes()
    {
        var m = AddMachine();
        var s = await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic"), BiosSnapshotReasons.PreUpdate, "admin", null, T0);
        var items = BiosSettingsService.Items(s);
        Assert.Equal(3, items.Count);
        Assert.Equal(3, s.Count);
        Assert.DoesNotContain(items, i => i.Type == "password");
        Assert.Equal(BiosRuleClasses.Ask, items.Single(i => i.Name == "SecureBoot").Rule);
        Assert.Equal(Crypto.Sha256(s.ItemsJson), s.Sha256);
        Assert.True(s.SecureBoot);
        Assert.Equal("Q9ZKT75A", s.BiosVersion);
        Assert.Equal("ThinkCentre M999q", s.Model);

        for (var i = 1; i <= BiosSettingsService.Keep + 5; i++)
            await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic"), BiosSnapshotReasons.Manual, "admin", null, T0.AddMinutes(i));
        var left = await db.BiosSnapshots.Where(x => x.MachineId == m.Id).ToListAsync();
        Assert.Equal(BiosSettingsService.Keep + 1, left.Count);
        Assert.Contains(left, x => x.Id == s.Id);   // den seneste "før opdatering" beholdes altid
    }

    private async Task<(Machine M, BiosSettingsSnapshot From, BiosSettingsSnapshot To)> TwoSnapshots(bool admin = false)
    {
        var m = AddMachine(admin);
        var from = await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic", admin: admin), BiosSnapshotReasons.PreUpdate, "admin", null, T0.AddHours(-2));
        var to = await BiosSettingsService.Store(db, m, Rep("Power Off", "Disabled", "Disabled", admin), BiosSnapshotReasons.PostBoot, "system", null, T0.AddHours(-1));
        return (m, from, to);
    }

    [Fact]
    public async Task Restore_creates_one_apply_job_with_only_valid_changes()
    {
        var (m, from, to) = await TwoSnapshots();
        var (job, errors) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m,
            new(from.Id, to.Id, ["AfterPowerLoss", "WakeOnLAN"], null, null), "admin", T0);
        Assert.Empty(errors);
        Assert.Equal(JobTypes.BiosSettingsApply, job!.Type);
        var prm = Json.Des<JobParams>(job.ParamsJson);
        Assert.Equal([("AfterPowerLoss", "Power On", "Power Off"), ("WakeOnLAN", "Automatic", "Disabled")],
            prm.BiosSettings!.Select(c => (c.Name, c.Value, c.ExpectOld)));
        Assert.Null(prm.Ticket);
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Action == "bios.settings.restore");

        // et BIOS-job ad gangen
        var (again, e2) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "admin", T0);
        Assert.Null(again);
        Assert.Contains("allerede et BIOS-job", e2.Single());
    }

    [Fact]
    public async Task Restore_refuses_ask_without_ack_stale_or_pending()
    {
        var (m, from, to) = await TwoSnapshots();
        Assert.Contains(BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["SecureBoot"], null, null), "a", T0).Result.Errors,
            e => e.Contains("bekræfter"));
        Assert.Contains("for gammel", BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "a", T0.AddDays(2)).Result.Errors.Single());
        Assert.Contains("nyere kopi", BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, from.Id, ["WakeOnLAN"], null, null), "a", T0).Result.Errors.Single());

        var f = Json.Des<SystemFacts>(m.FactsJson);
        f.BiosSettings!.PendingReboot = true;
        m.FactsJson = Json.Ser(f);
        await db.SaveChangesAsync();
        Assert.Contains("Genstart maskinen først", BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "a", T0).Result.Errors.Single());
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task Read_only_drivers_cannot_restore()
    {
        var m = AddMachine(driver: BiosSettingsDrivers.DellSysman);
        var (job, errors) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(1, null, ["WakeOnLAN"], null, null), "a", T0);
        Assert.Null(job);
        Assert.Contains("kun læse", errors.Single());
        var (read, err) = await BiosSettingsService.RequestRead(db, jobs, m, BiosSnapshotReasons.Manual, "a");
        Assert.Null(err);
        Assert.Equal(JobTypes.BiosSettingsRead, read!.Type);
    }

    [Fact]
    public async Task Password_stays_in_memory_and_ticket_is_cleared_after_the_job()
    {
        var (m, from, to) = await TwoSnapshots(admin: true);
        var (none, e) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "a", T0);
        Assert.Null(none);
        Assert.Contains("administratoradgangskode", e.Single());

        var (job, errors) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, "BiosKode!7"), "a", T0);
        Assert.Empty(errors);
        var stored = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job!.Id);
        Assert.DoesNotContain("BiosKode!7", stored.ParamsJson);
        Assert.DoesNotContain(await db.Audit.ToListAsync(), a => a.Detail.Contains("BiosKode!7"));
        var ticket = Json.Des<JobParams>(stored.ParamsJson).Ticket;
        Assert.NotNull(ticket);
        Assert.True(secrets.Has(job!.Id));

        // jobbet slutter (fx fejlet) uden at koden er hentet: billet og kode forsvinder
        job.Status = JobStatus.Failed; job.Finished = T0;
        await BiosSettingsService.OnResult(db, secrets, m, job, new JobResult { Status = JobStatus.Failed, BiosSettingsBefore = Rep("Power Off", "Disabled", "Disabled", true) }, T0);
        Assert.Null(Json.Des<JobParams>((await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).ParamsJson).Ticket);
        Assert.False(secrets.Has(job.Id));
        Assert.Equal(BiosSnapshotReasons.PreRestore, (await db.BiosSnapshots.OrderByDescending(s => s.Id).FirstAsync()).Reason);
    }

    [Fact]
    public async Task Read_result_is_stored_with_its_reason()
    {
        var m = AddMachine();
        var (job, _) = await BiosSettingsService.RequestRead(db, jobs, m, BiosSnapshotReasons.PreUpdate, "admin");
        job!.Status = JobStatus.Success; job.Finished = T0;
        await BiosSettingsService.OnResult(db, secrets, m, job, new JobResult { BiosSettings = Rep("Power On", "Automatic") }, T0);
        var s = await db.BiosSnapshots.SingleAsync();
        Assert.Equal((BiosSnapshotReasons.PreUpdate, job.Id, "admin"), (s.Reason, s.JobId, s.By));
    }

    [Fact]
    public async Task Successful_apply_marks_pending_reboot()
    {
        var (m, from, to) = await TwoSnapshots();
        var (job, _) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "a", T0);
        job!.Status = JobStatus.Success; job.Finished = T0;
        await BiosSettingsService.OnResult(db, secrets, m, job, new JobResult { BiosApply = new() { Ok = true, Saved = true, PendingReboot = true } }, T0);
        Assert.True(Json.Des<SystemFacts>(m.FactsJson).BiosSettings!.PendingReboot);
    }

    [Fact]
    public async Task New_bios_version_triggers_a_post_boot_copy()
    {
        var m = AddMachine();
        var old = Json.Des<SystemFacts>(m.FactsJson);
        var now = Json.Des<SystemFacts>(m.FactsJson);
        now.BiosVersion = "Q9ZKT79A";
        Assert.Null(await BiosSettingsService.AfterInventory(db, jobs, m, old, now, T0));   // ingen kopi endnu: intet at sammenligne med
        await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic"), BiosSnapshotReasons.PreUpdate, "a", null, T0.AddHours(-1));
        Assert.Null(await BiosSettingsService.AfterInventory(db, jobs, m, old, old, T0));    // samme version
        var j = await BiosSettingsService.AfterInventory(db, jobs, m, old, now, T0);
        Assert.Equal(BiosSnapshotReasons.PostBoot, Json.Des<JobParams>(j!.ParamsJson).BiosReason);
        Assert.Equal(BiosSettingsService.Source, j.Source);
        Assert.Null(await BiosSettingsService.AfterInventory(db, jobs, m, old, now, T0));    // ét BIOS-job ad gangen
    }

    [Fact]
    public async Task Reboot_after_restore_triggers_one_control_copy()
    {
        var (m, from, to) = await TwoSnapshots();
        var (apply, _) = await BiosSettingsService.RequestRestore(db, jobs, secrets, m, new(from.Id, to.Id, ["WakeOnLAN"], null, null), "a", T0);
        apply!.Status = JobStatus.Success; apply.Finished = T0;
        await db.SaveChangesAsync();
        var facts = Json.Des<SystemFacts>(m.FactsJson);

        var h = new HealthReport();
        h.Live.BootAt = T0.AddMinutes(-5);   // ikke genstartet endnu
        m.HealthJson = Json.Ser(h);
        Assert.Null(await BiosSettingsService.AfterInventory(db, jobs, m, facts, facts, T0.AddMinutes(1)));

        h.Live.BootAt = T0.AddMinutes(10);
        m.HealthJson = Json.Ser(h);
        var read = await BiosSettingsService.AfterInventory(db, jobs, m, facts, facts, T0.AddMinutes(12));
        Assert.Equal(BiosSnapshotReasons.PostRestore, Json.Des<JobParams>(read!.ParamsJson).BiosReason);

        read.Status = JobStatus.Success; read.Finished = T0.AddMinutes(13);
        await BiosSettingsService.OnResult(db, secrets, m, read, new JobResult { BiosSettings = Rep("Power Off", "Automatic", "Disabled") }, T0.AddMinutes(13));
        Assert.Null(await BiosSettingsService.AfterInventory(db, jobs, m, facts, facts, T0.AddMinutes(20)));   // kun én kontrolkopi

        var post = await db.BiosSnapshots.OrderByDescending(s => s.Id).FirstAsync();
        var check = BiosRestorePlanner.Check(Json.Des<JobParams>(apply.ParamsJson).BiosSettings!, BiosSettingsService.Items(post));
        Assert.Equal(BiosCheckColors.Green, check.Single().Color);
    }

    [Fact]
    public async Task Default_pair_prefers_the_copy_taken_before_the_update()
    {
        var m = AddMachine();
        var pre = await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic"), BiosSnapshotReasons.PreUpdate, "a", null, T0.AddHours(-3));
        await BiosSettingsService.Store(db, m, Rep("Power On", "Automatic"), BiosSnapshotReasons.Manual, "a", null, T0.AddHours(-2));
        var f = Json.Des<SystemFacts>(m.FactsJson); f.BiosVersion = "Q9ZKT79A"; m.FactsJson = Json.Ser(f);
        var post = await BiosSettingsService.Store(db, m, Rep("Power Off", "Disabled"), BiosSnapshotReasons.PostBoot, "system", null, T0.AddHours(-1));
        var all = await db.BiosSnapshots.OrderByDescending(s => s.TakenAt).ToListAsync();
        var (a, b) = BiosSettingsService.DefaultPair(all);
        Assert.Equal((pre.Id, post.Id), (a!.Id, b!.Id));
    }

    [Fact]
    public void Virtual_machines_and_machines_without_driver_are_unsupported()
    {
        var m = new Machine { FactsJson = Json.Ser(new SystemFacts { IsVirtual = true, BiosSettings = new() { Driver = "thinklmi" } }) };
        Assert.NotNull(BiosSettingsService.Unsupported(m));
        Assert.Equal(BiosSettingsJob.NotSupported, BiosSettingsService.Unsupported(new Machine { FactsJson = Json.Ser(new SystemFacts()) }));
    }

    private sealed class NoHub<T> : IHubContext<T> where T : Hub
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();
    }
    private sealed class NoClients : IHubClients
    {
        private static readonly IClientProxy P = new NoProxy();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }
    private sealed class NoProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
