using System.Net;
using System.Text.Json;

namespace Fleetdeck.Shared;

/// <summary>
/// Endelige jobresultater, der ikke nåede frem (netværksfejl, serveren genstartede): gemmes atomisk i agentens datamappe
/// og sendes igen ved hvert check-in, til serveren har kvitteret. Serveren tager imod det samme resultat igen uden at gøre noget to gange.
/// Mappen er agentens egen (Windows: ProgramData\Fleetdeck med dens rettigheder; Linux/Mac: kun root, filerne 0600).
/// </summary>
public sealed class PendingResults(string dir)
{
    /// <summary>Ældre end dette smides ud: serveren har for længst opgivet jobbet.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Dir => dir;
    private string FileOf(int jobId) => Path.Combine(dir, $"{jobId}.json");

    /// <summary>
    /// Gem resultatet atomisk på disken FØR det sendes, og slet det først, når serveren har kvitteret – så går det heller ikke tabt,
    /// hvis agenten stopper eller genstarter midt i sendingen. Lykkes det ikke, sendes det igen ved næste check-in.
    /// Returnerer true, hvis serveren tog imod det nu.
    /// </summary>
    public async Task<bool> Send(int jobId, JobResult r, Func<int, JobResult, CancellationToken, Task> send, Action<string> log, CancellationToken ct)
    {
        try { Save(jobId, r); }
        catch (Exception se) when (se is IOException or UnauthorizedAccessException) { log($"Resultatet af job {jobId} kunne ikke gemmes: {se.Message}"); }
        try { await send(jobId, r, ct); }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) { TryDelete(FileOf(jobId)); throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            log($"Resultatet af job {jobId} nåede ikke frem ({e.Message}) – sendes igen ved næste check-in");
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            return false;
        }
        TryDelete(FileOf(jobId));
        return true;
    }

    public void Save(int jobId, JobResult r)
    {
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = FileOf(jobId);
        var tmp = file + ".tmp";
        var fo = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fo.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, fo))
        {
            JsonSerializer.Serialize(fs, r, Json);
            fs.Flush(true);
        }
        File.Move(tmp, file, true);   // atomisk: enten det gamle eller det nye, aldrig en halv fil
    }

    public IReadOnlyList<int> Waiting() => !Directory.Exists(dir) ? []
        : Directory.GetFiles(dir, "*.json").Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? id : 0).Where(id => id > 0).Order().ToList();

    /// <summary>Send gemte resultater igen (ældste først). Stopper ved første netværksfejl; resten venter til næste check-in.</summary>
    public async Task<int> Flush(Func<int, JobResult, CancellationToken, Task> send, Action<string> log, CancellationToken ct)
    {
        var sent = 0;
        foreach (var id in Waiting())
        {
            var file = FileOf(id);
            JobResult? r;
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge) { File.Delete(file); log($"Gammelt resultat af job {id} er smidt ud"); continue; }
                r = JsonSerializer.Deserialize<JobResult>(File.ReadAllText(file), Json);
            }
            catch (JsonException) { r = null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Resultatet af job {id} kunne ikke læses: {e.Message}"); continue; }
            if (r is null) { TryDelete(file); continue; }
            try { await send(id, r, ct); }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                TryDelete(file); log($"Serveren kender ikke job {id} længere – resultatet er smidt ud"); continue;
            }
            catch (Exception e) when (e is HttpRequestException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested)) { return sent; }
            TryDelete(file);
            sent++;
            log($"Resultatet af job {id} er sendt igen");
        }
        return sent;
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
