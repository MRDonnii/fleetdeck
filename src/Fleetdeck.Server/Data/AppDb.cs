using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fleetdeck.Server.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<ProfilePackage> ProfilePackages => Set<ProfilePackage>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<SoftwareItem> Software => Set<SoftwareItem>();
    public DbSet<SoftwareIcon> SoftwareIcons => Set<SoftwareIcon>();
    public DbSet<NetworkDrive> NetworkDrives => Set<NetworkDrive>();
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<PrinterAssignment> PrinterAssignments => Set<PrinterAssignment>();
    public DbSet<MachineWingetId> WingetIds => Set<MachineWingetId>();
    public DbSet<UpgradeItem> Upgrades => Set<UpgradeItem>();
    public DbSet<UpgradeMark> UpgradeMarks => Set<UpgradeMark>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Metric> Metrics => Set<Metric>();
    public DbSet<SysLogRow> SysLog => Set<SysLogRow>();
    public DbSet<ChangeLogRow> ChangeLog => Set<ChangeLogRow>();
    public DbSet<DriverItem> Drivers => Set<DriverItem>();
    public DbSet<TimelineMark> Timeline => Set<TimelineMark>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<TerminalHost> TerminalHosts => Set<TerminalHost>();
    public DbSet<TerminalSnippet> TerminalSnippets => Set<TerminalSnippet>();
    public DbSet<TerminalCredential> TerminalCredentials => Set<TerminalCredential>();
    public DbSet<RecoveryKey> RecoveryKeys => Set<RecoveryKey>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<MaintenanceRun> MaintenanceRuns => Set<MaintenanceRun>();
    public DbSet<ServiceCheck> Checks => Set<ServiceCheck>();
    public DbSet<CheckBeat> CheckBeats => Set<CheckBeat>();
    public DbSet<Ipad> Ipads => Set<Ipad>();
    public DbSet<IpadSlot> IpadSlots => Set<IpadSlot>();
    public DbSet<MdmDevice> MdmDevices => Set<MdmDevice>();
    public DbSet<MdmEnrollToken> MdmEnrollTokens => Set<MdmEnrollToken>();
    public DbSet<MdmCommand> MdmCommands => Set<MdmCommand>();
    public DbSet<IpadApp> IpadApps => Set<IpadApp>();
    public DbSet<IpadAppScan> IpadAppScans => Set<IpadAppScan>();
    public DbSet<AppStoreApp> AppStoreApps => Set<AppStoreApp>();
    public DbSet<EnrollCode> EnrollCodes => Set<EnrollCode>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<AccessBlock> AccessBlocks => Set<AccessBlock>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PiCard> PiCards => Set<PiCard>();
    public DbSet<MachineDebloat> MachineDebloats => Set<MachineDebloat>();
    public DbSet<SpareCardState> SpareCards => Set<SpareCardState>();
    public DbSet<HealthDaily> HealthDaily => Set<HealthDaily>();
    public DbSet<HelpdeskConversation> HelpdeskConversations => Set<HelpdeskConversation>();
    public DbSet<HelpdeskMessage> HelpdeskMessages => Set<HelpdeskMessage>();
    public DbSet<HelpdeskScreenshot> HelpdeskScreenshots => Set<HelpdeskScreenshot>();
    public DbSet<HelpdeskAttachment> HelpdeskAttachments => Set<HelpdeskAttachment>();
    public DbSet<WatchedApp> WatchedApps => Set<WatchedApp>();
    public DbSet<MachineBiosStatus> BiosStatus => Set<MachineBiosStatus>();
    public DbSet<BiosPackage> BiosPackages => Set<BiosPackage>();
    public DbSet<BiosSettingsSnapshot> BiosSnapshots => Set<BiosSettingsSnapshot>();
    public DbSet<BiosUpdateRun> BiosUpdateRuns => Set<BiosUpdateRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var listConv = new ValueConverter<List<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            // en tom tekst (fx en ny kolonne med standardværdien "") er en tom liste – ikke en fejl, der vælter hele siden
            v => string.IsNullOrWhiteSpace(v) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
        var listCmp = new ValueComparer<List<string>>(
            (a, c) => (a ?? new()).SequenceEqual(c ?? new()), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())), v => v.ToList());

        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<ApiKey>().HasIndex(k => k.Sha256).IsUnique();
        b.Entity<AuditEntry>().HasIndex(a => a.Ts);
        b.Entity<AuditEntry>().HasIndex(a => a.MachineId);
        b.Entity<RecoveryKey>().HasIndex(k => new { k.MachineId, k.Superseded });
        b.Entity<Machine>().HasIndex(m => m.Name).IsUnique();
        b.Entity<PrinterAssignment>().HasOne(a => a.Printer).WithMany(p => p.Assignments).HasForeignKey(a => a.PrinterId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PrinterAssignment>().HasIndex(a => new { a.PrinterId, a.ProfileId, a.MachineId });
        b.Entity<Machine>().Property(m => m.Tags).HasConversion(listConv, listCmp);
        b.Entity<Profile>().Property(p => p.UpgradeExclude).HasConversion(listConv, listCmp);
        b.Entity<Profile>().Property(p => p.PiExclude).HasConversion(listConv, listCmp);
        b.Entity<Machine>().Property(m => m.UpgradeExclude).HasConversion(listConv, listCmp);
        b.Entity<Profile>().Property(p => p.KeepRunning).HasConversion(listConv, listCmp);
        b.Entity<Profile>().Property(p => p.KitPaths).HasConversion(listConv, listCmp);
        b.Entity<Profile>().Property(p => p.AutoFix).HasConversion(listConv, listCmp)
            .HasDefaultValue(new List<string> { "defender_signatures", "crash_analysis", "retry_failed" });
        b.Entity<Profile>().Property(p => p.MaintTasks).HasConversion(listConv, listCmp)
            .HasDefaultValue(new List<string> { "windows_update", "upgrade" });
        var intListConv = new ValueConverter<List<int>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new List<int>());
        var intListCmp = new ValueComparer<List<int>>(
            (a, c) => (a ?? new()).SequenceEqual(c ?? new()), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)), v => v.ToList());
        b.Entity<MaintenanceRun>().Property(r => r.JobIds).HasConversion(intListConv, intListCmp);
        b.Entity<MaintenanceRun>().Property(r => r.LastLeft).HasConversion(listConv, listCmp);
        b.Entity<MaintenanceRun>().HasIndex(r => new { r.MachineId, r.Finished });
        b.Entity<User>().Property(u => u.Ipads).HasConversion(intListConv, intListCmp);
        b.Entity<User>().Property(u => u.Perms).HasConversion(listConv, listCmp);
        b.Entity<Deployment>().Property(d => d.AskFields).HasConversion(listConv, listCmp);
        b.Entity<User>().Property(u => u.RecoveryCodes).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().Property(i => i.BlockedServices).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().Property(i => i.AllowedDomains).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().Property(i => i.BlockedDomains).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().Property(i => i.HiddenApps).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().Property(i => i.SetupDone).HasConversion(listConv, listCmp);
        b.Entity<Ipad>().HasIndex(i => i.ClientId).IsUnique();
        b.Entity<Ipad>().Property(i => i.DeviceType).HasDefaultValue(DeviceTypes.Ipad);
        b.Entity<IpadSlot>().HasIndex(s => new { s.IpadId, s.Service, s.Slot }).IsUnique();
        b.Entity<HealthDaily>().HasIndex(h => new { h.MachineId, h.Day }).IsUnique();
        b.Entity<MaintenanceRun>().HasIndex(r => new { r.MachineId, r.Created });
        // Apple-styring: rækkerne følger iPad'en (slettes med den)
        b.Entity<MdmDevice>().HasIndex(d => d.EnrollKey).IsUnique();
        b.Entity<MdmDevice>().HasIndex(d => d.Udid);
        b.Entity<MdmDevice>().HasOne<Ipad>().WithOne().HasForeignKey<MdmDevice>(d => d.IpadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MdmEnrollToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<EnrollCode>().HasIndex(t => t.CodeHash).IsUnique();
        b.Entity<AccessRequest>().HasIndex(t => t.SecretHash).IsUnique();
        b.Entity<AccessRequest>().HasIndex(t => new { t.State, t.Ip });
        b.Entity<AccessBlock>().HasIndex(t => new { t.Kind, t.Value });
        b.Entity<MdmEnrollToken>().HasOne<Ipad>().WithMany().HasForeignKey(t => t.IpadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MdmCommand>().HasIndex(c => new { c.IpadId, c.Created });
        b.Entity<MdmCommand>().HasOne<Ipad>().WithMany().HasForeignKey(c => c.IpadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IpadApp>().HasIndex(x => new { x.IpadId, x.BundleId }).IsUnique();
        b.Entity<IpadApp>().HasIndex(x => x.BundleId);
        b.Entity<IpadApp>().HasOne<Ipad>().WithMany().HasForeignKey(x => x.IpadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<IpadAppScan>().HasOne<Ipad>().WithOne().HasForeignKey<IpadAppScan>(x => x.IpadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CheckBeat>().HasIndex(x => new { x.CheckId, x.Ts });
        b.Entity<ServiceCheck>().HasIndex(x => x.PushToken);
        b.Entity<MaintenanceRun>().HasOne(r => r.Machine).WithMany().HasForeignKey(r => r.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProfilePackage>().HasKey(x => new { x.ProfileId, x.PackageId });
        b.Entity<MachineDebloat>().HasOne(x => x.Machine).WithOne().HasForeignKey<MachineDebloat>(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SpareCardState>().HasOne(x => x.Machine).WithOne().HasForeignKey<SpareCardState>(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        // maskiner fra før profilversioner beholder version 1, indtil nogen vælger at opdatere dem
        b.Entity<MachineDebloat>().Property(x => x.ProfileVersion).HasDefaultValue(1);
        b.Entity<MachineDebloat>().Property(x => x.PowerJson).HasDefaultValue("{}");
        foreach (var p in new System.Linq.Expressions.Expression<Func<MachineDebloat, List<string>>>[] { x => x.Include, x => x.Exclude, x => x.Applied, x => x.Drift })
            b.Entity<MachineDebloat>().Property(p).HasConversion(listConv, listCmp).HasDefaultValueSql("'[]'");
        b.Entity<SoftwareIcon>().HasKey(x => x.Hash);
        b.Entity<Machine>().HasOne(m => m.Profile).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<Job>().HasIndex(j => new { j.MachineId, j.Status });
        b.Entity<Job>().HasIndex(j => j.Created);
        b.Entity<Alert>().HasIndex(a => new { a.MachineId, a.Resolved });
        b.Entity<Metric>().HasIndex(m => new { m.MachineId, m.Ts });
        b.Entity<SysLogRow>().HasIndex(x => new { x.MachineId, x.Time });
        b.Entity<SysLogRow>().HasIndex(x => new { x.MachineId, x.Hash }).IsUnique();
        b.Entity<SysLogRow>().HasIndex(x => x.Time);
        b.Entity<ChangeLogRow>().HasIndex(x => new { x.MachineId, x.Time });
        b.Entity<DriverItem>().HasIndex(x => x.MachineId);
        b.Entity<TimelineMark>().HasIndex(x => new { x.MachineId, x.Start });
        // skjulte opdateringer og dem, der ikke kan installeres, tæller ikke med nogen steder (ventende, rapporter, Opdatér alt) –
        // kun listen "Skjulte", maskinens opdateringsliste og "sprunget over" ser dem (IgnoreQueryFilters)
        b.Entity<UpgradeItem>().HasQueryFilter(u => !u.Hidden && u.Blocked == null);
        b.Entity<UpgradeMark>().HasIndex(x => new { x.MachineId, x.WingetId }).IsUnique();
        foreach (var t in new[] { typeof(SoftwareItem), typeof(MachineWingetId), typeof(UpgradeItem), typeof(UpgradeMark), typeof(Metric), typeof(SysLogRow),
            typeof(ChangeLogRow), typeof(DriverItem), typeof(TimelineMark) })
            b.Entity(t).HasOne(typeof(Machine)).WithMany().HasForeignKey("MachineId").OnDelete(DeleteBehavior.Cascade);
        b.Entity<Job>().HasOne(j => j.Machine).WithMany().HasForeignKey(j => j.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Alert>().HasOne(a => a.Machine).WithMany().HasForeignKey(a => a.MachineId).OnDelete(DeleteBehavior.Cascade);
        // Beskeder: én samtale pr. maskine; slettes maskinen, forsvinder samtalen med
        b.Entity<HelpdeskConversation>().HasIndex(c => c.MachineId).IsUnique();
        b.Entity<HelpdeskConversation>().HasIndex(c => new { c.Status, c.LastActivity });
        b.Entity<HelpdeskConversation>().HasOne(c => c.Machine).WithMany().HasForeignKey(c => c.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HelpdeskMessage>().HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HelpdeskMessage>().HasIndex(x => new { x.MachineId, x.FromMachine, x.DeliveredAt });
        b.Entity<HelpdeskMessage>().Property(x => x.ButtonsJson).HasDefaultValue("[]");
        b.Entity<HelpdeskScreenshot>().HasIndex(x => x.MessageId);
        b.Entity<HelpdeskScreenshot>().HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HelpdeskAttachment>().HasIndex(x => new { x.MachineId, x.Created });
        b.Entity<HelpdeskAttachment>().HasIndex(x => x.MessageId);
        b.Entity<HelpdeskAttachment>().HasOne(x => x.Machine).WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HelpdeskAttachment>().HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<HelpdeskAttachment>().Property(x => x.EntriesJson).HasDefaultValue("[]");
        // Apps: maskinen kan slettes, appen bliver (uden maskine)
        b.Entity<WatchedApp>().HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<WatchedApp>().Property(x => x.ReleasesJson).HasDefaultValue("[]");
        // BIOS: status og snapshots følger maskinen; pakkerne i cachen er fælles for alle maskiner af samme model
        b.Entity<MachineBiosStatus>().HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MachineBiosStatus>().Property(x => x.ChangeSummaryJson).HasDefaultValue("[]");
        b.Entity<BiosPackage>().HasIndex(x => new { x.Vendor, x.CatalogKey, x.Version, x.Kind }).IsUnique();
        b.Entity<BiosSettingsSnapshot>().HasIndex(x => new { x.MachineId, x.TakenAt });
        b.Entity<BiosSettingsSnapshot>().HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BiosSettingsSnapshot>().Property(x => x.ItemsJson).HasDefaultValue("[]");
        b.Entity<BiosUpdateRun>().HasIndex(x => new { x.MachineId, x.CreatedAt });
        b.Entity<BiosUpdateRun>().HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BiosUpdateRun>().Property(x => x.ChecksJson).HasDefaultValue("[]");

        // SQLite gemmer DateTime uden tidszone: marker alt som UTC, så JSON får "Z" på.
        var utc = new ValueConverter<DateTime, DateTime>(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcN = new ValueConverter<DateTime?, DateTime?>(v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var e in b.Model.GetEntityTypes())
            foreach (var p in e.GetProperties())
            {
                if (p.ClrType == typeof(DateTime)) p.SetValueConverter(utc);
                else if (p.ClrType == typeof(DateTime?)) p.SetValueConverter(utcN);
            }
    }
}
