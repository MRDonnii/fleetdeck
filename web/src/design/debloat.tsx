import { useMemo, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import {
  Alert, Badge, Button, Card, Checkbox, Collapse, Group, List, Modal, ScrollArea, SegmentedControl, SimpleGrid, Stack, Stepper, Switch, Table,
  Text, ThemeIcon, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import { IconAlertTriangle, IconBolt, IconChevronDown, IconChevronRight, IconInfoCircle, IconRefresh, IconShieldCheck, IconSparkles } from '@tabler/icons-react';
import { post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, JobLive, JobModal, Loading, noTruncate, PageHeader, Panel, StatusBadge } from '../components';
import { ago, fmt, JOB } from '../format';
import type { JobStatus } from '../types';
import { stateText } from './debloatText';

// ------------------------------------------------------------------ typer (fra /api/debloat/*)
type Risk = 'sikker' | 'moderat' | 'avanceret';
type Level = '' | 'let' | 'anbefalet' | 'maks';
export interface DebloatItem {
  /** title = hvad Anvend gør ("Slå reklame-id fra"); subject + stateOn/stateOff = tilstanden ("Reklame-id: fra"); undo = hvad Fortryd gør. */
  id: string; category: string; title: string; subject: string; stateOn: string; stateOff: string; undo: string; needsLogoff: boolean;
  description: string; risk: Risk; profiles: string[]; minBuild: number; maxBuild: number;
  editions: string[]; note?: string | null; fullyReversible: boolean; notReinstallable: string[];
  registry: { hive: string; path: string; name: string; kind: string; value: number | string | null }[];
  services: { name: string; start: number }[]; tasks: string[]; apps: { name: string; title: string; reinstallable: boolean }[];
  winget: { id: string; title: string }[];
  exclusive?: string | null; needsReboot: boolean; profileSince: number; hibernateOff: boolean; wakeOnLan: boolean;
  powerPlan?: { base: string; copy?: string | null } | null;
  powerSettings: { subgroup: string; setting: string; ac?: number | null; dc?: number | null }[];
  features: string[];
}
interface DebloatCategoryInfo { id: string; title: string; description: string; appliedLabel: string; notAppliedLabel: string }
export interface DebloatCatalog {
  categories: DebloatCategoryInfo[];
  powerPlans: Record<string, string>;
  profiles: { id: Exclude<Level, ''>; title: string; description: string; items: string[] }[];
  items: DebloatItem[];
}
interface ItemState { state: 'applied' | 'partial' | 'not_applied' | 'na' | 'error'; changed: number; errors: string[]; hives: { h: string; n?: string | null; ok: boolean }[]; reboot?: boolean; at: string }
interface BeforeView { reg: { k: string; h: string; e: boolean; v: string | null }[]; svc: Record<string, number>; task: Record<string, boolean>; appx: string[]; at: string }
interface DJob { id: number; type: string; title: string; status: JobStatus; created: string; createdBy: string; started: string | null; finished: string | null;
  exitCode: number | null; progress: number | null; step: string; stepSince: string | null }
export interface MachineDebloat {
  machineId: number; name: string; profile: Level; include: string[]; exclude: string[]; enforce: boolean; enforceMode: 'alert' | 'reapply';
  applied: string[]; drift: string[]; selected: string[]; states: Record<string, ItemState>; undoable: string[]; build: number | null;
  edition: string | null; lastScan: string | null; lastApplied: string | null; restorePoint: string | null; busy: boolean;
  before: Record<string, BeforeView>; jobs: DJob[];
  profileVersion: number; profileCurrent: number; profileNew: string[];
  power: { active?: string | null; name?: string | null; plans: { guid: string; name: string }[] };
}
type CatState = 'off' | 'ok' | 'drift' | 'pending';
interface FleetRow {
  machineId: number; name: string; lastSeen: string | null; online: boolean; profile: Level; enforce: boolean; enforceMode: string; selected: number;
  applied: number; drift: number; pending: number; errors: number; build: number | null; edition: string | null; lastScan: string | null; busy: boolean;
  cats: Record<string, CatState>; profileNew: number; powerPlan?: string | null;
}

// ------------------------------------------------------------------ fælles
const RISK: Record<Risk, string> = { sikker: 'green', moderat: 'yellow', avanceret: 'red' };
const STATE: Record<ItemState['state'] | 'drift', { label: string; color: string }> = {
  applied: { label: 'anvendt', color: 'green' }, partial: { label: 'delvist', color: 'yellow' }, not_applied: { label: 'ikke anvendt', color: 'gray' },
  na: { label: 'gælder ikke denne pc', color: 'gray' }, error: { label: 'fejl', color: 'red' }, drift: { label: 'vendt tilbage', color: 'orange' },
};
const RISK_TIP: Record<Risk, string> = { sikker: 'Kan ikke mærkes i hverdagen.', moderat: 'Kan mærkes – læs beskrivelsen først.', avanceret: 'Kun hvis du ved, hvad det slår fra.' };
/** Fortryd-teksten for almindelige punkter (serverens standard) – kun de særlige vises i bekræftelsen. */
const PLAIN_UNDO = 'Det, der stod før, sættes tilbage. Har nogen ændret det siden, får det lov at blive.';
const NONE_INFO = "Intet vælges af sig selv – kun det, du slår til. Det, der allerede er anvendt, bliver på pc'en, indtil du trykker Fortryd.";
const LEVELS: { value: Level; label: string }[] = [{ value: '', label: 'Intet' }, { value: 'let', label: 'Let' }, { value: 'anbefalet', label: 'Anbefalet' }, { value: 'maks', label: 'Maks' }];
const levelTitle = (l: Level) => LEVELS.find((x) => x.value === l)?.label ?? l;
const HIVE: Record<string, string> = { HKLM: 'Hele pc\'en', HKCU: 'Hver bruger + nye brugere', DEFAULT: 'Nye brugere (standardprofil)' };
const winName = (b: number | null) => (b == null ? null : b >= 22000 ? 'Windows 11' : 'Windows 10');
const buildText = (i: DebloatItem) => [i.minBuild >= 22000 ? 'Windows 11' : '', i.minBuild >= 26100 ? '24H2 og nyere' : '', i.maxBuild && i.maxBuild < 22000 ? 'kun Windows 10' : '',
  i.editions.length ? i.editions.join('/') : ''].filter(Boolean).join(' · ') || 'Windows 10 og 11 · alle udgaver';
const shown = (v: unknown) => (v === null || v === undefined ? 'slettes' : String(v));

const PLAN_GROUP = 'powerplan';
/** Strømplanerne har deres eget kort; alt andet vises som punkter i kategorierne. */
const listed = (i: DebloatItem) => i.exclusive !== PLAN_GROUP;
const seconds = (v: number) => (v === 0 ? 'aldrig' : v % 60 === 0 ? `${v / 60} min` : `${v} sek`);
const POWER_NAMES: Record<string, string> = { '29f6c1db-86da-48c5-9fdb-f2b67b1f44da': 'Strømsparetilstand', '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e': 'Skærm slukker',
  '48e6b7a6-50f5-4782-a5d4-53bb8f07e226': 'USB-strømbesparelse', 'ee12f906-d277-404b-b6da-e5fa1a576df5': 'PCI Express-strømbesparelse' };
const powerValue = (setting: string, v: number) => (POWER_NAMES[setting]?.startsWith('Str') || POWER_NAMES[setting]?.startsWith('Sk') ? seconds(v) : v === 0 ? 'fra' : String(v));

function stateOf(m: MachineDebloat, id: string): keyof typeof STATE | null {
  if (m.drift.includes(id)) return 'drift';
  return m.states[id]?.state ?? null;
}
function useCatalog() { return useApi<DebloatCatalog>(['debloat-catalog'], '/api/debloat/catalog'); }

/** Hvad der sker, før man trykker: punkter pr. kategori, apps der fjernes/hentes igen, og advarsler. */
function Summary({ cat, items, mode, m }: { cat: DebloatCatalog; items: DebloatItem[]; mode: 'apply' | 'undo'; m?: MachineDebloat }) {
  const apps = items.flatMap((i) => i.apps);
  const lost = items.flatMap((i) => i.apps.filter((a) => !a.reinstallable).map((a) => a.title));
  const removedBefore = (i: DebloatItem) => m?.before[i.id]?.appx ?? [];
  const reinstall = mode === 'undo' ? items.flatMap((i) => i.apps.filter((a) => a.reinstallable && removedBefore(i).some((r) => r.toLowerCase().startsWith(a.name.replace('*', '').toLowerCase())))) : [];
  const advanced = items.filter((i) => i.risk === 'avanceret');
  const reboot = items.filter((i) => i.needsReboot || i.features.length > 0);
  const logoff = items.filter((i) => i.needsLogoff);
  const special = mode === 'undo' ? items.filter((i) => !i.apps.length && i.undo && i.undo !== PLAIN_UNDO) : [];
  return (
    <Stack gap="sm">
      {cat.categories.map((c) => {
        const its = items.filter((i) => i.category === c.id);
        return its.length ? (
          <div key={c.id}><Text fw={600} size="sm">{c.title} <Text span c="dimmed" size="sm">· {its.length}</Text></Text>
            <Text size="sm" c="dimmed">{its.map((i) => (mode === 'apply' ? i.title : i.subject)).join(' · ')}</Text></div>
        ) : null;
      })}
      {mode === 'apply' && apps.length > 0 && <Text size="sm">Fjerner op til {apps.length} apps for alle brugere og fra Windows' standardbillede (kun dem, der findes på pc'en).</Text>}
      {mode === 'apply' && lost.length > 0 && <Alert color="red" icon={<IconAlertTriangle size={18} />} p="xs">Kan ikke installeres igen bagefter: {lost.join(', ')}</Alert>}
      {mode === 'apply' && items.some((i) => i.winget.length) && <Alert color="yellow" p="xs">Afinstallerer {items.flatMap((i) => i.winget.map((w) => w.title)).join(', ')} for den bruger, der er logget ind.</Alert>}
      {advanced.length > 0 && mode === 'apply' && <Alert color="yellow" icon={<IconAlertTriangle size={18} />} p="xs">Avancerede punkter: {advanced.map((i) => i.title).join(', ')}</Alert>}
      {reboot.length > 0 && <Alert color="blue" icon={<IconInfoCircle size={18} />} p="xs">Virker først efter en genstart: {reboot.map((i) => i.subject).join(', ')}. Fleetdeck genstarter ikke selv pc'en.</Alert>}
      {logoff.length > 0 && <Text size="sm">Ses først, når brugeren har logget af og på igen: {logoff.map((i) => i.subject).join(', ')}.</Text>}
      {special.map((i) => <Text key={i.id} size="sm"><b>{i.subject}:</b> {i.undo}</Text>)}
      {mode === 'undo' && reinstall.length > 0 && <Text size="sm">Henter igen fra Microsoft Store for den bruger, der er logget ind: {reinstall.map((a) => a.title).join(', ')}</Text>}
      {mode === 'undo' && lost.length > 0 && <Alert color="red" p="xs">Kommer ikke tilbage (findes ikke længere i Store): {lost.join(', ')}</Alert>}
      <Text size="xs" c="dimmed">
        {mode === 'apply'
          ? 'Er systemgendannelse slået til på pc\'en, oprettes der først et gendannelsespunkt. De fleste punkter gælder alle brugere og nye brugere. De gamle værdier gemmes, så punkterne kan fortrydes – undtagen apps, der ikke længere findes i Microsoft Store.'
          : `${PLAIN_UNDO} Punkterne bliver stående som valgt – skal de ikke anvendes igen, så slå dem fra.`}
      </Text>
    </Stack>
  );
}

/** Bekræftelse med opsummering før anvend og fortryd. */
function ConfirmRun({ opened, onClose, title, cat, items, mode, m, onRun, loading }: {
  opened: boolean; onClose: () => void; title: string; cat: DebloatCatalog; items: DebloatItem[]; mode: 'apply' | 'undo'; m?: MachineDebloat; onRun: () => void; loading?: boolean;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title} size="lg">
      <Stack>
        {items.length === 0 ? <Text c="dimmed">Ingen punkter.</Text> : <ScrollArea.Autosize mah="55vh"><Summary cat={cat} items={items} mode={mode} m={m} /></ScrollArea.Autosize>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color={mode === 'undo' ? 'red' : undefined} disabled={!items.length} loading={loading} onClick={onRun}>
            {mode === 'apply' ? 'Anvend' : 'Fortryd'} {items.length} {items.length === 1 ? 'punkt' : 'punkter'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

const ENFORCE_INFO: Record<string, string> = {
  alert: 'Giv besked: en advarsel under Helbred. Fleetdeck ændrer ikke noget selv.',
  reapply: 'Anvend igen: Fleetdeck anvender det igen (højst én gang i døgnet) og advarer kun, hvis det ikke hjælper.',
};

// ------------------------------------------------------------------ strømprofil
const PLAN_CHOICES = [{ value: 'balanced', label: 'Balanceret' }, { value: 'high', label: 'Høj ydelse' }, { value: 'ultimate', label: 'Ultimativ ydelse' }];

/** Vælg strømplan: viser den plan, pc'en kører med nu, og skifter først, når man har bekræftet valget. */
export function PowerPlanPicker({ cat, m, onDone }: { cat: DebloatCatalog; m: MachineDebloat; onDone?: () => void }) {
  const [pick, setPick] = useState<string | null>(null);
  const run = useAction((plan: string) => post<{ jobIds: number[] }>(`/api/debloat/machines/${m.machineId}/powerplan`, { plan }), 'Strømplanen skiftes');
  const undo = useAction((id: string) => post(`/api/debloat/machines/${m.machineId}/undo`, { items: [id] }), 'Den tidligere strømplan sættes tilbage');
  const itemOf = (choice: string) => cat.items.find((i) => i.id === cat.powerPlans[choice]);
  const active = m.power?.active?.toLowerCase();
  const current = PLAN_CHOICES.find((c) => { const p = itemOf(c.value)?.powerPlan; return !!active && (p?.base === active || p?.copy === active); })?.value ?? '';
  const value = pick ?? current;
  const chosen = pick && pick !== current ? itemOf(pick) : undefined;
  const applied = cat.items.find((i) => i.exclusive === PLAN_GROUP && m.applied.includes(i.id));
  return (
    <Stack gap="xs">
      <Group justify="space-between" gap="sm" wrap="wrap">
        <SegmentedControl data={PLAN_CHOICES} value={value} disabled={m.busy} onChange={setPick} aria-label="Strømplan" />
        <Group gap={6}>
          <Text size="sm" c="dimmed">Aktiv nu:</Text>
          {m.power?.name ? <Badge variant="light" color={current ? 'green' : 'gray'} styles={noTruncate}>{m.power.name}</Badge> : <Text size="sm" c="dimmed">ikke tjekket endnu</Text>}
        </Group>
      </Group>
      {m.power?.name && !current && !chosen && <Text size="xs" c="dimmed">Pc'en bruger en plan fra producenten eller en, du selv har lavet. Den bliver stående, indtil du vælger en anden.</Text>}
      {chosen && (
        <Card withBorder radius="md" p="sm">
          <Text size="sm">{chosen.description}</Text>
          {chosen.note && <Text size="xs" c="yellow.5" mt={4}>{chosen.note}</Text>}
          <Group gap="xs" mt="xs">
            <Button size="compact-sm" loading={run.isPending} disabled={m.busy} onClick={() => run.mutate(pick!, { onSuccess: () => { setPick(null); onDone?.(); } })}>Skift til {PLAN_CHOICES.find((c) => c.value === pick)?.label}</Button>
            <Button size="compact-sm" variant="default" onClick={() => setPick(null)}>Annullér</Button>
          </Group>
        </Card>
      )}
      {!chosen && applied && <Group><Button size="compact-xs" variant="subtle" color="gray" loading={undo.isPending} disabled={m.busy} onClick={() => undo.mutate(applied.id)}>Fortryd: tilbage til den oprindelige plan</Button></Group>}
    </Stack>
  );
}

/** Strøm-menuen på maskinens side: samme vælger i et lille vindue. */
export function PowerPlanModal({ machineId, name, onClose }: { machineId: number; name: string; onClose: () => void }) {
  const { data: cat } = useCatalog();
  const { data: m } = useApi<MachineDebloat>(['debloat', machineId], `/api/debloat/machines/${machineId}`);
  return (
    <Modal opened onClose={onClose} title={`Strømprofil på ${name}`} size="lg">
      {cat && m ? <PowerPlanPicker cat={cat} m={m} onDone={onClose} /> : <Loading />}
    </Modal>
  );
}

/** Opdateringspolitik: to tilstande. At slå Windows Update fra tilbydes ikke. */
function UpdatePolicy({ i, m, on, onChange }: { i: DebloatItem; m: MachineDebloat; on: boolean; onChange: (security: boolean) => void }) {
  const st = stateOf(m, i.id);
  return (
    <Stack gap={6} pt={8} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
      <Group justify="space-between" gap="xs">
        <SegmentedControl size="xs" disabled={m.busy || st === 'na'} value={on ? 'security' : 'standard'} onChange={(v) => onChange(v === 'security')}
          data={[{ value: 'standard', label: 'Standard' }, { value: 'security', label: 'Udsæt nye versioner' }]} aria-label="Opdateringspolitik" />
        {st && <Badge size="sm" variant="light" color={STATE[st].color} styles={noTruncate}>{stateText(i, st)}</Badge>}
      </Group>
      <Text size="sm" c="dimmed">{on ? `Udsæt nye versioner: ${i.description}` : 'Standard: Windows installerer nye versioner og månedens opdateringer, så snart de er klar.'}</Text>
      <Text size="xs" c="yellow.5">{i.note}</Text>
    </Stack>
  );
}

// ------------------------------------------------------------------ maskinfanen
/** Fanen "Debloat & privatliv" på en Windows-maskine (kun administratorer). */
export function DebloatTab({ machineId }: { machineId: number }) {
  const { isAdmin } = useMe();
  const { data: cat } = useCatalog();
  const { data: m } = useApi<MachineDebloat>(['debloat', machineId], `/api/debloat/machines/${machineId}`, { refetchInterval: 10_000 });
  const [filter, setFilter] = useState('alle');
  const [confirm, setConfirm] = useState<{ mode: 'apply' | 'undo'; ids: string[] | null } | null>(null);
  const [jobOpen, setJobOpen] = useState<number | null>(null);
  const save = useAction((b: Partial<MachineDebloat>) => put(`/api/debloat/machines/${machineId}`, b));
  const run = useAction((b: { action: string; items?: string[] | null }) => post<{ jobIds: number[] }>(`/api/debloat/machines/${machineId}/${b.action}`, { items: b.items ?? null }),
    (r) => `${r.jobIds.length} job sat i kø`);
  const levelInfo = (l: Level) => (l ? cat?.profiles.find((p) => p.id === l)?.description : NONE_INFO);
  if (!isAdmin) return <Empty>Kun administratorer kan se debloat og privatliv.</Empty>;
  if (!cat || !m) return <Loading />;

  const byId = new Map(cat.items.map((i) => [i.id, i]));
  const sel = new Set(m.selected);
  const pending = m.selected.filter((id) => !m.drift.includes(id) && !['applied', 'na'].includes(m.states[id]?.state ?? ''));
  const orphans = m.applied.filter((id) => !sel.has(id));
  const running = m.jobs.find((j) => ['pending', 'running', 'waiting_user'].includes(j.status) && j.type === 'script');
  const toggle = (i: DebloatItem, on: boolean) => {
    const inProfile = i.profiles.includes(m.profile);
    const include = new Set(m.include), exclude = new Set(m.exclude);
    if (on && i.exclusive) cat.items.filter((x) => x.exclusive === i.exclusive).forEach((x) => include.delete(x.id));
    if (on) { if (inProfile) exclude.delete(i.id); else include.add(i.id); } else if (inProfile) exclude.add(i.id); else include.delete(i.id);
    save.mutate({ include: [...include], exclude: [...exclude] });
  };
  const visible = (i: DebloatItem) => listed(i) && (filter === 'alle' || (filter === 'valgt' && sel.has(i.id)) || (filter === 'drift' && m.drift.includes(i.id))
    || (filter === 'fejl' && (m.states[i.id]?.errors?.length ?? 0) > 0));
  const confirmItems = confirm ? (confirm.ids ?? (confirm.mode === 'apply' ? m.selected : [...new Set([...m.applied, ...m.undoable])])).map((id) => byId.get(id)!).filter(Boolean) : [];
  const errors = Object.values(m.states).filter((s) => s.errors?.length).length;
  const reboot = cat.items.filter((i) => m.states[i.id]?.reboot);
  const fresh = m.profileNew.map((id) => byId.get(id)).filter((i): i is DebloatItem => !!i);
  const setUpdatePolicy = (i: DebloatItem, security: boolean) => {
    toggle(i, security);
    if (security) setConfirm({ mode: 'apply', ids: [i.id] });
    else if (m.applied.includes(i.id) || m.undoable.includes(i.id)) setConfirm({ mode: 'undo', ids: [i.id] });
  };

  return (
    <Stack>
      {/* strømprofil */}
      <Card withBorder radius="md" p="md">
        <Group gap={8} mb="xs"><ThemeIcon variant="light" size="md"><IconBolt size={18} /></ThemeIcon><Title order={4}>Strømprofil</Title></Group>
        <PowerPlanPicker cat={cat} m={m} />
      </Card>

      {fresh.length > 0 && (
        <Alert color="blue" icon={<IconSparkles size={18} />} title={`${levelTitle(m.profile)} har fået ${fresh.length} nye punkter`}>
          <Text size="sm">{fresh.map((i) => i.title).join(' · ')}</Text>
          <Text size="xs" c="dimmed" mt={4}>Pc'en beholder det, du valgte, indtil du tager dem med. Bagefter skal de anvendes som alt andet.</Text>
          <Button size="compact-sm" variant="light" mt="xs" loading={save.isPending} onClick={() => save.mutate({ upgradeProfile: true } as Partial<MachineDebloat>)}>Tag dem med i valget</Button>
        </Alert>
      )}
      {reboot.length > 0 && <Alert color="blue" icon={<IconRefresh size={18} />} p="xs">Venter på en genstart: {reboot.map((i) => i.subject).join(', ')}. Fleetdeck genstarter ikke selv pc'en.</Alert>}

      {/* niveau + tal */}
      <Group justify="space-between" align="flex-end" gap="md">
        <Stack gap={4}>
          <Text size="sm" c="dimmed">Niveau</Text>
          <SegmentedControl data={LEVELS} value={m.profile} onChange={(v) => save.mutate({ profile: v as Level, include: [], exclude: [] })} />
          <Text size="xs" c="dimmed" maw={560}>{levelTitle(m.profile)}: {levelInfo(m.profile)}</Text>
          <Text size="xs" c="dimmed" maw={560}>
            {m.include.length > 0 || m.exclude.length > 0 ? `Egne valg: ${m.include.length} slået til og ${m.exclude.length} slået fra ud over niveauet. Skifter du niveau, nulstilles de. ` : 'Skifter du niveau, nulstilles egne til- og fravalg. '}
            Intet ændres på pc'en, før du trykker Anvend.
          </Text>
        </Stack>
        <Card withBorder p={0} radius="md">
          <Group gap={0} wrap="nowrap">
            {[['Anvendt', m.applied.length - m.drift.length, 'green'], ['Vendt tilbage', m.drift.length, 'orange'], ['Valgt, ikke anvendt', pending.length, undefined]].map(([l, v, c], i) => (
              <Stack key={String(l)} gap={0} px="md" py="xs" style={{ borderLeft: i ? '1px solid var(--mantine-color-default-border)' : undefined }}>
                <Text size="xs" c="dimmed">{l}</Text><Text fw={700} size="xl" c={c as string | undefined}>{v}</Text>
              </Stack>
            ))}
          </Group>
        </Card>
      </Group>

      {/* håndhævelse */}
      <Card withBorder radius="md" p="sm">
        <Group gap="md" wrap="wrap">
          <Switch checked={m.enforce} onChange={(e) => save.mutate({ enforce: e.currentTarget.checked })} label={<Text fw={600} size="sm">Hold håndhævet</Text>}
            description="Fleetdeck tjekker dagligt og efter nye Windows-builds, om anvendte punkter er vendt tilbage" />
          <Stack gap={2}>
            <SegmentedControl size="xs" disabled={!m.enforce} value={m.enforceMode} onChange={(v) => save.mutate({ enforceMode: v as MachineDebloat['enforceMode'] })}
              data={[{ value: 'alert', label: 'Giv besked' }, { value: 'reapply', label: 'Anvend igen' }]} />
            <Text size="xs" c="dimmed">{ENFORCE_INFO[m.enforceMode] ?? ENFORCE_INFO.alert}</Text>
          </Stack>
          <Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 200 }} ta="right">
            {m.lastScan ? `Sidst tjekket ${ago(m.lastScan)}` : 'Ikke tjekket endnu'}{m.build ? ` · ${winName(m.build)} build ${m.build}` : ''}{m.edition ? ` ${m.edition}` : ''}
          </Text>
        </Group>
      </Card>

      {running && (
        <Alert color="blue" icon={<IconRefresh size={18} />} title={running.title}>
          <Group gap="xs"><StatusBadge map={JOB} value={running.status} /><Text size="sm" c="dimmed">startet af {running.createdBy} {ago(running.created)}</Text>
            <Button size="compact-xs" variant="subtle" onClick={() => setJobOpen(running.id)}>Vis output</Button></Group>
          <JobLive job={running} />
          <Text size="xs" c="dimmed" mt={4}>Resultatet for hvert punkt vises herunder, når jobbet er færdigt.</Text>
        </Alert>
      )}
      {orphans.length > 0 && <Alert color="gray" icon={<IconInfoCircle size={18} />} p="xs">
        {orphans.length} punkt{orphans.length > 1 ? 'er' : ''} er anvendt, men ikke valgt: {orphans.map((id) => byId.get(id)?.subject).join(', ')}. De tælles ikke med i Anvend valgte.
        <Button size="compact-xs" variant="subtle" ml="xs" onClick={() => setConfirm({ mode: 'undo', ids: orphans })}>Fortryd dem</Button></Alert>}
      {m.restorePoint?.startsWith('failed') && <Text size="xs" c="dimmed">Sidste gendannelsespunkt kunne ikke oprettes: {m.restorePoint.slice(8)}</Text>}

      <Group justify="space-between" gap="xs">
        <SegmentedControl size="xs" value={filter} onChange={setFilter} data={[
          { value: 'alle', label: `Alle ${cat.items.filter(listed).length}` }, { value: 'valgt', label: `Valgt ${m.selected.length}` },
          { value: 'drift', label: `Vendt tilbage ${m.drift.length}` }, { value: 'fejl', label: `Fejl ${errors}` }]} />
      </Group>
      <Group gap={6} wrap="nowrap" align="flex-start"><IconInfoCircle size={15} style={{ flex: 'none', marginTop: 2, color: 'var(--mantine-color-dimmed)' }} />
        <Text size="xs" c="dimmed">Kontakten vælger et punkt. Pc'en ændres først, når du trykker Anvend – for alle valgte nederst eller for ét punkt i dets detaljer.</Text></Group>

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
        {cat.categories.map((c) => {
          const its = cat.items.filter((i) => i.category === c.id && visible(i));
          if (!its.length) return null;
          const all = cat.items.filter((i) => i.category === c.id && listed(i));
          return (
            <Card key={c.id} withBorder radius="md" p="md">
              <Group justify="space-between" mb={4} wrap="nowrap"><Text fw={700}>{c.title}</Text>
                <Text size="xs" c="dimmed">{all.filter((i) => sel.has(i.id)).length}/{all.length} valgt</Text></Group>
              <Text size="xs" c="dimmed" mb="xs">{c.description}</Text>
              <Stack gap={0}>
                {its.map((i) => c.id === 'updates' ? <UpdatePolicy key={i.id} i={i} m={m} on={sel.has(i.id)} onChange={(v) => setUpdatePolicy(i, v)} /> : <ItemRow key={i.id} i={i} m={m} on={sel.has(i.id)} busy={save.isPending} onToggle={(v) => toggle(i, v)}
                  onApply={() => setConfirm({ mode: 'apply', ids: [i.id] })} onUndo={() => setConfirm({ mode: 'undo', ids: [i.id] })} />)}
              </Stack>
            </Card>
          );
        })}
      </SimpleGrid>

      <Group justify="space-between" gap="sm">
        <Group gap="sm">
          <Button disabled={!m.selected.length || m.busy} onClick={() => setConfirm({ mode: 'apply', ids: null })}>Anvend valgte ({m.selected.length})</Button>
          <Button variant="default" disabled={m.busy} loading={run.isPending && run.variables?.action === 'scan'} onClick={() => run.mutate({ action: 'scan' })}>Tjek nu</Button>
        </Group>
        <Button variant="default" color="red" disabled={m.busy || !(m.applied.length || m.undoable.length)} onClick={() => setConfirm({ mode: 'undo', ids: null })}>Fortryd alt</Button>
      </Group>
      <Text size="xs" c="dimmed" mt={-8}>Tjek nu læser kun tilstanden og ændrer intet. Fortryd alt sætter alt, Fleetdeck har anvendt, tilbage – valget på pc'en bliver stående.</Text>

      {m.jobs.length > 0 && (
        <Panel title="Seneste debloat-jobs" p={0}>
          <Stack gap={0}>
            {m.jobs.slice(0, 6).map((j) => (
              <UnstyledButton key={j.id} onClick={() => setJobOpen(j.id)} px="md" py={8} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
                <Group gap="sm" wrap="nowrap"><StatusBadge map={JOB} value={j.status} /><Text size="sm" style={{ flex: 1, minWidth: 0 }} truncate>{j.title}</Text>
                  <Tooltip label={fmt(j.created)}><Text size="xs" c="dimmed">{ago(j.created)} · {j.createdBy}</Text></Tooltip></Group>
                <JobLive job={j} />
              </UnstyledButton>
            ))}
          </Stack>
        </Panel>
      )}

      {confirm && <ConfirmRun opened onClose={() => setConfirm(null)} cat={cat} items={confirmItems} mode={confirm.mode} m={m} loading={run.isPending}
        title={confirm.mode === 'apply' ? `Anvend på ${m.name}?` : `Fortryd på ${m.name}?`}
        onRun={() => run.mutate({ action: confirm.mode, items: confirm.ids }, { onSuccess: () => setConfirm(null) })} />}
      <JobModal id={jobOpen} onClose={() => setJobOpen(null)} />
    </Stack>
  );
}

/** Ét punkt: kontakt, tilstand og en fold-ud med detaljer (hvad der ændres, før-værdier, status pr. bruger). */
function ItemRow({ i, m, on, busy, onToggle, onApply, onUndo }: {
  i: DebloatItem; m: MachineDebloat; on: boolean; busy: boolean; onToggle: (v: boolean) => void; onApply: () => void; onUndo: () => void;
}) {
  const [open, setOpen] = useState(false);
  const st = stateOf(m, i.id);
  const s = m.states[i.id];
  const before = m.before[i.id];
  const canUndo = m.applied.includes(i.id) || m.undoable.includes(i.id);
  return (
    <div style={{ borderTop: '1px solid var(--mantine-color-default-border)', padding: '8px 0' }}>
      <Group gap="sm" wrap="nowrap" align="center">
        <Switch checked={on} disabled={busy || m.busy} onChange={(e) => onToggle(e.currentTarget.checked)} aria-label={i.title} />
        <UnstyledButton onClick={() => setOpen((o) => !o)} style={{ flex: 1, minWidth: 0 }}>
          <Group gap={6} wrap="nowrap">
            {open ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
            <Stack gap={0} style={{ minWidth: 0 }}>
              <Text size="sm">{i.title}</Text>
              {st && <Text size="xs" c={STATE[st].color === 'gray' ? 'dimmed' : `${STATE[st].color}.7`}>{stateText(i, st)}</Text>}
            </Stack>
            {!i.fullyReversible && <Tooltip label={`Kan ikke installeres igen: ${i.notReinstallable.join(', ')}`}><Badge size="xs" color="red" variant="light">kan ikke hentes igen</Badge></Tooltip>}
          </Group>
        </UnstyledButton>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={RISK_TIP[i.risk]}><Badge size="sm" variant="light" color={RISK[i.risk]} visibleFrom="xs">{i.risk}</Badge></Tooltip>
          {s?.reboot && <Badge size="sm" variant="light" color="blue">venter på genstart</Badge>}
        </Group>
      </Group>
      {s?.errors?.length > 0 && <Text size="xs" c="red" ml={46} mt={2}>{s.errors.slice(0, 2).join(' · ')}{s.errors.length > 2 ? ' …' : ''}</Text>}
      <Collapse expanded={open}>
        <Stack gap={8} ml={46} mt={6}>
          <Text size="sm"><b>Når du trykker Anvend:</b> <Text span c="dimmed" inherit>{i.description}</Text></Text>
          <Text size="sm"><b>Fortryd:</b> <Text span c="dimmed" inherit>{i.undo}</Text></Text>
          {i.needsReboot && <Text size="xs" c="dimmed">Virker først efter en genstart. Fleetdeck genstarter ikke selv pc'en.</Text>}
          {i.needsLogoff && <Text size="xs" c="dimmed">Ses først, når brugeren har logget af og på igen.</Text>}
          {i.note && <Text size="xs" c="yellow.5">{i.note}</Text>}
          <Text size="xs" c="dimmed">Gælder: {buildText(i)}{s ? ` · sidst tjekket: ${stateText(i, s.state)} (${ago(s.at)}${s.changed ? `, ${s.changed} ændret` : ''})` : ''}</Text>
          {i.registry.length > 0 && <Details title="Registrering">
            {i.registry.map((r) => {
              const key = `${r.hive}|${r.path}|${r.name}`.toLowerCase();
              const prev = before?.reg.filter((b) => b.k === key) ?? [];
              return (
                <div key={key}>
                  <Text size="xs" ff="monospace" c="dimmed" style={{ wordBreak: 'break-all' }}>{r.hive === 'HKLM' ? 'HKLM' : r.hive === 'DEFAULT' ? 'Standardprofil' : 'HKCU'}\{r.path}</Text>
                  <Group gap="xs" wrap="wrap"><Text size="xs" ff="monospace">{r.name}</Text><Text size="xs">→ {shown(r.value)}</Text>
                    <Text size="xs" c="dimmed">({HIVE[r.hive] ?? r.hive})</Text>
                    {prev.length > 0 && <Text size="xs" c="dimmed">før: {[...new Set(prev.map((p) => (p.e ? p.v ?? '?' : 'fandtes ikke')))].join(' / ')}</Text>}</Group>
                </div>
              );
            })}
          </Details>}
          {i.services.length > 0 && <Details title="Tjenester">{i.services.map((sv) => <Text key={sv.name} size="xs"><Text span ff="monospace">{sv.name}</Text> → {sv.start === 4 ? 'deaktiveret' : 'efter behov'}
            {before?.svc?.[sv.name] !== undefined && <Text span c="dimmed"> · før: {({ 2: 'automatisk', 3: 'efter behov', 4: 'deaktiveret' } as Record<number, string>)[before.svc[sv.name]] ?? before.svc[sv.name]}</Text>}</Text>)}</Details>}
          {i.tasks.length > 0 && <Details title="Planlagte opgaver (slås fra)">{i.tasks.map((t) => <Text key={t} size="xs" ff="monospace" style={{ wordBreak: 'break-all' }}>{t}</Text>)}</Details>}
          {i.apps.length > 0 && <Details title="Apps (fjernes for alle brugere og nye brugere)">
            <Group gap={6}>{i.apps.map((a) => <Badge key={a.name} size="sm" variant="outline" color={a.reinstallable ? 'gray' : 'red'}>{a.title}</Badge>)}</Group>
            {before?.appx?.length ? <Text size="xs" c="dimmed">Fjernet: {before.appx.join(', ')}</Text> : null}
          </Details>}
          {i.winget.length > 0 && <Details title="Afinstalleres for den bruger, der er logget ind">{i.winget.map((w) => <Text key={w.id} size="xs">{w.title} <Text span c="dimmed" ff="monospace">{w.id}</Text></Text>)}</Details>}
          {i.powerSettings.length > 0 && <Details title="Strømindstilling (den aktive plan)">{i.powerSettings.map((w) => <Text key={w.setting} size="xs">{POWER_NAMES[w.setting] ?? w.setting}:
            {w.ac != null ? ` på lysnet ${powerValue(w.setting, w.ac)}` : ''}{w.dc != null ? ` · på batteri ${powerValue(w.setting, w.dc)}` : ''}</Text>)}</Details>}
          {i.features.length > 0 && <Details title="Windows-funktioner"><Group gap={6}>{i.features.map((f) => <Badge key={f} size="sm" variant="outline" color="gray" styles={noTruncate}>{f}</Badge>)}</Group></Details>}
          {s?.hives?.length > 0 && <Details title="Pr. brugerprofil">
            <Group gap={6}>{s.hives.map((h) => <Badge key={h.h} size="sm" variant="light" color={h.ok ? 'green' : 'orange'}>{h.h === 'DEFAULT' ? 'Nye brugere' : h.n ?? h.h}: {h.ok ? 'ok' : 'mangler'}</Badge>)}</Group>
          </Details>}
          <Group gap="xs">
            <Button size="compact-sm" variant="light" disabled={m.busy} onClick={onApply}>{st === 'drift' ? 'Anvend igen' : 'Anvend dette punkt'}</Button>
            {canUndo && <Button size="compact-sm" variant="default" disabled={m.busy} onClick={onUndo}>Fortryd dette punkt</Button>}
          </Group>
          {!on && <Text size="xs" c="dimmed">Punktet er ikke valgt. Anvender du det her, tælles det ikke med i Anvend valgte.</Text>}
        </Stack>
      </Collapse>
    </div>
  );
}

const Details = ({ title, children }: { title: string; children: ReactNode }) => (
  <Stack gap={4} p={8} style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8 }}>
    <Text size="xs" fw={600}>{title}</Text>{children}
  </Stack>
);

// ------------------------------------------------------------------ flådeoversigten
const CELL: Record<CatState, { color: string; label: string; mark: string }> = {
  ok: { color: 'green', label: 'Anvendt', mark: '✓' }, drift: { color: 'orange', label: 'Vendt tilbage siden det blev anvendt', mark: '!' },
  pending: { color: 'gray', label: 'Valgt, ikke anvendt endnu', mark: '–' }, off: { color: 'dark', label: 'Ikke valgt', mark: '' },
};
const SHORT: Record<string, string> = { telemetry: 'Telemetri', ads: 'Reklamer', ai: 'AI', search: 'Websøgning', widgets: 'Widgets', consumer: 'Foreslåede apps', apps: 'Medfølgende apps', extra: 'Valgfrit',
  power: 'Strøm', ux: 'Brugervenlighed', network: 'Netværk', updates: 'Opdateringer', features: 'Funktioner' };

function Cell({ s }: { s: CatState }) {
  const c = CELL[s];
  return (
    <Tooltip label={c.label}>
      <Badge fullWidth radius="sm" size="lg" variant={s === 'off' ? 'outline' : 'light'} color={c.color} style={s === 'off' ? { borderStyle: 'dashed', opacity: 0.6 } : undefined}>{c.mark || ' '}</Badge>
    </Tooltip>
  );
}

/** Side: alle Windows-pc'er med tilstand pr. kategori, handlinger på flere ad gangen og en guide til at vælge niveau. */
export function DebloatPage() {
  const { isAdmin } = useMe();
  const nav = useNavigate();
  const { data: cat } = useCatalog();
  const { data: rows } = useApi<FleetRow[]>(['debloat-fleet'], isAdmin ? '/api/debloat/fleet' : null, { refetchInterval: 15_000 });
  const [sel, setSel] = useState<number[]>([]);
  const [guide, setGuide] = useState<FleetRow | null>(null);
  const [bulk, setBulk] = useState<'scan' | 'apply' | null>(null);
  const run = useAction((b: { machineIds: number[]; action: string }) => post<{ done: number[]; skipped: number[] }>('/api/debloat/bulk', b),
    (r) => `${r.done.length} maskiner sat i gang${r.skipped.length ? `, ${r.skipped.length} sprunget over` : ''}`);
  if (!isAdmin) return <Empty>Kun administratorer kan se debloat og privatliv.</Empty>;
  if (!cat || !rows) return <Loading />;
  const cats = cat.categories;
  const full = rows.filter((r) => r.selected > 0 && r.pending === 0 && r.drift === 0).length;
  const chosen = rows.filter((r) => sel.includes(r.machineId));
  return (
    <>
      <PageHeader title="Debloat & privatliv" sub="Reklamer, telemetri, apps, strøm og Windows-funktioner på alle Windows-pc'er. Næsten alt kan fortrydes fra pc'ens egen side.">
        <Button variant="default" disabled={!sel.length} onClick={() => setBulk('scan')}>Tjek markerede</Button>
        <Button disabled={!sel.length} onClick={() => setBulk('apply')}>Anvend på markerede{sel.length ? ` (${sel.length})` : ''}</Button>
      </PageHeader>
      <SimpleGrid cols={{ base: 2, sm: 4 }} mb="md">
        {[['Pc\'er', rows.length, undefined], ['Fuldt anvendt', full, 'green'], ['Vendt tilbage', rows.filter((r) => r.drift > 0).length, 'orange'],
          ['Ikke sat op', rows.filter((r) => !r.profile && r.selected === 0).length, undefined]].map(([l, v, c]) => (
          <Card key={String(l)} withBorder radius="md" p="sm"><Text size="xs" c="dimmed">{l}</Text><Text fw={700} size="xl" c={c as string | undefined}>{v}</Text></Card>
        ))}
      </SimpleGrid>
      <Panel p={0}>
        {rows.length === 0 ? <Empty>Ingen Windows-pc'er endnu.</Empty> : (
          <ScrollArea type="auto">
            <Table verticalSpacing="xs" miw={1640} highlightOnHover>
              <Table.Thead><Table.Tr>
                <Table.Th w={36}><Checkbox aria-label="Markér alle" checked={sel.length === rows.length} indeterminate={sel.length > 0 && sel.length < rows.length}
                  onChange={(e) => setSel(e.currentTarget.checked ? rows.map((r) => r.machineId) : [])} /></Table.Th>
                <Table.Th miw={210}>Pc</Table.Th><Table.Th miw={150}>Niveau</Table.Th>
                {cats.map((c) => <Table.Th key={c.id} ta="center" style={{ fontSize: 11 }}>{SHORT[c.id] ?? c.title}</Table.Th>)}
                <Table.Th>Sidst tjekket</Table.Th><Table.Th />
              </Table.Tr></Table.Thead>
              <Table.Tbody>
                {rows.map((r) => (
                  <Table.Tr key={r.machineId}>
                    <Table.Td><Checkbox aria-label={`Markér ${r.name}`} checked={sel.includes(r.machineId)}
                      onChange={(e) => setSel((s) => (e.currentTarget.checked ? [...s, r.machineId] : s.filter((x) => x !== r.machineId)))} /></Table.Td>
                    <Table.Td><UnstyledButton onClick={() => nav(`/maskiner/${r.machineId}/debloat`)}>
                      <Text fw={600} size="sm">{r.name}</Text>
                      <Text size="xs" c="dimmed">{[winName(r.build), r.build, r.edition].filter(Boolean).join(' · ') || (r.online ? 'online' : 'offline')}</Text>
                      {r.powerPlan && <Text size="xs" c="dimmed">Strøm: {r.powerPlan}</Text>}</UnstyledButton></Table.Td>
                    <Table.Td><Group gap={4} wrap="nowrap">{r.profile ? <Badge variant="light" styles={noTruncate}>{levelTitle(r.profile)}</Badge> : <Text size="sm" c="dimmed">{r.selected ? `Eget valg (${r.selected})` : 'Ikke valgt'}</Text>}
                      {r.profileNew > 0 && <Tooltip label={`Profilen har fået ${r.profileNew} nye punkter. Pc'en beholder sit valg, indtil de tages med.`}><Badge size="sm" variant="light" color="blue" styles={noTruncate}>+{r.profileNew}</Badge></Tooltip>}
                      {r.enforce && <Tooltip label={r.enforceMode === 'reapply' ? 'Holdes håndhævet: det, der vender tilbage, anvendes igen' : 'Holdes håndhævet: giver besked, når noget vender tilbage'}>
                        <ThemeIcon size="xs" variant="subtle" color="green"><IconShieldCheck size={14} /></ThemeIcon></Tooltip>}</Group></Table.Td>
                    {cats.map((c) => <Table.Td key={c.id} miw={74}><Cell s={r.cats[c.id] ?? 'off'} /></Table.Td>)}
                    <Table.Td><Text size="sm" c="dimmed">{r.busy ? 'kører nu …' : r.lastScan ? ago(r.lastScan) : 'aldrig'}</Text></Table.Td>
                    <Table.Td><Button size="compact-sm" variant="default" onClick={() => setGuide(r)}>Vælg niveau</Button></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea>
        )}
      </Panel>
      <Modal opened={bulk !== null} onClose={() => setBulk(null)} title={bulk === 'apply' ? 'Anvend på markerede pc\'er?' : 'Tjek markerede pc\'er?'}>
        <Stack>
          <List size="sm">{chosen.map((r) => <List.Item key={r.machineId}>{r.name}: {r.profile ? levelTitle(r.profile) : r.selected ? `eget valg (${r.selected} punkter)` : <Text span c="dimmed">intet valgt – springes over</Text>}</List.Item>)}</List>
          <Text size="xs" c="dimmed">{bulk === 'apply' ? 'Hver pc får de punkter, der er valgt på den (niveau og egne valg). Er systemgendannelse slået til, oprettes der først et gendannelsespunkt. Det kan fortrydes fra pc\'ens egen side.' : 'Læser kun tilstanden – ændrer intet.'}</Text>
          <Group justify="flex-end"><Button variant="default" onClick={() => setBulk(null)}>Annullér</Button>
            <Button loading={run.isPending} onClick={() => run.mutate({ machineIds: sel, action: bulk! }, { onSuccess: () => setBulk(null) })}>{bulk === 'apply' ? 'Anvend' : 'Tjek'}</Button></Group>
        </Stack>
      </Modal>
      {guide && <LevelGuide cat={cat} row={guide} onClose={() => setGuide(null)} />}
    </>
  );
}

/** Guide i tre trin: niveau → tilpas kategorier → anvend (med håndhævelse). */
function LevelGuide({ cat, row, onClose }: { cat: DebloatCatalog; row: FleetRow; onClose: () => void }) {
  const [step, setStep] = useState(0);
  const [level, setLevel] = useState<Exclude<Level, ''>>(row.profile || 'anbefalet');
  const [off, setOff] = useState<string[]>([]);   // kategorier fravalgt i trin 2
  const [enforce, setEnforce] = useState(row.profile ? row.enforce : true);
  const [mode, setMode] = useState(row.enforceMode === 'reapply' ? 'reapply' : 'alert');
  const profile = cat.profiles.find((p) => p.id === level)!;
  const items = useMemo(() => cat.items.filter((i) => profile.items.includes(i.id)), [cat, profile]);
  const chosen = items.filter((i) => !off.includes(i.category));
  const save = useAction(async (applyNow: boolean) => {
    await put(`/api/debloat/machines/${row.machineId}`, { profile: level, include: [], exclude: items.filter((i) => off.includes(i.category)).map((i) => i.id), enforce, enforceMode: mode });
    if (applyNow) await post(`/api/debloat/machines/${row.machineId}/apply`, { items: null });
    return applyNow;
  }, (applied) => (applied ? 'Gemt – anvendelsen er sat i gang' : 'Gemt – intet er ændret på pc\'en endnu'));
  return (
    <Modal opened onClose={onClose} title={`${row.name} · vælg niveau`} size="xl">
      <Stepper active={step} onStepClick={setStep} size="sm" mb="md">
        <Stepper.Step label="Niveau" />
        <Stepper.Step label="Tilpas" />
        <Stepper.Step label="Anvend" />
      </Stepper>
      {step === 0 && (
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          {cat.profiles.map((p) => (
            <Card key={p.id} withBorder radius="md" p="md" onClick={() => setLevel(p.id)} style={{ cursor: 'pointer', borderColor: level === p.id ? 'var(--mantine-primary-color-filled)' : undefined,
              boxShadow: level === p.id ? '0 0 0 1px var(--mantine-primary-color-filled) inset' : undefined }}>
              <Group justify="space-between" mb={4}><Text fw={700} size="lg">{p.title}</Text><Badge variant="light" color={p.id === 'let' ? 'green' : p.id === 'maks' ? 'red' : undefined}>{p.items.length} punkter</Badge></Group>
              <Text size="sm" c="dimmed">{p.description}</Text>
            </Card>
          ))}
        </SimpleGrid>
      )}
      {step === 1 && (
        <Stack gap="xs">
          <Text size="sm" c="dimmed">Fravælg hele kategorier her. Enkelte punkter vælges til og fra på pc'ens egen side. Når du gemmer, erstatter dette pc'ens egne til- og fravalg.</Text>
          {cat.categories.map((c) => {
            const n = items.filter((i) => i.category === c.id).length;
            return n ? <Checkbox key={c.id} checked={!off.includes(c.id)} onChange={(e) => setOff((o) => (e.currentTarget.checked ? o.filter((x) => x !== c.id) : [...o, c.id]))}
              label={<Text size="sm">{c.title} <Text span c="dimmed">· {n} punkter</Text></Text>} description={c.description} /> : null;
          })}
        </Stack>
      )}
      {step === 2 && (
        <Stack>
          <Summary cat={cat} items={chosen} mode="apply" />
          <Group gap="md" align="flex-start"><Switch checked={enforce} onChange={(e) => setEnforce(e.currentTarget.checked)} label="Hold håndhævet"
            description="Tjek dagligt og efter nye Windows-builds, om anvendte punkter er vendt tilbage" />
            <Stack gap={2}><SegmentedControl size="xs" disabled={!enforce} value={mode} onChange={setMode} data={[{ value: 'alert', label: 'Giv besked' }, { value: 'reapply', label: 'Anvend igen' }]} />
              <Text size="xs" c="dimmed">{ENFORCE_INFO[mode]}</Text></Stack></Group>
        </Stack>
      )}
      <Group justify="space-between" mt="lg">
        <Button variant="default" onClick={() => (step ? setStep(step - 1) : onClose())}>{step ? 'Tilbage' : 'Annullér'}</Button>
        <Group gap="xs">
          {step < 2 ? <Button onClick={() => setStep(step + 1)}>Næste</Button> : <>
            <Button variant="default" loading={save.isPending && save.variables === false} onClick={() => save.mutate(false, { onSuccess: onClose })}>Gem uden at anvende</Button>
            <Button loading={save.isPending && save.variables === true} disabled={!chosen.length} onClick={() => save.mutate(true, { onSuccess: onClose })}>Anvend {chosen.length} punkter</Button>
          </>}
        </Group>
      </Group>
    </Modal>
  );
}
