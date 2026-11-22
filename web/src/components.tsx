import { useState, type MouseEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router';
import {
  Anchor, Badge, Box, Button, Center, Checkbox, Code, Group, Loader, Modal, NumberInput, Paper, Progress, RingProgress, ScrollArea, Select,
  Stack, Switch, Table, Text, Textarea, TextInput, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import { LineChart } from '@mantine/charts';
import { useDisclosure, useMediaQuery } from '@mantine/hooks';
import { get, notifyErr, post, useAction, useApi, useJobProgress } from './api';
import { ago, COMP, duration, fmt, HEALTH, JOB, options, RUNAS, SOURCE, STEPS } from './format';
import type { AlertRow, ComplianceSummary, HealthState, JobDetail, JobRow, MachineRow, Metric, Package, Profile, Script } from './types';
import { useMe } from './auth';
import { osBadge, osDetail, type OsInput } from './design/osLabel';
import { AlertCard, InsightBox } from './helper';

// ---------------------------------------------------------------- små byggeklodser
export function PageHeader({ title, sub, children }: { title: ReactNode; sub?: ReactNode; children?: ReactNode }) {
  return (
    <Group justify="space-between" align="flex-end" mb="lg" wrap="wrap" gap="sm">
      <Box style={{ minWidth: 0 }}>
        <Title order={2} fz={{ base: 22, sm: 24 }}>{title}</Title>
        {sub && <Text c="dimmed" fz={{ base: 14, sm: 14 }}>{sub}</Text>}
      </Box>
      {children && <Group gap="xs" wrap="wrap" w={{ base: '100%', sm: 'auto' }} className="ws-header-actions">{children}</Group>}
    </Group>
  );
}

export const Loading = () => <Center h={200}><Loader /></Center>;

export function StatusBadge({ map, value }: { map: Record<string, { label: string; color: string }>; value: string }) {
  const l = map[value] ?? { label: value, color: 'gray' };
  return <Badge color={l.color} variant="light" radius="sm" styles={noTruncate}>{l.label}</Badge>;
}
/** Mærker må aldrig forkortes med "…" i smalle tabelkolonner. */
export const noTruncate = { root: { flexShrink: 0, maxWidth: 'none', overflow: 'visible' }, label: { overflow: 'visible', textOverflow: 'clip' } } as const;

export const OnlineDot = ({ online }: { online: boolean }) => (
  <Tooltip label={online ? 'Online' : 'Offline'}>
    <Box component="span" w={9} h={9} style={{ borderRadius: 9, display: 'inline-block', flex: 'none',
      background: online ? 'var(--mantine-color-green-6)' : 'var(--mantine-color-gray-5)',
      boxShadow: online ? '0 0 0 3px var(--mantine-color-green-light)' : undefined }} />
  </Tooltip>
);

export function HealthCell({ state, score }: { state: HealthState; score: number | null }) {
  return <Group gap={6} wrap="nowrap"><StatusBadge map={HEALTH} value={state || 'unknown'} />{state !== 'unknown' && <Text size="xs" c="dimmed" fw={600}>{score}</Text>}</Group>;
}

export function UsageBar({ value, warn = 80, bad = 92, w = 80 }: { value: number | null | undefined; warn?: number; bad?: number; w?: number }) {
  if (value === null || value === undefined) return <Text c="dimmed" size="sm">–</Text>;
  return <Tooltip label={`${value.toFixed(0)} %`}><Progress w={w} value={value} color={value >= bad ? 'red' : value >= warn ? 'yellow' : 'green'} /></Tooltip>;
}

export function ComplianceCell({ c }: { c: ComplianceSummary }) {
  if (!c?.total) return <Text c="dimmed" size="sm">–</Text>;
  return (
    <Group gap={8} wrap="nowrap">
      <Progress w={70} value={c.pct} color={c.failed ? 'red' : c.missing ? 'yellow' : 'green'} />
      <Text size="xs" c="dimmed">{c.ok}/{c.total}</Text>
    </Group>
  );
}

export function Gauge({ value, label, unit = '%', max = 100 }: { value: number | null | undefined; label: string; unit?: string; max?: number }) {
  const v = value ?? null;
  const frac = v === null ? 0 : Math.min(1, v / max);
  const color = v === null ? 'gray' : frac >= 0.9 ? 'red' : frac >= 0.75 ? 'yellow' : 'green';
  return (
    <Stack align="center" gap={0}>
      <RingProgress size={110} thickness={10} roundCaps sections={[{ value: frac * 100, color }]}
        label={<Text ta="center" fw={700} size="lg">{v === null ? '–' : Math.round(v)}<Text span size="xs" c="dimmed">{unit}</Text></Text>} />
      <Text size="xs" c="dimmed" ta="center">{label}</Text>
    </Stack>
  );
}

export function KeyValue({ rows }: { rows: [string, ReactNode][] }) {
  return (
    <Table withRowBorders={false} verticalSpacing={4} horizontalSpacing={0}>
      <Table.Tbody>
        {rows.map(([k, v]) => (
          <Table.Tr key={k}><Table.Td w={170} c="dimmed" style={{ verticalAlign: 'top' }}>{k}</Table.Td><Table.Td style={{ overflowWrap: 'anywhere' }}>{v ?? '–'}</Table.Td></Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  );
}

export function Panel({ title, right, children, p = 'md' }: { title?: ReactNode; right?: ReactNode; children: ReactNode; p?: string | number }) {
  return (
    <Paper withBorder radius="md" p={p} mb="md" style={{ minWidth: 0 }}>
      {(title || right) && <Group justify="space-between" mb="sm" px={p === 0 ? 'md' : 0} pt={p === 0 ? 'md' : 0}>{title && <Title order={4}>{title}</Title>}{right}</Group>}
      {children}
    </Paper>
  );
}

export const Empty = ({ children }: { children: ReactNode }) => <Text c="dimmed" ta="center" py="xl">{children}</Text>;

export function MetricsChart({ data, series, unit, max }: { data: Metric[]; series: { name: keyof Metric; label: string; color: string }[]; unit: string; max?: number }) {
  if (!data.length) return <Empty>Ingen målinger endnu</Empty>;
  const long = data.length > 1 && new Date(data[data.length - 1].ts).getTime() - new Date(data[0].ts).getTime() > 36 * 3600_000;
  const rows = data.map((m) => ({
    ...m, t: new Date(m.ts).toLocaleString('da-DK', long ? { day: '2-digit', month: '2-digit', hour: '2-digit' } : { hour: '2-digit', minute: '2-digit' }),
  }));
  return (
    <LineChart h={190} data={rows} dataKey="t" withDots={data.length < 3} curveType="monotone" unit={unit} connectNulls
      yAxisProps={max ? { domain: [0, max] } : undefined} withLegend legendProps={{ verticalAlign: 'bottom', height: 30 }}
      series={series.map((s) => ({ name: s.name as string, label: s.label, color: s.color }))} />
  );
}

// ---------------------------------------------------------------- tabeller
export function MachineTable({ machines, selectable, selected, onSelect, compact }: {
  machines: MachineRow[]; selectable?: boolean; selected?: number[]; onSelect?: (ids: number[]) => void; compact?: boolean;
}) {
  const nav = useNavigate();
  const mobile = useMediaQuery('(max-width: 48em)');
  if (!machines.length) return <Empty>Ingen maskiner endnu. Gå til Indstillinger for at tilmelde den første.</Empty>;
  const sel = new Set(selected ?? []);
  const toggle = (id: number) => { const s = new Set(sel); if (s.has(id)) s.delete(id); else s.add(id); onSelect?.([...s]); };
  // Mobil: ét kort pr. maskine med det vigtigste
  if (mobile) return (
    <Stack gap={0}>
      {machines.map((m) => (
        <UnstyledButton key={m.id} onClick={(e) => { if (!(e.target as HTMLElement).closest('input')) nav(`/maskiner/${m.id}`); }}
          px="md" py="sm" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
          <Group gap={10} wrap="nowrap" align="flex-start">
            {selectable && <Checkbox mt={2} size="md" checked={sel.has(m.id)} onChange={() => toggle(m.id)} onClick={(e) => e.stopPropagation()} />}
            <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
              <Group gap={8} wrap="wrap">
                <OnlineDot online={m.online} /><Text fw={700} fz={16}>{m.name}</Text>{m.renamed && <Text fz={12} c="dimmed" ff="monospace">{m.hostname}</Text>}
                <OsBadge m={m} size="sm" />
                {m.alerts > 0 && <Badge size="sm" color="red">{m.alerts} alarm{m.alerts > 1 ? 'er' : ''}</Badge>}
                {m.replacesId != null && <Badge size="sm" color="orange">Venter på godkendelse</Badge>}
                {m.trustError && <Badge size="sm" color="red" variant="light" title={m.trustError}>Afviste jobs</Badge>}
              </Group>
              <Group gap={8} wrap="wrap">
                <HealthCell state={m.healthState} score={m.healthScore} />
                {m.upgrades > 0 && <UpgradesBadge m={m} size="sm" label={`${m.upgrades} opdatering${m.upgrades > 1 ? 'er' : ''}`} />}
                {m.jobsActive > 0 && <Badge size="sm" variant="light" color="blue">{m.jobsActive} job</Badge>}
              </Group>
              <Text fz={13} c="dimmed" lineClamp={1}>
                {osDetail(m) ? `${osDetail(m)} · ` : ''}{m.online ? 'Online' : `Sidst set ${ago(m.lastSeen)}`}{m.profile ? ` · ${m.profile}` : ''}{m.user ? ` · ${m.user.split('\\').pop()}` : ''}
              </Text>
              {m.tags.length > 0 && <Group gap={4}>{m.tags.map((t) => <Badge key={t} size="sm" variant="outline">{t}</Badge>)}</Group>}
            </Stack>
          </Group>
        </UnstyledButton>
      ))}
    </Stack>
  );
  return (
    <ScrollArea type="auto">
      <Table highlightOnHover verticalSpacing="xs" miw={compact ? 300 : 900}>
        <Table.Thead>
          <Table.Tr>
            {selectable && <Table.Th w={32}><Checkbox size="xs" checked={sel.size === machines.length && sel.size > 0}
              indeterminate={sel.size > 0 && sel.size < machines.length} onChange={(e) => onSelect?.(e.currentTarget.checked ? machines.map((m) => m.id) : [])} /></Table.Th>}
            <Table.Th>Maskine</Table.Th><Table.Th w={130}>Helbred</Table.Th>
            {!compact && <><Table.Th visibleFrom="md">Profil</Table.Th><Table.Th visibleFrom="lg">Bruger</Table.Th><Table.Th visibleFrom="lg">IP</Table.Th><Table.Th visibleFrom="md">CPU</Table.Th><Table.Th visibleFrom="md">RAM</Table.Th></>}
            <Table.Th visibleFrom="sm">Profiloverholdelse</Table.Th><Table.Th visibleFrom="sm" ta="right">Opdat.</Table.Th><Table.Th visibleFrom="sm">Sidst set</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {machines.map((m) => (
            <Table.Tr key={m.id} style={{ cursor: 'pointer' }} onClick={(e) => { if (!(e.target as HTMLElement).closest('input')) nav(`/maskiner/${m.id}`); }}>
              {selectable && <Table.Td><Checkbox size="xs" checked={sel.has(m.id)} onChange={() => toggle(m.id)} /></Table.Td>}
              <Table.Td>
                <Stack gap={0}><Group gap={8} wrap="nowrap"><OnlineDot online={m.online} /><Text fw={600} size="sm">{m.name}</Text>{m.renamed && <Text size="xs" c="dimmed" ff="monospace" title="Værtsnavn">{m.hostname}</Text>}<OsBadge m={m} size="xs" />
                  {m.tags.map((t) => <Badge key={t} size="xs" variant="outline">{t}</Badge>)}
                  {m.alerts > 0 && <Badge size="xs" color="red" circle>{m.alerts}</Badge>}
                  {m.replacesId != null && <Badge size="xs" color="orange">Venter på godkendelse</Badge>}
                  {m.trustError && <Badge size="xs" color="red" variant="light" title={m.trustError}>Afviste jobs</Badge>}</Group>
                {osDetail(m) && <Text size="xs" c="dimmed" lineClamp={1}>{osDetail(m)}</Text>}</Stack>
              </Table.Td>
              <Table.Td><HealthCell state={m.healthState} score={m.healthScore} /></Table.Td>
              {!compact && <>
                <Table.Td visibleFrom="md"><Text size="sm">{m.profile ?? '–'}</Text></Table.Td>
                <Table.Td visibleFrom="lg"><Text size="sm" c="dimmed">{m.user?.split('\\').pop() ?? '–'}</Text></Table.Td>
                <Table.Td visibleFrom="lg"><Code>{m.ip ?? ''}</Code></Table.Td>
                <Table.Td visibleFrom="md"><UsageBar value={m.cpu} /></Table.Td>
                <Table.Td visibleFrom="md"><UsageBar value={m.ram} warn={85} bad={95} /></Table.Td>
              </>}
              <Table.Td visibleFrom="sm"><ComplianceCell c={m.compliance} /></Table.Td>
              <Table.Td visibleFrom="sm" ta="right">{m.upgrades > 0 && <UpgradesBadge m={m} size="sm" label={String(m.upgrades)} />}</Table.Td>
              <Table.Td visibleFrom="sm"><Text size="sm" c="dimmed">{ago(m.lastSeen)}</Text></Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </ScrollArea>
  );
}

export function AlertList({ alerts, withMachine, onAck, onFix, onIgnore }: {
  alerts: AlertRow[]; withMachine?: boolean; onAck?: (id: number) => void; onFix?: (a: AlertRow) => void; onIgnore?: (a: AlertRow, all: boolean) => void;
}) {
  const nav = useNavigate();
  const [job, setJob] = useState<number | null>(null);
  if (!alerts.length) return <Empty>Ingen alarmer – alt ser fint ud 👍</Empty>;
  return (
    <Stack gap="sm">
      {alerts.map((a) => (
        <AlertCard key={a.id} a={a} withMachine={withMachine} onAck={onAck} onFix={onFix} onIgnore={onIgnore} onJob={setJob}
          onOpen={withMachine ? () => nav(`/maskiner/${a.machineId}/helbred`) : undefined} />
      ))}
      <JobModal id={job} onClose={() => setJob(null)} />
    </Stack>
  );
}

/** Fremdrift for et kørende job: bjælke (stribet, når procenten er ukendt) og hvad agenten laver lige nu. */
export function JobLive({ job, w }: { job: Pick<JobRow, 'id' | 'status' | 'progress' | 'step' | 'stepSince'>; w?: number | string }) {
  const p = useJobProgress(job.id, job.progress, job.step, job.stepSince);
  // "uændret i 12 min": samme trin længe (agenten sender livstegn, så bjælken alene ser ud til at arbejde)
  const still = p.stepSince ? Math.floor((Date.now() - new Date(p.stepSince.endsWith('Z') ? p.stepSince : p.stepSince + 'Z').getTime()) / 60000) : 0;
  if (job.status !== 'running') return null;
  return (
    <Stack gap={2} mt={4} w={w} maw="100%" miw={0}>
      <Group gap={6} wrap="nowrap">
        <Progress value={p.progress ?? 100} striped animated={p.progress === null || p.progress < 100} size="sm" style={{ flex: 1 }}
          color={p.progress === null ? 'gray' : 'blue'} />
        <Text size="xs" c="dimmed" w={34} ta="right">{p.progress === null ? '' : `${p.progress} %`}</Text>
      </Group>
      {p.step && <Text size="xs" c="dimmed" lineClamp={1} title={p.step}>{p.step}</Text>}
      {still >= 2 && <Text size="xs" c={still >= 10 ? 'orange' : 'dimmed'}>uændret i {still} min</Text>}
    </Stack>
  );
}

/** BIOS- og USB-job startes forfra fra BIOS-siden (serveren afviser "Prøv igen" for dem). */
const NO_RETRY = ['bios_update', 'bios_settings_apply', 'bios_usb_write', 'usb_disks'];

export function JobTable({ jobs, withMachine = true }: { jobs: JobRow[]; withMachine?: boolean }) {
  const { isAdmin } = useMe();
  const [open, setOpen] = useState<number | null>(null);
  const cancel = useAction((id: number) => post(`/api/jobs/${id}/cancel`), 'Annulleret');
  const retry = useAction((id: number) => post(`/api/jobs/${id}/retry`), 'Sat i kø igen');
  if (!jobs.length) return <Empty>Ingen jobs</Empty>;
  return (
    <>
      <ScrollArea type="auto">
        <Table highlightOnHover verticalSpacing="xs" miw={700}>
          <Table.Thead><Table.Tr><Table.Th>#</Table.Th><Table.Th>Status</Table.Th><Table.Th>Job</Table.Th>{withMachine && <Table.Th>Maskine</Table.Th>}
            <Table.Th visibleFrom="sm">Kilde</Table.Th><Table.Th>Oprettet</Table.Th><Table.Th visibleFrom="sm">Varighed</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>
            {jobs.map((j) => (
              <Table.Tr key={j.id} style={{ cursor: 'pointer' }} onClick={() => setOpen(j.id)}>
                <Table.Td c="dimmed">{j.id}</Table.Td><Table.Td><StatusBadge map={JOB} value={j.status} /></Table.Td><Table.Td>{j.title}<JobLive job={j} w={360} /></Table.Td>
                {withMachine && <Table.Td>{j.machine}</Table.Td>}
                <Table.Td visibleFrom="sm" c="dimmed">{SOURCE[j.source] ?? j.source}</Table.Td>
                <Table.Td><Tooltip label={fmt(j.created)}><Text size="sm" c="dimmed">{ago(j.created)} · {j.createdBy}</Text></Tooltip></Table.Td>
                <Table.Td visibleFrom="sm" c="dimmed">{duration(j.started, j.finished)}</Table.Td>
                <Table.Td onClick={(e) => e.stopPropagation()}>
                  {isAdmin && (['pending', 'waiting_user', 'running'].includes(j.status)
                    ? <Button size="compact-xs" variant="default" color={j.status === 'running' ? 'red' : undefined}
                        title={j.status === 'running' ? 'Stopper jobbet på pc\'en og lukker en installation, der hænger' : undefined}
                        onClick={() => cancel.mutate(j.id)}>{j.status === 'running' ? 'Stop' : 'Annullér'}</Button>
                    : ['failed', 'cancelled', 'expired'].includes(j.status) && !NO_RETRY.includes(j.type) && <Button size="compact-xs" variant="default" onClick={() => retry.mutate(j.id)}>Prøv igen</Button>)}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
      <JobModal id={open} onClose={() => setOpen(null)} />
    </>
  );
}

export function JobModal({ id, onClose }: { id: number | null; onClose: () => void }) {
  const { data: j } = useApi<JobDetail>(['job', id], id ? `/api/jobs/${id}` : null, { refetchInterval: 3000 });
  const params = { ...(j?.params ?? {}) } as Record<string, unknown>;
  const body = params.body as string | undefined;
  delete params.body;
  return (
    <Modal opened={id !== null} onClose={onClose} title={j ? `Job #${j.id}: ${j.title}` : 'Job'} size="xl">
      {!j ? <Loading /> : (
        <Stack>
          <KeyValue rows={[
            ['Status', <Stack gap={0}><Group gap={6}><StatusBadge map={JOB} value={j.status} />{j.exitCode !== null && <Text size="xs" c="dimmed">kode {j.exitCode}</Text>}</Group><JobLive job={j} w={420} /></Stack>],
            ['Maskine', j.machine], ['Kører som', RUNAS[j.runAs] ?? j.runAs], ['Kilde', `${SOURCE[j.source] ?? j.source} (${j.createdBy})`],
            ['Tider', `Oprettet ${fmt(j.created)} · start ${fmt(j.started)} · slut ${fmt(j.finished)} ${duration(j.started, j.finished)}`],
            ['Parametre', <Code block>{JSON.stringify(params, null, 1)}</Code>],
          ]} />
          {j.insight && <InsightBox insight={j.insight} job={j} onDone={onClose} />}
          {body && <><Title order={5}>Script</Title><Code block>{body}</Code></>}
          <Title order={5}>Output</Title>
          <Code block style={{ maxHeight: '50vh', overflow: 'auto', whiteSpace: 'pre-wrap' }}>{j.output || '(intet output endnu)'}</Code>
        </Stack>
      )}
    </Modal>
  );
}

// ---------------------------------------------------------------- handlinger på maskiner
const ACTIONS: Record<string, string> = {
  install: 'Installér pakke eller spil', uninstall: 'Afinstallér pakke', upgrade: 'Opdatér alle programmer (winget)',
  windows_update: 'Installér Windows-opdateringer', script: 'Kør script', message: 'Send besked til brugeren',
  lock: 'Lås skærmen', logoff: 'Log brugeren af', reboot: 'Genstart', shutdown: 'Sluk', wake: 'Tænd (Wake-on-LAN)',
  inventory: 'Synkronisér inventar og helbred', winsetup: 'WinSetup-trin', provision: 'Kør nyinstallations-sekvens',
  assign_profile: 'Tildel profil', enforce: 'Gennemtving profil nu', remove_agent: 'Fjern agenten',
};
const HINTS: Record<string, string> = {
  upgrade: 'Alle programmer med en nyere version i winget opdateres. Undtagelserne i profilen respekteres.',
  windows_update: 'Henter og installerer alle ventende Windows-opdateringer. En genstart kan være nødvendig bagefter.',
  inventory: 'Agenten indsamler software, opdateringer, hardware og helbred med det samme.',
  provision: 'Kører: drivere → programmer og spil → filer → baggrund og opsætning → Windows Update → genstart.',
  enforce: 'Opretter jobs for alt, der mangler eller skal fjernes i forhold til profilen.',
  wake: 'Sender en magic packet. Kræver Wake-on-LAN i BIOS og at containeren kører med netværk "host".',
  remove_agent: 'Agenten afinstallerer sig selv. Maskinen bliver stående i listen, indtil du sletter den.',
  lock: 'Låser skærmen med det samme. Brugeren skal logge ind igen.',
  logoff: 'Logger den indloggede bruger af. Ikke gemt arbejde går tabt.',
};

export function ActionModal({ ids, opened, onClose, preset = 'install', presetParams = {} }: {
  ids: number[]; opened: boolean; onClose: () => void; preset?: string; presetParams?: Record<string, unknown>;
}) {
  const [action, setAction] = useState(preset);
  const [p, setP] = useState<Record<string, unknown>>(presetParams);
  const { data: pkgs = [] } = useApi<Package[]>(['packages'], opened ? '/api/packages' : null);
  const { data: scripts = [] } = useApi<Script[]>(['scripts'], opened ? '/api/scripts' : null);
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], opened ? '/api/profiles' : null);
  const run = useAction((b: object) => post<{ jobsCreated: number; blocked?: string[] }>('/api/machines/actions', b),
    (r) => (action === 'wake' ? 'Magic packet sendt' : `${r.jobsCreated} job(s) sat i kø${r.blocked?.length ? `. Sprunget over: ${r.blocked.join('; ')}` : ''}`));
  const set = (k: string, v: unknown) => setP((x) => ({ ...x, [k]: v }));
  const pkgOptions = pkgs.map((x) => ({ value: String(x.id), label: `${x.name} (${x.type})`, group: x.category || 'Andet' }));
  const grouped = Object.entries(pkgOptions.reduce<Record<string, { value: string; label: string }[]>>((a, o) => { (a[o.group] ??= []).push(o); return a; }, {}))
    .map(([group, items]) => ({ group, items }));

  const submit = async () => {
    const params: Record<string, unknown> = { ...p };
    for (const k of ['packageId', 'scriptId', 'profileId', 'delay', 'timeoutMin']) if (params[k] !== undefined && params[k] !== '') params[k] = Number(params[k]); else delete params[k];
    await run.mutateAsync({ machineIds: ids, action, params });
    onClose();
  };

  return (
    <Modal opened={opened} onClose={onClose} title={ids.length > 1 ? `Handling på ${ids.length} maskiner` : 'Handling'} size="lg">
      <Stack>
        <Select label="Handling" data={options(ACTIONS)} value={action} onChange={(v) => { setAction(v ?? 'install'); setP({}); }} allowDeselect={false} maxDropdownHeight={400} />
        {(action === 'install' || action === 'uninstall') &&
          <Select label="Pakke" searchable data={grouped} value={p.packageId ? String(p.packageId) : null} onChange={(v) => set('packageId', v)} nothingFoundMessage="Intet fundet" />}
        {action === 'script' && <>
          <Select label="Gemt script" clearable placeholder="Skriv selv nedenfor" data={scripts.map((s) => ({ value: String(s.id), label: s.name }))}
            value={p.scriptId ? String(p.scriptId) : null}
            onChange={(v) => { const s = scripts.find((x) => String(x.id) === v); setP({ scriptId: v ?? undefined, body: s?.body, runAs: s?.runAs ?? 'system' }); }} />
          <Select label="Kør som" data={options(RUNAS)} value={(p.runAs as string) ?? 'system'} onChange={(v) => set('runAs', v)} allowDeselect={false} />
          <Textarea label="PowerShell" autosize minRows={8} maxRows={20} ff="monospace" value={(p.body as string) ?? ''} onChange={(e) => set('body', e.currentTarget.value)} disabled={!!p.scriptId} />
        </>}
        {action === 'message' && <Textarea label="Besked" autosize minRows={3} value={(p.text as string) ?? ''} onChange={(e) => set('text', e.currentTarget.value)} />}
        {(action === 'reboot' || action === 'shutdown') && <>
          <NumberInput label="Om (sekunder)" value={(p.delay as number) ?? 60} min={0} onChange={(v) => set('delay', v)} />
          <TextInput label="Besked til brugeren" value={(p.message as string) ?? ''} onChange={(e) => set('message', e.currentTarget.value)} />
          {action === 'reboot' && <Switch label="Kun hvis Windows venter på genstart" checked={!!p.onlyIfPending} onChange={(e) => set('onlyIfPending', e.currentTarget.checked)} />}
        </>}
        {action === 'windows_update' && <Switch label="Også driveropdateringer" checked={!!p.includeDrivers} onChange={(e) => set('includeDrivers', e.currentTarget.checked)} />}
        {action === 'winsetup' && <Select label="Trin" data={options(STEPS)} value={(p.step as string) ?? null} onChange={(v) => set('step', v)} />}
        {action === 'assign_profile' && <Select label="Profil" clearable data={profiles.map((x) => ({ value: String(x.id), label: x.name }))}
          value={p.profileId ? String(p.profileId) : null} onChange={(v) => set('profileId', v ?? '')} />}
        {HINTS[action] && <Text size="sm" c="dimmed">{HINTS[action]}</Text>}
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color={action === 'remove_agent' ? 'red' : undefined} loading={run.isPending} onClick={submit}>Udfør</Button></Group>
      </Stack>
    </Modal>
  );
}

export function useActionModal() {
  const [opened, { open, close }] = useDisclosure(false);
  const [state, setState] = useState<{ ids: number[]; preset?: string; presetParams?: Record<string, unknown> }>({ ids: [] });
  const show = (ids: number[], preset?: string, presetParams?: Record<string, unknown>) => { setState({ ids, preset, presetParams }); open(); };
  const node = opened ? <ActionModal key={`${state.preset}-${state.ids.join()}`} opened ids={state.ids} preset={state.preset} presetParams={state.presetParams} onClose={close} /> : null;
  return { show, node };
}

/** Vælg maskiner og udfør en handling på dem. */
export function MachinePicker({ opened, onClose, title, onRun }: { opened: boolean; onClose: () => void; title: string; onRun: (ids: number[]) => Promise<unknown> }) {
  const { data: ms = [] } = useApi<MachineRow[]>(['machines'], opened ? '/api/machines' : null);
  const [sel, setSel] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      <Checkbox.Group value={sel} onChange={setSel}>
        <Stack gap="xs">{ms.map((m) => <Checkbox key={m.id} value={String(m.id)} label={<Group gap={6}><OnlineDot online={m.online} />{m.name}<Text size="xs" c="dimmed">{m.profile}</Text></Group>} />)}</Stack>
      </Checkbox.Group>
      {!ms.length && <Empty>Ingen maskiner</Empty>}
      <Group justify="flex-end" mt="md"><Button variant="default" onClick={onClose}>Annullér</Button>
        <Button disabled={!sel.length} loading={busy} onClick={async () => { setBusy(true); try { await onRun(sel.map(Number)); onClose(); } catch (e) { notifyErr(e); } finally { setBusy(false); } }}>Udfør</Button></Group>
    </Modal>
  );
}

export const prefetchMachine = (id: number) => get(`/api/machines/${id}`);
export { COMP };

/** Antal ventende opdateringer; klik går direkte til maskinens Opdateringer-fane. */
function UpgradesBadge({ m, size, label }: { m: MachineRow; size: 'xs' | 'sm'; label: string }) {
  const nav = useNavigate();
  return (
    <Badge size={size} variant="light" color="yellow" component="button" type="button" title="Se opdateringer"
      style={{ cursor: 'pointer', border: 0 }}
      onClick={(e: MouseEvent) => { e.stopPropagation(); nav(`/maskiner/${m.id}/opdateringer`); }}>{label}</Badge>
  );
}

/** Hent agenten/opsætningsprogrammet – kun til det styresystem, der vises (eller alle). */
export function AgentDownloads({ platform }: { platform?: 'windows' | 'linux' | 'mac' }) {
  const items: { p: 'windows' | 'linux' | 'mac'; label: string; href: string; hint?: string }[] = [
    { p: 'windows', label: 'Windows (Fleetdeck.exe)', href: 'api/agent/download', hint: 'Dobbeltklik åbner opsætningsvinduet' },
    { p: 'linux', label: 'Linux x64', href: 'api/agent/download?platform=linux&arch=x64' },
    { p: 'linux', label: 'Raspberry Pi 64-bit', href: 'api/agent/download?platform=linux&arch=arm64' },
    { p: 'linux', label: 'Raspberry Pi 32-bit', href: 'api/agent/download?platform=linux&arch=arm' },
    { p: 'mac', label: 'Mac (Apple Silicon)', href: 'api/agent/download?platform=mac&arch=arm64' },
    { p: 'mac', label: 'Mac (Intel)', href: 'api/agent/download?platform=mac&arch=x64' },
  ];
  return (
    <Stack gap={6}>
      <Group gap="xs">
        {items.filter((i) => !platform || i.p === platform).map((i) => (
          <Button key={i.href} size="compact-sm" variant="default" component="a" href={i.href} title={i.hint}>Hent {i.label}</Button>))}
      </Group>
      <Text size="xs" c="dimmed">{platform && platform !== 'windows'
        ? <>Kør <Code>chmod +x fleetdeck-agent && ./fleetdeck-agent setup</Code> på maskinen for at åbne opsætningen.</>
        : <>Windows: dobbeltklik på filen åbner opsætningsvinduet (skift server, eller fjern agenten).</>}
        {' '}Downloadsiden til brugerne: <Anchor size="xs" href="hent" target="_blank">/hent</Anchor></Text>
    </Stack>
  );
}

/** Styresystemets mærke (Windows / macOS / Linux) ud for en maskine. */
export function OsBadge({ m, size = 'xs' }: { m: OsInput; size?: 'xs' | 'sm' }) {
  const b = osBadge(m);
  return b ? <Badge size={size} variant="light" color={b.color}>{b.label}</Badge> : null;
}
