using System.Runtime.InteropServices;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Temperaturer på Apple silicon. macOS har ingen kommando til det, men IOKit's HID-sensorer (samme kilde som fx Stats og iStat bruger)
/// giver chippens "PMU tdie"-målere (CPU-kernerne) og "NAND" (SSD'en). Læses uden ekstra værktøjer; på Intel-Mac'er findes de ikke.
/// </summary>
public static class MacSensors
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const long TemperatureEvent = 15;

    [DllImport(IOKit)] private static extern IntPtr IOHIDEventSystemClientCreate(IntPtr allocator);
    [DllImport(IOKit)] private static extern int IOHIDEventSystemClientSetMatching(IntPtr client, IntPtr matching);
    [DllImport(IOKit)] private static extern IntPtr IOHIDEventSystemClientCopyServices(IntPtr client);
    [DllImport(IOKit)] private static extern IntPtr IOHIDServiceClientCopyProperty(IntPtr service, IntPtr key);
    [DllImport(IOKit)] private static extern IntPtr IOHIDServiceClientCopyEvent(IntPtr service, long type, int options, long timestamp);
    [DllImport(IOKit)] private static extern double IOHIDEventGetFloatValue(IntPtr ev, int field);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint enc);
    [DllImport(CF)] private static extern IntPtr CFNumberCreate(IntPtr alloc, long type, ref int value);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CF)] private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CF)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CF)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CF)] private static extern nuint CFStringGetTypeID();
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, nint size, uint enc);
    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);

    private static bool _broken;
    public static string? LastError { get; private set; }

    /// <summary>"fleetdeck-agent sensors": alle sensorer og evt. fejl (fejlsøgning).</summary>
    public static IEnumerable<string> Diagnose()
    {
        var all = Read();
        yield return $"sensorer: {all.Count}, fejl: {LastError ?? "ingen"}";
        foreach (var (n, c) in all.Take(40)) yield return $"{n}: {c:F1}";
        var t = Temps();
        yield return $"CPU: {t.Cpu?.ToString("F1") ?? "-"} ({t.Source ?? "-"})";
    }

    /// <summary>Navn → grader for alle temperatur-sensorer (tom liste på Intel, eller hvis API'et ikke svarer).</summary>
    public static List<(string Name, double C)> Read()
    {
        var list = new List<(string, double)>();
        if (!OperatingSystem.IsMacOS() || _broken) return list;
        var refs = new List<IntPtr>();
        try
        {
            var lib = NativeLibrary.Load(CF);
            var keyCb = NativeLibrary.GetExport(lib, "kCFTypeDictionaryKeyCallBacks");
            var valCb = NativeLibrary.GetExport(lib, "kCFTypeDictionaryValueCallBacks");
            var client = IOHIDEventSystemClientCreate(IntPtr.Zero);
            if (client == IntPtr.Zero) { _broken = true; return list; }
            refs.Add(client);
            var dict = CFDictionaryCreateMutable(IntPtr.Zero, 2, keyCb, valCb); refs.Add(dict);
            void Set(string k, int v)
            {
                var key = CFStringCreateWithCString(IntPtr.Zero, k, Utf8); var num = CFNumberCreate(IntPtr.Zero, 3, ref v);   // kCFNumberSInt32Type
                CFDictionarySetValue(dict, key, num); refs.Add(key); refs.Add(num);
            }
            Set("PrimaryUsagePage", 0xff00); Set("PrimaryUsage", 5);   // Apple-leverandørside: temperatursensorer
            IOHIDEventSystemClientSetMatching(client, dict);
            var services = IOHIDEventSystemClientCopyServices(client);
            if (services == IntPtr.Zero) return list;
            refs.Add(services);
            var product = CFStringCreateWithCString(IntPtr.Zero, "Product", Utf8); refs.Add(product);
            var buf = new byte[256];
            for (nint i = 0, n = CFArrayGetCount(services); i < n; i++)
            {
                var s = CFArrayGetValueAtIndex(services, i);
                var nameRef = IOHIDServiceClientCopyProperty(s, product);
                var name = "";
                if (nameRef != IntPtr.Zero)
                {
                    if (CFGetTypeID(nameRef) == CFStringGetTypeID() && CFStringGetCString(nameRef, buf, buf.Length, Utf8))
                        name = System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is var z and >= 0 ? z : buf.Length);
                    CFRelease(nameRef);
                }
                var ev = IOHIDServiceClientCopyEvent(s, TemperatureEvent, 0, 0);
                if (ev == IntPtr.Zero) continue;
                var c = IOHIDEventGetFloatValue(ev, (int)(TemperatureEvent << 16));
                CFRelease(ev);
                if (c is > 0 and < 130) list.Add((name, c));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _broken = true; LastError = e.ToString(); }
        catch (Exception e) { LastError = e.ToString(); }
        finally { for (var i = refs.Count - 1; i >= 0; i--) CFRelease(refs[i]); }
        return list;
    }

    /// <summary>CPU = varmeste kerne-måler (tdie); falder tilbage til tdev, hvis der ingen tdie er.</summary>
    public static Temperatures Temps()
    {
        var all = Read();
        var die = all.Where(x => x.Name.Contains("tdie", StringComparison.OrdinalIgnoreCase)).Select(x => x.C).ToList();
        if (die.Count == 0) die = all.Where(x => x.Name.Contains("tdev", StringComparison.OrdinalIgnoreCase)).Select(x => x.C).ToList();
        return die.Count == 0 ? new Temperatures() : new Temperatures { Cpu = Math.Round(die.Max(), 1), Source = "applesilicon" };
    }
}
