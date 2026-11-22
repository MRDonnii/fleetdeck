import { useEffect, useState } from 'react';
import {
  Alert, Badge, Box, Button, Code, Group, Modal, NumberInput, PasswordInput, ScrollArea, SegmentedControl, Select, Stack, Switch, Text, Textarea, TextInput,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAlertTriangle, IconArrowUpCircle, IconCircleCheck, IconFlask, IconLock, IconPlayerPlay, IconQuestionMark } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { post, put, useAction, useApi } from '../api';

// ---------------------------------------------------------------- typer (fra /api/apps)

export type AppStatus = 'new' | 'ok' | 'ahead' | 'unknown' | 'norelease';
export type ReadKind = 'http' | 'docker' | 'file' | 'command' | 'none';
export type PlaceKind = 'container' | 'service' | 'program';
export interface AppRelease { version: string; tag: string; name: string; notes: string; prerelease: boolean; published: string | null; url: string | null }
export interface AppRow {
  id: number; name: string; repo: string; channel: 'stable' | 'beta'; machineId: number | null; machine: string | null; online: boolean; platform: string | null;
  placeKind: PlaceKind; placeName: string; readKind: ReadKind; readTarget: string; readField: string; updateCommand: string; hasUpdateCommand: boolean;
  autoUpdate: boolean; neverAuto: boolean; notify: boolean;
  running: string | null; runningAt: string | null; readError: string | null; checked: string | null; checkError: string | null;
  status: AppStatus; latest: AppRelease | null; newer: AppRelease[]; releases: { version: string; prerelease: boolean; published: string | null }[];
  reading: boolean; updateJob: { id: number; status: string; progress: number | null; step: string | null; to: string | null } | null;
}
export interface AppRate { limit: number | null; remaining: number | null; reset: string | null; blockedUntil: string | null }
export interface AppsResponse {
  apps: AppRow[];
  settings: { hours: number; autoHour: number; hasToken: boolean; rate: AppRate | null };
  machines: { id: number; name: string; platform: string; online: boolean }[] | null;
}

export const STATUS: Record<AppStatus, { label: string; color: string; icon: typeof IconCircleCheck }> = {
  new: { label: 'Ny version', color: 'orange', icon: IconArrowUpCircle },
  ok: { label: 'Opdateret', color: 'teal', icon: IconCircleCheck },
  ahead: { label: 'Nyere end udgivelsen', color: 'blue', icon: IconFlask },
  unknown: { label: 'Version ukendt', color: 'gray', icon: IconQuestionMark },
  norelease: { label: 'Ingen udgivelse', color: 'gray', icon: IconQuestionMark },
};
export const READ: Record<ReadKind, string> = { http: 'HTTP', docker: 'Docker-tag', file: 'Fil', command: 'Kommando', none: 'Læses ikke' };
export const PLACE: Record<PlaceKind, string> = { container: 'container', service: 'tjeneste', program: 'program' };

/** Status med fejl: kan versionen ikke læses, siges det i stedet for "ukendt". */
export function appState(a: AppRow): { label: string; color: string; icon: typeof IconCircleCheck } {
  if (a.status === 'unknown' && a.readError) return { label: 'Kan ikke læse version', color: 'red', icon: IconAlertTriangle };
  if (a.status === 'unknown' && a.readKind === 'none') return { label: 'Seneste udgivelse', color: 'gray', icon: IconCircleCheck };
  return STATUS[a.status];
}

export function AppStatusBadge({ a }: { a: AppRow }) {
  const s = appState(a);
  const Icon = s.icon;
  return <Badge size="sm" variant="light" color={s.color} leftSection={<Icon size={12} />} styles={{ root: { flexShrink: 0 } }}>{s.label}</Badge>;
}

export const NeverBadge = () => <Badge size="sm" variant="light" color="orange" leftSection={<IconLock size={12} />} styles={{ root: { flexShrink: 0 } }}>Aldrig automatisk</Badge>;

/** Kanalen som et lille mærke. */
export const ChannelBadge = ({ c }: { c: string }) => c === 'beta'
  ? <Badge size="xs" variant="light" color="grape">Beta</Badge> : <Badge size="xs" variant="light" color="gray">Stabil</Badge>;

/** Udgivelsesnoter (Markdown fra GitHub) som enkel tekst: overskrifter fede, punkter med prik. */
export function Notes({ text, max = 40 }: { text: string; max?: number }) {
  const out: { kind: 'h' | 'li' | 'p' | 'code' | 'gap'; t: string }[] = [];
  let fence = false;
  for (const raw of text.split('\n')) {
    const l = raw.trimEnd();
    if (/^\s*```/.test(l)) { fence = !fence; continue; }
    if (fence) { out.push({ kind: 'code', t: l }); continue; }
    const c = l.replace(/\*\*/g, '').replace(/`/g, '');
    const h = /^\s*#{1,6}\s+(.*)$/.exec(c), b = /^\s*[-*+]\s+(.*)$/.exec(c);
    if (h) out.push({ kind: 'h', t: h[1] });
    else if (b) out.push({ kind: 'li', t: b[1] });
    else if (c.trim()) out.push({ kind: 'p', t: c });
    else if (out.length && out[out.length - 1].kind !== 'gap') out.push({ kind: 'gap', t: '' });
  }
  if (!out.some((x) => x.kind !== 'gap')) return <Text size="sm" c="dimmed">(ingen noter)</Text>;
  return (
    <Stack gap={2}>
      {out.slice(0, max).map((x, i) => x.kind === 'h' ? <Text key={i} size="sm" fw={700} mt={i ? 4 : 0}>{x.t}</Text>
        : x.kind === 'li' ? <Text key={i} size="sm">• {x.t}</Text>
        : x.kind === 'code' ? <Text key={i} size="xs" ff="monospace" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{x.t || ' '}</Text>
        : x.kind === 'p' ? <Text key={i} size="sm">{x.t}</Text> : <Box key={i} h={4} />)}
      {out.length > max && <Text size="xs" c="dimmed">… se resten på GitHub</Text>}
    </Stack>
  );
}

/** "i dag 08.12", "i går 17.40" eller "15. nov.". */
export function when(iso: string | null | undefined, withTime = true) {
  if (!iso) return '';
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');
  const t = d.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }).replace(':', '.');
  const day = new Date(); day.setHours(0, 0, 0, 0);
  const diff = Math.round((new Date(d).setHours(0, 0, 0, 0) - day.getTime()) / 86400000);
  const date = d.toLocaleDateString('da-DK', { day: 'numeric', month: 'short' });
  return diff === 0 ? (withTime ? `i dag ${t}` : 'i dag') : diff === -1 ? (withTime ? `i går ${t}` : 'i går') : date;
}

// ---------------------------------------------------------------- tilføj / ret app

export interface AppDraft {
  id?: number; name: string; repo: string; channel: 'stable' | 'beta'; machineId: number | null; placeKind: PlaceKind; placeName: string;
  readKind: ReadKind; readTarget: string; readField: string; updateCommand: string; autoUpdate: boolean; neverAuto: boolean; notify: boolean;
}
export const emptyApp = (): AppDraft => ({
  name: '', repo: '', channel: 'stable', machineId: null, placeKind: 'container', placeName: '', readKind: 'http', readTarget: '', readField: '',
  updateCommand: '', autoUpdate: false, neverAuto: false, notify: true,
});
export const draftOfApp = (a: AppRow): AppDraft => ({
  id: a.id, name: a.name, repo: a.repo, channel: a.channel, machineId: a.machineId, placeKind: a.placeKind, placeName: a.placeName, readKind: a.readKind,
  readTarget: a.readTarget, readField: a.readField, updateCommand: a.updateCommand, autoUpdate: a.autoUpdate, neverAuto: a.neverAuto, notify: a.notify,
});

const TARGET: Record<ReadKind, { label: string; placeholder: string; hint: string } | null> = {
  http: { label: 'Adresse', placeholder: 'http://192.0.2.10:8080/api/version', hint: 'Fleetdeck henter adressen og læser versionen i svaret.' },
  docker: { label: 'Container', placeholder: 'minapp', hint: 'Tom = containerens navn ovenfor. Versionen læses fra billedets tag eller mærket org.opencontainers.image.version.' },
  file: { label: 'Fil på maskinen', placeholder: '/opt/minapp/VERSION', hint: 'Agenten læser filen. Står versionen i JSON, så skriv feltet nedenfor.' },
  command: { label: 'Kommando', placeholder: 'minapp --version', hint: 'Agenten kører kommandoen og læser versionen i det, den skriver ud.' },
  none: null,
};

export function AppModal({ draft, machines, onClose }: { draft: AppDraft | null; machines: AppsResponse['machines']; onClose: () => void }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const qc = useQueryClient();
  const [f, setF] = useState<AppDraft>(draft ?? emptyApp());
  useEffect(() => { setF(draft ?? emptyApp()); }, [draft]);
  const set = <K extends keyof AppDraft>(k: K, v: AppDraft[K]) => setF((x) => ({ ...x, [k]: v }));
  const editing = f.id != null;
  const save = useAction(() => (editing ? put(`/api/apps/${f.id}`, f) : post('/api/apps', f)), editing ? 'Appen er gemt' : 'Appen er tilføjet – Fleetdeck tjekker GitHub');
  const target = TARGET[f.readKind];
  const opts = (machines ?? []).map((m) => ({ value: String(m.id), label: `${m.name}${m.online ? '' : ' (offline)'}` }));
  return (
    <Modal opened={!!draft} onClose={onClose} title={editing ? `Ret ${draft?.name}` : 'Tilføj app'} size="lg" fullScreen={mobile}>
      <Stack gap="sm">
        <Group grow align="flex-start" wrap={mobile ? 'wrap' : 'nowrap'}>
          <TextInput label="Navn" placeholder="Minapp" value={f.name} onChange={(e) => set('name', e.currentTarget.value)} data-autofocus />
          <TextInput label="GitHub-projekt" placeholder="ejer/minapp" value={f.repo} onChange={(e) => set('repo', e.currentTarget.value)} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
        </Group>
        <Box>
          <Text size="sm" fw={500} mb={4}>Kanal</Text>
          <SegmentedControl size="xs" value={f.channel} onChange={(v) => set('channel', v as AppDraft['channel'])}
            data={[{ value: 'stable', label: 'Stabil' }, { value: 'beta', label: 'Beta (også forhåndsudgaver)' }]} />
        </Box>
        <Group grow align="flex-start" wrap={mobile ? 'wrap' : 'nowrap'}>
          <Select label="Kører på" placeholder="Vælg maskine" data={opts} value={f.machineId != null ? String(f.machineId) : null} clearable searchable
            onChange={(v) => set('machineId', v ? Number(v) : null)} />
          <Select label="Som" data={[{ value: 'container', label: 'Container' }, { value: 'service', label: 'Tjeneste' }, { value: 'program', label: 'Program' }]}
            value={f.placeKind} onChange={(v) => v && set('placeKind', v as PlaceKind)} allowDeselect={false} />
          <TextInput label={f.placeKind === 'container' ? 'Containerens navn' : f.placeKind === 'service' ? 'Tjenestens navn' : 'Programmets navn'} placeholder={f.placeKind === 'container' ? 'minapp' : f.placeKind === 'service' ? 'minapp.service' : 'valgfrit'}
            value={f.placeName} onChange={(e) => set('placeName', e.currentTarget.value)} />
        </Group>
        <Box>
          <Text size="sm" fw={500} mb={4}>Kørende version læses fra</Text>
          <SegmentedControl size="xs" fullWidth={mobile} value={f.readKind} onChange={(v) => set('readKind', v as ReadKind)}
            data={(['http', 'docker', 'file', 'command', 'none'] as ReadKind[]).map((k) => ({ value: k, label: READ[k] }))} />
        </Box>
        {target && (f.readKind === 'command'
          ? <Textarea label={target.label} placeholder={target.placeholder} description={target.hint} value={f.readTarget} autosize minRows={1} maxRows={4}
              onChange={(e) => set('readTarget', e.currentTarget.value)} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
          : <TextInput label={target.label} placeholder={target.placeholder} description={target.hint} value={f.readTarget}
              onChange={(e) => set('readTarget', e.currentTarget.value)} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />)}
        {f.readKind !== 'none' && f.readKind !== 'docker' && (
          <TextInput label="JSON-felt (valgfrit)" placeholder="version eller data.app.version" value={f.readField} onChange={(e) => set('readField', e.currentTarget.value)}
            description="Tom = feltet version, eller det første versionsnummer i svaret." />
        )}
        {f.readKind === 'none' && <Text size="xs" c="dimmed">Fleetdeck viser kun seneste udgivelse og giver besked, når der kommer en ny.</Text>}
        <Textarea label="Opdatér-kommando (valgfrit)" placeholder={'cd /opt/minapp && ./opdater.sh {version}'} autosize minRows={2} maxRows={8}
          description="Din egen kommando. Den kører som et job på maskinen, altid efter bekræftelse. {version} og {tag} udfyldes. Tom = ingen knap."
          value={f.updateCommand} onChange={(e) => set('updateCommand', e.currentTarget.value)} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} />
        <Switch label="Aldrig automatisk" description="Fx en app, der styrer et anlæg. Så findes kun knappen Opdatér." checked={f.neverAuto}
          onChange={(e) => { const v = e.currentTarget.checked; setF((x) => ({ ...x, neverAuto: v, autoUpdate: v ? false : x.autoUpdate })); }} />
        <Switch label="Opdatér automatisk om natten" description="Kører Opdatér-kommandoen, når der er en ny version på kanalen (én gang pr. version)."
          checked={f.autoUpdate} disabled={f.neverAuto || !f.updateCommand.trim()} onChange={(e) => set('autoUpdate', e.currentTarget.checked)} />
        <Switch label="Send besked om nye versioner" description="Via Fleetdecks notifikationer – én gang pr. version." checked={f.notify} onChange={(e) => set('notify', e.currentTarget.checked)} />
        <Group justify="flex-end" gap="xs" mt="xs">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button loading={save.isPending} disabled={!f.name.trim() || !f.repo.trim()}
            onClick={() => save.mutate(undefined, { onSuccess: () => { qc.invalidateQueries({ queryKey: ['apps'] }); onClose(); } })}>{editing ? 'Gem' : 'Tilføj'}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

// ---------------------------------------------------------------- bekræft Opdatér

/** Bekræftelsen før Opdatér: versionerne, kommandoen præcis som den køres, og noterne. */
export function UpdateConfirm({ app, onClose }: { app: AppRow | null; onClose: () => void }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const qc = useQueryClient();
  const run = useAction((v: string) => post<{ jobId: number }>(`/api/apps/${app?.id}/update`, { version: v, confirm: true }), (r) => `Opdateringen er sendt (job ${r.jobId})`);
  if (!app?.latest) return null;
  const rel = app.latest;
  const cmd = app.updateCommand.replaceAll('{version}', rel.version).replaceAll('{tag}', rel.tag);
  return (
    <Modal opened onClose={onClose} title={`Opdatér ${app.name}?`} size="lg" fullScreen={mobile}>
      <Stack gap="sm">
        <Text size="sm">{app.running ?? 'Ukendt version'} → <b>{rel.version}</b>{app.machine ? ` på ${app.machine}` : ''}. Fleetdeck kører din kommando som et job:</Text>
        <Code block style={{ whiteSpace: 'pre-wrap' }}>{cmd}</Code>
        {app.neverAuto && <Alert color="orange" icon={<IconLock size={16} />} p="xs"><b>Aldrig automatisk.</b> Tjek, at det er et godt tidspunkt at opdatere.</Alert>}
        {!app.online && <Alert color="red" icon={<IconAlertTriangle size={16} />} p="xs">{app.machine ?? 'Maskinen'} er offline – jobbet kan ikke sendes nu.</Alert>}
        <ScrollArea.Autosize mah={mobile ? 260 : 300}><Box p="xs" style={{ background: 'var(--mantine-color-default-hover)', borderRadius: 8 }}><Notes text={rel.notes} /></Box></ScrollArea.Autosize>
        <Group justify="flex-end" gap="xs" style={mobile ? { flexDirection: 'column-reverse', alignItems: 'stretch' } : undefined}>
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button leftSection={<IconPlayerPlay size={16} />} loading={run.isPending} disabled={!app.online}
            onClick={() => run.mutate(rel.version, { onSuccess: () => { qc.invalidateQueries({ queryKey: ['apps'] }); onClose(); } })}>Opdatér til {rel.version}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

// ---------------------------------------------------------------- indstillinger

export function AppSettingsModal({ opened, settings, onClose }: { opened: boolean; settings: AppsResponse['settings']; onClose: () => void }) {
  const qc = useQueryClient();
  const [hours, setHours] = useState<number>(settings.hours);
  const [autoHour, setAutoHour] = useState<number>(settings.autoHour);
  const [token, setToken] = useState('');
  useEffect(() => { if (opened) { setHours(settings.hours); setAutoHour(settings.autoHour); setToken(''); } }, [opened, settings]);
  const save = useAction((body: { hours: number; autoHour: number; token?: string }) => put('/api/apps/settings', body), 'Indstillingerne er gemt');
  const done = () => { qc.invalidateQueries({ queryKey: ['apps'] }); onClose(); };
  return (
    <Modal opened={opened} onClose={onClose} title="Indstillinger for Apps" size="md">
      <Stack gap="sm">
        <NumberInput label="Tjek GitHub hver" suffix=" time(r)" min={1} max={168} value={hours} onChange={(v) => setHours(Number(v) || 6)} />
        <NumberInput label="Automatisk opdatering kl." min={0} max={23} value={autoHour} onChange={(v) => setAutoHour(Number(v) || 0)}
          description="Kun apps med automatisk opdatering slået til – aldrig apps markeret aldrig automatisk." />
        <PasswordInput label="GitHub-nøgle (valgfri)" placeholder={settings.hasToken ? 'Gemt – skriv en ny for at skifte' : 'github_pat_…'} value={token}
          onChange={(e) => setToken(e.currentTarget.value)} description="Til private projekter og en højere grænse (5.000 i timen i stedet for 60). Gemmes krypteret og kommer aldrig med i opsætningsfilen." />
        <Group justify="space-between" gap="xs">
          {settings.hasToken ? <Button variant="subtle" color="red" size="xs" onClick={() => save.mutate({ hours, autoHour, token: '' }, { onSuccess: done })}>Fjern nøglen</Button> : <span />}
          <Group gap="xs">
            <Button variant="default" onClick={onClose}>Annullér</Button>
            <Button loading={save.isPending} onClick={() => save.mutate({ hours, autoHour, ...(token ? { token } : {}) }, { onSuccess: done })}>Gem</Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}

// ---------------------------------------------------------------- forsiden

/** Forsiden: én linje, når en af dine egne apps har en ny version (intet, hvis alt er opdateret). */
export function AppUpdatesLine() {
  const { data } = useApi<{ total: number; updates: { id: number; name: string; running: string | null; latest: string; neverAuto: boolean }[] }>(
    ['apps', 'summary'], '/api/apps/summary', { refetchInterval: 120_000 });
  const nav = useNavigate();
  if (!data?.updates.length) return null;
  const u = data.updates;
  const names = u.slice(0, 3).map((a) => `${a.name} ${a.latest}`).join(', ') + (u.length > 3 ? ` +${u.length - 3}` : '');
  return (
    <Alert color="orange" variant="light" p="xs" icon={<IconArrowUpCircle size={18} />} style={{ cursor: 'pointer' }} onClick={() => nav('/apps')}>
      <Text size="sm"><b>{u.length === 1 ? 'Ny version af en af dine apps' : `${u.length} af dine apps har en ny version`}:</b> {names}. <Text span c="blue" size="sm">Se Apps</Text></Text>
    </Alert>
  );
}
