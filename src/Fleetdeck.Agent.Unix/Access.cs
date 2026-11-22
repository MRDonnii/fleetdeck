using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// "Anmod om adgang" på Linux og Mac: status uden hemmeligheder i en fil, alle kan læse (opsætningen og "fleetdeck-agent status"
/// kører som almindelig bruger), og ventetiden på administratorens godkendelse i tjenesten.
/// </summary>
public static class AccessStatus
{
    /// <summary>macOS: menulinjens statusfil; Linux: /var/lib/fleetdeck/status.json.</summary>
    public static string File => Mac.Is ? MacPaths.StatusFile : "/var/lib/fleetdeck/status.json";

    public static void Write(AgentConfig cfg, TrayAccessRequest? request)
    {
        if (Mac.Is) { MacTray.Access(request, cfg.MachineId > 0 && cfg.Token.Length > 0); return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var s = new TrayStatus
            {
                Machine = Environment.MachineName, Server = cfg.Server, Enrolled = cfg.MachineId > 0 && cfg.Token.Length > 0,
                Connected = cfg.Token.Length > 0, AccessRequest = request, Written = DateTime.UtcNow,
            };
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(s, TrayCommands.Json));
            System.IO.File.SetUnixFileMode(tmp, (UnixFileMode)0b110_100_100);
            System.IO.File.Move(tmp, File, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static TrayStatus? Read()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<TrayStatus>(System.IO.File.ReadAllText(File), TrayCommands.Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static TrayAccessRequest View(AgentConfig cfg, string? state = null, string? reason = null) => cfg.Access is { } a
        ? new TrayAccessRequest { Code = a.Code, Name = a.Name, Since = a.Created, Expires = a.Expires, State = state ?? AccessStates.Pending, Reason = reason }
        : new TrayAccessRequest { State = cfg.AccessState ?? AccessStates.Unknown, Reason = cfg.AccessReason };

    /// <summary>"fleetdeck-agent status": tilmeldt, venter (med kode) eller afvist. Viser aldrig nøgler eller hemmeligheder.</summary>
    public static int Print(bool json)
    {
        AgentConfig? cfg = null;
        try { cfg = AgentConfig.Load(); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var st = Read();
        var enrolled = cfg is not null ? cfg.MachineId > 0 && cfg.Token.Length > 0 : st?.Enrolled == true;
        var ar = cfg is not null && cfg.Token.Length == 0 && (cfg.Access is not null || cfg.AccessState is not null)
            ? View(cfg, st?.AccessRequest?.State, st?.AccessRequest?.Reason) : st?.AccessRequest;
        // "Kan ikke nå serveren": tjenestens seneste fejl (kun mens anmodningen stadig er åben)
        if (ar is not null && cfg?.Access is not null) ar.Problem = st?.AccessRequest?.Problem;
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { enrolled, machineId = cfg?.MachineId, server = cfg?.Server ?? st?.Server, accessRequest = enrolled ? null : ar }, TrayCommands.Json));
            return 0;
        }
        if (enrolled) Console.WriteLine($"Tilmeldt{(cfg is not null ? $" som maskine {cfg.MachineId}" : "")} hos {cfg?.Server ?? st?.Server}.");
        else if (ar is not null) Console.WriteLine(ar.Text);
        else Console.WriteLine(cfg is null && st is null ? "Fleetdeck er ikke installeret." : "Ikke tilmeldt endnu.");
        return enrolled ? 0 : AccessStates.Open(ar?.State ?? "") ? 9 : 1;
    }
}
