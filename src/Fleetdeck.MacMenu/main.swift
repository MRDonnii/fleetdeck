// Fleetdeck i menulinjen (macOS): et lille, ægte macOS-program med NSStatusItem.
// Læser tjenestens status (/Library/Application Support/Fleetdeck/status.json) og lægger ordrer som filer i …/cmd,
// præcis som Fleetdeck.app gør. Statusvinduet og opsætningen åbnes i Fleetdeck.app.
// Bygges af CI på en macOS-runner (.github/workflows/release-image.yml, job "macmenu") og følger med i Fleetdeck.app
// (Contents/Resources/menu); agenten bygger den om til FleetdeckMenu.app (bundle-id com.fleetdeck.menu, LSUIElement) i
// /usr/local/fleetdeck/menu og starter den hos brugeren via LaunchAgent'en. Lokalt: swiftc -O main.swift -o FleetdeckMenu
import Cocoa

// Versionsstempel – skal passe med MacMenuStamp.Marker i src/Fleetdeck.Shared/MacApp.cs. Hæv begge, når menuen får nye punkter:
// en menu med et andet stempel regnes for forældet (appen viser så sin egen menu, og agenten fjerner den gamle).
let menuStamp = "fleetdeck-menu-api=3"

let supportDir = "/Library/Application Support/Fleetdeck"
let statusPath = supportDir + "/status.json"
let cmdDir = supportDir + "/cmd"
let appPath = "/Applications/Fleetdeck.app"

final class MenuApp: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem!
    private var timer: Timer?
    private var st: [String: Any] = [:]

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        // fast navn: macOS husker ikonets plads (og om det er skjult) pr. app – og vi beder altid om at blive vist
        item.autosaveName = "com.fleetdeck.menu.status"
        item.isVisible = true
        if let b = item.button {
            // Fleetdeck-logoet som skabelon-ikon (følger lyst/mørkt tema); ellers et standardsymbol.
            // I FleetdeckMenu.app ligger det i Contents/Resources; en løs binær har det ved siden af sig.
            let icon = Bundle.main.path(forResource: "menu-icon@2x", ofType: "png")
                ?? (CommandLine.arguments[0] as NSString).deletingLastPathComponent + "/menu-icon@2x.png"
            var img = NSImage(contentsOfFile: icon)
            img?.size = NSSize(width: 18, height: 18)
            if img == nil { img = NSImage(systemSymbolName: "square.stack.3d.up.fill", accessibilityDescription: "Fleetdeck") }
            img?.isTemplate = true
            b.image = img
            b.imagePosition = .imageLeading
        }
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in self?.refresh() }
    }

    private func read() -> [String: Any] {
        guard let d = FileManager.default.contents(atPath: statusPath),
              let j = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return [:] }
        return j
    }

    private func str(_ k: String) -> String? { (st[k] as? String).flatMap { $0.isEmpty ? nil : $0 } }
    private func num(_ k: String) -> Int { (st[k] as? Int) ?? (st[k] as? Double).map { Int($0) } ?? 0 }
    private func bool(_ k: String) -> Bool { (st[k] as? Bool) ?? false }
    private func date(_ k: String) -> Date? {
        guard let s = str(k) else { return nil }
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: s) ?? ISO8601DateFormatter().date(from: s)
    }

    private func state() -> (NSColor, String) {
        if st.isEmpty { return (.systemRed, "Tjenesten kører ikke") }
        // "Anmod om adgang": venter på administratoren (gul) eller afvist/udløbet (rød)
        if let ar = st["accessRequest"] as? [String: Any] {
            let s = (ar["state"] as? String) ?? "pending"
            let open = ["pending", "approved", "issued"].contains(s)
            return (open ? .systemOrange : .systemRed, (ar["text"] as? String) ?? "Venter på godkendelse")
        }
        if !bool("connected") { return (.systemRed, "Ingen forbindelse til Fleetdeck") }
        if let job = st["job"] as? [String: Any], let t = job["title"] as? String {
            let p = (job["progress"] as? Int).map { " (\($0) %)" } ?? ""
            return (.systemBlue, "Arbejder: \(t)\(p)")
        }
        if let p = date("pausedUntil"), p > Date() { return (.systemOrange, "Sat på pause til \(clock(p))") }
        if let p = date("powerAt"), p > Date() { return (.systemOrange, "Genstart planlagt kl. \(clock(p))") }
        if let sec = str("security") { return (.systemOrange, "Sikkerhed: \(sec)") }
        if num("alerts") > 0 { return (.systemOrange, "\(num("alerts")) alarm(er)") }
        return (.systemGreen, "Forbundet")
    }

    private func clock(_ d: Date) -> String { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f.string(from: d) }

    private func refresh() {
        st = read()
        let (color, line) = state()
        if let b = item.button {
            b.attributedTitle = NSAttributedString(string: " ●", attributes: [.foregroundColor: color, .font: NSFont.systemFont(ofSize: 11)])
            b.toolTip = "Fleetdeck – \(line)"
        }
        let m = NSMenu()
        let machine = str("machine") ?? Host.current().localizedName ?? "Mac"
        m.addItem(disabled("Fleetdeck · \(machine)"))
        m.addItem(disabled(line))
        if num("upgrades") > 0 { m.addItem(disabled("\(num("upgrades")) opdatering(er) venter")) }
        if let p = str("profile") { m.addItem(disabled("Profil: \(p)")) }
        if let k = keepAwakeLine() { m.addItem(disabled(k)) }
        m.addItem(.separator())
        m.addItem(action("Synkronisér nu", #selector(sync)))
        m.addItem(action(num("upgrades") > 0 ? "Opdatér alt nu (\(num("upgrades")))" : "Opdatér alt nu", #selector(upgradeAll)))
        if let p = date("powerAt"), p > Date() { m.addItem(action("Udsæt genstarten", #selector(postpone))) }
        if keepAwake() != nil {   // kun når agenten kan (nyere agent på macOS)
            let ka = action("Hold vågen på strøm (også med lukket låg)", #selector(toggleKeepAwake))
            ka.state = keepAwakeOn() ? .on : .off
            ka.toolTip = "På strøm holdes Mac'en vågen, også med lukket låg, så Fleetdeck kan bruge den. På batteri sover den altid normalt."
            m.addItem(ka)
        }
        m.addItem(.separator())
        m.addItem(action("Status…", #selector(openApp)))
        m.addItem(action("Beskeder…", #selector(openMessages)))
        m.addItem(action("Skriv til support…", #selector(newMessage)))
        m.addItem(action("Opsætning (server, profil, fuld diskadgang)…", #selector(openSetup)))
        m.addItem(action("Åbn Fleetdeck i browseren", #selector(openWeb)))
        m.addItem(action("ESP Flasher", #selector(openEsp)))
        m.addItem(.separator())
        m.addItem(action("Skjul ikonet (til næste login)", #selector(quit)))
        item.menu = m
    }

    private func disabled(_ t: String) -> NSMenuItem { let i = NSMenuItem(title: t, action: nil, keyEquivalent: ""); i.isEnabled = false; return i }
    private func action(_ t: String, _ s: Selector) -> NSMenuItem { let i = NSMenuItem(title: t, action: s, keyEquivalent: ""); i.target = self; return i }

    private func send(_ action: String) { write("{\"action\":\"\(action)\"}") }

    private func write(_ body: String) {
        let path = "\(cmdDir)/\(UUID().uuidString).json"
        FileManager.default.createFile(atPath: path, contents: body.data(using: .utf8))
    }

    // "Hold vågen på strøm": indstillingen og tilstanden fra tjenesten (status.json → keepAwake)
    private func keepAwake() -> [String: Any]? { st["keepAwake"] as? [String: Any] }
    private func keepAwakeOn() -> Bool { (keepAwake()?["setting"] as? Bool) ?? false }
    private func keepAwakeLine() -> String? {
        switch keepAwake()?["state"] as? String {
        case "held": return "Holdes vågen (strøm)"
        case "battery": return "Batteri · sover normalt"
        case "user": return "Dvale slået fra af brugeren (pmset)"
        case "error": return "Hold vågen: fejl – se Fleetdeck"
        default: return nil
        }
    }

    @objc private func toggleKeepAwake() {
        let on = !keepAwakeOn()
        write("{\"action\":\"keepawake\",\"on\":\(on)}")
        // tjenesten tager ordren inden for få sekunder, skriver status.json (fluebenet følger med) og melder det til Fleetdeck
    }

    @objc private func sync() { send("sync") }
    @objc private func upgradeAll() { send("upgrade_all") }
    @objc private func postpone() { send("power_postpone") }
    @objc private func openApp() { runApp([]) }
    // Fleetdeck.app med "setup" – samme opsætningsvindue som på Windows
    @objc private func openSetup() { runApp(["--setup"]) }
    // Beskeder: samtalen med support og "Skriv til support" åbnes i Fleetdeck.app (samme vindue som i Windows)
    @objc private func openMessages() { runApp(["--messages"]) }
    @objc private func newMessage() { runApp(["--new-message"]) }
    /// Åbn Fleetdeck.app gennem macOS (LaunchServices), så vinduet kommer forrest – ikke bag andre vinduer, som når programmet
    /// startes direkte. En ny kopi giver ordren videre til en kørende app og lukker. Mangler appen, siges det tydeligt.
    private func runApp(_ args: [String]) {
        guard FileManager.default.isExecutableFile(atPath: appPath + "/Contents/MacOS/Fleetdeck") else { missingApp(); return }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.arguments = args
        cfg.activates = true
        cfg.createsNewApplicationInstance = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: appPath), configuration: cfg) { _, error in
            guard let error = error else { return }
            DispatchQueue.main.async { self.alert("Fleetdeck kunne ikke åbnes", error.localizedDescription) }
        }
    }
    private func missingApp() {
        send("sync")   // tjenesten lægger appen på igen ved næste runde (hvis serveren har den)
        let hent = (str("webUrl") ?? str("server")).map { ($0.hasSuffix("/") ? String($0.dropLast()) : $0) + "/hent" }
        alert("Fleetdeck.app mangler", "Programmet Fleetdeck findes ikke i Programmer. Fleetdeck-tjenesten lægger det selv på igen inden for nogle minutter"
            + (hent.map { " – eller hent det her: \($0)" } ?? "."))
    }
    private func alert(_ title: String, _ text: String) {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert(); a.messageText = title; a.informativeText = text; a.alertStyle = .warning
        a.addButton(withTitle: "OK")
        a.runModal()
    }
    @objc private func openWeb() {
        if let u = str("webUrl").flatMap(URL.init(string:)) { NSWorkspace.shared.open(u) }
    }
    // gennem agenten på http://localhost (en sikker side uden certifikat), så USB virker i Chrome og Edge
    @objc private func openEsp() {
        let local = str("localUrl").flatMap { $0.hasPrefix("http://") ? $0 : nil }
        guard let base = local ?? str("webUrl") ?? str("server"), base.hasPrefix("http") else { return }
        let trimmed = base.hasSuffix("/") ? String(base.dropLast()) : base
        if let u = URL(string: trimmed + "/esp") { NSWorkspace.shared.open(u) }
    }
    @objc private func quit() { NSApp.terminate(nil) }
}

if CommandLine.arguments.contains("--version") { print(menuStamp); exit(0) }

// "Vis Fleetdeck ved login" (Fleetdeck.app → Status) er slået fra hos brugeren: intet ikon ved login – som appens eget ikon (--tray).
// Samme fil og nøgle som appen: ~/Library/Application Support/Fleetdeck/settings.json, "autostart". Afslut med 0, så launchd ikke
// starter menuen igen, og tjenesten regner den for skjult af brugeren (MacMenuLaunch.Decide), indtil brugeren slår det til igen.
func autostartOff() -> Bool {
    let path = NSHomeDirectory() + "/Library/Application Support/Fleetdeck/settings.json"
    guard let data = FileManager.default.contents(atPath: path),
          let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return false }
    return (obj["autostart"] as? Bool) == false
}
if autostartOff() { exit(0) }

// kun én kopi pr. bruger: en lås, som styresystemet slipper, når processen slutter
let lockPath = "/tmp/fleetdeck-menu-\(getuid()).lock"
let lockFd = open(lockPath, O_CREAT | O_RDWR, 0o600)
if lockFd < 0 || flock(lockFd, LOCK_EX | LOCK_NB) != 0 { exit(0) }

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = MenuApp()
app.delegate = delegate
app.run()
