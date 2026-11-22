using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fleetdeck.Server.Data;

/// <summary>Bruges kun af "dotnet ef" ved oprettelse af migrationer (starter ikke webserveren).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDb>
{
    public AppDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=design-time.db").Options);
}
