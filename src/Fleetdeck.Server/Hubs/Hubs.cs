using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Hubs;

/// <summary>
/// Browseren lytter her og får besked når noget ændrer sig. Administratorer og læsere er i gruppen <see cref="Staff"/>;
/// forældre kun i <see cref="Family"/> og får derfor kun besked om iPads og jobs (aldrig fremdriften på andre maskiners jobs).
/// </summary>
[Authorize]
public sealed class UiHub : Hub
{
    public const string Staff = "staff", Family = "family";

    public static string[] GroupsFor(ClaimsPrincipal u) =>
        u.IsInRole(Roles.Admin) || u.IsInRole(Roles.Viewer) ? [Staff] : u.IsInRole(Roles.Parent) ? [Family] : [];

    /// <summary>Hvem der får besked om en ændring af denne slags.</summary>
    public static string[] GroupsForKind(string kind) => kind is "ipads" or "jobs" ? [Staff, Family] : [Staff];

    public override async Task OnConnectedAsync()
    {
        if (Context.User is { } u) foreach (var g in GroupsFor(u)) await Groups.AddToGroupAsync(Context.ConnectionId, g);
        await base.OnConnectedAsync();
    }
}

/// <summary>Agenterne holder en åben forbindelse, så nye jobs udføres med det samme (ingen ventetid på næste check-in).</summary>
public sealed class AgentHub(AppDb db) : Hub
{
    public static string Group(int machineId) => $"m{machineId}";

    public override async Task OnConnectedAsync()
    {
        var m = await AgentAuth.Resolve(db, Context.GetHttpContext()?.Request.Query["access_token"].ToString());
        if (m is null) { Context.Abort(); return; }
        Context.Items["mid"] = m.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(m.Id));
        await base.OnConnectedAsync();
    }
}

public static class AgentAuth
{
    /// <summary>Token-format: "&lt;machineId&gt;.&lt;hemmelighed&gt;". Kun SHA-256 af hemmeligheden gemmes.</summary>
    public static async Task<Machine?> Resolve(AppDb db, string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !int.TryParse(token[..dot], out var id)) return null;
        var hash = Crypto.Sha256(token[(dot + 1)..]);
        // TokenAlias: en godkendt erstatning bruger stadig det token, den fik som ventende maskine
        var list = await db.Machines.Where(x => x.Id == id || x.TokenAlias == id).ToListAsync();
        return list.FirstOrDefault(m => m.TokenHash is { } h && Crypto.FixedEquals(h, hash));
    }
}
