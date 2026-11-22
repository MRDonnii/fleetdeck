using System.Text;
using System.Xml.Linq;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Det, en installation skal sættes op med. Tomme felter ("" eller null) gives ikke til installationen,
/// så den spørger om dem på maskinen (eller vælger selv, se <see cref="OsEntry.Blank"/>).
/// Adgangskoden står kun som hash (sha512-crypt); krypteringsnøgler skrives altid på maskinen.
/// </summary>
public sealed record InstallSpec
{
    public string Hostname { get; init; } = "";
    public string UserName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public string SshKey { get; init; } = "";
    public bool Admin { get; init; } = true;
    public InstallLanguage? Language { get; init; }
    public InstallKeyboard? Keyboard { get; init; }
    public string? TimeZone { get; init; }
    public bool AutoDisk { get; init; }
    public bool Encrypt { get; init; }
    public string Variant { get; init; } = "server";
    public IReadOnlyList<string> Packages { get; init; } = [];
    public int? ProfileId { get; init; }
    /// <summary>Fleetdeck-agentens installationsscript (med tilmeldingsnøglen) og adressen, installationen melder "færdig" til.</summary>
    public string InstallUrl { get; init; } = "";
    public string DoneUrl { get; init; } = "";

    // Windows
    public string Mode { get; init; } = "managed";
    public int? DeployId { get; init; }

    public bool Desktop => Variant == "desktop";
}

/// <summary>Svarfilerne til de automatiske installationer. Rene funktioner, så de kan testes uden server.</summary>
public static class AnswerFiles
{
    // ------------------------------------------------------------------ Ubuntu: autoinstall (cloud-init NoCloud)
    public static string Autoinstall(InstallSpec s)
    {
        var identityKnown = s.Hostname.Length > 0 && s.UserName.Length > 0 && s.PasswordHash.Length > 0;
        var interactive = new List<string>();
        if (s.Language is null) interactive.Add("locale");
        if (s.Keyboard is null) interactive.Add("keyboard");
        if (!identityKnown) interactive.Add("identity");
        if (!s.AutoDisk || s.Encrypt) interactive.Add("storage");   // kryptering: nøglen skrives på maskinen

        var sb = new StringBuilder("#cloud-config\nautoinstall:\n  version: 1\n");
        if (interactive.Count > 0) sb.Append("  interactive-sections:\n").Append(string.Concat(interactive.Select(x => $"    - {x}\n")));
        if (s.Language is { } l) sb.Append($"  locale: {l.Linux}\n");
        if (s.Keyboard is { } k)
        {
            sb.Append($"  keyboard:\n    layout: {k.Xkb}\n");
            if (k.XkbVariant is { } v) sb.Append($"    variant: {v}\n");
        }
        sb.Append($"  timezone: {s.TimeZone ?? "geoip"}\n");
        if (identityKnown)
            sb.Append($"  identity:\n    hostname: {s.Hostname}\n    realname: \"{Or(s.FullName, s.UserName)}\"\n    username: {s.UserName}\n    password: \"{s.PasswordHash}\"\n");
        else if (s.Hostname.Length > 0 || s.UserName.Length > 0)
        {
            // Skærmen vises på maskinen med det, der er udfyldt, som forslag
            sb.Append("  identity:\n");
            if (s.Hostname.Length > 0) sb.Append($"    hostname: {s.Hostname}\n");
            if (s.UserName.Length > 0) sb.Append($"    realname: \"{Or(s.FullName, s.UserName)}\"\n    username: {s.UserName}\n");
        }
        sb.Append("  ssh:\n    install-server: true\n    allow-pw: true\n");   // adgangskoden er altid med (eller skrives på maskinen)
        if (s.SshKey.Length > 0) sb.Append($"    authorized-keys:\n      - \"{s.SshKey}\"\n");
        sb.Append("  storage:\n    layout:\n      name: lvm\n");
        var pk = new List<string> { "curl" };
        if (s.Desktop) pk.Add("ubuntu-desktop-minimal");
        pk.AddRange(s.Packages);
        sb.Append("  packages:\n").Append(string.Concat(pk.Select(x => $"    - {x}\n")));
        sb.Append("  late-commands:\n");
        if (s.Desktop)
        {
            // Skrivebord: NetworkManager styrer netværket (ellers viser Ubuntu "ingen forbindelse")
            sb.Append("    - rm -f /target/etc/netplan/50-cloud-init.yaml\n");
            sb.Append("    - \"printf 'network:\\n  version: 2\\n  renderer: NetworkManager\\n' > /target/etc/netplan/01-network-manager-all.yaml\"\n");
            sb.Append("    - chmod 600 /target/etc/netplan/01-network-manager-all.yaml\n");
        }
        if (!s.Admin && s.UserName.Length > 0) sb.Append($"    - curtin in-target -- deluser {s.UserName} sudo || true\n");
        // Fleetdeck-agenten installeres i den nye Ubuntu, tilmelder sig med værtsnavnet og lægger genskab-kittet på ved første opstart
        var profile = s.ProfileId is int p ? $" WS_PROFILE={p}" : "";
        if (s.Hostname.Length > 0)
            sb.Append($"    - 'curtin in-target -- env WS_NAME={s.Hostname}{profile} WS_PROVISION=1 sh -c \"curl -fsSL ''{s.InstallUrl}'' | bash\" || true'\n");
        else   // værtsnavnet blev valgt på maskinen: læs det i den nye installation
            sb.Append($"    - 'curtin in-target -- sh -c \"export WS_NAME=\\$(cat /etc/hostname){profile} WS_PROVISION=1; curl -fsSL ''{s.InstallUrl}'' | bash\" || true'\n");
        sb.Append($"    - \"curl -fsS -m 20 -X POST {s.DoneUrl} || true\"\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Debian: preseed
    /// <summary>Spørges der om noget på maskinen, skal installeren køre med priority=high (ellers springer den spørgsmålene over).</summary>
    public static bool PreseedAsks(InstallSpec s) =>
        s.Hostname.Length == 0 || s.UserName.Length == 0 || s.PasswordHash.Length == 0 || s.Language is null || s.Keyboard is null || s.TimeZone is null || !s.AutoDisk;

    public static string Preseed(InstallSpec s, string mirrorHost = "deb.debian.org")
    {
        var sb = new StringBuilder("# Fleetdeck: Debian-installation. Spørgsmål, der ikke står her, stilles på maskinen.\n");
        void D(string line) => sb.Append("d-i ").Append(line).Append('\n');
        if (s.Language is { } l)
        {
            D($"debian-installer/locale string {l.Linux}");
            D($"debian-installer/language string {l.Id.Split('-')[0]}");
            D($"debian-installer/country string {l.Id.Split('-')[1]}");
        }
        if (s.Keyboard is { } k)
        {
            D($"keyboard-configuration/xkb-keymap select {k.Xkb}");
            D($"keyboard-configuration/layoutcode string {k.Xkb}");
            if (k.XkbVariant is { } v) D($"keyboard-configuration/variantcode string {v}");
        }
        D("netcfg/choose_interface select auto");
        if (s.Hostname.Length > 0) { D($"netcfg/get_hostname string {s.Hostname}"); D($"netcfg/hostname string {s.Hostname}"); }
        D("netcfg/get_domain string");
        D("mirror/country string manual");
        D($"mirror/http/hostname string {mirrorHost}");
        D("mirror/http/directory string /debian");
        D("mirror/http/proxy string");
        D("passwd/root-login boolean false");   // brugeren får sudo i stedet for en root-adgangskode
        if (s.UserName.Length > 0) { D($"passwd/user-fullname string {Or(s.FullName, s.UserName)}"); D($"passwd/username string {s.UserName}"); }
        if (s.PasswordHash.Length > 0) D($"passwd/user-password-crypted password {s.PasswordHash}");
        D("clock-setup/utc boolean true");
        if (s.TimeZone is { } tz) D($"time/zone string {tz}");
        D("clock-setup/ntp boolean true");
        if (s.AutoDisk)
        {
            D($"partman-auto/method string {(s.Encrypt ? "crypto" : "lvm")}");   // crypto: nøglen skrives på maskinen
            D("partman-auto/choose_recipe select atomic");
            D("partman-auto-lvm/guided_size string max");
            D("partman-lvm/device_remove_lvm boolean true");
            D("partman-md/device_remove_md boolean true");
            D("partman-lvm/confirm boolean true");
            D("partman-lvm/confirm_nooverwrite boolean true");
            if (s.Encrypt) D("partman-auto-crypto/erase_disks boolean false");
            D("partman-partitioning/confirm_write_new_label boolean true");
            D("partman/choose_partition select finish");
            D("partman/confirm boolean true");
            D("partman/confirm_nooverwrite boolean true");
            D("partman-efi/non_efi_system boolean true");
        }
        D("apt-setup/cdrom/set-first boolean false");
        D("apt-setup/non-free-firmware boolean true");
        sb.Append($"tasksel tasksel/first multiselect standard, ssh-server{(s.Desktop ? ", gnome-desktop" : "")}\n");
        D($"pkgsel/include string {string.Join(' ', new[] { "curl", "sudo", "openssh-server" }.Concat(s.Packages).Distinct())}");
        D("pkgsel/upgrade select full-upgrade");
        sb.Append("popularity-contest popularity-contest/participate boolean false\n");
        D("grub-installer/only_debian boolean true");
        D("grub-installer/bootdev string default");
        D("finish-install/reboot_in_progress note");

        var late = new List<string>();
        if (s.SshKey.Length > 0 && s.UserName.Length > 0)
            late.Add($"mkdir -p /target/home/{s.UserName}/.ssh; echo '{s.SshKey}' > /target/home/{s.UserName}/.ssh/authorized_keys; in-target chown -R {s.UserName}:{s.UserName} /home/{s.UserName}/.ssh; chmod 700 /target/home/{s.UserName}/.ssh; chmod 600 /target/home/{s.UserName}/.ssh/authorized_keys");
        if (!s.Admin && s.UserName.Length > 0) late.Add($"in-target deluser {s.UserName} sudo || true");
        late.Add(AgentShell(s, "in-target sh -c"));
        late.Add($"in-target curl -fsS -m 20 -X POST {s.DoneUrl} || true");
        D($"preseed/late_command string {string.Join("; ", late)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Fedora, Rocky Linux, AlmaLinux: kickstart
    /// <param name="family">"fedora", "rocky" eller "alma" (Fedora har Workstation og Server, de andre er servere).</param>
    public static string Kickstart(InstallSpec s, string family, string repoUrl, string? appStreamUrl = null)
    {
        var asks = s.Hostname.Length == 0 || s.UserName.Length == 0 || s.PasswordHash.Length == 0 || s.Language is null || s.Keyboard is null
                   || s.TimeZone is null || !s.AutoDisk || s.Encrypt;
        var sb = new StringBuilder("# Fleetdeck: kickstart. Det, der ikke står her, spørges der om på maskinen.\n");
        sb.Append(asks ? "graphical\n" : "text\n");
        sb.Append($"url --url=\"{repoUrl}\"\n");
        if (appStreamUrl is not null) sb.Append($"repo --name=AppStream --baseurl=\"{appStreamUrl}\"\n");
        if (s.Language is { } l) sb.Append($"lang {l.Linux}\n");
        if (s.Keyboard is { } k) sb.Append($"keyboard --xlayouts='{k.Xkb}{(k.XkbVariant is { } v ? $" ({v})" : "")}'\n");
        if (s.TimeZone is { } tz) sb.Append($"timezone {tz} --utc\n");
        sb.Append($"network --bootproto=dhcp --device=link --activate{(s.Hostname.Length > 0 ? $" --hostname={s.Hostname}" : "")}\n");
        if (s.UserName.Length > 0 && s.PasswordHash.Length > 0)
        {
            sb.Append("rootpw --lock\n");
            sb.Append($"user --name={s.UserName} --gecos=\"{Or(s.FullName, s.UserName)}\" --password={s.PasswordHash} --iscrypted{(s.Admin ? " --groups=wheel" : "")}\n");
            if (s.SshKey.Length > 0) sb.Append($"sshkey --username={s.UserName} \"{s.SshKey}\"\n");
        }
        if (s.AutoDisk)
        {
            sb.Append("zerombr\nclearpart --all --initlabel\n");
            sb.Append($"autopart --type=lvm{(s.Encrypt ? " --encrypted" : "")}\n");   // uden --passphrase: nøglen skrives på maskinen
        }
        sb.Append("bootloader\nservices --enabled=sshd\nreboot\n\n%packages\n");
        sb.Append(family == "fedora" && s.Desktop ? "@^workstation-product-environment\n" : "@^server-product-environment\n");
        foreach (var p in new[] { "curl", "openssh-server" }.Concat(s.Packages).Distinct()) sb.Append(p).Append('\n');
        sb.Append("%end\n\n%post\n");
        sb.Append(AgentShell(s, "sh -c")).Append('\n');
        sb.Append($"curl -fsS -m 20 -X POST {s.DoneUrl} || true\n%end\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Proxmox VE: answer.toml
    /// <summary>Proxmox kan ikke spørge under en automatisk installation: alt skal være udfyldt (se <see cref="OsEntry.Blank"/>).</summary>
    public static string ProxmoxAnswer(InstallSpec s, string disk = "sda", string? firstBootUrl = null)
    {
        if (s.Hostname.Length == 0 || s.PasswordHash.Length == 0 || s.Keyboard is null || s.Language is null || s.TimeZone is null)
            throw new ArgumentException("Proxmox skal have navn, adgangskode, sprog, tastatur og tidszone");
        var fqdn = s.Hostname.Contains('.') ? s.Hostname : s.Hostname + ".localdomain";
        var sb = new StringBuilder("# Fleetdeck: Proxmox VE (proxmox-auto-install-assistant)\n[global]\n");
        sb.Append($"keyboard = \"{ProxmoxKeyboard(s.Keyboard.Id)}\"\n");
        sb.Append($"country = \"{s.Language.Id.Split('-')[1].ToLowerInvariant()}\"\n");
        sb.Append($"fqdn = \"{fqdn}\"\nmailto = \"root@localhost\"\ntimezone = \"{s.TimeZone}\"\n");
        sb.Append($"root-password-hashed = \"{s.PasswordHash}\"\n");
        if (s.SshKey.Length > 0) sb.Append($"root-ssh-keys = [\"{s.SshKey}\"]\n");
        sb.Append("reboot-on-error = false\n\n[network]\nsource = \"from-dhcp\"\n\n[disk-setup]\nfilesystem = \"ext4\"\n");
        sb.Append($"disk-list = [\"{disk}\"]\n");
        if (firstBootUrl is not null) sb.Append($"\n[first-boot]\nsource = \"from-url\"\nordering = \"fully-up\"\nurl = \"{firstBootUrl}\"\n");
        return sb.ToString();
    }

    /// <summary>Proxmox' egne tastaturnavne.</summary>
    public static string ProxmoxKeyboard(string id) => id switch
    {
        "us" or "us-intl" => "en-us", "gb" => "en-gb", "ch" => "de-ch", "be" => "fr-be", _ => id,
    };

    // ------------------------------------------------------------------ Windows: autounattend.xml
    private static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>
    /// Svarfilen til Windows 10/11/Server. Fuldt udfyldt giver den det samme som provision/usb/autounattend-pxe.xml (afprøvet),
    /// tomme felter udelades, så Windows spørger på maskinen.
    /// </summary>
    public static string Autounattend(InstallSpec s, OsEntry os, int? imageIndex = null)
    {
        var client = !os.IsWindowsServer;
        XElement Comp(string name, params object[] content) => new(U + "component",
            new XAttribute("name", name), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"), content);
        XElement E(string name, params object?[] content) => new(U + name, content);
        XElement Add(string name, params object?[] content) => new(U + name, new XAttribute(Wcm + "action", "add"), content);
        XElement? Intl(string name, bool winPe)
        {
            if (s.Language is null && s.Keyboard is null) return null;
            var lang = s.Language?.Id;
            return Comp(name,
                winPe && lang is not null ? E("SetupUILanguage", E("UILanguage", lang)) : null,
                s.Keyboard is { } k ? E("InputLocale", k.Windows) : null,
                lang is not null ? new object[] { E("SystemLocale", lang), E("UILanguage", lang), E("UserLocale", lang) } : null);
        }

        // windowsPE: sprog, disk, udgave og nøgle
        var disk = s.AutoDisk ? E("DiskConfiguration",
            Add("Disk", E("DiskID", 0), E("WillWipeDisk", "true"),
                E("CreatePartitions",
                    Add("CreatePartition", E("Order", 1), E("Type", "EFI"), E("Size", 300)),
                    Add("CreatePartition", E("Order", 2), E("Type", "MSR"), E("Size", 16)),
                    Add("CreatePartition", E("Order", 3), E("Type", "Primary"), E("Extend", "true"))),
                E("ModifyPartitions",
                    Add("ModifyPartition", E("Order", 1), E("PartitionID", 1), E("Format", "FAT32"), E("Label", "System")),
                    Add("ModifyPartition", E("Order", 2), E("PartitionID", 2)),
                    Add("ModifyPartition", E("Order", 3), E("PartitionID", 3), E("Format", "NTFS"), E("Label", "Windows"), E("Letter", "C")))),
            E("WillShowUI", "OnError")) : null;
        var image = s.AutoDisk || imageIndex is not null ? E("ImageInstall", E("OSImage",
            imageIndex is int ix ? E("InstallFrom", Add("MetaData", E("Key", "/IMAGE/INDEX"), E("Value", ix))) : null,
            s.AutoDisk ? E("InstallTo", E("DiskID", 0), E("PartitionID", 3)) : null,
            E("WillShowUI", "OnError"))) : null;
        var setup = Comp("Microsoft-Windows-Setup",
            E("DynamicUpdate", E("Enable", "false"), E("WillShowUI", "Never")),
            disk, image,
            E("UserData", os.WinKey is { } key ? E("ProductKey", E("Key", key), E("WillShowUI", "OnError")) : null, E("AcceptEula", "true")));

        // specialize: navn, tidszone, bruger og Fleetdecks deploy.cmd
        var cmds = new List<string>();
        if (!s.Encrypt) cmds.Add(@"reg.exe add ""HKLM\SYSTEM\CurrentControlSet\Control\BitLocker"" /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
        if (client) cmds.Add(@"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE"" /v BypassNRO /t REG_DWORD /d 1 /f");
        if (s.UserName.Length > 0)
            cmds.Add($"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"$u='{s.UserName}'; New-LocalUser -Name $u -FullName $u -NoPassword -AccountNeverExpires | Out-Null; Set-LocalUser -Name $u -PasswordNeverExpires $true; Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $u\"");
        cmds.Add($"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"$d=Join-Path $env:WINDIR 'Setup\\Scripts'; New-Item -ItemType Directory -Force $d | Out-Null; Set-Content -Path (Join-Path $d 'deploy.cmd') -Encoding Ascii -Value 'set WS_MODE={s.Mode}','set WS_PROFILE={s.ProfileId}','set WS_DEPLOY={s.DeployId}','set WS_USER={s.UserName}'\"");
        var shellSpec = s.Hostname.Length > 0 || s.TimeZone is not null
            ? Comp("Microsoft-Windows-Shell-Setup", s.Hostname.Length > 0 ? E("ComputerName", s.Hostname) : null, s.TimeZone is { } tz ? E("TimeZone", InstallLocale.WindowsZoneOf(tz) ?? "UTC") : null)
            : null;

        // oobeSystem: skærmene, der springes over, automatisk login og WinSetup
        var first = new List<(string Desc, string Cmd)>
        {
            ("Windows Opsætning", "cmd.exe /c for %d in (C D E F G H I J K L M N) do @if exist \"%d:\\WinSetupBoot\\Bootstrap.ps1\" (start \"\" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File \"%d:\\WinSetupBoot\\Bootstrap.ps1\" & exit /b 0)"),
        };
        if (s.Encrypt && client)   // BitLocker med TPM; agenten gemmer gendannelsesnøglen i Fleetdeck
            first.Add(("BitLocker", "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"if ((Get-Tpm).TpmPresent) { Add-BitLockerKeyProtector -MountPoint $env:SystemDrive -RecoveryPasswordProtector | Out-Null; Enable-BitLocker -MountPoint $env:SystemDrive -EncryptionMethod XtsAes256 -UsedSpaceOnly -TpmProtector -SkipHardwareTest }\""));
        var oobeShell = Comp("Microsoft-Windows-Shell-Setup",
            E("OOBE", E("HideEULAPage", "true"), E("HideOEMRegistrationScreen", "true"),
                client ? E("HideOnlineAccountScreens", "true") : null,
                E("HideLocalAccountScreen", client && s.UserName.Length > 0 ? "true" : "false"),
                E("HideWirelessSetupInOOBE", "true"), E("ProtectYourPC", 3)),
            s.UserName.Length > 0 ? E("AutoLogon", E("Enabled", "true"), E("LogonCount", 99), E("Username", s.UserName),
                E("Password", E("Value", ""), E("PlainText", "true"))) : null,
            E("FirstLogonCommands", first.Select((c, i) => Add("SynchronousCommand", E("Order", i + 1), E("Description", c.Desc), E("CommandLine", c.Cmd)))));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(U + "unattend", new XAttribute("xmlns", U.NamespaceName), new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
            E("settings", new XAttribute("pass", "windowsPE"), Intl("Microsoft-Windows-International-Core-WinPE", true), setup),
            E("settings", new XAttribute("pass", "offlineServicing"), Comp("Microsoft-Windows-PnpCustomizationsNonWinPE",
                E("DriverPaths", new XElement(U + "PathAndCredentials", new XAttribute(Wcm + "action", "add"), new XAttribute(Wcm + "keyValue", "1"),
                    E("Path", @"Z:\WinSetup\PXE\drivers"))))),
            E("settings", new XAttribute("pass", "specialize"), shellSpec, Comp("Microsoft-Windows-Deployment",
                E("RunSynchronous", cmds.Select((c, i) => Add("RunSynchronousCommand", E("Order", i + 1), E("Path", c)))))),
            E("settings", new XAttribute("pass", "oobeSystem"), Intl("Microsoft-Windows-International-Core", false), oobeShell)));
        using var w = new Utf8StringWriter();
        doc.Save(w);
        return w.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }

    private sealed class Utf8StringWriter : StringWriter { public override Encoding Encoding => new UTF8Encoding(false); }

    // ------------------------------------------------------------------ fælles
    /// <summary>Fleetdeck-agenten i den nye installation: tilmelder sig med værtsnavnet (også når det blev valgt på maskinen).</summary>
    private static string AgentShell(InstallSpec s, string runner)
    {
        var profile = s.ProfileId is int p ? $" WS_PROFILE={p}" : "";
        var name = s.Hostname.Length > 0 ? s.Hostname : "$(cat /etc/hostname)";
        return $"{runner} 'export WS_NAME={name}{profile} WS_PROVISION=1; curl -fsSL \"{s.InstallUrl}\" | bash' || true";
    }

    private static string Or(string a, string b) => a.Length > 0 ? a : b;
}
