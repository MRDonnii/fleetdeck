using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Temperaturmåling på Mac (Funktioner → Temperaturmåling): Apple silicon via IOKit's HID-sensorer (<see cref="MacSensors"/>),
/// Intel via SMC'en (<see cref="MacSmc"/>) og ellers Apples egen powermetrics (root). Ingen ekstra programmer eller kerneudvidelser.
/// Slås fra med markørfilen <see cref="MacTemps.OffFile"/> (kun root kan lægge den): så meldes "off" til Fleetdeck.
/// </summary>
public static class MacTempReader
{
    private static readonly object L = new();
    private static (DateTime At, Temperatures T, string? Note)? _last;

    public static bool Enabled => !File.Exists(MacTemps.OffFile);

    /// <summary>Til helbred/check-in: målingen (højst hvert 20. sek.; powermetrics højst hvert 2. min., ingen sensor: igen efter 10 min.).</summary>
    public static Temperatures Current() => Measure().T;

    private static (Temperatures T, string? Note, DateTime At) Measure()
    {
        if (!Mac.Is) return (new Temperatures(), null, DateTime.UtcNow);
        if (!Enabled) return (new Temperatures { Source = MacTemps.Off }, null, DateTime.UtcNow);
        var now = DateTime.UtcNow;
        lock (L)
            if (_last is { } c && now - c.At < (c.T.Source switch { MacTemps.Powermetrics => TimeSpan.FromMinutes(2), MacTemps.None => TimeSpan.FromMinutes(10), _ => TimeSpan.FromSeconds(20) }))
                return (c.T, c.Note, c.At);
        var (t, note) = Read();
        lock (L) _last = (now, t, note);
        return (t, note, now);
    }

    private static (Temperatures, string?) Read()
    {
        var notes = new List<string>();
        try
        {
            var a = MacSensors.Temps();
            if (a.Cpu is not null) return (a, null);
        }
        catch (Exception e) { notes.Add($"sensorer: {e.Message}"); }
        try
        {
            var s = MacSmc.Temps();
            if (s.Cpu is not null) return (s, null);
            if (MacSmc.LastError is { } se) notes.Add(se);
        }
        catch (Exception e) { notes.Add($"SMC: {e.Message}"); }
        if (Environment.UserName == "root" && File.Exists("/usr/bin/powermetrics"))
        {
            var (cpu, gpu) = MacTemps.ParsePowermetrics(Shell.Out("/usr/bin/powermetrics --samplers smc -i 500 -n 1 2>/dev/null", 15));
            if (cpu is not null) return (new Temperatures { Cpu = cpu, Gpu = gpu, Source = MacTemps.Powermetrics }, null);
            notes.Add("powermetrics gav ingen temperatur");
        }
        return (new Temperatures { Source = MacTemps.None }, "ingen af Mac'ens temperatursensorer svarede" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""));
    }

    /// <summary>Til appens Funktioner-vindue (status.json).</summary>
    public static MacTempState State()
    {
        var (t, note, at) = Measure();
        var on = t.Source != MacTemps.Off;
        return new MacTempState
        {
            On = on, Cpu = t.Cpu, Gpu = t.Gpu, At = on ? at : null,
            Source = t.Source is MacTemps.Off or MacTemps.None ? null : t.Source,
            Note = on && t.Cpu is null ? note : null,
        };
    }

    /// <summary>Slå til/fra (ordre fra appen eller hjælperen). Måler straks, så appen ser resultatet med det samme.</summary>
    public static MacTempState Set(bool on)
    {
        if (on) File.Delete(MacTemps.OffFile);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacTemps.OffFile)!);
            File.WriteAllText(MacTemps.OffFile, $"Temperaturmåling slået fra {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        }
        lock (L) _last = null;
        return State();
    }

    /// <summary>"fleetdeck-agent sensors": hver kilde for sig og til sidst "Temperatur: …" (hjælperens script viser den linje).</summary>
    public static IEnumerable<string> Diagnose()
    {
        var smc = MacSmc.ReadAll();
        yield return $"SMC (Intel): {(smc.Count == 0 ? $"ingen ({MacSmc.LastError ?? "ingen nøgler"})" : string.Join(", ", smc.Select(kv => $"{kv.Key}={kv.Value:F1}")))}";
        lock (L) _last = null;
        var s = State();
        yield return $"Temperatur: {MacTemps.Text(s)}";
    }
}

/// <summary>
/// SMC'en på Intel-Mac'er (AppleSMC via IOKit, samme vej som smcFanControl og iStat): læs temperaturnøgler som TC0D (CPU-die) og TG0D (GPU).
/// SMCKeyData_t er 80 bytes: key@0, keyInfo.dataSize@28, keyInfo.dataType@32, result@40, data8@42, bytes@48 (32 bytes).
/// </summary>
public static class MacSmc
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const int Size = 80, KernelIndex = 2, ReadKey = 5, GetKeyInfo = 9;

    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] private static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
    [DllImport(IOKit)] private static extern int IOServiceClose(uint connect);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)] private static extern int IOConnectCallStructMethod(uint connection, uint selector, byte[] input, nuint inputSize, byte[] output, ref nuint outputSize);

    private static bool _broken;
    public static string? LastError { get; private set; }

    public static Temperatures Temps()
    {
        var (cpu, gpu) = MacTemps.FromSmc(ReadAll());
        return cpu is null ? new Temperatures() : new Temperatures { Cpu = cpu, Gpu = gpu, Source = MacTemps.Smc };
    }

    /// <summary>Temperaturnøglerne, der svarer (tom på Apple silicon, hvor nøglerne hedder noget andet, eller hvis SMC'en ikke kan åbnes).</summary>
    public static Dictionary<string, double> ReadAll()
    {
        var values = new Dictionary<string, double>();
        if (!OperatingSystem.IsMacOS() || _broken) return values;
        uint service = 0, conn = 0;
        try
        {
            service = IOServiceGetMatchingService(0, IOServiceMatching("AppleSMC"));   // kIOMainPortDefault = 0; matching-ordbogen forbruges
            if (service == 0) { LastError = "ingen AppleSMC (Apple silicon?)"; return values; }
            var rc = IOServiceOpen(service, TaskSelf(), 0, out conn);
            if (rc != 0) { LastError = $"IOServiceOpen fejlede (0x{rc:x})"; return values; }
            foreach (var key in MacTemps.SmcKeys)
                if (Read(conn, key) is double v) values[key] = v;
            if (values.Count == 0) LastError = "ingen af temperaturnøglerne svarede";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _broken = true; LastError = e.Message; }
        catch (Exception e) { LastError = e.Message; }
        finally
        {
            if (conn != 0) IOServiceClose(conn);
            if (service != 0) IOObjectRelease(service);
        }
        return values;
    }

    private static double? Read(uint conn, string key)
    {
        var input = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0), MacTemps.FourCC(key));
        input[42] = GetKeyInfo;
        if (Call(conn, input) is not { } info) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var type = MacTemps.FourCCText(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32)));
        if (size is 0 or > 32) return null;
        input = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0), MacTemps.FourCC(key));
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(28), size);
        input[42] = ReadKey;
        if (Call(conn, input) is not { } data) return null;
        return MacTemps.DecodeSmc(type, data.AsSpan(48, (int)size));
    }

    private static byte[]? Call(uint conn, byte[] input)
    {
        var output = new byte[Size];
        nuint outSize = Size;
        var rc = IOConnectCallStructMethod(conn, KernelIndex, input, Size, output, ref outSize);
        return rc == 0 && output[40] == 0 ? output : null;   // result ≠ 0: nøglen findes ikke
    }

    /// <summary>mach_task_self() er en makro for den globale variabel mach_task_self_ i libSystem.</summary>
    private static uint TaskSelf()
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(lib, "mach_task_self_"));
    }
}
