using System.Text.Json;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Teksterne i debloat-delen skal passe til det, knapperne faktisk gør.</summary>
public class DebloatTextTests
{
    private static DebloatItem Item(string id) => DebloatCatalog.Find(id)!;

    [Fact]
    public void Every_item_has_its_own_state_words_and_undo_text()
    {
        foreach (var i in DebloatCatalog.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Subject), i.Id);
            var on = DebloatCatalog.StateOn(i); var off = DebloatCatalog.StateOff(i);
            Assert.False(string.IsNullOrWhiteSpace(on), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(off), i.Id);
            Assert.NotEqual(on, off);
            // det gamle fælles ord "slået fra" passede ikke til punkter, der slår noget til
            Assert.NotEqual("slået fra", on);
            Assert.False(string.IsNullOrWhiteSpace(DebloatCatalog.UndoText(i)), i.Id);
        }
    }

    [Fact]
    public void Items_that_turn_something_on_never_say_off()
    {
        string[] enabling = ["Vis ", "Installér ", "Tillad ", "Tænd ", "Brug ", "Slå Wake-on-LAN til", "Foretræk ", "Flyt ", "Lad "];
        foreach (var i in DebloatCatalog.Items.Where(i => enabling.Any(e => i.Title.StartsWith(e, StringComparison.Ordinal))))
            Assert.NotEqual("fra", DebloatCatalog.StateOn(i));
    }

    [Fact]
    public void Removing_items_say_removed_and_explain_store_reinstall()
    {
        foreach (var i in DebloatCatalog.Items.Where(i => i.Apps.Length > 0 && i.Registry.Length == 0))
        {
            Assert.StartsWith("Fjern", i.Title);
            Assert.Equal("fjernet", DebloatCatalog.StateOn(i));
            Assert.Contains("Store", DebloatCatalog.UndoText(i));
        }
        // apps, der ikke længere findes i Store, nævnes ved navn i fortryd-teksten
        var lost = DebloatCatalog.Items.First(i => i.NotReinstallable.Any());
        Assert.Contains(lost.NotReinstallable.First().Title, DebloatCatalog.UndoText(lost));
    }

    [Fact]
    public void Undo_texts_follow_what_the_engine_does()
    {
        Assert.Contains("forbliver slået til", DebloatCatalog.UndoText(DebloatCatalog.Items.Single(i => i.WakeOnLan)));
        Assert.Contains("strømplan", DebloatCatalog.UndoText(Item("power.plan_high")));
        Assert.Contains("kun hvis Fleetdeck installerede", DebloatCatalog.UndoText(Item("features.wsl")));
        Assert.Contains("tidligere starttype", DebloatCatalog.UndoText(Item("telemetry.diagtrack")));
    }

    [Fact]
    public void Light_level_removes_no_apps_as_its_description_promises()
    {
        Assert.Contains("Ingen apps fjernes", DebloatProfiles.Info[DebloatProfiles.Let].Description);
        Assert.DoesNotContain(DebloatCatalog.ForProfile(DebloatProfiles.Let), i => i.Apps.Length > 0 || i.Winget.Length > 0);
        // Anbefalet og Maks fjerner apps og siger det
        foreach (var p in new[] { DebloatProfiles.Anbefalet, DebloatProfiles.Maks })
        {
            Assert.Contains(DebloatCatalog.ForProfile(p), i => i.Apps.Length > 0);
            Assert.Contains("fjernes", DebloatProfiles.Info[p].Description);
        }
    }

    [Fact]
    public void Job_output_and_drift_alert_use_the_items_own_words()
    {
        var plan = DebloatScript.BuildPlan(DebloatActions.Apply, [Item("ux.file_extensions"), Item("telemetry.advertising_id")]);
        Assert.Equal(("Filtypenavne", "vises", "skjult"), (plan.Items[0].Title, plan.Items[0].Label, plan.Items[0].NotLabel));
        Assert.Equal(("Reklame-id", "fra", "til"), (plan.Items[1].Title, plan.Items[1].Label, plan.Items[1].NotLabel));

        var f = DebloatService.DriftFinding(new Fleetdeck.Server.Data.MachineDebloat { Enforce = true, EnforceMode = DebloatEnforce.Alert, Drift = ["ux.file_extensions"] }, DateTime.UtcNow)!;
        Assert.DoesNotContain("slået til igen", f.Title);
        Assert.Equal("Filtypenavne er ændret tilbage", f.Title);
    }

    [Fact]
    public void Catalog_view_carries_state_words_and_undo_text()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(DebloatEndpoints.CatalogView(), Json.Web));
        var it = doc.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "ux.file_extensions");
        Assert.Equal("Filtypenavne", it.GetProperty("subject").GetString());
        Assert.Equal("vises", it.GetProperty("stateOn").GetString());
        Assert.Equal("skjult", it.GetProperty("stateOff").GetString());
        Assert.False(string.IsNullOrEmpty(it.GetProperty("undo").GetString()));
        Assert.True(it.TryGetProperty("needsLogoff", out _));
    }
}
