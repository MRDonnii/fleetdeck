namespace Fleetdeck.Shared;

/// <summary>
/// Status, som agent-tjenesten skriver til ikonet i proceslinjen (userwork\status.json – kan læses af brugerne).
/// </summary>
public sealed class TrayStatus
{
    public string Machine { get; set; } = "";
    public string Server { get; set; } = "";
    public string? WebUrl { get; set; }
    /// <summary>Fleetdeck gennem agenten på http://localhost:porten/ (sikker side til ESP Flasher), mens agenten lytter; ellers null.</summary>
    public string? LocalUrl { get; set; }
    public bool Connected { get; set; }
    /// <summary>Hvorfor der ikke er forbindelse (vises i installationen og bakkeikonet).</summary>
    public string? Error { get; set; }
    /// <summary>Sikkerhedsadvarsel: agenten har afvist usignerede/ugyldige jobs fra serveren (null = alt i orden).</summary>
    public string? Security { get; set; }
    public DateTime? LastContact { get; set; }
    public string? Profile { get; set; }
    public int Upgrades { get; set; }
    public int Alerts { get; set; }
    public string? Health { get; set; }
    public TrayJob? Job { get; set; }
    public DateTime? PausedUntil { get; set; }
    /// <summary>Planlagt genstart/slukning (UTC) – ikonet viser en nedtælling.</summary>
    public DateTime? PowerAt { get; set; }
    public string? PowerMode { get; set; }
    public string Version { get; set; } = "";
    /// <summary>Agenten er tilmeldt (har sit eget token). Nøglen selv står aldrig her – filen kan læses af alle.</summary>
    public bool Enrolled { get; set; }
    /// <summary>"Anmod om adgang": anmodningen, der venter (kode og tilstand – aldrig hemmeligheden). Null = ingen.</summary>
    public TrayAccessRequest? AccessRequest { get; set; }
    public List<ExtensionView> Extensions { get; set; } = [];
    /// <summary>Kun macOS: Fuld diskadgang og agentens signatur (Mac-appen viser "Fuld diskadgang mangler").</summary>
    public MacAccessInfo? MacAccess { get; set; }
    /// <summary>Mac: "Hold vågen på strøm" (indstilling og tilstand lige nu).</summary>
    public KeepAwakeState? KeepAwake { get; set; }
    /// <summary>Mac: temperaturmåling (til/fra og den seneste måling). Null = en ældre tjeneste, der ikke kan slå den til og fra.</summary>
    public MacTempState? Temps { get; set; }
    public DateTime Written { get; set; }
}

public sealed class TrayJob
{
    public string Title { get; set; } = "";
    public int? Percent { get; set; }
    public string Step { get; set; } = "";
}

public sealed class ExtensionView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Gives { get; set; } = "";
    public bool Installed { get; set; }
    public bool Pending { get; set; }
}

/// <summary>En ordre fra ikonet til tjenesten (en fil i en kommando-mappe).</summary>
public sealed class TrayCommand
{
    public string Action { get; set; } = "";   // pause | resume | upgrade_all | sync | extension | keepawake | feature (Mac: Id + On)
    public string? Id { get; set; }
    public int? Minutes { get; set; }
    /// <summary>keepawake og feature: til eller fra.</summary>
    public bool? On { get; set; }
    /// <summary>helpdesk_reply: brugerens tekst, den valgte svarknap og beskeden, der svares på (null = ny sag).</summary>
    public string? Text { get; set; }
    public int? Button { get; set; }
    public int? ReplyTo { get; set; }
    /// <summary>helpdesk_reply: skærmbilledet, ikonet har lagt i brugermappen (kun filnavnet).</summary>
    public string? File { get; set; }
    /// <summary>helpdesk_read: de beskeder, brugeren har set.</summary>
    public List<int>? Ids { get; set; }
}

/// <summary>Agenten beder serveren om noget på maskinens vegne (fra ikonet i proceslinjen).</summary>
public sealed class AgentActionRequest
{
    public string Action { get; set; } = "";   // upgrade_all | sync | extension
    public string? Id { get; set; }
}
