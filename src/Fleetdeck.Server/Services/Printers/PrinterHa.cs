using System.Text.Json;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services.Printers;

/// <summary>
/// Printerne som enheder i Home Assistant (MQTT discovery): status, problem, lav på blæk/toner og en %-sensor pr. patron.
/// Ren logik: emner og indhold. Selve afsendelsen sker i HomeAssistantBridge.
/// </summary>
public static class PrinterHa
{
    public const int MaxMarkers = 8;

    public static string StateTopic(string @base, int id) => $"{@base}/printer/{id}/state";

    /// <summary>Alle konfigurationsemner for en printer (også dem, der skal tømmes, når der er færre patroner).</summary>
    public static IEnumerable<string> ConfigTopics(string prefix, string @base, int id)
    {
        yield return $"{prefix}/sensor/{@base}_printer_{id}/status/config";
        yield return $"{prefix}/binary_sensor/{@base}_printer_{id}/problem/config";
        yield return $"{prefix}/binary_sensor/{@base}_printer_{id}/low/config";
        for (var i = 0; i < MaxMarkers; i++) yield return $"{prefix}/sensor/{@base}_printer_{id}/m{i}/config";
    }

    /// <summary>(emne, indhold); tomt indhold fjerner en sensor (patron, der ikke findes mere).</summary>
    public static List<(string Topic, string Payload)> Configs(string prefix, string @base, Printer p, PrinterInfo? info)
    {
        var state = StateTopic(@base, p.Id);
        var device = new Dictionary<string, object?>
        {
            ["identifiers"] = new[] { $"{@base}_printer_{p.Id}" }, ["name"] = p.Name, ["manufacturer"] = "Fleetdeck",
            ["model"] = p.MakeModel.Length > 0 ? p.MakeModel : "Printer",
        };
        if (p.Location.Length > 0) device["suggested_area"] = p.Location;
        var avail = new[] { new { topic = $"{@base}/status" } };
        var slug = Slug(p.Name);
        string Conf(string obj, string name, Dictionary<string, object?> extra)
        {
            var c = new Dictionary<string, object?>
            {
                ["name"] = name, ["unique_id"] = $"{@base}_printer_{p.Id}_{obj}", ["object_id"] = $"{@base}_printer_{slug}_{obj}",
                ["device"] = device, ["availability"] = avail, ["state_topic"] = state,
            };
            foreach (var (k, v) in extra) if (v is not null) c[k] = v;
            return JsonSerializer.Serialize(c);
        }
        var list = new List<(string, string)>
        {
            ($"{prefix}/sensor/{@base}_printer_{p.Id}/status/config", Conf("status", "Status", new() { ["value_template"] = "{{ value_json.status }}", ["icon"] = "mdi:printer", ["json_attributes_topic"] = state })),
            ($"{prefix}/binary_sensor/{@base}_printer_{p.Id}/problem/config", Conf("problem", "Problem", new() { ["value_template"] = "{{ value_json.problem }}", ["device_class"] = "problem" })),
            ($"{prefix}/binary_sensor/{@base}_printer_{p.Id}/low/config", Conf("low", "Lav på blæk/toner", new() { ["value_template"] = "{{ value_json.low }}", ["device_class"] = "problem", ["icon"] = "mdi:water-alert" })),
        };
        var markers = info?.Markers ?? [];
        for (var i = 0; i < MaxMarkers; i++)
        {
            var topic = $"{prefix}/sensor/{@base}_printer_{p.Id}/m{i}/config";
            if (i >= markers.Count) { list.Add((topic, "")); continue; }
            var m = markers[i];
            list.Add((topic, Conf($"m{i}", Cap(PrinterStatus.ColorName(m)), new()
            {
                ["value_template"] = $"{{{{ value_json.m{i} }}}}", ["unit_of_measurement"] = "%", ["state_class"] = "measurement",
                ["icon"] = (m.Type ?? "").Contains("toner") ? "mdi:printer-pos" : "mdi:water-percent",
            })));
        }
        return list;
    }

    public static string State(Printer p, PrinterInfo? info)
    {
        var (text, level) = PrinterStatus.Describe(info);
        var d = new Dictionary<string, object?>
        {
            ["status"] = text, ["level"] = level,
            ["problem"] = level == "error" ? "ON" : "OFF",
            ["low"] = info is not null && PrinterStatus.Low(info).Count > 0 ? "ON" : "OFF",
            ["reasons"] = info?.Reasons ?? [], ["uri"] = p.Uri, ["last_check"] = p.LastPolled?.ToUniversalTime().ToString("o"),
        };
        var markers = info?.Markers ?? [];
        for (var i = 0; i < markers.Count && i < MaxMarkers; i++) d[$"m{i}"] = markers[i].Level;
        return JsonSerializer.Serialize(d);
    }

    /// <summary>Hvornår konfigurationen skal sendes igen: navn, model, sted og patronerne.</summary>
    public static string Signature(Printer p, PrinterInfo? info) =>
        $"{p.Name}|{p.MakeModel}|{p.Location}|{string.Join(",", (info?.Markers ?? []).Select(m => m.Name))}";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Slug(string s) => new(s.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());
}
