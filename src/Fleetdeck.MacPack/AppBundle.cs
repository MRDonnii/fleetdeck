using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security;
using System.Text;

namespace Fleetdeck.MacPack;

/// <summary>
/// Laver Fleetdeck.app ud fra "dotnet publish" (én selvstændig fil): Contents/Info.plist, PkgInfo, MacOS/Fleetdeck, Resources/Fleetdeck.icns,
/// og forsegler pakken med en ad hoc-signatur (_CodeSignature/CodeResources + signatur i hovedprogrammet), som "codesign --force -s -" ville.
/// </summary>
public static class AppBundle
{
    public sealed record Options(string PublishDir, string OutApp, string Version, string BundleId = "com.fleetdeck.app",
        string Name = "Fleetdeck", string Executable = "Fleetdeck", byte[]? Icns = null, string? MinimumSystem = null, string? MenuDir = null);

    /// <summary>Hvis ikke andet er sagt: det almindelige byg (.NET 10 + Avalonia 12) kræver macOS 12.</summary>
    public const string DefaultMinimumSystem = "12.0";

    public static void Build(Options o)
    {
        if (Directory.Exists(o.OutApp)) Directory.Delete(o.OutApp, true);
        var contents = Path.Combine(o.OutApp, "Contents");
        var macos = Directory.CreateDirectory(Path.Combine(contents, "MacOS")).FullName;
        var res = Directory.CreateDirectory(Path.Combine(contents, "Resources")).FullName;
        var exe = Path.Combine(o.PublishDir, o.Executable);
        if (!File.Exists(exe)) throw new FileNotFoundException("Programmet mangler i publish-mappen", exe);
        // kun hovedprogrammet må ligge i MacOS/ (alt andet ville være "indlejret kode", der skal signeres for sig)
        var extra = Directory.GetFiles(o.PublishDir).Select(Path.GetFileName)
            .Where(f => f != o.Executable && !f!.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase)).ToList();
        if (extra.Count > 0) throw new InvalidOperationException($"Publish-mappen skal kun indeholde ét program (PublishSingleFile + IncludeNativeLibrariesForSelfExtract). Fandt også: {string.Join(", ", extra)}");
        File.Copy(exe, Path.Combine(macos, o.Executable));
        File.WriteAllText(Path.Combine(contents, "PkgInfo"), "APPL????");
        if (o.Icns is not null) File.WriteAllBytes(Path.Combine(res, o.Name + ".icns"), o.Icns);
        // LSMinimumSystemVersion = den macOS, programmet er bygget til (LC_BUILD_VERSION), så Finder siger "kræver macOS X" i stedet
        // for at appen bare ikke starter: 12.0 for .NET 10-bygget, 10.15 for legacy-bygget (.NET 8)
        o = o with { MinimumSystem = MinimumSystemFor(File.ReadAllBytes(exe), o.MinimumSystem) };
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(o), new UTF8Encoding(false));
        if (o.MenuDir is not null) AddMenu(o.MenuDir, res);
        Sign(o.OutApp, o.BundleId, o.Executable);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(macos, o.Executable), (UnixFileMode)0b111_101_101);
        }
    }

    /// <summary>
    /// Den native menu (FleetdeckMenu, bygget og ad hoc-signeret med swiftc af CI på macOS) i Resources/menu – agenten lægger den
    /// i /usr/local/fleetdeck/menu. En menu uden det rigtige versionsstempel afvises, så en gammel binær aldrig kommer med i pakken.
    /// </summary>
    public static void AddMenu(string menuDir, string resourcesDir)
    {
        var src = Path.Combine(menuDir, "FleetdeckMenu");
        if (!File.Exists(src)) throw new FileNotFoundException("FleetdeckMenu mangler i --menu-mappen", src);
        var bin = File.ReadAllBytes(src);
        if (!IsMachO(bin)) throw new InvalidOperationException("FleetdeckMenu er ikke et macOS-program (Mach-O)");
        if (!Fleetdeck.Shared.MacMenuStamp.IsCurrent(bin))
            throw new InvalidOperationException($"FleetdeckMenu er forældet: mangler \"{Fleetdeck.Shared.MacMenuStamp.Marker}\" (byg den igen fra src/Fleetdeck.MacMenu/main.swift)");
        var dst = Directory.CreateDirectory(Path.Combine(resourcesDir, "menu")).FullName;
        File.WriteAllBytes(Path.Combine(dst, "FleetdeckMenu"), bin);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dst, "FleetdeckMenu"), (UnixFileMode)0b111_101_101);
        var icon = Path.Combine(menuDir, "menu-icon@2x.png");
        if (File.Exists(icon)) File.Copy(icon, Path.Combine(dst, "menu-icon@2x.png"), true);
    }

    /// <summary>Den højeste af den ønskede og programmets egen minimums-macOS (fx "12.0"); kan den ikke læses: den ønskede eller 12.0.</summary>
    public static string MinimumSystemFor(byte[] exe, string? wanted)
    {
        var want = Version.TryParse(wanted, out var w) ? w : null;
        var min = MinimumOs(exe);
        var v = (min, want) switch { (null, null) => Version.Parse(DefaultMinimumSystem), (null, _) => want!, (_, null) => min, _ => min > want ? min : want };
        return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
    }

    /// <summary>Minimums-macOS fra LC_BUILD_VERSION (eller LC_VERSION_MIN_MACOSX); en universel binær: den højeste. Null = ukendt.</summary>
    public static Version? MinimumOs(byte[] b)
    {
        if (b.Length < 8) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(b) == 0xCAFEBABE)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
            Version? best = null;
            for (var i = 0; i < n && 8 + 20 * i + 20 <= b.Length; i++)
            {
                var off = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8 + 20 * i + 8));
                if (Thin(b, off) is { } v && (best is null || v > best)) best = v;
            }
            return best;
        }
        return Thin(b, 0);

        static Version? Thin(byte[] b, int off)
        {
            if (off < 0 || off + 32 > b.Length || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off)) != 0xFEEDFACF) return null;
            var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off + 16));
            var p = off + 32;
            for (var i = 0; i < ncmds && p + 16 <= b.Length; i++)
            {
                var cmd = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 4));
                if (cmd == 0x32) return V(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 12)));   // LC_BUILD_VERSION: platform, minos, sdk
                if (cmd == 0x24) return V(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 8)));    // LC_VERSION_MIN_MACOSX: version, sdk
                if (size <= 0) break;
                p += size;
            }
            return null;
        }
        static Version V(uint x) => new((int)(x >> 16), (int)((x >> 8) & 0xFF), (int)(x & 0xFF));
    }

    /// <summary>Mach-O (64-bit) eller en universel (fat) binær.</summary>
    public static bool IsMachO(byte[] b) => b.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b) is 0xFEEDFACF or 0xBEBAFECA;

    public static string InfoPlist(Options o)
    {
        string E(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>CFBundleDevelopmentRegion</key><string>da</string>
            	<key>CFBundleDisplayName</key><string>{E(o.Name)}</string>
            	<key>CFBundleExecutable</key><string>{E(o.Executable)}</string>
            	<key>CFBundleIconFile</key><string>{E(o.Name)}</string>
            	<key>CFBundleIdentifier</key><string>{E(o.BundleId)}</string>
            	<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
            	<key>CFBundleName</key><string>{E(o.Name)}</string>
            	<key>CFBundlePackageType</key><string>APPL</string>
            	<key>CFBundleShortVersionString</key><string>{E(o.Version)}</string>
            	<key>CFBundleVersion</key><string>{E(o.Version)}</string>
            	<key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
            	<key>LSMinimumSystemVersion</key><string>{E(o.MinimumSystem ?? DefaultMinimumSystem)}</string>
            	<key>NSHighResolutionCapable</key><true/>
            	<key>NSSupportsAutomaticGraphicsSwitching</key><true/>
            	<key>NSAppleEventsUsageDescription</key><string>Fleetdeck viser beskeder og beder om administratoradgang til at installere agenten.</string>
            	<key>NSHumanReadableCopyright</key><string>Fleetdeck</string>
            </dict>
            </plist>

            """.Replace("\r\n", "\n");
    }

    /// <summary>Forsegl pakken: hash alle ressourcer i CodeResources, og signér hovedprogrammet med Info.plist og CodeResources bundet ind.</summary>
    public static void Sign(string app, string bundleId, string executable)
    {
        var contents = Path.Combine(app, "Contents");
        var resources = Directory.Exists(Path.Combine(contents, "Resources"))
            ? Directory.GetFiles(Path.Combine(contents, "Resources"), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(contents, f).Replace('\\', '/'))
                .Where(f => !f.EndsWith("/.DS_Store")).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
        var cr = CodeResources(resources.Select(r => (r, File.ReadAllBytes(Path.Combine(contents, r)))));
        var sigDir = Directory.CreateDirectory(Path.Combine(contents, "_CodeSignature")).FullName;
        File.WriteAllBytes(Path.Combine(sigDir, "CodeResources"), cr);
        var info = File.ReadAllBytes(Path.Combine(contents, "Info.plist"));
        MachOSigner.SignFile(Path.Combine(contents, "MacOS", executable), bundleId,
            new MachOSigner.Special(SHA256.HashData(info), SHA256.HashData(cr)));
    }

    /// <summary>_CodeSignature/CodeResources med Apples standardregler (som codesign skriver dem for en .app).</summary>
    public static byte[] CodeResources(IEnumerable<(string Path, byte[] Data)> files)
    {
        var list = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n<dict>\n");
        sb.Append("\t<key>files</key>\n\t<dict>\n");
        foreach (var (p, data) in list)
            sb.Append($"\t\t<key>{SecurityElement.Escape(p)}</key>\n\t\t<data>\n\t\t{Convert.ToBase64String(SHA1.HashData(data))}\n\t\t</data>\n");
        sb.Append("\t</dict>\n\t<key>files2</key>\n\t<dict>\n");
        foreach (var (p, data) in list)
            sb.Append($"\t\t<key>{SecurityElement.Escape(p)}</key>\n\t\t<dict>\n\t\t\t<key>hash</key>\n\t\t\t<data>\n\t\t\t{Convert.ToBase64String(SHA1.HashData(data))}\n\t\t\t</data>\n" +
                      $"\t\t\t<key>hash2</key>\n\t\t\t<data>\n\t\t\t{Convert.ToBase64String(SHA256.HashData(data))}\n\t\t\t</data>\n\t\t</dict>\n");
        sb.Append("\t</dict>\n");
        sb.Append(Rules);
        sb.Append("</dict>\n</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private const string Rules = """
        	<key>rules</key>
        	<dict>
        		<key>^Resources/</key>
        		<true/>
        		<key>^Resources/.*\.lproj/</key>
        		<dict>
        			<key>optional</key>
        			<true/>
        			<key>weight</key>
        			<real>1000</real>
        		</dict>
        		<key>^Resources/.*\.lproj/locversion.plist$</key>
        		<dict>
        			<key>omit</key>
        			<true/>
        			<key>weight</key>
        			<real>1100</real>
        		</dict>
        		<key>^Resources/Base\.lproj/</key>
        		<dict>
        			<key>weight</key>
        			<real>1010</real>
        		</dict>
        		<key>^version.plist$</key>
        		<true/>
        	</dict>
        	<key>rules2</key>
        	<dict>
        		<key>.*\.dSYM($|/)</key>
        		<dict>
        			<key>weight</key>
        			<real>11</real>
        		</dict>
        		<key>^(.*/)?\.DS_Store$</key>
        		<dict>
        			<key>omit</key>
        			<true/>
        			<key>weight</key>
        			<real>2000</real>
        		</dict>
        		<key>^(Frameworks|SharedFrameworks|PlugIns|Plug-ins|XPCServices|Helpers|MacOS|Library/(Automator|Spotlight|LoginItems))/</key>
        		<dict>
        			<key>nested</key>
        			<true/>
        			<key>weight</key>
        			<real>10</real>
        		</dict>
        		<key>^.*</key>
        		<true/>
        		<key>^Info\.plist$</key>
        		<dict>
        			<key>omit</key>
        			<true/>
        			<key>weight</key>
        			<real>20</real>
        		</dict>
        		<key>^PkgInfo$</key>
        		<dict>
        			<key>omit</key>
        			<true/>
        			<key>weight</key>
        			<real>20</real>
        		</dict>
        		<key>^Resources/</key>
        		<dict>
        			<key>weight</key>
        			<real>20</real>
        		</dict>
        		<key>^Resources/.*\.lproj/</key>
        		<dict>
        			<key>optional</key>
        			<true/>
        			<key>weight</key>
        			<real>1000</real>
        		</dict>
        		<key>^Resources/.*\.lproj/locversion.plist$</key>
        		<dict>
        			<key>omit</key>
        			<true/>
        			<key>weight</key>
        			<real>1100</real>
        		</dict>
        		<key>^Resources/Base\.lproj/</key>
        		<dict>
        			<key>weight</key>
        			<real>1010</real>
        		</dict>
        		<key>^[^/]+$</key>
        		<dict>
        			<key>nested</key>
        			<true/>
        			<key>weight</key>
        			<real>10</real>
        		</dict>
        		<key>^embedded\.provisionprofile$</key>
        		<dict>
        			<key>weight</key>
        			<real>20</real>
        		</dict>
        		<key>^version\.plist$</key>
        		<dict>
        			<key>weight</key>
        			<real>20</real>
        		</dict>
        	</dict>

        """;
}

/// <summary>.icns ud fra PNG-billeder (macOS læser PNG direkte i ic07/ic08/… -felterne).</summary>
public static class Icns
{
    /// <summary>Billederne fra en Windows-.ico (Fleetdecks ikon har PNG i alle størrelser).</summary>
    public static Dictionary<int, byte[]> PngsFromIco(byte[] ico)
    {
        var n = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var r = new Dictionary<int, byte[]>();
        for (var i = 0; i < n; i++)
        {
            var e = ico.AsSpan(6 + 16 * i);
            var w = e[0] == 0 ? 256 : e[0];
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            var data = ico.AsSpan(off, size);
            if (data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P') r[w] = data.ToArray();
        }
        return r;
    }

    public static byte[] FromPngs(IReadOnlyDictionary<int, byte[]> pngs)
    {
        // størrelse → type (1x); @2x-udgaverne genbruger det dobbelt så store billede
        var types = new (string Type, int Px)[] { ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic11", 32), ("ic12", 64), ("ic13", 256) };
        using var ms = new MemoryStream();
        ms.Write("icns"u8); ms.Write(new byte[4]);
        foreach (var (type, px) in types)
        {
            if (!pngs.TryGetValue(px, out var png)) continue;
            ms.Write(Encoding.ASCII.GetBytes(type));
            var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)(png.Length + 8)); ms.Write(len);
            ms.Write(png);
        }
        var b = ms.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)b.Length);
        return b;
    }
}
