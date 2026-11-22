using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>TPM: producent-id, Win32_Tpm, sysfs, tpm2_getcap, Mac, BitLocker-beskytter og helbredsreglen.</summary>
public class TpmTests
{
    [Theory]
    [InlineData(0x494E5443u, "INTC")]
    [InlineData(0x414D4400u, "AMD")]
    [InlineData(0x49465800u, "IFX")]
    [InlineData(0x4E544300u, "NTC")]
    [InlineData(0x53544D20u, "STM")]
    [InlineData(0x4D534654u, "MSFT")]
    [InlineData(0x49424D00u, "IBM")]
    public void Manufacturer_id_decodes_to_text(uint id, string text) => Assert.Equal(text, TpmVendors.Decode(id));

    [Fact]
    public void Manufacturer_id_zero_or_binary_is_null()
    {
        Assert.Null(TpmVendors.Decode(0));
        Assert.Null(TpmVendors.Decode(0x01020304));
    }

    [Theory]
    [InlineData("INTC", "Intel", TpmKinds.Firmware)]
    [InlineData("AMD", "AMD", TpmKinds.Firmware)]
    [InlineData("IFX", "Infineon", TpmKinds.Discrete)]
    [InlineData("NTC", "Nuvoton", TpmKinds.Discrete)]
    [InlineData("STM", "STMicroelectronics", TpmKinds.Discrete)]
    [InlineData("IBM", "IBM (software-TPM)", TpmKinds.Virtual)]
    [InlineData("XYZ", "XYZ", null)]
    public void Manufacturer_name_and_kind(string id, string name, string? kind)
    {
        Assert.Equal(name, TpmVendors.Name(id));
        Assert.Equal(kind, TpmVendors.Kind(id));
    }

    [Theory]
    [InlineData("IFX ", "IFX")]
    [InlineData("0x494E5443", "INTC")]
    [InlineData("1229870147", "INTC")]
    [InlineData("\"AMD\"", "AMD")]
    [InlineData("", null)]
    public void Manufacturer_id_normalizes(string input, string? expected) => Assert.Equal(expected, TpmVendors.Normalize(input));

    [Fact]
    public void Wmi_intel_ptt_ready()
    {
        var t = TpmParse.FromWmi(new Dictionary<string, object?>
        {
            ["IsActivated_InitialValue"] = true, ["IsEnabled_InitialValue"] = true, ["IsOwned_InitialValue"] = true,
            ["ManufacturerId"] = 0x494E5443u, ["ManufacturerIdTxt"] = "INTC", ["ManufacturerVersion"] = "600.18.0.0", ["SpecVersion"] = "2.0, 0, 1.59",
        }, ready: true, readyInfo: 0);
        Assert.True(t.Present);
        Assert.Equal("2.0", t.Version);
        Assert.Equal("INTC", t.ManufacturerId);
        Assert.Equal("Intel", t.Manufacturer);
        Assert.Equal(TpmKinds.Firmware, t.Kind);
        Assert.Equal("600.18.0.0", t.Firmware);
        Assert.True(t.Enabled);
        Assert.True(t.Activated);
        Assert.True(t.Owned);
        Assert.True(t.Ready);
        Assert.Null(t.Attention);
        Assert.Equal("wmi", t.Source);
    }

    [Fact]
    public void Wmi_without_text_id_decodes_number_and_reports_attention()
    {
        // ældre Windows uden ManufacturerIdTxt; TPM 1.2 der skal ryddes og kræver genstart
        var t = TpmParse.FromWmi(new Dictionary<string, object?>
        {
            ["IsActivated_InitialValue"] = false, ["IsEnabled_InitialValue"] = true, ["IsOwned_InitialValue"] = false,
            ["ManufacturerId"] = 1229346816u, ["ManufacturerVersion"] = "3.19", ["SpecVersion"] = "1.2, 2, 3",
        }, ready: false, readyInfo: 0x2 | 0x20);
        Assert.Equal("1.2", t.Version);
        Assert.Equal("IFX", t.ManufacturerId);
        Assert.Equal(TpmKinds.Discrete, t.Kind);
        Assert.False(t.Activated);
        Assert.False(t.Ready);
        Assert.Equal(["Kræver genstart", "Skal ryddes, før den kan bruges"], t.Attention);
    }

    [Fact]
    public void Wmi_no_instance_is_no_tpm()
    {
        var t = TpmParse.FromWmi(null);
        Assert.False(t.Present);
        Assert.Null(t.Version);
    }

    [Theory]
    [InlineData("2.0, 0, 1.38", "2.0")]
    [InlineData("1.2, 2, 3", "1.2")]
    [InlineData("2", "2.0")]
    [InlineData(null, null)]
    public void Spec_version(string? spec, string? expected) => Assert.Equal(expected, TpmParse.SpecVersion(spec));

    [Fact]
    public void Firmware_version_like_windows() => Assert.Equal("7.2.2.0", TpmParse.FirmwareVersion(0x00070002, 0x00020000));

    [Fact]
    public void Sysfs_no_tpm()
    {
        var t = TpmParse.FromSysfs(false, null);
        Assert.False(t.Present);
        Assert.Equal("sysfs", t.Source);
    }

    [Fact]
    public void Sysfs_tpm2_only_gives_version()
    {
        var t = TpmParse.FromSysfs(true, "2\n");
        Assert.True(t.Present);
        Assert.Equal("2.0", t.Version);
        Assert.Null(t.ManufacturerId);
        Assert.Null(t.Ready);
    }

    [Fact]
    public void Sysfs_old_kernel_with_tpmrm_is_tpm2() => Assert.Equal("2.0", TpmParse.FromSysfs(true, null, tpmrmDevice: true).Version);

    [Fact]
    public void Sysfs_tpm12_caps_and_flags()
    {
        const string caps = "Manufacturer: 0x49465800\nTCG version: 1.2\nFirmware version: 3.19\n";
        var t = TpmParse.FromSysfs(true, "1", caps, enabled: "1", active: "1", owned: "0");
        Assert.Equal("1.2", t.Version);
        Assert.Equal("IFX", t.ManufacturerId);
        Assert.Equal("Infineon", t.Manufacturer);
        Assert.Equal("3.19", t.Firmware);
        Assert.True(t.Enabled);
        Assert.True(t.Activated);
        Assert.False(t.Owned);
        Assert.False(t.Ready);
    }

    private const string Getcap5 = """
        TPM2_PT_FAMILY_INDICATOR:
          raw: 0x322E3000
          value: "2.0"
        TPM2_PT_LEVEL:
          raw: 0
        TPM2_PT_REVISION:
          raw: 0x9F
          value: 1.59
        TPM2_PT_MANUFACTURER:
          raw: 0x494E5443
          value: "INTC"
        TPM2_PT_VENDOR_STRING_1:
          raw: 0x496E7465
          value: "Inte"
        TPM2_PT_FIRMWARE_VERSION_1:
          raw: 0x258000E
        TPM2_PT_FIRMWARE_VERSION_2:
          raw: 0x0
        """;

    private const string Getcap3 = """
        TPM_PT_FAMILY_INDICATOR:
          as UINT32:                0x08322e3000
          as string:                "2.0"
        TPM_PT_LEVEL:               0
        TPM_PT_MANUFACTURER:        0x49465800
        TPM_PT_FIRMWARE_VERSION_1:  0x00070002
        TPM_PT_FIRMWARE_VERSION_2:  0x00020000
        """;

    [Fact]
    public void Tpm2_getcap_v5_output()
    {
        var t = TpmParse.WithTpm2Getcap(TpmParse.FromSysfs(true, "2"), Getcap5);
        Assert.Equal("tpm2-tools", t.Source);
        Assert.Equal("2.0", t.Version);
        Assert.Equal("INTC", t.ManufacturerId);
        Assert.Equal("Intel", t.Manufacturer);
        Assert.Equal(TpmKinds.Firmware, t.Kind);
        Assert.Equal("600.14.0.0", t.Firmware);
    }

    [Fact]
    public void Tpm2_getcap_v3_output()
    {
        var t = TpmParse.WithTpm2Getcap(TpmParse.FromSysfs(true, "2"), Getcap3);
        Assert.Equal("2.0", t.Version);
        Assert.Equal("IFX", t.ManufacturerId);
        Assert.Equal(TpmKinds.Discrete, t.Kind);
        Assert.Equal("7.2.2.0", t.Firmware);
    }

    [Fact]
    public void Tpm2_getcap_empty_keeps_sysfs()
    {
        var t = TpmParse.WithTpm2Getcap(TpmParse.FromSysfs(true, "2"), "");
        Assert.Equal("sysfs", t.Source);
        Assert.Null(t.Firmware);
    }

    [Theory]
    [InlineData("arm64", "", "Apple Silicon (Secure Enclave)")]
    [InlineData("x86_64", "      Model Name: Apple T2 Security Chip", "Apple T2 (Secure Enclave)")]
    [InlineData("x86_64", "", null)]
    public void Mac_has_no_tpm(string arch, string bridge, string? apple)
    {
        var t = TpmParse.Mac(arch, bridge);
        Assert.False(t.Present);
        Assert.Equal(apple, t.Apple);
    }

    [Fact]
    public void BitLocker_volume_knows_tpm_protector()
    {
        var r = BitLockerParse.FromPowerShell(Fixture.Read("get-bitlockervolume.json"));
        Assert.True(r.Volumes[0].UsesTpm);    // C: Tpm + RecoveryPassword
        Assert.False(r.Volumes[1].UsesTpm);   // D: ingen beskyttere
        Assert.False(r.Volumes[2].UsesTpm);   // E: ExternalKey
        Assert.True(BitLockerParse.IsTpmProtector("TpmPin"));
        Assert.False(BitLockerParse.IsTpmProtector("RecoveryPassword"));
    }

    [Fact]
    public void BitLocker_raw_enum_tpm_pin_is_tpm()
    {
        const string json = """{"MountPoint":"C:","VolumeType":0,"ProtectionStatus":1,"VolumeStatus":1,"KeyProtector":[{"KeyProtectorType":4}]}""";
        Assert.True(BitLockerParse.FromPowerShell(json).Volumes[0].UsesTpm);
    }

    // ------------------------------------------------------------ helbredsreglen

    private static readonly SystemFacts Win11 = new() { Os = "Microsoft Windows 11 Pro", Build = "26100.2314" };
    private static readonly SystemFacts Win10 = new() { Os = "Microsoft Windows 10 Pro", Build = "19045.5011" };
    private static readonly SystemFacts Server2025 = new() { Os = "Microsoft Windows Server 2025 Standard", Build = "26100.1" };
    private static EncryptionInfo BitLockerOn() => new() { Kind = EncryptionKinds.BitLocker, Volumes = [new EncryptedVolume { Mount = "C:", System = true, Encrypted = true }] };
    private static TpmInfo Good() => new() { Present = true, Version = "2.0", Enabled = true, Activated = true, Ready = true };

    [Fact]
    public void Health_ok_tpm_gives_nothing()
    {
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, Good(), BitLockerOn(), Win11));
    }

    [Fact]
    public void Health_windows11_without_tpm_warns()
    {
        var f = HealthService.TpmFinding(Platforms.Windows, new TpmInfo { Present = false }, null, Win11);
        Assert.NotNull(f);
        Assert.Equal("tpm", f.Key);
        Assert.Equal("warning", f.Severity);
        Assert.Contains("Ingen TPM", f.Detail);
        Assert.Contains("Windows 11", f.Detail);
    }

    [Fact]
    public void Health_bitlocker_with_disabled_tpm_warns_on_windows10()
    {
        var t = Good(); t.Enabled = false;
        var f = HealthService.TpmFinding(Platforms.Windows, t, BitLockerOn(), Win10);
        Assert.NotNull(f);
        Assert.Contains("slået fra", f.Detail);
        Assert.Contains("BitLocker", f.Detail);
    }

    [Fact]
    public void Health_not_ready_includes_attention()
    {
        var t = Good(); t.Ready = false; t.Attention = ["Kræver genstart"];
        var f = HealthService.TpmFinding(Platforms.Windows, t, null, Win11);
        Assert.NotNull(f);
        Assert.Contains("ikke klar (kræver genstart)", f.Detail);
    }

    [Fact]
    public void Health_tpm12_on_windows11_warns_but_not_on_windows10()
    {
        var t = Good(); t.Version = "1.2";
        Assert.NotNull(HealthService.TpmFinding(Platforms.Windows, t, null, Win11));
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, t, BitLockerOn(), Win10));
    }

    [Fact]
    public void Health_ignores_windows10_without_bitlocker_server_linux_unknown_and_errors()
    {
        var none = new TpmInfo { Present = false };
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, none, null, Win10));
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, none, new EncryptionInfo { Kind = EncryptionKinds.BitLocker, Volumes = [new EncryptedVolume { Mount = "C:", Encrypted = false }] }, Win10));
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, none, null, Server2025));
        Assert.Null(HealthService.TpmFinding(Platforms.Linux, none, null, new SystemFacts()));
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, null, BitLockerOn(), Win11));
        Assert.Null(HealthService.TpmFinding(Platforms.Windows, new TpmInfo { Present = false, Error = "x" }, BitLockerOn(), Win11));
    }

    [Theory]
    [InlineData("Microsoft Windows 11 Home", "22631.4317", null, true)]
    [InlineData("Microsoft Windows 10 Pro", "19045.1", null, false)]
    [InlineData("Microsoft Windows Server 2025 Standard", "26100.1", "ServerStandard", false)]
    [InlineData("Microsoft Windows 11 Pro", null, null, true)]
    public void Windows11_detection(string os, string? build, string? edition, bool expected) =>
        Assert.Equal(expected, HealthService.IsWindows11(new SystemFacts { Os = os, Build = build, Edition = edition }));
}
