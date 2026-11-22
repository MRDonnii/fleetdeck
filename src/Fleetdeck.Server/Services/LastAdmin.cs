using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Der skal altid være mindst én administrator. Sletning og rolleskift kører én ad gangen (<see cref="Gate"/>), så to
/// administratorer ikke kan fjerne hinanden samtidig.
/// </summary>
public static class LastAdmin
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
    public const string Message = "Der skal være mindst én administrator";

    /// <param name="newRole">Den nye rolle, eller null når brugeren slettes.</param>
    public static async Task<bool> WouldRemove(AppDb db, User user, string? newRole) =>
        user.Role == Roles.Admin && newRole != Roles.Admin && !await db.Users.AnyAsync(u => u.Role == Roles.Admin && u.Id != user.Id);
}

/// <summary>Hvor længe en login-session varer uden brug (forlænges ved brug).</summary>
public static class SessionLifetime
{
    public static readonly TimeSpan Days = TimeSpan.FromDays(7);
}
