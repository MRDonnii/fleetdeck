import { useEffect, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Box, Button, Checkbox, Group, Loader, Modal, MultiSelect, Select, Stack, Switch, Text, TextInput, ThemeIcon, Tooltip,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAlertTriangle, IconCheck, IconCircleCheck, IconPlus, IconPrinter, IconTrash, IconWand } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { del, post, put, useAction, useApi } from '../api';
import { Panel } from '../components';

// ---------------------------------------------------------------- typer (fra /api/printers)

export interface PrinterMarker { name: string; level: number | null; color?: string | null; type?: string | null; colorName: string; low: boolean }
export interface PrinterStatusView {
  text: string; level: 'ok' | 'warn' | 'error' | 'off'; state?: string | null; reasons: string[]; error?: string | null; at?: string | null;
  markers: PrinterMarker[]; low: string[];
}
export interface PrinterAssignmentRow { id: number; profileId: number | null; machineId: number | null; makeDefault: boolean; keep: boolean; label: string; machines: number }
export interface PrinterRow {
  id: number; name: string; uri: string; location: string; makeModel: string; paper: string; duplex: string; color: boolean;
  needsDriver: boolean; driver: string; driverPackageId: number | null; source: string; host: string | null; driverless: boolean;
  status: PrinterStatusView; assignments: PrinterAssignmentRow[]; machines: number;
}
export interface FoundPrinter {
  name: string; host: string; uri: string; via: 'bonjour' | 'scan' | 'agent'; machine?: string | null; makeModel?: string | null;
  driverless?: boolean | null; color?: boolean | null; duplex?: boolean | null; location?: string | null;
  /** bonjour | scan | wsd | installed */
  sources?: string[]; machines?: string[];
}
export interface TargetMachine { id: number; name: string; platform: string; profileId: number | null; profile: string | null; lastSeen: string | null }
export interface TargetProfile { id: number; name: string; machines: number }
export interface PrinterSearch {
  auto: boolean; hours: number; running: boolean; lastRun: string | null; nextRun: string | null; error: string | null; networkOff: boolean;
  subnets: string[]; ownSubnets: string[]; bonjour: number; scan: number; agents: number;
}
export interface PrintersResponse {
  printers: PrinterRow[]; found: FoundPrinter[]; scanning: boolean; lastScan: string | null; subnet: string;
  targets?: { machines: TargetMachine[]; profiles: TargetProfile[] }; search?: PrinterSearch;
}
export interface MachinePrinterRow {
  printerId: number | null; assignmentId: number | null; name: string; uri?: string | null; fromProfile: boolean; makeDefault: boolean; keep: boolean; fleetdeck: boolean;
  state?: { ok: boolean; isDefault: boolean; foreign: boolean; dropped: boolean; error?: string | null; addedAt?: string | null; readdedAt?: string | null } | null;
}

export const PAPER = ['A4', 'A3', 'A5', 'Letter', 'Legal'].map((p) => ({ value: p, label: p }));
export const DUPLEX = [{ value: 'none', label: 'Ingen' }, { value: 'long', label: 'Lang kant' }, { value: 'short', label: 'Kort kant' }];
export const COLOR = [{ value: 'color', label: 'Farve' }, { value: 'mono', label: 'Sort/hvid' }];
const LEVEL_COLOR = { ok: 'teal', warn: 'orange', error: 'red', off: 'gray' } as const;
const VIA = { bonjour: 'fundet via Bonjour (mDNS)', scan: 'fundet via netværksscanning', agent: 'fundet på' } as const;
/** Kilden til et fund: hvordan printeren blev fundet. */
export const SOURCE: Record<string, { label: string; short: string; color: string }> = {
  bonjour: { label: 'Bonjour', short: 'Bonjour', color: 'teal' }, scan: { label: 'Netværksscanning', short: 'Scanning', color: 'gray' },
  wsd: { label: 'WSD', short: 'WSD', color: 'grape' }, installed: { label: 'Installeret på pc', short: 'Installeret', color: 'gray' },
};
const names = (l: string[]) => (l.length <= 2 ? l.join(' og ') : `${l.slice(0, 2).join(', ')} og ${l.length - 2} til`);
/** "set af ARBEJDS-PC og STUE-PC" / "installeret på ARBEJDS-PC" / "serverens net". */
export function foundBy(f: FoundPrinter) {
  const pcs = f.machines?.length ? f.machines : f.machine ? [f.machine] : [];
  const src = f.sources ?? [f.via];
  if (pcs.length) return `${src.length === 1 && src[0] === 'installed' ? 'installeret på' : 'set af'} ${names(pcs)}`;
  return src.includes('scan') && !src.includes('bonjour') ? 'fundet ved scanning af nettet' : 'fundet af serveren';
}
export function SourceBadge({ f }: { f: FoundPrinter }) {
  const s = SOURCE[(f.sources ?? [])[0] ?? (f.via === 'agent' ? 'installed' : f.via)] ?? SOURCE.scan;
  return <Badge size="xs" variant="light" color={s.color}>{s.label}</Badge>;
}

/** "i dag 08.12", "i går 17.40" eller "15. nov. 08.12". */
export function when(iso: string | null | undefined) {
  if (!iso) return '';
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');
  const t = d.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }).replace(':', '.');
  const day = new Date(); day.setHours(0, 0, 0, 0);
  const diff = Math.round((new Date(d).setHours(0, 0, 0, 0) - day.getTime()) / 86400000);
  return diff === 0 ? `i dag ${t}` : diff === -1 ? `i går ${t}` : `${d.toLocaleDateString('da-DK', { day: 'numeric', month: 'short' })} ${t}`;
}

/** Den grønne prik ved navnet (klar / advarsel / fejl / slukket). */
export function StatusDot({ level }: { level: PrinterStatusView['level'] }) {
  return <Box component="span" w={7} h={7} ml={6} style={{ display: 'inline-block', borderRadius: 99, background: `var(--mantine-color-${LEVEL_COLOR[level]}-6)`, verticalAlign: 'middle' }} />;
}

export function DriverBadge({ driverless }: { driverless: boolean | null | undefined }) {
  if (driverless == null) return <Badge size="xs" variant="light" color="gray">Ukendt</Badge>;
  return driverless ? <Badge size="xs" variant="light" color="blue">IPP Everywhere</Badge> : <Badge size="xs" variant="light" color="orange">Kræver driver</Badge>;
}

/** Blæk/toner: én lille bjælke pr. patron (i patronens farve), rød under 15 %. */
export function InkBars({ markers, w = 70 }: { markers: PrinterMarker[]; w?: number }) {
  if (!markers.length) return <Text size="xs" c="dimmed">–</Text>;
  const each = Math.max(10, Math.floor((w - (markers.length - 1) * 3) / markers.length));
  return (
    <Tooltip label={markers.map((m) => `${m.colorName}: ${m.level == null ? 'ukendt' : `${m.level} %`}`).join(' · ')}>
      <Group gap={3} wrap="nowrap">
        {markers.map((m, i) => (
          <Box key={i} w={each} h={6} style={{ borderRadius: 3, background: 'var(--mantine-color-default-border)', overflow: 'hidden' }}>
            <Box h="100%" w={`${m.level ?? 0}%`} style={{ background: m.low ? 'var(--mantine-color-red-6)' : m.color ?? 'var(--mantine-color-teal-6)' }} />
          </Box>
        ))}
      </Group>
    </Tooltip>
  );
}

// ---------------------------------------------------------------- tilføj / ret printer

interface ProfileOpt { id: number; name: string; machines?: number }
interface MachineOpt { id: number; name: string; platform: string }
interface PackageOpt { id: number; name: string }

export interface PrinterDraft {
  id?: number; name: string; uri: string; location: string; paper: string; duplex: string; color: boolean; needsDriver: boolean; driver: string;
  driverPackageId: number | null; makeModel?: string | null; source?: string; driverless?: boolean | null;
}

export const draftFrom = (f: FoundPrinter): PrinterDraft => ({
  name: f.name, uri: f.uri, location: f.location ?? '', paper: 'A4', duplex: f.duplex === false ? 'none' : 'long', color: f.color !== false,
  needsDriver: f.driverless === false, driver: f.via === 'agent' ? f.makeModel ?? '' : '', driverPackageId: null, makeModel: f.makeModel, source: f.via, driverless: f.driverless,
});

export const draftOf = (p: PrinterRow): PrinterDraft => ({
  id: p.id, name: p.name, uri: p.uri, location: p.location, paper: p.paper, duplex: p.duplex, color: p.color, needsDriver: p.needsDriver,
  driver: p.driver, driverPackageId: p.driverPackageId, makeModel: p.makeModel, source: p.source, driverless: !p.needsDriver,
});

export const emptyDraft = (): PrinterDraft => ({ name: '', uri: '', location: '', paper: 'A4', duplex: 'long', color: true, needsDriver: false, driver: '', driverPackageId: null });

/**
 * "Tilføj printer" (og ret): navn, adresse, placering, standarder og hvor den lægges på. Uden driver (IPP Everywhere) som udgangspunkt;
 * "Spørg printeren" henter model og egenskaber med IPP.
 */
export function PrinterModal({ draft, onClose, assignments = [] }: { draft: PrinterDraft | null; onClose: () => void; assignments?: PrinterAssignmentRow[] }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const qc = useQueryClient();
  const [f, setF] = useState<PrinterDraft>(draft ?? emptyDraft());
  const [targets, setTargets] = useState<string[]>([]);
  const [makeDefault, setMakeDefault] = useState(true);
  const [probe, setProbe] = useState<{ status: string; error?: string | null } | null>(null);
  useEffect(() => { setF(draft ?? emptyDraft()); setTargets([]); setProbe(null); setMakeDefault(true); }, [draft]);
  const { data: profiles = [] } = useApi<ProfileOpt[]>(['profiles'], draft ? '/api/profiles' : null);
  const { data: machines = [] } = useApi<MachineOpt[]>(['machines'], draft ? '/api/machines' : null);
  const { data: packages = [] } = useApi<PackageOpt[]>(['packages'], draft && f.needsDriver ? '/api/packages' : null);
  const set = <K extends keyof PrinterDraft>(k: K, v: PrinterDraft[K]) => setF((x) => ({ ...x, [k]: v }));
  const editing = f.id != null;
  const taken = new Set(assignments.map((a) => (a.profileId != null ? `p${a.profileId}` : `m${a.machineId}`)));
  const options = [
    { group: 'Profiler', items: profiles.filter((p) => !taken.has(`p${p.id}`)).map((p) => ({ value: `p${p.id}`, label: `Profilen ${p.name}${p.machines != null ? ` (${p.machines} maskiner)` : ''}` })) },
    { group: 'Maskiner', items: machines.filter((m) => m.platform !== 'ios' && !taken.has(`m${m.id}`)).map((m) => ({ value: `m${m.id}`, label: m.name })) },
  ];
  const ask = useAction(() => post<{ makeModel?: string | null; info?: string | null; location?: string | null; color?: boolean | null; duplex?: boolean | null;
    driverless?: boolean | null; error?: string | null; status: string; uri: string }>('/api/printers/probe', { uri: f.uri }));
  const runProbe = () => ask.mutate(undefined, {
    onSuccess: (r) => {
      setProbe({ status: r.status, error: r.error });
      if (r.error) return;
      setF((x) => ({
        ...x, uri: r.uri, makeModel: r.makeModel ?? x.makeModel, name: x.name || r.info || r.makeModel || x.name, location: x.location || r.location || '',
        color: r.color ?? x.color, duplex: r.duplex === false ? 'none' : x.duplex, driverless: r.driverless, needsDriver: r.driverless === false ? true : x.needsDriver,
      }));
    },
  });
  const body = {
    name: f.name, uri: f.uri, location: f.location, paper: f.paper, duplex: f.duplex, color: f.color, needsDriver: f.needsDriver, driver: f.driver,
    driverPackageId: f.driverPackageId, makeModel: f.makeModel, source: f.source,
    profileIds: targets.filter((t) => t[0] === 'p').map((t) => Number(t.slice(1))), machineIds: targets.filter((t) => t[0] === 'm').map((t) => Number(t.slice(1))), makeDefault,
  };
  const save = useAction(async () => {
    if (!editing) return post('/api/printers', body);
    await put(`/api/printers/${f.id}`, body);
    for (const t of targets) await post('/api/printers/assignments', { printerId: f.id, profileId: t[0] === 'p' ? Number(t.slice(1)) : null, machineId: t[0] === 'm' ? Number(t.slice(1)) : null, makeDefault });
  }, editing ? 'Printeren er gemt' : 'Printeren er tilføjet');
  const unassign = useAction((id: number) => del(`/api/printers/assignments/${id}`), 'Printeren fjernes fra maskinerne igen');
  const done = () => { qc.invalidateQueries(); onClose(); };
  return (
    <Modal opened={draft != null} onClose={onClose} title={editing ? 'Ret printer' : 'Tilføj printer'} size="lg" fullScreen={mobile} yOffset={mobile ? 0 : undefined}
      styles={mobile ? { content: { marginTop: 'auto', height: 'auto', maxHeight: '88dvh', borderRadius: '16px 16px 0 0' } } : undefined}>
      <Stack gap="sm">
        <TextInput label="Navn" value={f.name} onChange={(e) => set('name', e.currentTarget.value)} placeholder="Kontorprinter" data-autofocus />
        <Group grow align="flex-start" wrap="nowrap" gap="sm">
          <TextInput style={{ flex: 2 }} label="Adresse" value={f.uri} onChange={(e) => { set('uri', e.currentTarget.value); setProbe(null); }} placeholder="ipp://192.0.2.44/ipp/print"
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 13 } }}
            rightSection={ask.isPending ? <Loader size={14} /> : <Tooltip label="Spørg printeren (model, farve, tosidet)"><ActionIcon variant="subtle" onClick={runProbe} disabled={!f.uri} aria-label="Spørg printeren"><IconWand size={16} /></ActionIcon></Tooltip>} />
          <TextInput style={{ flex: 1 }} label="Placering" value={f.location} onChange={(e) => set('location', e.currentTarget.value)} placeholder="fx Kontoret" />
        </Group>
        {probe?.error && <Text size="xs" c="red">Printeren svarede ikke: {probe.error}</Text>}
        {f.makeModel && <Text size="xs" c="dimmed">{f.makeModel}{probe && !probe.error ? ` · ${probe.status}` : ''}</Text>}
        {!f.needsDriver ? (
          <Alert color="teal" variant="light" icon={<IconCircleCheck size={18} />} p="xs">
            <Text size="sm"><b>Virker uden driver (IPP Everywhere).</b> Windows, Mac og Linux bruger deres indbyggede printersystem.</Text>
          </Alert>
        ) : (
          <Alert color="orange" variant="light" icon={<IconAlertTriangle size={18} />} p="xs">
            <Text size="sm"><b>Kræver driver.</b> Skriv driverens navn (Windows) eller CUPS-modellen, og vælg evt. pakken med driveren – den lægges så på de maskiner, der får printeren.</Text>
          </Alert>
        )}
        <Checkbox size="xs" label="Kræver driver" checked={f.needsDriver} onChange={(e) => set('needsDriver', e.currentTarget.checked)} />
        {f.needsDriver && (
          <Group grow align="flex-start" gap="sm">
            <TextInput label="Driver" value={f.driver} onChange={(e) => set('driver', e.currentTarget.value)} placeholder="HP Universal Printing PCL 6" />
            <Select label="Driverpakke" clearable searchable placeholder="Ingen" value={f.driverPackageId != null ? String(f.driverPackageId) : null}
              onChange={(v) => set('driverPackageId', v ? Number(v) : null)} data={packages.map((p) => ({ value: String(p.id), label: p.name }))} />
          </Group>
        )}
        <Group grow gap="sm" wrap="nowrap">
          <Select label="Papir" data={PAPER} value={f.paper} onChange={(v) => v && set('paper', v)} allowDeselect={false} />
          <Select label="Tosidet" data={DUPLEX} value={f.duplex} onChange={(v) => v && set('duplex', v)} allowDeselect={false} />
          <Select label="Farve" data={COLOR} value={f.color ? 'color' : 'mono'} onChange={(v) => v && set('color', v === 'color')} allowDeselect={false} />
        </Group>
        {editing && assignments.length > 0 && (
          <Stack gap={4}>
            <Text size="sm" fw={500}>Lagt på</Text>
            {assignments.map((a) => (
              <Group key={a.id} gap="xs" wrap="nowrap">
                <Text size="sm" style={{ flex: 1 }}>{a.label}{a.profileId != null ? ` (${a.machines} maskiner)` : ''}</Text>
                {a.makeDefault && <Badge size="xs" variant="light">Standard</Badge>}
                <Tooltip label="Fjern herfra (agenten fjerner printeren igen)"><ActionIcon variant="subtle" color="red" onClick={() => unassign.mutate(a.id, { onSuccess: () => qc.invalidateQueries() })} aria-label="Fjern"><IconTrash size={14} /></ActionIcon></Tooltip>
              </Group>
            ))}
          </Stack>
        )}
        <MultiSelect label={editing ? 'Læg også på' : 'Læg på'} data={options} value={targets} onChange={setTargets} searchable clearable
          placeholder={targets.length ? undefined : 'Vælg profiler eller maskiner'} description="Fleetdeck tilføjer printeren og lægger den på igen, hvis nogen fjerner den." />
        <Checkbox label="Gør til standardprinter på maskiner, der ikke har en" checked={makeDefault} onChange={(e) => setMakeDefault(e.currentTarget.checked)} disabled={!targets.length} />
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button leftSection={<IconCheck size={16} />} loading={save.isPending} disabled={!f.name.trim() || !f.uri.trim()} onClick={() => save.mutate(undefined, { onSuccess: done })}>
            {editing ? 'Gem' : 'Tilføj printer'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

// ---------------------------------------------------------------- på en profil / maskine

interface AssignRow { id: number; printerId: number; profileId: number | null; machineId: number | null; makeDefault: boolean; keep: boolean; name: string; uri: string; makeModel: string }

/** Tilføj en printer til en profil eller maskine: en af Fleetdecks printere eller (på maskinen) en, der er fundet på netværket. */
function AddAssignment({ profileId, machineId, have, onDone }: { profileId?: number; machineId?: number; have: number[]; onDone: () => void }) {
  const { data } = useApi<PrintersResponse>(['printers'], '/api/printers');
  const [pick, setPick] = useState<string | null>(null);
  const [def, setDef] = useState(false);
  const [driver, setDriver] = useState('');
  const free = (data?.printers ?? []).filter((p) => !have.includes(p.id));
  const found = machineId ? data?.found ?? [] : [];
  const picked = pick?.startsWith('f:') ? found.find((f) => f.host === pick.slice(2)) : undefined;
  const needsDriver = picked?.driverless === false;
  const add = useAction(async () => {
    if (!picked) return post('/api/printers/assignments', { printerId: Number(pick), profileId, machineId, makeDefault: def, keep: true });
    // en fundet printer: tilføjes til Fleetdeck og lægges på maskinen i ét
    return post('/api/printers', {
      name: picked.name, uri: picked.uri, location: picked.location ?? '', paper: 'A4', duplex: picked.duplex === false ? 'none' : 'long', color: picked.color !== false,
      needsDriver, driver: needsDriver ? driver : '', makeModel: picked.makeModel, source: picked.via, machineIds: [machineId], makeDefault: def,
    });
  }, 'Printeren lægges på');
  if (!free.length && !found.length)
    return <Text size="xs" c="dimmed">{data?.printers.length ? 'Alle printere er lagt på.' : machineId ? 'Ingen printere fundet på netværket endnu. Se siden Printere.' : 'Tilføj først printere under Printere.'}</Text>;
  const options = [
    ...(free.length ? [{ group: 'I Fleetdeck', items: free.map((p) => ({ value: String(p.id), label: p.name })) }] : []),
    ...(found.length ? [{ group: 'Fundet på netværket', items: found.map((f) => ({ value: `f:${f.host}`, label: `${f.name} (${f.host})` })) }] : []),
  ];
  return (
    <Stack gap="xs">
      <Group gap="xs" align="center" wrap="wrap">
        <Select style={{ flex: '1 1 220px' }} size="xs" placeholder={machineId ? 'Tilføj printer' : 'Vælg printer'} leftSection={<IconPrinter size={14} />} value={pick}
          onChange={(v) => { setPick(v); const f = v?.startsWith('f:') ? found.find((x) => x.host === v.slice(2)) : undefined; setDriver(f?.via === 'agent' ? f.makeModel ?? '' : ''); }}
          data={options} searchable aria-label="Tilføj printer" />
        <Checkbox size="xs" label="Standard, hvis maskinen ikke har en" checked={def} onChange={(e) => setDef(e.currentTarget.checked)} />
        <Button size="xs" leftSection={<IconPlus size={14} />} disabled={!pick || (needsDriver && !driver.trim())} loading={add.isPending}
          onClick={() => add.mutate(undefined, { onSuccess: () => { setPick(null); setDef(false); setDriver(''); onDone(); } })}>{profileId ? 'Læg printer på profilen' : 'Tilføj printer'}</Button>
      </Group>
      {needsDriver && <TextInput size="xs" label="Kræver driver – skriv driverens navn (Windows) eller CUPS-modellen" value={driver} onChange={(e) => setDriver(e.currentTarget.value)} placeholder="HP Universal Printing PCL 6" />}
    </Stack>
  );
}

/**
 * "Opsæt på…": en fundet printer lægges på de valgte profiler og pc'er (flueben). Den tilføjes til Fleetdeck, og agenterne installerer
 * den og holder den på. Kræver den en driver, skrives driverens navn her.
 */
export function SetupModal({ found, data, onClose }: { found: FoundPrinter | null; data: PrintersResponse; onClose: () => void }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const [machines, setMachines] = useState<number[]>([]);
  const [profiles, setProfiles] = useState<number[]>([]);
  const [makeDefault, setMakeDefault] = useState(true);
  const [driver, setDriver] = useState('');
  useEffect(() => {
    setName(found?.name ?? ''); setMachines([]); setProfiles([]); setMakeDefault(true);
    setDriver(found?.via === 'agent' && found.driverless === false ? found.makeModel ?? '' : '');
  }, [found]);
  const t = data.targets ?? { machines: [], profiles: [] };
  const needsDriver = found?.driverless === false;
  // pc'er i en valgt profil får printeren gennem profilen
  const viaProfile = new Set(t.machines.filter((m) => m.profileId != null && profiles.includes(m.profileId)).map((m) => m.id));
  const count = new Set([...machines.filter((id) => !viaProfile.has(id)), ...viaProfile]).size;
  const toggle = (l: number[], id: number, on: boolean) => (on ? [...l, id] : l.filter((x) => x !== id));
  const save = useAction(() => post('/api/printers', {
    name, uri: found!.uri, location: found!.location ?? '', paper: 'A4', duplex: found!.duplex === false ? 'none' : 'long', color: found!.color !== false,
    needsDriver, driver: needsDriver ? driver : '', makeModel: found!.makeModel, source: found!.via,
    profileIds: profiles, machineIds: machines.filter((id) => !viaProfile.has(id)), makeDefault,
  }), count ? `Printeren sættes op på ${count} ${count === 1 ? 'pc' : 'pc\'er'}` : 'Printeren er tilføjet');
  const online = (iso: string | null) => iso != null && Date.now() - new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z').getTime() < 10 * 60000;
  return (
    <Modal opened={found != null} onClose={onClose} title={found ? `Opsæt ${found.name}` : ''} size="md" fullScreen={mobile} yOffset={mobile ? 0 : undefined}
      styles={mobile ? { content: { marginTop: 'auto', height: 'auto', maxHeight: '92dvh', borderRadius: '16px 16px 0 0' } } : undefined}>
      {found && <Stack gap="sm">
        <Group gap={6}><Text size="xs" c="dimmed" ff="monospace">{found.uri}</Text><DriverBadge driverless={found.driverless} /></Group>
        <TextInput label="Navn på pc'erne" value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
        {needsDriver && <TextInput label="Driver" description="Printeren kræver en driver: skriv driverens navn (Windows) eller CUPS-modellen." value={driver}
          onChange={(e) => setDriver(e.currentTarget.value)} placeholder="HP Universal Printing PCL 6" />}
        {t.profiles.length > 0 && <Stack gap={6}>
          <Text size="sm" fw={600}>Profiler</Text>
          {t.profiles.map((p) => (
            <Group key={p.id} gap="xs" wrap="nowrap">
              <Checkbox style={{ flex: 1 }} label={p.name} checked={profiles.includes(p.id)} onChange={(e) => setProfiles(toggle(profiles, p.id, e.currentTarget.checked))} />
              <Text size="xs" c="dimmed">{p.machines} {p.machines === 1 ? 'maskine' : 'maskiner'}</Text>
            </Group>
          ))}
        </Stack>}
        <Stack gap={6}>
          <Text size="sm" fw={600}>Pc'er</Text>
          {t.machines.length === 0 && <Text size="sm" c="dimmed">Ingen pc'er med Fleetdeck-agenten endnu (Windows, Mac eller Linux).</Text>}
          {t.machines.map((m) => (
            <Group key={m.id} gap="xs" wrap="nowrap">
              <Checkbox style={{ flex: 1 }} checked={machines.includes(m.id) || viaProfile.has(m.id)} disabled={viaProfile.has(m.id)}
                label={<>{m.name}{m.profile && <Text span size="xs" c="dimmed"> · {m.profile}</Text>}</>}
                onChange={(e) => setMachines(toggle(machines, m.id, e.currentTarget.checked))} />
              {viaProfile.has(m.id) ? <Text size="xs" c="dimmed">via profilen</Text> : !online(m.lastSeen) && <Text size="xs" c="dimmed">offline – installeres senere</Text>}
            </Group>
          ))}
        </Stack>
        <Checkbox label="Gør til standardprinter på pc'er, der ikke har en" checked={makeDefault} onChange={(e) => setMakeDefault(e.currentTarget.checked)} />
        <Text size="xs" c="dimmed">{needsDriver ? 'Agenterne installerer printeren med driveren' : 'Agenterne installerer printeren uden driver (IPP Everywhere)'} og lægger den på igen, hvis nogen fjerner den.</Text>
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button leftSection={<IconCheck size={16} />} loading={save.isPending} disabled={!name.trim() || (needsDriver && !driver.trim())}
            onClick={() => save.mutate(undefined, { onSuccess: () => { qc.invalidateQueries(); onClose(); } })}>
            {count ? `Opsæt på ${count} ${count === 1 ? 'pc' : "pc'er"}` : 'Tilføj uden pc\'er'}
          </Button>
        </Group>
      </Stack>}
    </Modal>
  );
}

/** Profilens printere: Standard, Hold på og fjern (agenterne fjerner dem igen). */
export function ProfilePrintersPanel({ profileId, isAdmin }: { profileId: number; isAdmin: boolean }) {
  const qc = useQueryClient();
  const { data: rows = [] } = useApi<AssignRow[]>(['printer-assign', 'p', profileId], `/api/printers/assignments?profileId=${profileId}`);
  const upd = useAction((a: { id: number; makeDefault?: boolean; keep?: boolean }) => put(`/api/printers/assignments/${a.id}`, a));
  const remove = useAction((id: number) => del(`/api/printers/assignments/${id}`), 'Printeren fjernes fra profilens maskiner');
  const refresh = () => qc.invalidateQueries();
  return (
    <Panel title="Printere">
      <Stack gap="xs">
        {rows.length === 0 && <Text size="sm" c="dimmed">Ingen printere på profilen endnu.</Text>}
        {rows.map((r) => (
          <Group key={r.id} gap="sm" wrap="nowrap">
            <ThemeIcon variant="light" color="gray" radius="md"><IconPrinter size={16} /></ThemeIcon>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Text size="sm" fw={600} lineClamp={1}>{r.name}</Text>
              <Text size="xs" c="dimmed" lineClamp={1}>{r.makeModel || r.uri}</Text>
            </div>
            {r.makeDefault ? <Badge size="xs" variant="light">Standard</Badge> : <Badge size="xs" variant="light" color="gray">Tilføjes</Badge>}
            <Tooltip label="Hold på: læg printeren på igen, hvis nogen fjerner den">
              <Switch size="xs" label="Hold på" labelPosition="left" checked={r.keep} disabled={!isAdmin}
                onChange={(e) => upd.mutate({ id: r.id, keep: e.currentTarget.checked }, { onSuccess: refresh })} />
            </Tooltip>
            {isAdmin && <Tooltip label={r.makeDefault ? 'Ikke standardprinter' : 'Gør til standard på maskiner uden en'}>
              <ActionIcon variant="subtle" color="gray" onClick={() => upd.mutate({ id: r.id, makeDefault: !r.makeDefault }, { onSuccess: refresh })} aria-label="Standard"><IconCheck size={14} /></ActionIcon>
            </Tooltip>}
            {isAdmin && <Tooltip label="Fjern fra profilen"><ActionIcon variant="subtle" color="red" onClick={() => remove.mutate(r.id, { onSuccess: refresh })} aria-label="Fjern"><IconTrash size={14} /></ActionIcon></Tooltip>}
          </Group>
        ))}
        {isAdmin && <AddAssignment profileId={profileId} have={rows.map((r) => r.printerId)} onDone={refresh} />}
        <Text size="xs" c="dimmed">Agenten tjekker ved hver helbredsrunde, at printerne findes. Mangler en, lægges den på igen. Printere, Fleetdeck ikke har lagt på, røres aldrig.</Text>
      </Stack>
    </Panel>
  );
}

/** Teksten til højre for en printer på maskinens side. */
export function machineStateText(r: MachinePrinterRow, online: boolean | undefined): { text: string; color: string } {
  if (!r.fleetdeck) return { text: 'Ikke fra Fleetdeck', color: 'dimmed' };
  const s = r.state;
  if (!s) return { text: online === false ? 'Venter – maskinen er offline' : 'Venter på maskinen', color: 'dimmed' };
  if (s.dropped) return { text: 'Fjernet på maskinen (Hold på er slået fra)', color: 'dimmed' };
  if (s.error && !s.ok) return { text: s.error, color: 'red' };
  if (s.foreign) return { text: 'Fandtes i forvejen · ikke lagt på af Fleetdeck', color: 'dimmed' };
  if (s.readdedAt && Date.now() - new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(s.readdedAt) ? s.readdedAt : s.readdedAt + 'Z').getTime() < 7 * 86400000)
    return { text: `Lagt på igen ${when(s.readdedAt)} (var fjernet)`, color: 'orange' };
  return { text: `Installeret${s.isDefault ? ' · standard' : ''}${s.error ? ` · ${s.error}` : ''}`, color: s.error ? 'orange' : 'teal' };
}

/** Fanen Printere på en maskine: Fleetdecks printere med status og maskinens egne. */
export function MachinePrintersPanel({ machineId, rows, isAdmin, online }: { machineId: number; rows: MachinePrinterRow[]; isAdmin: boolean; online?: boolean }) {
  const qc = useQueryClient();
  const remove = useAction((id: number) => del(`/api/printers/assignments/${id}`), 'Printeren fjernes fra maskinen');
  const refresh = () => qc.invalidateQueries();
  return (
    <Panel p={0}>
      <Stack gap={0}>
        {rows.length === 0 && <Text size="sm" c="dimmed" p="md">Ingen printere på maskinen endnu.</Text>}
        {rows.map((r, i) => {
          const st = machineStateText(r, online);
          return (
            <Group key={`${r.name}-${i}`} gap="sm" wrap="nowrap" px="md" py={10} style={{ borderTop: i ? '1px solid var(--mantine-color-default-border)' : undefined }}>
              <IconPrinter size={16} style={{ flex: 'none' }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" fw={600} lineClamp={1}>{r.name}{r.fromProfile && <Badge size="xs" variant="light" color="gray" ml={6}>fra profil</Badge>}</Text>
              </div>
              <Text size="xs" c={st.color} ta="right" lineClamp={2} style={{ maxWidth: '55%' }}>{st.text}</Text>
              {isAdmin && r.fleetdeck && !r.fromProfile && r.assignmentId != null &&
                <Tooltip label="Fjern fra maskinen"><ActionIcon variant="subtle" color="red" onClick={() => remove.mutate(r.assignmentId!, { onSuccess: refresh })} aria-label="Fjern"><IconTrash size={14} /></ActionIcon></Tooltip>}
            </Group>
          );
        })}
        {isAdmin && <Box p="md" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <AddAssignment machineId={machineId} have={rows.filter((r) => r.printerId != null && !r.fromProfile).map((r) => r.printerId!)} onDone={refresh} />
        </Box>}
      </Stack>
    </Panel>
  );
}

export { VIA, LEVEL_COLOR };
