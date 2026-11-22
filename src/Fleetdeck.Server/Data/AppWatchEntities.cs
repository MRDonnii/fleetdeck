using System.ComponentModel.DataAnnotations;

namespace Fleetdeck.Server.Data;

/// <summary>
/// En af brugerens egne apps, som Fleetdeck holder øje med (siden Apps): GitHub-projektet, hvor den kører, og hvordan den
/// kørende version læses. Alt skrives ind af brugeren. Felterne øverst kommer med i opsætningsfilen; tilstanden nederst gør ikke.
/// </summary>
public class WatchedApp
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>GitHub-projektet som ejer/navn.</summary>
    [MaxLength(140)] public string Repo { get; set; } = "";
    /// <summary>stable | beta (beta tager også forhåndsudgaver med).</summary>
    [MaxLength(8)] public string Channel { get; set; } = "stable";
    /// <summary>Maskinen, appen kører på (agenten læser versionen og kører Opdatér). Tom = ingen maskine.</summary>
    public int? MachineId { get; set; }
    /// <summary>container | service | program – kun til visning og som standard for Docker-læsning.</summary>
    [MaxLength(16)] public string PlaceKind { get; set; } = "container";
    /// <summary>Containerens eller tjenestens navn.</summary>
    [MaxLength(100)] public string PlaceName { get; set; } = "";
    /// <summary>Hvordan den kørende version læses: http | docker | file | command | none.</summary>
    [MaxLength(16)] public string ReadKind { get; set; } = "none";
    /// <summary>Adressen (http), containeren (docker, tom = PlaceName), stien (file) eller kommandoen (command).</summary>
    [MaxLength(1000)] public string ReadTarget { get; set; } = "";
    /// <summary>JSON-feltet med versionen, fx version eller data.app.version. Tom = find versionsnummeret i svaret.</summary>
    [MaxLength(100)] public string ReadField { get; set; } = "";
    /// <summary>Brugerens egen kommando til at opdatere (job på maskinen). {version} og {tag} udfyldes. Tom = ingen knap.</summary>
    [MaxLength(4000)] public string UpdateCommand { get; set; } = "";
    /// <summary>Opdatér automatisk om natten, når der er en ny version.</summary>
    public bool AutoUpdate { get; set; }
    /// <summary>Aldrig automatisk (fx et anlæg): kun knappen, altid med bekræftelse.</summary>
    public bool NeverAuto { get; set; }
    /// <summary>Besked via de almindelige notifikationer, når der er en ny version (én gang pr. version).</summary>
    public bool Notify { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;

    // ---- tilstand (Fleetdeck skriver selv)
    [MaxLength(64)] public string? Running { get; set; }
    public DateTime? RunningAt { get; set; }
    [MaxLength(300)] public string? ReadError { get; set; }
    /// <summary>Jobbet, der læser versionen på maskinen (fil, kommando, Docker).</summary>
    public int? ReadJobId { get; set; }
    /// <summary>Seneste udgivelser fra GitHub (JSON-liste af ReleaseInfo, nyeste først).</summary>
    public string ReleasesJson { get; set; } = "[]";
    [MaxLength(200)] public string? ETag { get; set; }
    public DateTime? Checked { get; set; }
    [MaxLength(300)] public string? CheckError { get; set; }
    /// <summary>Den version, der senest er sendt besked om.</summary>
    [MaxLength(64)] public string? NotifiedVersion { get; set; }
    /// <summary>Den version, automatisk opdatering senest prøvede (aldrig den samme to gange).</summary>
    [MaxLength(64)] public string? AutoLastVersion { get; set; }
    public int? UpdateJobId { get; set; }
    [MaxLength(64)] public string? UpdateTo { get; set; }
}
