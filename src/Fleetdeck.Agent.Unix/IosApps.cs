using System.Diagnostics;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// ios_apps: læser de installerede apps på én iPad, der er tilsluttet Mac'en med kabel (cfgutil get installedApps; giver den ingen liste,
/// prøves "get all"). Kun læsning – intet ændres på iPad'en. Bevidst adskilt fra <see cref="Ios"/>.
/// </summary>
public static class IosApps
{
    public static async Task<JobResult> Run(AgentJob j, CancellationToken ct)
    {
        if (!Mac.Is) return Fail("Apps på iPad'en kan kun læses fra en Mac med Apple Configurator.");
        if (Ios.Tool is not { } tool) return Fail("cfgutil blev ikke fundet. Installér Apple Configurator, og vælg Apple Configurator → \"Installér automatiseringsværktøjer\".");
        var ecid = Cfgutil.NormEcid(j.Params.Ecid);
        if (ecid.Length == 0) return Fail("Jobbet mangler iPad'ens ECID – link iPad'en i Fleetdeck først.");
        var timeout = TimeSpan.FromMinutes(Math.Clamp(j.Params.TimeoutMin ?? 5, 1, 30));

        LiveJob.Phase(0, 60, "Læser apps på iPad'en");
        var (json, err) = await Cfg(tool, ecid, timeout, ct, "get", CfgutilApps.Property, "name");
        var apps = json is { } a ? CfgutilApps.Parse(a, ecid) : null;
        var name = json is { } n ? Cfgutil.ParseGet(n, ecid)?.Name : null;
        if (apps is null)
        {
            LiveJob.Phase(60, 95, "Prøver igen med alle oplysninger");
            var (all, err2) = await Cfg(tool, ecid, timeout, ct, "get", "all");
            apps = all is { } b ? CfgutilApps.Parse(b, ecid) : null;
            name ??= all is { } c ? Cfgutil.ParseGet(c, ecid)?.Name : null;
            err = err2 ?? err;
        }
        if (apps is null)
            return Fail("Kunne ikke læse apps på iPad'en: " + (err ?? "cfgutil gav ingen app-liste") +
                        "\nTjek at iPad'en er tilsluttet med kabel, låst op og har trykket \"Stol på\".");
        return new JobResult
        {
            Status = JobStatus.Success, ExitCode = 0,
            Output = $"Fandt {apps.Count} apps på {name ?? "iPad'en"}.",
            IosApps = new IosAppsReport { Ecid = ecid, DeviceName = name, Apps = apps },
        };
    }

    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };

    /// <summary>cfgutil uden skal og uden overvågningsidentitet (kun læsning). Svaret som JSON, eller en fejltekst.</summary>
    private static async Task<(JsonElement? Json, string? Error)> Cfg(string tool, string ecid, TimeSpan timeout, CancellationToken ct, params string[] cmd)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var x in new[] { "--format", "JSON", "-e", ecid }.Concat(cmd)) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        try { p.StandardInput.Close(); } catch (IOException) { }
        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, Shell.JobCancel);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested || Shell.JobCancel.IsCancellationRequested) throw;
            return (null, "tiden løb ud");
        }
        var so = await outTask; var se = await errTask;
        var json = Cfgutil.ExtractJson(so) ?? Cfgutil.ExtractJson(se);
        if (json is { } j && Cfgutil.Error(j) is { } e) return (null, e.Message + (e.Code is int c ? $" (kode {c})" : ""));
        if (json is null)
        {
            var last = string.Join(" ", (se.Length > 0 ? se : so).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
            return (null, last.Length > 0 ? last : $"cfgutil sluttede med kode {p.ExitCode}");
        }
        return (json, null);
    }
}
