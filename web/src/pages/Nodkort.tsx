import { useEffect, useState, type FormEvent } from 'react';
import { createPortal } from 'react-dom';
import { Alert, Button, Group, PasswordInput, SimpleGrid, Stack, Text } from '@mantine/core';
import { ApiError, CSRF_HEADER, notifyErr, notifyOk, post } from '../api';
import { Panel } from '../components';
import '../design/nodkort.css';

// Svaret fra /api/emergency/reveal. Ligger kun i komponentens tilstand (ingen react-query-cache, intet i localStorage)
// og ryddes, når fanen forlades, når der trykkes "Skjul", eller efter 10 minutter.
interface EmergencyMdm { state: string; supervised?: boolean | null; activationLock?: boolean | null; lastCheckin?: string | null; lockedViaMdm: boolean; profileName: string }
interface EmergencyIpad {
  id: number; name: string; owner?: string | null; kind: string; model?: string | null; serial?: string | null; supervised: boolean;
  locked: 'static' | 'mdm' | 'none'; lockedProfileName: string; removalPassword?: string | null; backupPassword?: string | null; mdm?: EmergencyMdm | null;
}
interface EmergencySheet {
  generated: string; serverUrl: string; dohHost: string; adguardUrl: string; mdmHost: string;
  tunnel: { configured: boolean; container: string; tunnelId?: string | null; machine?: string | null };
  supervision: { available: boolean; fingerprint?: string | null; subject?: string | null; notAfter?: string | null; note: string };
  ipads: EmergencyIpad[];
  /** Husstandens ene backup-adgangskode (alle iPads, Mac og USB). */
  backupPassword?: string | null;
  /** "iPad via USB"s gamle kode fra før sammenlægningen, hvis den er en anden (ældre USB-backupper). */
  previousBackupPassword?: string | null;
}

const HIDE_AFTER_MS = 10 * 60 * 1000;
const MENU = 'Indstillinger → Generelt → VPN og enhedsadministration';

// ---------------------------------------------------------------- kortets indhold (samme model til skærm, print og tekstfil)
interface Row { label: string; value?: string | null; secret?: boolean; blank?: boolean }
interface Section { title: string; lead?: string; rows?: Row[]; steps?: string[]; notes?: string[]; ipad?: boolean }

const dash = (v?: string | null) => (v && v.trim() ? v : '–');
const date = (v?: string | null) => (v ? new Date(v).toLocaleString('da-DK', { dateStyle: 'medium', timeStyle: 'short' }) : '–');
const yesNo = (v?: boolean | null) => (v == null ? 'ukendt' : v ? 'ja' : 'nej');
const MDM_STATES: Record<string, string> = { pending: 'Venter på tilmelding', authenticated: 'Profil installeret – venter på iPad\'en', enrolled: 'Tilmeldt', checked_out: 'Afmeldt' };

function sections(s: EmergencySheet): Section[] {
  const out: Section[] = [{
    title: 'Hvis serveren er nede',
    lead: 'iPad\'ernes internet går gennem hjemmets AdGuard. Er serveren, AdGuard eller tunnelen nede, kan en låst iPad ikke slå adresser op – så virker internettet ikke. Sådan kommer I videre:',
    steps: [
      `Fjern den låste profil med fjernelseskoden: ${MENU} → vælg "Fleetdeck – <navn> (låst …)" → Fjern profil → skriv iPad'ens egen kode → skriv fjernelseskoden fra dette kort. Så bruger iPad'en almindeligt internet igen (uden filter og tidsgrænser).`,
      `Er iPad'en under Apple-styring (MDM): fjern i stedet profilen "Fleetdeck – <navn>" samme sted (Fjern administration). Alt, Fleetdeck har lagt på via Apple-styring – også den låste profil – forsvinder med. Der skal ingen kode til.`,
      `Har iPad'en kun DNS-profilen ("Fleetdeck – sikker DNS"), kan den fjernes samme sted uden kode.`,
      'Skal iPad\'en gendannes: slut den til en Mac (Finder) eller en pc (Apple Devices-appen / iTunes), vælg iPad\'en → "Gendan backup…" eller "Gendan iPad…". En krypteret backup kræver backup-adgangskoden fra dette kort.',
      'Aktiveringslås: efter en nulstilling beder iPad\'en om den Apple-konto, der var logget ind – typisk barnets Apple-id. Hav barnets Apple-id og adgangskode klar (familiens organisator kan nulstille barnets adgangskode under Familiedeling).',
      'Når serveren kører igen: læg den låste profil på igen fra Fleetdeck (iPads → Statisk tilstand), eller tilmeld iPad\'en til Apple-styring igen.',
    ],
  }, {
    title: 'Hjemmets adresser',
    rows: [
      { label: 'Fleetdeck', value: dash(s.serverUrl) },
      { label: 'Offentlig DNS-adresse (krypteret DNS)', value: dash(s.dohHost) },
      { label: 'AdGuard (administration)', value: dash(s.adguardUrl) },
      { label: 'Apple-styring (MDM-adresse)', value: dash(s.mdmHost) },
      { label: 'Cloudflare-tunnel', value: s.tunnel.configured ? `container "${s.tunnel.container}"${s.tunnel.machine ? ` på ${s.tunnel.machine}` : ''}` : 'ikke sat op' },
      { label: 'Tunnel-id', value: dash(s.tunnel.tunnelId) },
    ],
    notes: ['Tunnelens navn og status ses i Cloudflare: Zero Trust → Networks → Tunnels (søg på tunnel-id\'et).'],
  }, {
    title: 'Husstandens backup-adgangskode og overvågningsidentitet',
    rows: [
      s.backupPassword
        ? { label: 'Backup-adgangskode (alle iPads)', value: s.backupPassword, secret: true }
        : { label: 'Backup-adgangskode (alle iPads)', blank: true },
      ...(s.previousBackupPassword ? [{ label: 'Ældre backup-kode (iPad via USB, før sammenlægningen)', value: s.previousBackupPassword, secret: true }] : []),
      ...(s.supervision.available
        ? [{ label: 'Overvågning – fingeraftryk (SHA-256)', value: s.supervision.fingerprint, secret: true }, { label: 'Navn', value: dash(s.supervision.subject) }, { label: 'Udløber', value: date(s.supervision.notAfter) }]
        : [{ label: 'Overvågningsidentitet', value: 'Ikke lavet endnu i Fleetdeck' }, { label: 'Kopi gemt i', blank: true }]),
    ],
    notes: [
      s.backupPassword
        ? 'Backup-adgangskoden er den samme for alle iPads – både via Mac (Apple Configurator) og iPad via USB. Den skal bruges, når en krypteret backup gendannes i Finder eller Apple Devices.'
        : 'Fleetdeck har endnu ingen backup-adgangskode (den laves ved første krypterede backup). Er en backup krypteret i Finder, så skriv den kode på stregen.',
      s.supervision.note,
    ],
  }];

  for (const p of s.ipads) {
    const rows: Row[] = [
      { label: 'Ejer / type', value: `${dash(p.owner)} · ${p.kind === 'family' ? 'familie-iPad' : 'barnets iPad'}` },
      { label: 'Model / serienummer', value: `${dash(p.model)} · ${dash(p.serial)}` },
      { label: 'Overvåget (supervised)', value: yesNo(p.supervised) },
    ];
    if (p.locked === 'mdm') rows.push({ label: 'Låst profil', value: `Lagt på via Apple-styring – fjernes sammen med "${p.mdm?.profileName ?? `Fleetdeck – ${p.name}`}" (ingen kode)` });
    else if (p.locked === 'static') {
      rows.push({ label: 'Låst profil', value: p.lockedProfileName });
      rows.push({ label: 'Kode til fjernelse', value: p.removalPassword ?? 'kan ikke læses – lav en ny kode i Fleetdeck', secret: !!p.removalPassword });
      rows.push({ label: 'Sådan fjernes den', value: `${MENU} → "${p.lockedProfileName}" → Fjern profil → iPad'ens kode → koden ovenfor` });
    } else rows.push({ label: 'Låst profil', value: 'ingen' });
    if (p.backupPassword) rows.push({ label: 'Backup-adgangskode', value: 'husstandens kode (se ovenfor)' });
    else rows.push({ label: 'Backup-adgangskode', blank: true });
    if (p.mdm) {
      rows.push({ label: 'Apple-styring (MDM)', value: `${MDM_STATES[p.mdm.state] ?? p.mdm.state} · sidst set ${date(p.mdm.lastCheckin)} · aktiveringslås ${yesNo(p.mdm.activationLock)}` });
      rows.push({ label: 'Afmeld Apple-styring', value: `${MENU} → "${p.mdm.profileName}" → Fjern administration` });
    } else rows.push({ label: 'Apple-styring (MDM)', value: 'ikke tilmeldt' });
    out.push({
      title: `iPad: ${p.name}`, rows, ipad: true,
      notes: p.backupPassword ? undefined : ['Er iPad\'ens backup krypteret uden for Fleetdeck (Finder → "Kryptér lokal backup"), så skriv den kode på stregen – uden den kan en krypteret backup ikke gendannes.'],
    });
  }
  return out;
}

function SheetView({ s }: { s: EmergencySheet }) {
  return (
    <article className="nodkort">
      <h1>Nødkort – Fleetdeck</h1>
      <p className="nk-lead">Lavet {date(s.generated)}. Kortet indeholder adgangskoder: opbevar det sikkert, og lav et nyt, når koderne ændres.</p>
      {sections(s).map((sec) => (
        <section key={sec.title} className={sec.ipad ? 'nk-ipad' : undefined}>
          <h2>{sec.title}</h2>
          {sec.lead && <p>{sec.lead}</p>}
          {sec.steps && <ol>{sec.steps.map((t) => <li key={t}>{t}</li>)}</ol>}
          {sec.rows && <table><tbody>{sec.rows.map((r) => (
            <tr key={r.label}><th>{r.label}</th><td>{r.blank ? <span className="nk-blank" /> : <span className={r.secret ? 'nk-secret' : undefined}>{r.value}</span>}</td></tr>
          ))}</tbody></table>}
          {sec.notes?.map((n) => <p key={n} className="nk-note">{n}</p>)}
        </section>
      ))}
    </article>
  );
}

function sheetText(s: EmergencySheet): string {
  const l: string[] = ['NØDKORT – FLEETDECK', `Lavet ${date(s.generated)}. Indeholder adgangskoder – gem det i en adgangskodeboks.`, ''];
  for (const sec of sections(s)) {
    l.push(sec.title.toUpperCase(), '-'.repeat(sec.title.length));
    if (sec.lead) l.push(sec.lead);
    sec.steps?.forEach((t, i) => l.push(`${i + 1}. ${t}`));
    sec.rows?.forEach((r) => l.push(`${r.label}: ${r.blank ? '________________' : r.value ?? ''}`));
    sec.notes?.forEach((n) => l.push(`(${n})`));
    l.push('');
  }
  return l.join('\n');
}

function download(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; a.rel = 'noopener';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

const errText = (e: unknown) => (e instanceof ApiError && e.status === 429 ? 'For mange forsøg – vent 10 minutter og prøv igen' : e);

// ---------------------------------------------------------------- fanen
export function NodkortPanel() {
  const [pw, setPw] = useState('');
  const [busy, setBusy] = useState(false);
  const [sheet, setSheet] = useState<EmergencySheet | null>(null);

  // skjul igen efter 10 minutter
  useEffect(() => {
    if (!sheet) return;
    const t = setTimeout(() => setSheet(null), HIDE_AFTER_MS);
    return () => clearTimeout(t);
  }, [sheet]);

  const reveal = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    try { setSheet(await post<EmergencySheet>('/api/emergency/reveal', { password: pw })); }
    catch (err) { notifyErr(errText(err)); }
    finally { setPw(''); setBusy(false); }
  };

  if (!sheet) return (
    <Panel title="Nødkort">
      <Stack gap="sm" maw={560}>
        <Text size="sm">Et udskrivbart kort med alt, I skal bruge, hvis Fleetdeck-serveren er nede: koderne til at fjerne de låste profiler, hvordan iPad'erne afmeldes Apple-styring, hjemmets adresser og trin for trin, hvad I gør.</Text>
        <Text size="sm" c="dimmed">Skriv din adgangskode igen for at vise kortet. Visningen skrives i loggen, og kortet skjules igen efter 10 minutter.</Text>
        <form onSubmit={reveal}>
          <Group align="flex-end" gap="xs">
            <PasswordInput style={{ flex: 1 }} label="Din adgangskode" value={pw} onChange={(e) => setPw(e.currentTarget.value)} autoComplete="current-password" required />
            <Button type="submit" loading={busy} disabled={!pw}>Vis nødkort</Button>
          </Group>
        </form>
      </Stack>
    </Panel>
  );

  const stamp = new Date().toISOString().slice(0, 10);
  return (
    <>
      <Panel title="Nødkort" right={<Group gap="xs">
        <Button onClick={() => window.print()}>Udskriv</Button>
        <Button variant="default" onClick={() => download(new Blob([sheetText(sheet)], { type: 'text/plain;charset=utf-8' }), `nodkort-fleetdeck-${stamp}.txt`)}>Gem som tekst</Button>
        <Button variant="subtle" color="gray" onClick={() => setSheet(null)}>Skjul</Button>
      </Group>}>
        <Alert color="yellow" mb="md">Kortet indeholder adgangskoder. Udskriv det eller gem teksten i en adgangskodeboks – gem ikke tekstfilen løst på computeren.</Alert>
        <SheetView s={sheet} />
      </Panel>
      {sheet.supervision.available && <SupervisionDownload />}
      {createPortal(<div className="nodkort-print"><SheetView s={sheet} /></div>, document.body)}
    </>
  );
}

function SupervisionDownload() {
  const [f, setF] = useState({ password: '', p12: '', p12again: '' });
  const [busy, setBusy] = useState(false);
  const mismatch = f.p12again.length > 0 && f.p12 !== f.p12again;
  const go = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    try {
      const r = await fetch('api/emergency/supervision.p12', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store', headers: { 'Content-Type': 'application/json', ...CSRF_HEADER },
        body: JSON.stringify({ password: f.password, p12Password: f.p12 }),
      });
      if (!r.ok) {
        const t = await r.text();
        let msg = r.status === 429 ? 'For mange forsøg – vent 10 minutter og prøv igen' : t;
        try { msg = (JSON.parse(t) as { detail?: string }).detail ?? msg; } catch { /* ren tekst */ }
        throw new Error(msg || `Fejl ${r.status}`);
      }
      download(await r.blob(), 'overvaagningsidentitet.p12');
      notifyOk('Identiteten er hentet – gem filen og dens adgangskode sikkert');
    } catch (err) { notifyErr(err); }
    finally { setF({ password: '', p12: '', p12again: '' }); setBusy(false); }
  };
  return (
    <Panel title="Hent overvågningsidentiteten (.p12)">
      <form onSubmit={go}>
        <Stack gap="xs" maw={720}>
          <Text size="sm" c="dimmed">Filen krypteres med den adgangskode, du vælger her (mindst 8 tegn). Din egen adgangskode skal skrives igen.</Text>
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <PasswordInput label="Din adgangskode" value={f.password} onChange={(e) => setF({ ...f, password: e.currentTarget.value })} autoComplete="current-password" required />
            <PasswordInput label="Adgangskode til filen" value={f.p12} onChange={(e) => setF({ ...f, p12: e.currentTarget.value })} autoComplete="new-password" required minLength={8} />
            <PasswordInput label="Gentag" value={f.p12again} onChange={(e) => setF({ ...f, p12again: e.currentTarget.value })} autoComplete="new-password" required error={mismatch ? 'Ikke ens' : undefined} />
          </SimpleGrid>
          <Group><Button type="submit" loading={busy} disabled={!f.password || f.p12.length < 8 || f.p12 !== f.p12again}>Hent .p12</Button></Group>
        </Stack>
      </form>
    </Panel>
  );
}
