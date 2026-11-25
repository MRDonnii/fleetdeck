using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Tests;

public sealed class OfflineAlertTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly SettingsService _s;

    public OfflineAlertTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _s = new SettingsService(_db);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task Offline_warning_is_off_by_default_and_uses_days_when_on()
    {
        Assert.Null(await HealthService.OfflineDaysFor(_s));
        await _s.Set("alert_offline_enabled", "1");
        Assert.Equal(1, await HealthService.OfflineDaysFor(_s));
        await _s.Set("alert_offline_days", "5");
        Assert.Equal(5, await HealthService.OfflineDaysFor(_s));
        await _s.Set("alert_offline_days", "0");
        Assert.Equal(1, await HealthService.OfflineDaysFor(_s));
        await _s.Set("alert_offline_enabled", "0");
        Assert.Null(await HealthService.OfflineDaysFor(_s));
    }
}
