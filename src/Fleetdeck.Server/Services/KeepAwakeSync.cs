using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>"Hold vågen på strøm" (Mac): ændringer fra Fleetdeck og fra Mac'en. Sidste ændring vinder (se <see cref="MacKeepAwake.Accept"/>).</summary>
public static class KeepAwakeSync
{
    public const string Web = "web", Mac = "mac";

    /// <summary>Kun Mac'er har indstillingen.</summary>
    public static bool Applies(Machine m) => m.Platform == Platforms.Mac;

    /// <summary>Ændret i Fleetdeck af en administrator.</summary>
    public static void FromWeb(Machine m, bool on, string user, DateTime nowUtc)
    {
        m.KeepAwakeOnAc = on; m.KeepAwakeAt = nowUtc; m.KeepAwakeSource = Web; m.KeepAwakeBy = user.Length > 64 ? user[..64] : user;
    }

    /// <summary>Ændret på Mac'en (menulinjen/appen), meldt ved check-in. True = gemt (den er nyere end den forrige ændring).</summary>
    public static bool FromMac(Machine m, KeepAwakeChange c, DateTime nowUtc)
    {
        if (!Applies(m) || MacKeepAwake.Accept(m.KeepAwakeAt, c, nowUtc) is not { } at) return false;
        m.KeepAwakeOnAc = c.On; m.KeepAwakeAt = at; m.KeepAwakeSource = Mac; m.KeepAwakeBy = null;
        return true;
    }

    /// <summary>Det, agenten får (i det signerede svar). Null for andet end Mac.</summary>
    public static KeepAwakeSetting? For(Machine m) =>
        Applies(m) ? new KeepAwakeSetting { On = m.KeepAwakeOnAc, At = m.KeepAwakeAt, Source = m.KeepAwakeSource } : null;
}
