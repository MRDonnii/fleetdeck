import { useEffect, useMemo, useState } from 'react';
import { ActionIcon, Badge, Button, Checkbox, Code, Group, Modal, NumberInput, Paper, Select, SimpleGrid, Stack, Switch, Text, TextInput, Title, Tooltip } from '@mantine/core';
import { LineChart } from '@mantine/charts';
import { modals } from '@mantine/modals';
import { IconPlayerPause, IconPlayerPlay, IconPencil, IconPlus, IconRefresh, IconTrash } from '@tabler/icons-react';
import { del, post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading } from '../components';
import { CopyButton } from '../design/copy';
import { ago } from '../format';
import type { MachineRow } from '../types';


interface Beat { ts: string; up: boolean; ms?: number | null; message?: string | null }
export interface Check {
  id: number; name: string; type: string; target: string; machineId?: number | null; machine?: string | null; runOnMachineId?: number | null; runOn?: string | null;
  group?: string | null; keyword?: string | null; keywordInvert: boolean; expectStatus?: string | null; ignoreTls: boolean; certWarnDays: number; expect?: string | null;
  intervalSec: number; timeoutSec: number; failThreshold: number; notify: boolean; enabled: boolean; state: 'up' | 'down' | 'pending' | 'paused';
  lastCheck?: string | null; lastChange?: string | null; lastMs?: number | null; lastMessage?: string | null; certExpires?: string | null; lastPush?: string | null;
  pushUrl?: string | null; uptime24?: number | null; uptime7?: number | null; uptime30?: number | null; avgMs?: number | null; beats: Beat[];
}

const TYPES = [
  { value: 'http', label: 'Hjemmeside (HTTP/HTTPS)' }, { value: 'tcp', label: 'Port (TCP)' }, { value: 'ping', label: 'Ping' },
  { value: 'dns', label: 'DNS-navn' }, { value: 'container', label: 'Docker-container (Unraid)' }, { value: 'push', label: 'Push (tjenesten melder selv)' },
];
const TYPE_SHORT: Record<string, string> = { http: 'HTTP', tcp: 'TCP', ping: 'Ping', dns: 'DNS', container: 'Container', push: 'Push' };
const COLOR = { up: 'var(--fd-ok)', down: 'var(--fd-bad)', pending: 'var(--fd-warn)', paused: 'var(--fd-off)' };
const STATE_LABEL = { up: 'Oppe', down: 'Nede', pending: 'Venter', paused: 'Pause' };
const pct = (v?: number | null) => v == null ? '–' : `${v === 100 ? '100' : v.toFixed(2).replace('.', ',')} %`;
const every = (s: number) => s % 3600 === 0 ? `hver ${s / 3600 === 1 ? 'time' : `${s / 3600}. time`}` : s % 60 === 0 ? (s === 60 ? 'hvert minut' : `hvert ${s / 60}. minut`) : `hvert ${s}. sek.`;

function Bar({ beats, n }: { beats: Beat[]; n: number }) {
  const list = beats.slice(-n);
  const pad = Array.from({ length: Math.max(0, n - list.length) });
  return (
    <div className="fd-hb">
      {pad.map((_, i) => <i key={`p${i}`} style={{ background: 'var(--fd-border)' }} />)}
      {list.map((b, i) => <Tooltip key={i} label={`${new Date(b.ts).toLocaleString('da-DK', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' })} · ${b.up ? 'oppe' : 'nede'}${b.ms != null ? ` · ${b.ms} ms` : ''}${b.message ? ` · ${b.message}` : ''}`} withinPortal>
        <i style={{ background: b.up ? COLOR.up : COLOR.down }} /></Tooltip>)}
    </div>
  );
}

function Pill({ s }: { s: Check['state'] }) {
  const c = { up: 'teal', down: 'red', pending: 'yellow', paused: 'gray' }[s];
  return <Badge size="sm" color={c} variant="light">{STATE_LABEL[s]}</Badge>;
}

export function MonitoringPage() {
  const { isAdmin } = useMe();
  const { data, isLoading } = useApi<Check[]>(['checks'], '/api/checks', { refetchInterval: 20_000 });
  const [sel, setSel] = useState<number | null>(null);
  const [edit, setEdit] = useState<Check | 'new' | null>(null);
  const checks = data ?? [];
  useEffect(() => { if (sel == null && checks.length) setSel((checks.find((c) => c.state === 'down') ?? checks[0]).id); }, [checks, sel]);
  if (isLoading) return <Loading />;
  const n = (s: string) => checks.filter((c) => c.state === s).length;
  const groups = checks.reduce<Record<string, Check[]>>((g, c) => { (g[c.group ?? 'Uden gruppe'] ??= []).push(c); return g; }, {});
  const current = checks.find((c) => c.id === sel);
  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-end">
        <div><Title order={2} style={{ letterSpacing: '-0.02em' }}>Overvågning</Title>
          <Text c="dimmed" size="sm" mt={2}>{checks.length} tjek · {n('up')} oppe{n('down') ? <> · <Text span c="red" inherit>{n('down')} nede</Text></> : ''}{n('pending') ? ` · ${n('pending')} venter` : ''}{n('paused') ? ` · ${n('paused')} på pause` : ''}</Text></div>
        {isAdmin && <Group gap="xs"><StatusPageButton checks={checks} />
          <Button className="fd-primary" leftSection={<IconPlus size={16} />} onClick={() => setEdit('new')}>Nyt tjek</Button></Group>}
      </Group>
      {checks.length === 0 ? <Empty>Ingen tjek endnu. Tilføj en hjemmeside, en port eller en container, der skal holdes øje med.</Empty> : (
        <div className="fd-mon">
          <Paper p={8}>
            {Object.entries(groups).map(([g, list]) => (
              <div key={g}>
                <Text size="xs" tt="uppercase" c="dimmed" fw={600} px={12} pt={10} pb={4} style={{ letterSpacing: '.06em' }}>{g}</Text>
                {list.map((c) => (
                  <div key={c.id} className="fd-mon-row" data-sel={c.id === sel || undefined} onClick={() => setSel(c.id)}>
                    <span className="fd-dot" style={{ background: COLOR[c.state] }} />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <Text size="sm" fw={600} truncate>{c.name}</Text>
                      <Text size="xs" c="dimmed" truncate>{TYPE_SHORT[c.type]} · {c.type === 'container' ? c.machine : c.type === 'push' ? (c.lastPush ? `meldte sig ${ago(c.lastPush)}` : 'venter på første signal') : c.target}{c.runOn ? ` · fra ${c.runOn}` : ''}</Text>
                    </div>
                    <Bar beats={c.beats} n={24} />
                    <Text size="sm" fw={600} w={64} ta="right" c={c.state === 'down' ? 'red' : undefined}>{pct(c.uptime24)}</Text>
                  </div>
                ))}
              </div>
            ))}
          </Paper>
          {current && <Detail c={current} isAdmin={isAdmin} onEdit={() => setEdit(current)} onDeleted={() => setSel(null)} />}
        </div>
      )}
      {edit && <CheckForm initial={edit === 'new' ? null : edit} onClose={() => setEdit(null)} />}
    </Stack>
  );
}

function Detail({ c, isAdmin, onEdit, onDeleted }: { c: Check; isAdmin: boolean; onEdit: () => void; onDeleted: () => void }) {
  const [hours, setHours] = useState('24');
  const { data: hist } = useApi<{ points: { ts: string; ms?: number | null; up: boolean }[]; events: { ts: string; up: boolean; message?: string | null }[] }>(
    ['check-beats', c.id, hours], `/api/checks/${c.id}/beats?hours=${hours}`, { refetchInterval: 60_000 });
  const run = useAction(() => post(`/api/checks/${c.id}/run`), 'Tjekket');
  const pause = useAction(() => post(`/api/checks/${c.id}/pause`), c.enabled ? 'Sat på pause' : 'Startet igen');
  const remove = useAction(() => del(`/api/checks/${c.id}`), 'Tjekket er slettet');
  const long = Number(hours) > 36;
  const rows = useMemo(() => (hist?.points ?? []).map((p) => ({
    t: new Date(p.ts).toLocaleString('da-DK', long ? { day: '2-digit', month: '2-digit', hour: '2-digit' } : { hour: '2-digit', minute: '2-digit' }), ms: p.ms ?? null,
  })), [hist, long]);
  const certDays = c.certExpires ? Math.floor((new Date(c.certExpires).getTime() - Date.now()) / 86400_000) : null;
  const since = c.lastChange ? new Date(c.lastChange).toLocaleString('da-DK', new Date(c.lastChange).toDateString() === new Date().toDateString()
    ? { hour: '2-digit', minute: '2-digit' } : { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : null;
  return (
    <Paper p="lg">
      <Group justify="space-between" align="flex-start" wrap="nowrap">
        <div style={{ minWidth: 0 }}>
          <Group gap="xs"><Title order={3}>{c.name}</Title><Pill s={c.state} /></Group>
          <Text size="sm" c="dimmed" truncate>{c.type === 'container' ? `Container ${c.target} på ${c.machine}` : c.type === 'push' ? 'Push-tjek' : c.target} · {every(c.intervalSec)}
            {c.runOn ? ` · tjekkes fra ${c.runOn}` : ''}{since && c.state !== 'pending' ? ` · ${c.state === 'down' ? 'nede' : c.state === 'paused' ? 'på pause' : 'oppe'} siden ${since}` : ''}</Text>
        </div>
        {isAdmin && <Group gap={6} wrap="nowrap">
          <Tooltip label="Tjek nu"><ActionIcon aria-label="Tjek nu" variant="default" loading={run.isPending} onClick={() => run.mutate(undefined)}><IconRefresh size={16} /></ActionIcon></Tooltip>
          <Tooltip label={c.enabled ? 'Pause' : 'Start igen'}><ActionIcon aria-label={c.enabled ? 'Pause' : 'Start igen'} variant="default" onClick={() => pause.mutate(undefined)}>{c.enabled ? <IconPlayerPause size={16} /> : <IconPlayerPlay size={16} />}</ActionIcon></Tooltip>
          <Tooltip label="Redigér"><ActionIcon aria-label="Redigér" variant="default" onClick={onEdit}><IconPencil size={16} /></ActionIcon></Tooltip>
          <Tooltip label="Slet"><ActionIcon aria-label="Slet" variant="default" color="red" onClick={() => modals.openConfirmModal({ title: `Slet ${c.name}?`, children: <Text size="sm">Tjekket og dets historik slettes.</Text>,
            labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: () => remove.mutate(undefined, { onSuccess: onDeleted }) })}><IconTrash size={16} /></ActionIcon></Tooltip>
        </Group>}
      </Group>
      <div style={{ marginTop: 14 }}><Bar beats={c.beats} n={60} /></div>
      {c.lastMessage && <Text size="sm" mt={8} c={c.state === 'down' ? 'red' : 'dimmed'}>{c.lastMessage}</Text>}
      <SimpleGrid cols={{ base: 2, md: 4 }} mt="md">
        <Tile label="Svartid nu" value={c.lastMs != null ? `${c.lastMs} ms` : '–'} sub={c.avgMs != null ? `gns. ${c.avgMs} ms (24 t)` : undefined} />
        <Tile label="Oppe 24 t" value={pct(c.uptime24)} />
        <Tile label="Oppe 30 d" value={pct(c.uptime30)} sub={`7 d: ${pct(c.uptime7)}`} />
        <Tile label={certDays != null ? 'Certifikat' : 'Sidst tjekket'} value={certDays != null ? `${certDays} dage` : c.lastCheck ? ago(c.lastCheck) : '–'}
          warn={certDays != null && certDays <= c.certWarnDays} />
      </SimpleGrid>
      {c.type === 'push' && c.pushUrl && <Paper p="sm" mt="md" withBorder>
        <Text size="sm" fw={600}>Push-adresse</Text>
        <Text size="xs" c="dimmed" mb={6}>Kald den fra fx et backup-script, når det er færdigt (GET eller POST). Tilføj ?status=down&amp;msg=… ved fejl.</Text>
        <Group wrap="nowrap"><Code block style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{`${window.location.origin}${c.pushUrl}`}</Code>
          <CopyButton value={`curl -fsS -m 10 "${window.location.origin}${c.pushUrl}"`}>{({ copied, copy }) => <Button size="xs" onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér curl'}</Button>}</CopyButton></Group>
      </Paper>}
      <Group justify="space-between" mt="lg" mb={6}>
        <Text size="sm" fw={600}>Svartid</Text>
        <Select size="xs" w={140} value={hours} onChange={(v) => setHours(v ?? '24')} allowDeselect={false}
          data={[{ value: '6', label: '6 timer' }, { value: '24', label: '24 timer' }, { value: '168', label: '7 dage' }, { value: '720', label: '30 dage' }]} />
      </Group>
      {rows.some((r) => r.ms != null)
        ? <LineChart h={180} data={rows} dataKey="t" unit=" ms" curveType="monotone" withDots={false} connectNulls series={[{ name: 'ms', label: 'Svartid', color: 'blue.6' }]} />
        : <Text size="sm" c="dimmed">Denne type tjek har ingen svartid.</Text>}
      <Text size="sm" fw={600} mt="lg" mb={6}>Hændelser</Text>
      {(hist?.events ?? []).length === 0 ? <Text size="sm" c="dimmed">Ingen skift i perioden.</Text> :
        <Stack gap={0}>{[...(hist?.events ?? [])].reverse().slice(0, 12).map((e, i) => (
          <Group key={i} gap="sm" py={6} style={{ borderTop: '1px solid var(--fd-border)' }} wrap="nowrap">
            <Badge size="sm" variant="light" color={e.up ? 'teal' : 'red'}>{e.up ? 'Oppe' : 'Nede'}</Badge>
            <Text size="xs" c="dimmed" w={110}>{new Date(e.ts).toLocaleString('da-DK', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })}</Text>
            <Text size="sm" truncate>{e.message}</Text>
          </Group>))}</Stack>}
    </Paper>
  );
}

function Tile({ label, value, sub, warn }: { label: string; value: string; sub?: string; warn?: boolean }) {
  return (
    <Paper p="sm" withBorder style={warn ? { borderColor: 'var(--fd-warn)' } : undefined}>
      <Text size="xs" c="dimmed">{label}</Text>
      <Text size="lg" fw={700} c={warn ? 'yellow' : undefined}>{value}</Text>
      {sub && <Text size="xs" c="dimmed">{sub}</Text>}
    </Paper>
  );
}

function CheckForm({ initial, onClose }: { initial: Check | null; onClose: () => void }) {
  const { data: machines = [] } = useApi<MachineRow[]>(['machines'], '/api/machines');
  const [f, setF] = useState(() => ({
    name: initial?.name ?? '', type: initial?.type ?? 'http', target: initial?.target ?? '', machineId: initial?.machineId ?? null as number | null,
    runOnMachineId: initial?.runOnMachineId ?? null as number | null, group: initial?.group ?? '', keyword: initial?.keyword ?? '', keywordInvert: initial?.keywordInvert ?? false,
    expectStatus: initial?.expectStatus ?? '', ignoreTls: initial?.ignoreTls ?? false, certWarnDays: initial?.certWarnDays ?? 14, expect: initial?.expect ?? '',
    intervalSec: initial?.intervalSec ?? 60, timeoutSec: initial?.timeoutSec ?? 10, failThreshold: initial?.failThreshold ?? 2, notify: initial?.notify ?? true,
  }));
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF((x) => ({ ...x, [k]: v }));
  const save = useAction(() => initial ? put(`/api/checks/${initial.id}`, f) : post('/api/checks', f), initial ? 'Gemt' : 'Tjekket er oprettet');
  const servers = machines.filter((m) => /unraid/i.test(m.os ?? ''));
  const canRunOn = ['http', 'tcp', 'ping', 'dns'].includes(f.type);
  const placeholder = { http: 'https://eksempel.dk', tcp: '192.168.1.25:8123', ping: '192.168.1.1', dns: 'eksempel.dk', container: 'fx immich', push: '' }[f.type] ?? '';
  return (
    <Modal opened onClose={onClose} title={initial ? `Redigér ${initial.name}` : 'Nyt tjek'} size="lg">
      <Stack gap="sm">
        <SimpleGrid cols={2}>
          <Select label="Type" data={TYPES} value={f.type} onChange={(v) => set('type', v ?? 'http')} allowDeselect={false} disabled={!!initial} comboboxProps={{ withinPortal: true }} />
          <TextInput label="Navn" value={f.name} onChange={(e) => set('name', e.currentTarget.value)} placeholder="fx Home Assistant" />
        </SimpleGrid>
        {f.type === 'container' && <Select label="Unraid-server" data={servers.map((m) => ({ value: String(m.id), label: m.name }))} value={f.machineId ? String(f.machineId) : null}
          onChange={(v) => set('machineId', v ? Number(v) : null)} comboboxProps={{ withinPortal: true }} />}
        {f.type !== 'push' && <TextInput label={f.type === 'container' ? 'Containerens navn' : f.type === 'http' ? 'Adresse' : f.type === 'tcp' ? 'Vært og port' : 'Vært'} value={f.target}
          onChange={(e) => set('target', e.currentTarget.value)} placeholder={placeholder} />}
        {f.type === 'http' && <>
          <SimpleGrid cols={2}>
            <TextInput label="Nøgleord (valgfrit)" value={f.keyword} onChange={(e) => set('keyword', e.currentTarget.value)} description="Skal stå på siden" />
            <TextInput label="Godkendte statuskoder" value={f.expectStatus} onChange={(e) => set('expectStatus', e.currentTarget.value)} placeholder="200-399" description="fx 200,204 eller 2xx" />
          </SimpleGrid>
          <Group><Checkbox label="Nøgleordet må IKKE stå der" checked={f.keywordInvert} onChange={(e) => set('keywordInvert', e.currentTarget.checked)} />
            <Checkbox label="Ignorér certifikatfejl" checked={f.ignoreTls} onChange={(e) => set('ignoreTls', e.currentTarget.checked)} /></Group>
          <NumberInput label="Advar når certifikatet udløber om (dage)" value={f.certWarnDays} min={0} max={365} onChange={(v) => set('certWarnDays', Number(v) || 0)} w={260} />
        </>}
        {f.type === 'dns' && <TextInput label="Svaret skal indeholde (valgfrit)" value={f.expect} onChange={(e) => set('expect', e.currentTarget.value)} placeholder="fx 192.168.1.25" />}
        {canRunOn && <Select label="Tjek fra" clearable placeholder="Fleetdeck-serveren" value={f.runOnMachineId ? String(f.runOnMachineId) : null}
          onChange={(v) => set('runOnMachineId', v ? Number(v) : null)} comboboxProps={{ withinPortal: true }}
          description="Vælg en maskine, hvis serveren ikke selv kan nå adressen (fx Unraid-værten selv)"
          data={machines.filter((m) => !/unraid/i.test(m.os ?? '')).map((m) => ({ value: String(m.id), label: m.name }))} />}
        <SimpleGrid cols={3}>
          <NumberInput label="Interval (sek.)" value={f.intervalSec} min={20} max={86400} onChange={(v) => set('intervalSec', Number(v) || 60)} />
          {f.type !== 'push' && f.type !== 'container' && <NumberInput label="Ventetid (sek.)" value={f.timeoutSec} min={1} max={120} onChange={(v) => set('timeoutSec', Number(v) || 10)} />}
          <NumberInput label="Nede efter fejl i træk" value={f.failThreshold} min={1} max={10} onChange={(v) => set('failThreshold', Number(v) || 2)} />
        </SimpleGrid>
        <TextInput label="Gruppe" value={f.group} onChange={(e) => set('group', e.currentTarget.value)} placeholder="fx Hjem, Server, Hjemmesider" />
        <Switch label="Giv besked, når den går ned og kommer op igen" checked={f.notify} onChange={(e) => set('notify', e.currentTarget.checked)} />
        {f.type === 'push' && <Text size="xs" c="dimmed">Efter oprettelsen får tjekket en hemmelig adresse, som tjenesten skal kalde inden for intervallet.</Text>}
        <Group justify="flex-end" mt="sm"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>{initial ? 'Gem' : 'Opret'}</Button></Group>
      </Stack>
    </Modal>
  );
}

interface StatusConf { enabled: boolean; title: string; token: string; checks: number[]; showMessages: boolean }

/** Statusside: en skrivebeskyttet side med udvalgte tjek, som kan deles med et hemmeligt link. */
function StatusPageButton({ checks }: { checks: { id: number; name: string; group?: string | null }[] }) {
  const [open, setOpen] = useState(false);
  const { data } = useApi<StatusConf>(['status-page'], open ? '/api/status-page' : null);
  const [f, setF] = useState<StatusConf | null>(null);
  useEffect(() => { if (data) setF(data); }, [data]);
  const save = useAction((c: StatusConf) => put<StatusConf>('/api/status-page', c), 'Statussiden er gemt');
  const relink = useAction(() => post<StatusConf>('/api/status-page/new-link'), 'Nyt link lavet – det gamle virker ikke mere');
  const url = f?.token ? `${window.location.origin}/status/${f.token}` : '';
  return (
    <>
      <Button variant="default" onClick={() => setOpen(true)}>Statusside</Button>
      <Modal opened={open} onClose={() => setOpen(false)} title="Statusside" size="lg">
        {!f ? <Loading /> : (
          <Stack>
            <Text size="sm" c="dimmed">En enkel side med de valgte tjek og deres oppetid – uden login. Del linket med dem, der skal kunne se status.</Text>
            <Switch label="Statussiden er slået til" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.currentTarget.checked })} />
            <TextInput label="Overskrift" value={f.title} onChange={(e) => setF({ ...f, title: e.currentTarget.value })} />
            <Switch label="Vis fejlbeskeden, når noget er nede" checked={f.showMessages} onChange={(e) => setF({ ...f, showMessages: e.currentTarget.checked })} />
            <div>
              <Group justify="space-between" mb={6}><Text size="sm" fw={500}>Tjek på siden</Text>
                <Group gap={4}><Button size="compact-xs" variant="subtle" onClick={() => setF({ ...f, checks: checks.map((c) => c.id) })}>Vælg alle</Button>
                  <Button size="compact-xs" variant="subtle" onClick={() => setF({ ...f, checks: [] })}>Ingen</Button></Group></Group>
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing={6}>{checks.map((c) => (
                <Checkbox key={c.id} label={c.group ? `${c.name} · ${c.group}` : c.name} checked={f.checks.includes(c.id)}
                  onChange={() => setF({ ...f, checks: f.checks.includes(c.id) ? f.checks.filter((x) => x !== c.id) : [...f.checks, c.id] })} />))}</SimpleGrid>
            </div>
            {f.enabled && url && <Group gap={6}><TextInput size="xs" readOnly value={url} style={{ flex: 1 }} />
              <CopyButton value={url}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
              <Button size="xs" variant="default" component="a" href={url} target="_blank">Åbn</Button></Group>}
            <Group justify="space-between">
              <Button variant="subtle" color="red" size="xs" onClick={() => relink.mutate(undefined, { onSuccess: setF })}>Lav nyt link</Button>
              <Button onClick={() => save.mutate(f, { onSuccess: setF })}>Gem</Button>
            </Group>
          </Stack>)}
      </Modal>
    </>
  );
}
