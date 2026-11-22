using System.Net.Sockets;
using System.Text.Json;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Home Assistant via MQTT-discovery: hver maskine bliver en enhed med sensorer (online, opdateringer, alarmer, helbred,
/// CPU, RAM, temperatur, kørende job, planlagt genstart) og knapper (genstart, opdatér alt, tjek helbred, vække, annullér genstart).
/// Slås til under Indstillinger (MQTT-broker). Knapperne opretter almindelige jobs med kilden "Home Assistant".
/// </summary>
public sealed class HomeAssistantBridge(IServiceScopeFactory scopes, ILogger<HomeAssistantBridge> log) : BackgroundService
{
    /// <summary>Til Indstillinger: "Forbundet til …", "Fejl: …" eller "Slået fra".</summary>
    public static string Status { get; private set; } = "Slået fra";

    /// <summary>Beskeder til mobilen: lægges i kø her og sendes til MQTT-emnet fleetdeck/notify (Home Assistant sender dem som push).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Outbox = new();
    public static void Notify(string title, string message, string kind)
    {
        if (Outbox.Count > 50) Outbox.TryDequeue(out _);
        Outbox.Enqueue(JsonSerializer.Serialize(new { title, message, kind, ts = DateTime.UtcNow }));
    }

    private const string Base = "fleetdeck";
    private readonly Dictionary<int, string> _lastState = [];
    private readonly Dictionary<int, string> _announced = [];   // id → visningsnavn (omdøbning annonceres igen)
    private readonly Dictionary<int, string> _checkState = [];
    private readonly Dictionary<int, string> _checkAnnounced = [];   // id → navn (navneskift annonceres igen)
    private readonly Dictionary<int, string> _printerState = [];
    private readonly Dictionary<int, string> _printerAnnounced = [];   // id → signatur (navn, model, patroner)

    private sealed record Conf(string Host, int Port, string User, string Pass, string Prefix)
    {
        public bool On => Host.Length > 0;
    }

    private async Task<Conf> ReadConf()
    {
        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var host = (await s.Get("mqtt_host")).Trim();
        var port = (int)await s.GetNum("mqtt_port", 1883);
        var prefix = (await s.Get("mqtt_prefix")).Trim();
        return new(host, port <= 0 ? 1883 : port, (await s.Get("mqtt_user")).Trim(), await s.Get("mqtt_pass"), prefix.Length > 0 ? prefix : "homeassistant");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var delay = TimeSpan.FromSeconds(10);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var conf = await ReadConf();
                if (!conf.On) { Status = "Slået fra"; await Task.Delay(TimeSpan.FromSeconds(30), stop); continue; }
                await using var mq = await MqttConnection.Connect(conf.Host, conf.Port, $"fleetdeck-{Environment.MachineName.ToLowerInvariant()}",
                    conf.User, conf.Pass, $"{Base}/status", "offline", stop);
                Status = $"Forbundet til {conf.Host}:{conf.Port}";
                log.LogInformation("Home Assistant: forbundet til MQTT {Host}:{Port}", conf.Host, conf.Port);
                delay = TimeSpan.FromSeconds(10);
                _lastState.Clear(); _announced.Clear(); _checkState.Clear(); _checkAnnounced.Clear(); _printerState.Clear(); _printerAnnounced.Clear();
                await mq.Publish($"{Base}/status", "online", true, stop);
                await mq.Subscribe($"{Base}/+/cmd", stop);
                await mq.Subscribe($"{conf.Prefix}/status", stop);   // HA genstartet: send discovery igen
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var reader = ReadCommands(mq, conf, session.Token);
                var pinged = DateTime.UtcNow;
                while (!session.IsCancellationRequested && !reader.IsCompleted)
                {
                    if (await ReadConf() != conf) break;   // indstillingerne er ændret: forbind igen
                    await PublishAll(mq, conf, session.Token);
                    while (Outbox.TryPeek(out var msg)) { await mq.Publish($"{Base}/notify", msg, false, session.Token); Outbox.TryDequeue(out _); }
                    if (DateTime.UtcNow - pinged > TimeSpan.FromSeconds(40)) { await mq.Ping(session.Token); pinged = DateTime.UtcNow; }
                    await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(10), session.Token));
                }
                await mq.Publish($"{Base}/status", "offline", true, CancellationToken.None);
                session.Cancel();
                try { await reader; } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                // Alle fejl (også fx en DB-fejl i PublishAll) giver genforbindelse – ellers stopper hele serveren
                Status = $"Fejl: {e.Message}";
                log.LogWarning("Home Assistant (MQTT): {Msg}", e.Message);
                await Task.Delay(delay, stop);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task ReadCommands(MqttConnection mq, Conf conf, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (topic, payload) = await mq.Receive(ct);
            if (topic == $"{conf.Prefix}/status") { if (payload == "online") { _announced.Clear(); _checkAnnounced.Clear(); _printerAnnounced.Clear(); } continue; }
            var parts = topic.Split('/');
            if (parts.Length == 3 && int.TryParse(parts[1], out var id)) await Command(id, payload.Trim(), ct);
        }
    }

    private async Task Command(int machineId, string action, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var m = await db.Machines.Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == machineId, ct);
        if (m is null) return;
        const string by = "Home Assistant", src = "homeassistant";
        switch (action)
        {
            case "reboot":
                // en BIOS-opdatering, der ligger klar, installeres ved næste genstart: den genstart vælger ejeren i Fleetdeck
                if (await Bios.BiosUpdateService.Waiting(db, m.Id))
                {
                    await audit.Log(by, "ha.reboot.skipped", $"{m.Label}: genstart sprunget over, fordi en BIOS-opdatering ligger klar", m.Id);
                    break;
                }
                await jobs.Create(m.Id, JobTypes.Reboot, "Genstart (Home Assistant)", new JobParams { Delay = 300, Message = "Fleetdeck: pc'en genstarter om 5 minutter." }, RunAs.System, null, src, by);
                break;
            case "cancel_power":
                await jobs.Create(m.Id, JobTypes.CancelPower, "Annullér genstart/slukning (Home Assistant)", new JobParams(), RunAs.System, null, src, by);
                break;
            case "upgrade_all":
                await jobs.Create(m.Id, JobTypes.Upgrade, "Opdatér alle programmer (Home Assistant)", new JobParams { Exclude = m.Profile?.UpgradeExclude ?? [] }, RunAs.System, null, src, by);
                break;
            case "health":
                await jobs.Create(m.Id, JobTypes.Health, "Tjek helbred (Home Assistant)", new JobParams(), RunAs.System, null, src, by);
                break;
            case "wake":
                await WakeOnLan.Send(m.Mac, m.Ip);
                break;
            default:
                return;
        }
        await audit.Log(by, "homeassistant.command", $"{m.Label}: {action}");
        log.LogInformation("Home Assistant: {Action} på {Name}", action, m.Label);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task PublishAll(MqttConnection mq, Conf conf, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var views = scope.ServiceProvider.GetRequiredService<MachineViews>();
        var machines = await db.Machines.AsNoTracking().Include(x => x.Profile).ToListAsync(ct);
        var poll = await views.Poll();
        var ids = machines.Select(m => m.Id).ToHashSet();
        foreach (var gone in _announced.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            foreach (var (comp, obj) in Entities) await mq.Publish($"{conf.Prefix}/{comp}/{Base}_{gone}/{obj}/config", "", true, ct);
            _announced.Remove(gone); _lastState.Remove(gone);
        }
        foreach (var m in machines)
        {
            var row = await views.Row(m, poll);
            if (_announced.GetValueOrDefault(m.Id) != m.Label) { _announced[m.Id] = m.Label; await Announce(mq, conf, m, row, ct); }
            var h = Fleetdeck.Server.Services.Json.Des<HealthReport>(m.HealthJson);
            var power = row["power"] is { } pw ? JsonSerializer.SerializeToElement(pw, Json) : (JsonElement?)null;
            var running = row["running"] is { } rn ? JsonSerializer.SerializeToElement(rn, Json) : (JsonElement?)null;
            var state = JsonSerializer.Serialize(new
            {
                online = (bool)row["online"]! ? "ON" : "OFF",
                remote = (bool)row["online"]! && h.Live.RemoteActive ? "ON" : "OFF",
                upgrades = row["upgrades"], alerts = row["alerts"], health = m.HealthScore, cpu = h.Live.Cpu, ram = h.Live.Ram,
                temp = h.Live.Temps.Cpu is double t ? Math.Round(t, 1) : (double?)null,
                job = running?.GetProperty("title").GetString() ?? "Ingen",
                power_at = power?.GetProperty("at").GetDateTime().ToUniversalTime().ToString("o"),
                last_seen = m.LastSeen?.ToUniversalTime().ToString("o"),
            }, Json);
            if (_lastState.GetValueOrDefault(m.Id) == state) continue;
            _lastState[m.Id] = state;
            await mq.Publish($"{Base}/{m.Id}/state", state, true, ct);
        }
        await PublishChecks(mq, conf, db, ct);
        await PublishPrinters(mq, conf, db, ct);
        await PublishSpareCards(mq, conf, db, ct);
    }

    private readonly Dictionary<int, string> _spareState = [];
    /// <summary>Reservekort (Raspberry Pi): status, alder, seneste kopi og "for gammelt" på Pi'ens egen enhed.</summary>
    private async Task PublishSpareCards(MqttConnection mq, Conf conf, AppDb db, CancellationToken ct)
    {
        var cards = await db.SpareCards.AsNoTracking().Include(s => s.Machine).Where(s => s.ConfirmedAt != null).ToListAsync(ct);
        var ids = cards.Select(s => s.MachineId).ToHashSet();
        foreach (var gone in _spareState.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            foreach (var t in SpareCardHa.ConfigTopics(conf.Prefix, Base, gone)) await mq.Publish(t, "", true, ct);
            _spareState.Remove(gone);
        }
        foreach (var s in cards)
        {
            if (!_spareState.ContainsKey(s.MachineId))
                foreach (var (t, payload) in SpareCardHa.Configs(conf.Prefix, Base, s.MachineId, Slug(s.Machine?.Name ?? s.MachineId.ToString()))) await mq.Publish(t, payload, true, ct);
            var state = SpareCardHa.State(s, DateTime.UtcNow);
            if (_spareState.GetValueOrDefault(s.MachineId) == state) continue;
            _spareState[s.MachineId] = state;
            await mq.Publish(SpareCardHa.StateTopic(Base, s.MachineId), state, true, ct);
        }
    }

    /// <summary>Printerne: én enhed pr. printer med status, problem, lav på blæk/toner og en %-sensor pr. patron.</summary>
    private async Task PublishPrinters(MqttConnection mq, Conf conf, AppDb db, CancellationToken ct)
    {
        var printers = await db.Printers.AsNoTracking().ToListAsync(ct);
        var ids = printers.Select(p => p.Id).ToHashSet();
        foreach (var gone in _printerAnnounced.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            foreach (var t in Printers.PrinterHa.ConfigTopics(conf.Prefix, Base, gone)) await mq.Publish(t, "", true, ct);
            _printerAnnounced.Remove(gone); _printerState.Remove(gone);
        }
        foreach (var p in printers)
        {
            var info = Printers.PrinterService.Info(p);
            var sig = Printers.PrinterHa.Signature(p, info);
            if (_printerAnnounced.GetValueOrDefault(p.Id) != sig)
            {
                _printerAnnounced[p.Id] = sig;
                foreach (var (t, payload) in Printers.PrinterHa.Configs(conf.Prefix, Base, p, info)) await mq.Publish(t, payload, true, ct);
                _printerState.Remove(p.Id);
            }
            var state = Printers.PrinterHa.State(p, info);
            if (_printerState.GetValueOrDefault(p.Id) == state) continue;
            _printerState[p.Id] = state;
            await mq.Publish(Printers.PrinterHa.StateTopic(Base, p.Id), state, true, ct);
        }
    }

    /// <summary>Overvågningen: én enhed "Fleetdeck Overvågning" med en forbindelses-sensor og en svartid pr. tjek.</summary>
    private async Task PublishChecks(MqttConnection mq, Conf conf, AppDb db, CancellationToken ct)
    {
        var checks = await db.Checks.AsNoTracking().ToListAsync(ct);
        var ids = checks.Select(c => c.Id).ToHashSet();
        foreach (var gone in _checkAnnounced.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            await mq.Publish($"{conf.Prefix}/binary_sensor/{Base}_check_{gone}/up/config", "", true, ct);
            await mq.Publish($"{conf.Prefix}/sensor/{Base}_check_{gone}/ms/config", "", true, ct);
            _checkAnnounced.Remove(gone); _checkState.Remove(gone);
        }
        var device = new { identifiers = new[] { $"{Base}_checks" }, name = "Fleetdeck Overvågning", manufacturer = "Fleetdeck", model = "Overvågning" };
        var avail = new[] { new { topic = $"{Base}/status" } };
        foreach (var c in checks)
        {
            var topic = $"{Base}/check/{c.Id}/state";
            if (_checkAnnounced.GetValueOrDefault(c.Id) != c.Name)
            {
                _checkAnnounced[c.Id] = c.Name;
                await mq.Publish($"{conf.Prefix}/binary_sensor/{Base}_check_{c.Id}/up/config", JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["name"] = c.Name, ["unique_id"] = $"{Base}_check_{c.Id}_up", ["object_id"] = $"{Base}_overvaagning_{Slug(c.Name)}",
                    ["device"] = device, ["availability"] = avail, ["state_topic"] = topic, ["value_template"] = "{{ value_json.up }}",
                    ["device_class"] = "connectivity", ["json_attributes_topic"] = topic,
                }), true, ct);
                if (c.Type is CheckTypes.Container or CheckTypes.Push) await mq.Publish($"{conf.Prefix}/sensor/{Base}_check_{c.Id}/ms/config", "", true, ct);   // ingen svartid
                else await mq.Publish($"{conf.Prefix}/sensor/{Base}_check_{c.Id}/ms/config", JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["name"] = $"{c.Name} svartid", ["unique_id"] = $"{Base}_check_{c.Id}_ms", ["object_id"] = $"{Base}_overvaagning_{Slug(c.Name)}_svartid",
                    ["device"] = device, ["availability"] = avail, ["state_topic"] = topic, ["value_template"] = "{{ value_json.ms }}",
                    ["unit_of_measurement"] = "ms", ["state_class"] = "measurement", ["icon"] = "mdi:timer-outline",
                }), true, ct);
            }
            var state = JsonSerializer.Serialize(new
            {
                up = !c.Enabled || c.State != CheckStates.Down ? "ON" : "OFF",
                ms = c.LastMs, status = c.Enabled ? c.State : CheckStates.Paused, message = c.LastMessage,
                last_check = c.LastCheck?.ToUniversalTime().ToString("o"), cert_expires = c.CertExpires?.ToUniversalTime().ToString("o"),
            }, Json);
            if (_checkState.GetValueOrDefault(c.Id) == state) continue;
            _checkState[c.Id] = state;
            await mq.Publish(topic, state, true, ct);
        }
    }



    // komponent, objekt-id – samme liste ved oprettelse og fjernelse
    private static readonly (string Comp, string Obj)[] Entities =
    [
        ("binary_sensor", "online"), ("binary_sensor", "remote"), ("sensor", "upgrades"), ("sensor", "alerts"), ("sensor", "health"), ("sensor", "cpu"), ("sensor", "ram"),
        ("sensor", "temp"), ("sensor", "job"), ("sensor", "power_at"), ("sensor", "last_seen"),
        ("button", "reboot"), ("button", "cancel_power"), ("button", "upgrade_all"), ("button", "health_check"), ("button", "wake"),
    ];

    private static async Task Announce(MqttConnection mq, Conf conf, Machine m, Dictionary<string, object?> row, CancellationToken ct)
    {
        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { $"{Base}_{m.Id}" }, ["name"] = m.Label, ["manufacturer"] = "Fleetdeck",
            ["model"] = row["os"] as string ?? m.Platform,
        };
        if (m.AgentVersion is { Length: > 0 } av) device["sw_version"] = av;
        var avail = new[] { new { topic = $"{Base}/status" } };
        var state = $"{Base}/{m.Id}/state";
        var cmd = $"{Base}/{m.Id}/cmd";
        async Task Pub(string comp, string obj, string name, Dictionary<string, object?> extra)
        {
            var c = new Dictionary<string, object?>
            {
                ["name"] = name, ["unique_id"] = $"{Base}_{m.Id}_{obj}", ["object_id"] = $"{Base}_{Slug(m.Name)}_{obj}",
                ["device"] = device, ["availability"] = avail,
            };
            foreach (var (k, v) in extra) if (v is not null) c[k] = v;   // HA vil ikke have null-felter
            await mq.Publish($"{conf.Prefix}/{comp}/{Base}_{m.Id}/{obj}/config", JsonSerializer.Serialize(c), true, ct);
        }
        Dictionary<string, object?> Sensor(string key, string? unit = null, string? cls = null, string? icon = null, string? stateClass = null) => new()
        {
            ["state_topic"] = state, ["value_template"] = $"{{{{ value_json.{key} }}}}",
            ["unit_of_measurement"] = unit, ["device_class"] = cls, ["icon"] = icon, ["state_class"] = stateClass,
        };
        Dictionary<string, object?> Button(string press, string icon) => new() { ["command_topic"] = cmd, ["payload_press"] = press, ["icon"] = icon };

        await Pub("binary_sensor", "online", "Online", new() { ["state_topic"] = state, ["value_template"] = "{{ value_json.online }}", ["device_class"] = "connectivity" });
        await Pub("binary_sensor", "remote", "Fjernskrivebord i brug", new() { ["state_topic"] = state, ["value_template"] = "{{ value_json.remote }}", ["icon"] = "mdi:remote-desktop" });
        await Pub("sensor", "upgrades", "Opdateringer", Sensor("upgrades", icon: "mdi:package-up", stateClass: "measurement"));
        await Pub("sensor", "alerts", "Alarmer", Sensor("alerts", icon: "mdi:alert", stateClass: "measurement"));
        await Pub("sensor", "health", "Helbred", Sensor("health", "%", icon: "mdi:heart-pulse", stateClass: "measurement"));
        await Pub("sensor", "cpu", "CPU", Sensor("cpu", "%", icon: "mdi:cpu-64-bit", stateClass: "measurement"));
        await Pub("sensor", "ram", "RAM", Sensor("ram", "%", icon: "mdi:memory", stateClass: "measurement"));
        await Pub("sensor", "temp", "CPU-temperatur", Sensor("temp", "°C", "temperature", stateClass: "measurement"));
        await Pub("sensor", "job", "Kører nu", Sensor("job", icon: "mdi:progress-wrench"));
        await Pub("sensor", "power_at", "Planlagt genstart", Sensor("power_at", cls: "timestamp", icon: "mdi:restart-alert"));
        await Pub("sensor", "last_seen", "Sidst set", Sensor("last_seen", cls: "timestamp"));
        await Pub("button", "reboot", "Genstart om 5 min.", Button("reboot", "mdi:restart"));
        await Pub("button", "cancel_power", "Annullér genstart", Button("cancel_power", "mdi:cancel"));
        await Pub("button", "upgrade_all", "Opdatér alt", Button("upgrade_all", "mdi:update"));
        await Pub("button", "health_check", "Tjek helbred", Button("health", "mdi:stethoscope"));
        await Pub("button", "wake", "Tænd (Wake-on-LAN)", Button("wake", "mdi:power"));
    }

    private static string Slug(string s) => new(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
}
