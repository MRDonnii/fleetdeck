namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Hovedløse browsere (Chrome, Chromium, Edge, Firefox, Playwrights headless_shell startet med --headless), fx efterladt af
/// automatisering. Tælles til maskinkortet og kan lukkes med ét klik. Browsere med vindue – også kiosken – røres aldrig.
/// </summary>
public static class HeadlessBrowsers
{
    // hovedprocessen: browserprogram med --headless og uden --type= (børneprocesserne lukker med den)
    private const string Find = "for p in $(pgrep -f -- '--headless' 2>/dev/null); do " +
        "c=$(tr '\\0' ' ' 2>/dev/null < /proc/$p/cmdline); case \"$c\" in *--type=*) continue;; esac; " +
        "case \"$c\" in *chrome*|*chromium*|*msedge*|*firefox*|*headless_shell*) echo $p;; esac; done";

    public static int Count() => Unraid.Is ? 0 : Shell.Out(Find, 10).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    public static ShellResult Close()
    {
        if (Unraid.Is) return new ShellResult(0, "Ingen browsere på Unraid.", false);
        return Shell.Run($"n=0; for p in $({Find}); do kill $p 2>/dev/null && n=$((n+1)); done; sleep 3; " +
                         $"for p in $({Find}); do kill -9 $p 2>/dev/null; done; echo \"Lukkede $n hovedløse browsere.\"", TimeSpan.FromSeconds(60));
    }
}
