using System.ComponentModel.DataAnnotations;

namespace Fleetdeck.Server.Data;

/// <summary>Beskeder: én samtale pr. maskine (åben eller lukket). Hører til installationens data – ikke opsætningsfilen.</summary>
public class HelpdeskConversation
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    /// <summary>open | closed. En ny besked (fra begge sider) åbner samtalen igen.</summary>
    [MaxLength(16)] public string Status { get; set; } = HelpdeskStatus.Open;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    /// <summary>Seneste besked på én linje (til oversigten).</summary>
    [MaxLength(200)] public string Preview { get; set; } = "";
    public bool LastFromMachine { get; set; }
    public DateTime? ClosedAt { get; set; }
    [MaxLength(64)] public string? ClosedBy { get; set; }
}

public static class HelpdeskStatus
{
    public const string Open = "open", Closed = "closed";
}

public class HelpdeskMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public HelpdeskConversation? Conversation { get; set; }
    public int MachineId { get; set; }
    /// <summary>Skrevet af brugeren ved maskinen (ellers af en administrator i Fleetdeck).</summary>
    public bool FromMachine { get; set; }
    /// <summary>Administratorens brugernavn eller den indloggede bruger på maskinen.</summary>
    [MaxLength(64)] public string Author { get; set; } = "";
    [MaxLength(4000)] public string Text { get; set; } = "";
    /// <summary>Svarknapper (JSON-liste af HelpdeskButton). Standard "[]".</summary>
    public string ButtonsJson { get; set; } = "[]";
    /// <summary>Den svarknap, brugeren valgte (indeks), og hvornår.</summary>
    public int? Answer { get; set; }
    public DateTime? AnswerAt { get; set; }
    /// <summary>Svaret gælder denne besked.</summary>
    public int? ReplyToId { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>Til maskinen: agenten har modtaget beskeden. Null = venter (maskinen er offline).</summary>
    public DateTime? DeliveredAt { get; set; }
    /// <summary>Til maskinen: brugeren har set den. Fra maskinen: en administrator har læst den.</summary>
    public DateTime? ReadAt { get; set; }
    /// <summary>Maskinens grundoplysninger, sendt med, når brugeren selv starter en sag (navn, OS, oppetid, seneste fejl).</summary>
    [MaxLength(1000)] public string Info { get; set; } = "";
    public bool HasScreenshot { get; set; }
}

/// <summary>Skærmbillede, brugeren selv har vedhæftet (gemt for sig, så beskedlisten er let at hente).</summary>
public class HelpdeskScreenshot
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public HelpdeskMessage? Message { get; set; }
    [MaxLength(32)] public string ContentType { get; set; } = "image/jpeg";
    public byte[] Data { get; set; } = [];
}

/// <summary>Logfiler (zip) fra en maskine: sendt af brugeren i Beskeder (med en besked) eller hentet af admin (jobbet collect_logs).</summary>
public class HelpdeskAttachment
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine? Machine { get; set; }
    /// <summary>Beskeden, logfilerne kom med (null = hentet af admin uden at brugeren gjorde noget).</summary>
    public int? MessageId { get; set; }
    public HelpdeskMessage? Message { get; set; }
    /// <summary>Jobbet, der hentede dem (kun "Hent logfiler").</summary>
    public int? JobId { get; set; }
    [MaxLength(16)] public string Kind { get; set; } = HelpdeskAttachmentKinds.Logs;
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(64)] public string ContentType { get; set; } = "application/zip";
    public long Size { get; set; }
    /// <summary>Filerne i pakken som JSON (navn og størrelse), så de kan vises uden at pakke ud.</summary>
    public string EntriesJson { get; set; } = "[]";
    /// <summary>Hvem: brugeren på maskinen eller administratoren, der hentede dem.</summary>
    [MaxLength(64)] public string By { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public byte[] Data { get; set; } = [];
}

public static class HelpdeskAttachmentKinds
{
    public const string Logs = "logs";
}
