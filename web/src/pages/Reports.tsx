import { useEffect, useMemo, useState } from 'react';
import { createPortal } from 'react-dom';
import { useSearchParams } from 'react-router';
import {
  Alert, Badge, Box, Button, Checkbox, Group, Menu, Modal, MultiSelect, Pagination, Paper, ScrollArea, SegmentedControl, Select, SimpleGrid, Stack, Switch, Table,
  Tabs, Text, TextInput, UnstyledButton,
} from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import {
  IconCalendarStats, IconChevronDown, IconDeviceDesktop, IconDeviceIpad, IconDownload, IconFileTypePdf, IconHeartbeat, IconListCheck, IconPackage, IconPlus,
  IconPrinter, IconRefresh, IconSearch, IconSend, IconShieldCheck,
} from '@tabler/icons-react';
import { get, post, put, useAction, useApi } from '../api';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { fmt, SEV } from '../format';
import '../design/reports.css';

// ---------------------------------------------------------------- typer fra /api/reports
interface ReportDef { name: string; title: string; description: string; group: string; machines: boolean; period: boolean; search: boolean; defaultDays: number; dayChoices: number[] }
interface Meta { reports: ReportDef[]; machines: { id: number; label: string; profileId: number | null }[]; profiles: { id: number; name: string }[] }
type Kind = 'text' | 'num' | 'pct' | 'date' | 'day' | 'bool' | 'sev' | 'min';
interface Column { key: string; label: string; kind: Kind }
type Value = string | number | boolean | null;
interface Section { key: string; title: string; note?: string | null; columns: Column[]; total: number; rows: Value[][] }
interface Stat { label: string; value: string; color?: string | null }
interface Report { name: string; title: string; description: string; filter: string; generated: string; stats: Stat[]; sections: Section[] }

interface Schedule { id: string; name: string; reports: string[]; frequency: 'weekly' | 'monthly'; day: number; time: string; enabled: boolean; machines: number[]; profile: number | null; lastRun?: string | null; lastResult?: string | null }
interface Summary { enabled: boolean; day: number; time: string; ipads: boolean; fleet: boolean; lastRun?: string | null }
interface Schedules { schedules: Schedule[]; summary: Summary; channels: { push: boolean; mqtt: boolean; webhook: boolean; email: boolean }; serverUrl: string }

const ICONS: Record<string, typeof IconDeviceDesktop> = {
  inventory: IconDeviceDesktop, software: IconPackage, updates: IconRefresh, security: IconShieldCheck, health: IconHeartbeat, jobs: IconListCheck, ipads: IconDeviceIpad,
};
const WEEKDAYS = ['mandag', 'tirsdag', 'onsdag', 'torsdag', 'fredag', 'lørdag', 'søndag'];
const PREVIEW = 500;   // rækker pr. tabel i forhåndsvisningen – eksporten har dem alle
const PAGE = 50;

// ---------------------------------------------------------------- celler
function minutes(m: number) { return m < 60 ? `${m} m` : `${Math.floor(m / 60)} t ${m % 60} m`; }
export function cellText(v: Value, kind: Kind): string {
  if (v === null || v === undefined || v === '') return '';
  switch (kind) {
    case 'date': return fmt(String(v));
    case 'day': return new Date(String(v).length === 10 ? `${v}T00:00:00` : String(v)).toLocaleDateString('da-DK');
    case 'bool': return v ? 'Ja' : 'Nej';
    case 'pct': return `${Number(v).toLocaleString('da-DK', { maximumFractionDigits: 1 })} %`;
    case 'min': return minutes(Number(v));
    case 'num': return Number(v).toLocaleString('da-DK', { maximumFractionDigits: 2 });
    case 'sev': return SEV[v as keyof typeof SEV]?.label ?? String(v);
    default: return String(v);
  }
}
function Cell({ v, kind }: { v: Value; kind: Kind }) {
  if (kind === 'sev' && typeof v === 'string' && SEV[v as keyof typeof SEV]) return <Badge variant="light" color={SEV[v as keyof typeof SEV].color}>{SEV[v as keyof typeof SEV].label}</Badge>;
  if (kind === 'bool' && v !== null) return <Text span size="sm" c={v ? undefined : 'dimmed'}>{v ? 'Ja' : 'Nej'}</Text>;
  const t = cellText(v, kind);
  return t ? <>{t}</> : <Text span c="dimmed">–</Text>;
}
const isNum = (k: Kind) => k === 'num' || k === 'pct' || k === 'min';

/** Rapporter (System → Rapporter, kun administratorer): færdige rapporter med filter, forhåndsvisning, CSV/PDF og planlagte rapporter. */
export function ReportsPage() {
  const { data: meta } = useApi<Meta>(['reports-meta'], '/api/reports');
  const [sp, setSp] = useSearchParams();
  const name = sp.get('r') ?? 'inventory';
  const def = meta?.reports.find((r) => r.name === name) ?? meta?.reports[0];
  const [machines, setMachines] = useState<string[]>([]);
  const [profile, setProfile] = useState<string | null>(null);
  const [days, setDays] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [dq] = useDebouncedValue(q, 300);
  useEffect(() => { setDays(null); setQ(''); }, [name]);

  const params = useMemo(() => {
    const p = new URLSearchParams();
    if (def?.machines && machines.length) p.set('machines', machines.join(','));
    if (def?.machines && profile) p.set('profile', profile);
    if (def?.period) p.set('days', days ?? String(def.defaultDays));
    if (def?.search && dq.trim()) p.set('q', dq.trim());
    return p.toString();
  }, [def, machines, profile, days, dq]);
  const { data, isFetching } = useApi<Report>(['report', def?.name, params], def ? `/api/reports/${def.name}?${params}${params ? '&' : ''}pageSize=${PREVIEW}` : null);
  const [printing, setPrinting] = useState<Report | null>(null);
  useEffect(() => {
    if (!printing) return;
    const t = setTimeout(() => { window.print(); setPrinting(null); }, 50);
    return () => clearTimeout(t);
  }, [printing]);

  if (!meta || !def) return <Loading />;
  const href = (fmtName: 'csv' | 'pdf') => `api/reports/${def.name}?format=${fmtName}${params ? '&' + params : ''}`;
  const print = async () => setPrinting(await get<Report>(`/api/reports/${def.name}?${params}${params ? '&' : ''}pageSize=5000`));
  const pick = (n: string) => setSp((p) => { p.set('r', n); return p; }, { replace: true });

  return (
    <>
      <PageHeader title="Rapporter" sub="Færdige rapporter med filter, forhåndsvisning og eksport som CSV eller PDF" />
      <Box className="fd-reports">
        <Paper withBorder radius="md" p={6} className="fd-reports-nav" visibleFrom="md">
          {meta.reports.map((r) => {
            const I = ICONS[r.name] ?? IconListCheck;
            return (
              <UnstyledButton key={r.name} className="fd-report-item" data-active={r.name === def.name || undefined} onClick={() => pick(r.name)} aria-current={r.name === def.name ? 'page' : undefined}>
                <Group gap="sm" wrap="nowrap" align="flex-start">
                  <I size={18} stroke={1.7} style={{ marginTop: 2, flexShrink: 0 }} />
                  <div><Text fw={600} size="sm">{r.title}</Text><Text size="xs" c="dimmed" lh={1.3}>{r.description}</Text></div>
                </Group>
              </UnstyledButton>
            );
          })}
        </Paper>
        <Box style={{ minWidth: 0 }}>
          <Select hiddenFrom="md" mb="sm" label="Rapport" allowDeselect={false} value={def.name} onChange={(v) => v && pick(v)}
            data={meta.reports.map((r) => ({ value: r.name, label: r.title }))} />
          <Panel>
            <Group justify="space-between" align="flex-end" gap="sm">
              <Group gap="xs" align="flex-end" wrap="wrap" style={{ flex: 1, minWidth: 0 }} className="fd-report-filters">
                {def.machines && <>
                  <MultiSelect label="Maskiner" placeholder={machines.length ? undefined : `Alle (${meta.machines.length})`} w={{ base: '100%', sm: 260 }} searchable clearable
                    data={meta.machines.map((m) => ({ value: String(m.id), label: m.label }))} value={machines} onChange={setMachines} maxDropdownHeight={280} />
                  <Select label="Profil" placeholder="Alle profiler" w={{ base: '100%', sm: 180 }} clearable value={profile} onChange={setProfile}
                    data={meta.profiles.map((p) => ({ value: String(p.id), label: p.name }))} />
                </>}
                {def.period && <Select label="Periode" w={{ base: '100%', sm: 170 }} allowDeselect={false} value={days ?? String(def.defaultDays)} onChange={setDays}
                  data={def.dayChoices.map((d) => ({ value: String(d), label: `Seneste ${d} dage` }))} />}
                {def.search && <TextInput label="Program" placeholder="Navn eller udgiver…" leftSection={<IconSearch size={16} />} w={{ base: '100%', sm: 220 }}
                  value={q} onChange={(e) => setQ(e.currentTarget.value)} />}
              </Group>
              <Group gap="xs">
                <Button component="a" href={href('csv')} download variant="default" leftSection={<IconDownload size={16} />}>CSV</Button>
                <Menu position="bottom-end">
                  <Menu.Target><Button variant="default" leftSection={<IconFileTypePdf size={16} />} rightSection={<IconChevronDown size={14} />}>PDF</Button></Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item component="a" href={href('pdf')} download leftSection={<IconDownload size={16} />}>Hent PDF</Menu.Item>
                    <Menu.Item onClick={print} leftSection={<IconPrinter size={16} />}>Udskriv eller gem som PDF…</Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              </Group>
            </Group>
          </Panel>
          {!data ? <Loading /> : <ReportView r={data} busy={isFetching} />}
        </Box>
      </Box>
      <SchedulesPanel meta={meta} />
      {printing && createPortal(<div className="report-print"><PrintView r={printing} /></div>, document.body)}
    </>
  );
}

function StatTiles({ stats }: { stats: Stat[] }) {
  if (!stats.length) return null;
  return (
    <SimpleGrid cols={{ base: 2, sm: Math.min(stats.length, 3), lg: Math.min(stats.length, 6) }} spacing="sm" mb="md">
      {stats.map((s) => (
        <Paper key={s.label} withBorder radius="md" p="sm">
          <Text size="xs" c="dimmed">{s.label}</Text>
          <Text fw={700} fz={20} c={s.color ?? undefined}>{s.value}</Text>
        </Paper>
      ))}
    </SimpleGrid>
  );
}

function ReportView({ r, busy }: { r: Report; busy: boolean }) {
  const [tab, setTab] = useState<string | null>(r.sections[0]?.key ?? null);
  useEffect(() => { if (!r.sections.some((s) => s.key === tab)) setTab(r.sections[0]?.key ?? null); }, [r, tab]);
  const sec = r.sections.find((s) => s.key === tab) ?? r.sections[0];
  return (
    <>
      <StatTiles stats={r.stats} />
      <Panel p={0}>
        <Group justify="space-between" px="md" pt="sm" gap="xs">
          <Text size="sm"><b>{r.title}</b> <Text span c="dimmed">· {r.filter} · dannet {fmt(r.generated)}{busy ? ' · opdaterer…' : ''}</Text></Text>
        </Group>
        {r.sections.length > 1 && (
          <Tabs value={sec?.key} onChange={setTab} px="md" mt={4}>
            <Tabs.List>{r.sections.map((s) => <Tabs.Tab key={s.key} value={s.key}>{s.title} <Text span c="dimmed" size="xs">({s.total.toLocaleString('da-DK')})</Text></Tabs.Tab>)}</Tabs.List>
          </Tabs>
        )}
        {sec && <SectionTable key={`${r.name}-${sec.key}`} s={sec} />}
      </Panel>
    </>
  );
}

function SectionTable({ s }: { s: Section }) {
  const [page, setPage] = useState(1);
  const pages = Math.max(1, Math.ceil(s.rows.length / PAGE));
  const rows = s.rows.slice((page - 1) * PAGE, page * PAGE);
  return (
    <>
      {s.note && <Text size="xs" c="dimmed" px="md" pt="xs">{s.note}</Text>}
      <ScrollArea type="auto">
        <Table highlightOnHover miw={Math.max(600, s.columns.length * 110)} fz="sm">
          <Table.Thead><Table.Tr>{s.columns.map((c) => <Table.Th key={c.key} ta={isNum(c.kind) ? 'right' : undefined} style={{ whiteSpace: 'nowrap' }}>{c.label}</Table.Th>)}</Table.Tr></Table.Thead>
          <Table.Tbody>{rows.map((row, i) => (
            <Table.Tr key={i}>{s.columns.map((c, j) => (
              <Table.Td key={c.key} ta={isNum(c.kind) ? 'right' : undefined} style={{ whiteSpace: c.kind === 'date' || c.kind === 'day' || isNum(c.kind) ? 'nowrap' : undefined, overflowWrap: 'break-word' }}>
                <Cell v={row[j]} kind={c.kind} />
              </Table.Td>))}
            </Table.Tr>))}
          </Table.Tbody>
        </Table>
      </ScrollArea>
      {!s.rows.length && <Empty>Ingen rækker</Empty>}
      <Group justify="space-between" p="sm">
        <Text size="sm" c="dimmed">
          {s.total.toLocaleString('da-DK')} rækker{s.total > s.rows.length ? ` – forhåndsvisningen viser de første ${s.rows.length}; eksporten har dem alle` : ''}
        </Text>
        {pages > 1 && <Pagination size="sm" total={pages} value={page} onChange={setPage} />}
      </Group>
    </>
  );
}

/** Printvenlig udgave (browserens "Gem som PDF"): alle tabeller under hinanden, sort på hvidt. */
function PrintView({ r }: { r: Report }) {
  return (
    <div className="report-sheet">
      <h1>{r.title}</h1>
      <p className="rs-sub">{r.filter} · dannet {fmt(r.generated)} · Fleetdeck</p>
      {r.stats.length > 0 && <p className="rs-stats">{r.stats.map((s) => `${s.label}: ${s.value}`).join('   ·   ')}</p>}
      {r.sections.map((s) => (
        <section key={s.key}>
          <h2>{s.title}</h2>
          {s.note && <p className="rs-sub">{s.note}</p>}
          <table>
            <thead><tr>{s.columns.map((c) => <th key={c.key} className={isNum(c.kind) ? 'num' : undefined}>{c.label}</th>)}</tr></thead>
            <tbody>{s.rows.map((row, i) => <tr key={i}>{s.columns.map((c, j) => <td key={c.key} className={isNum(c.kind) ? 'num' : undefined}>{cellText(row[j], c.kind)}</td>)}</tr>)}</tbody>
          </table>
          {!s.rows.length && <p className="rs-sub">Ingen rækker</p>}
        </section>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------- planlagte rapporter
function when(s: { frequency?: string; day: number; time: string }) {
  return s.frequency === 'monthly' ? `Den ${s.day}. i måneden kl. ${s.time}` : `Hver ${WEEKDAYS[s.day] ?? '?'} kl. ${s.time}`;
}
const blank = (): Schedule => ({ id: '', name: '', reports: ['updates', 'security'], frequency: 'weekly', day: 0, time: '07:00', enabled: true, machines: [], profile: null });

function SchedulesPanel({ meta }: { meta: Meta }) {
  const { data } = useApi<Schedules>(['report-schedules'], '/api/reports/schedules');
  const [edit, setEdit] = useState<Schedule | null>(null);
  const save = useAction((c: { schedules: Schedule[]; summary: Summary }) => put('/api/reports/schedules', c), 'Gemt');
  const send = useAction((id: string) => post<{ text: string }>(`/api/reports/schedules/${id}/send`), 'Sendt');
  if (!data) return null;
  const titles = (names: string[]) => names.map((n) => meta.reports.find((r) => r.name === n)?.title ?? n).join(', ');
  const store = (schedules: Schedule[], summary: Summary = data.summary) => save.mutate({ schedules, summary });
  const remove = (s: Schedule) => modals.openConfirmModal({
    title: 'Slet plan?', children: <Text size="sm">"{s.name || 'Uden navn'}" sendes ikke længere.</Text>, labels: { confirm: 'Slet', cancel: 'Annullér' },
    confirmProps: { color: 'red' }, onConfirm: () => store(data.schedules.filter((x) => x.id !== s.id)),
  });
  const sum = data.summary;
  return (
    <>
      <Panel title={<Group gap="xs"><IconCalendarStats size={20} />Planlagte rapporter</Group>}
        right={<Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => setEdit(blank())}>Ny plan</Button>}>
        {!data.channels.push && <Alert color="yellow" mb="sm">Der er ingen push-kanal endnu. Sæt MQTT (Home Assistant) eller en webhook op under Indstillinger, ellers sendes planerne ingen steder.</Alert>}
        <Text size="xs" c="dimmed" mb="xs">
          Planerne sendes som en kort push-besked med et link hertil{data.channels.mqtt ? ' via Home Assistant' : ''}{data.channels.webhook ? (data.channels.mqtt ? ' og webhook' : ' via webhook') : ''}.
          E-mail er ikke muligt, fordi Fleetdeck ikke har SMTP-indstillinger.{!data.serverUrl && ' Linket kræver, at serverens adresse er sat under Indstillinger.'}
        </Text>
        <ScrollArea type="auto"><Table miw={720} verticalSpacing="xs">
          <Table.Thead><Table.Tr><Table.Th>Navn</Table.Th><Table.Th>Rapporter</Table.Th><Table.Th>Hvornår</Table.Th><Table.Th>Sidst sendt</Table.Th><Table.Th /><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>
            {data.schedules.map((s) => (
              <Table.Tr key={s.id}>
                <Table.Td fw={500}>{s.name || 'Uden navn'}</Table.Td>
                <Table.Td>{titles(s.reports)}</Table.Td>
                <Table.Td>{when(s)}</Table.Td>
                <Table.Td c="dimmed">{s.lastResult === 'sendt' && s.lastRun ? fmt(s.lastRun) : '–'}</Table.Td>
                <Table.Td><Badge variant="light" color={s.enabled ? 'green' : 'gray'}>{s.enabled ? 'Til' : 'Fra'}</Badge></Table.Td>
                <Table.Td><Group gap={4} justify="flex-end" wrap="nowrap">
                  <Button size="compact-xs" variant="subtle" leftSection={<IconSend size={12} />} onClick={() => send.mutate(s.id)} loading={send.isPending && send.variables === s.id}>Send nu</Button>
                  <Button size="compact-xs" variant="subtle" onClick={() => setEdit(s)}>Redigér</Button>
                  <Button size="compact-xs" variant="subtle" color="red" onClick={() => remove(s)}>Slet</Button>
                </Group></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table></ScrollArea>
        {!data.schedules.length && <Empty>Ingen planer endnu</Empty>}
      </Panel>
      <Panel title="Ugentlig opsummering" right={<Switch checked={sum.enabled} onChange={(e) => store(data.schedules, { ...sum, enabled: e.currentTarget.checked })} aria-label="Ugentlig opsummering til eller fra" />}>
        <Text size="sm" c="dimmed" mb="sm">Én besked om ugen med børnenes iPad-forbrug de seneste 7 dage og flådens helbred. Slået fra som standard.</Text>
        <Group gap="sm" align="flex-end">
          <Select label="Dag" w={150} allowDeselect={false} value={String(sum.day)} onChange={(v) => store(data.schedules, { ...sum, day: Number(v) })}
            data={WEEKDAYS.map((d, i) => ({ value: String(i), label: d[0].toUpperCase() + d.slice(1) }))} />
          <TextInput label="Kl." type="time" w={120} defaultValue={sum.time} key={sum.time} onBlur={(e) => e.currentTarget.value && e.currentTarget.value !== sum.time && store(data.schedules, { ...sum, time: e.currentTarget.value })} />
          <Checkbox label="iPad-forbrug" checked={sum.ipads} onChange={(e) => store(data.schedules, { ...sum, ipads: e.currentTarget.checked })} mb={8} />
          <Checkbox label="Flådens helbred" checked={sum.fleet} onChange={(e) => store(data.schedules, { ...sum, fleet: e.currentTarget.checked })} mb={8} />
          <Button variant="default" leftSection={<IconSend size={16} />} onClick={() => send.mutate('summary')} loading={send.isPending && send.variables === 'summary'}>Send nu</Button>
        </Group>
        {sum.enabled && <Text size="xs" c="dimmed" mt="xs">{when({ day: sum.day, time: sum.time })}{sum.lastRun ? ` · sidst ${fmt(sum.lastRun)}` : ''}</Text>}
      </Panel>
      {edit && <ScheduleModal s={edit} meta={meta} onClose={() => setEdit(null)} busy={save.isPending}
        onSave={(s) => { store(s.id ? data.schedules.map((x) => (x.id === s.id ? s : x)) : [...data.schedules, s]); setEdit(null); }} />}
    </>
  );
}

function ScheduleModal({ s, meta, onClose, onSave, busy }: { s: Schedule; meta: Meta; onClose: () => void; onSave: (s: Schedule) => void; busy: boolean }) {
  const [v, setV] = useState<Schedule>(s);
  const set = <K extends keyof Schedule>(k: K, x: Schedule[K]) => setV((o) => ({ ...o, [k]: x }));
  const days = v.frequency === 'monthly'
    ? Array.from({ length: 28 }, (_, i) => ({ value: String(i + 1), label: `Den ${i + 1}.` }))
    : WEEKDAYS.map((d, i) => ({ value: String(i), label: d[0].toUpperCase() + d.slice(1) }));
  return (
    <Modal opened onClose={onClose} title={s.id ? 'Redigér plan' : 'Ny planlagt rapport'} size="lg">
      <Stack gap="sm">
        <TextInput label="Navn" placeholder="Fx Månedlig til forsikringen" value={v.name} onChange={(e) => set('name', e.currentTarget.value)} maxLength={80} />
        <MultiSelect label="Rapporter" data={meta.reports.map((r) => ({ value: r.name, label: r.title }))} value={v.reports} onChange={(x) => set('reports', x)} />
        <SegmentedControl value={v.frequency} onChange={(x) => setV((o) => ({ ...o, frequency: x as Schedule['frequency'], day: x === 'monthly' ? 1 : 0 }))}
          data={[{ value: 'weekly', label: 'Ugentligt' }, { value: 'monthly', label: 'Månedligt' }]} />
        <Group grow>
          <Select label="Dag" allowDeselect={false} data={days} value={String(v.day)} onChange={(x) => set('day', Number(x))} />
          <TextInput label="Kl." type="time" value={v.time} onChange={(e) => set('time', e.currentTarget.value)} />
        </Group>
        <Group grow align="flex-start">
          <MultiSelect label="Maskiner" placeholder={v.machines.length ? undefined : 'Alle'} searchable clearable data={meta.machines.map((m) => ({ value: String(m.id), label: m.label }))}
            value={v.machines.map(String)} onChange={(x) => set('machines', x.map(Number))} />
          <Select label="Profil" placeholder="Alle profiler" clearable data={meta.profiles.map((p) => ({ value: String(p.id), label: p.name }))}
            value={v.profile ? String(v.profile) : null} onChange={(x) => set('profile', x ? Number(x) : null)} />
        </Group>
        <Switch label="Slået til" checked={v.enabled} onChange={(e) => set('enabled', e.currentTarget.checked)} />
        <Text size="xs" c="dimmed">Kanal: push-besked (Home Assistant/webhook) med en kort tekst pr. rapport og et link til Rapporter.</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button onClick={() => onSave(v)} disabled={!v.reports.length || !/^\d\d:\d\d$/.test(v.time)} loading={busy}>Gem</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
