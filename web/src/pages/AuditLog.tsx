import { useEffect, useMemo, useState } from 'react';
import { Badge, Button, Code, Group, NumberInput, Pagination, ScrollArea, Select, SimpleGrid, Table, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconDownload, IconSearch } from '@tabler/icons-react';
import { put, useAction, useApi } from '../api';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { fmt } from '../format';

interface Row { id: number; ts: string; user: string; action: string; detail: string; machineId: number | null; machine: string | null }
interface Page { total: number; page: number; pageSize: number; rows: Row[] }
interface Meta { users: string[]; actions: string[]; machines: { id: number; label: string }[]; retentionDays: number; minRetention: number }

const PAGE = 100;
/** Danske navne på handlingstyperne (første led i fx "job.create"). Ukendte vises som de er. */
const ACTIONS: Record<string, string> = {
  login: 'Login', logout: 'Log ud', '2fa': 'Totrinsbekræftelse', account: 'Min konto', user: 'Brugere', setup: 'Opsætning', admin: 'Administrator',
  job: 'Jobs', jobs: 'Jobs (oprydning)', action: 'Handlinger på maskiner', machine: 'Maskiner', agent: 'Agenter', enroll: 'Tilmelding',
  settings: 'Indstillinger', debloat: 'Debloat', backup: 'Backup', update: 'Opdatering af Fleetdeck', recovery_key: 'Gendannelsesnøgler',
  emergency: 'Nødkort', syslog: 'Fejllog', livelog: 'Live-log', terminal: 'Terminal', audit: 'Revisionslog', alert: 'Alarmer', helper: 'Hjælperen', deploy: 'Udrulning', package: 'Pakker', profile: 'Profiler',
  script: 'Scripts', check: 'Overvågning', drive: 'Netværksdrev', ipad: 'iPads', apple_mdm: 'Apple-styring', schedule: 'Planlægger', report: 'Rapporter',
};

const ranges = [
  { value: '1', label: 'Seneste døgn' }, { value: '7', label: 'Seneste 7 dage' }, { value: '30', label: 'Seneste 30 dage' },
  { value: '365', label: 'Seneste år' }, { value: 'all', label: 'Alt' }, { value: 'custom', label: 'Vælg datoer' },
];

/** Revisionslog (kun administratorer): hvem gjorde hvad og hvornår, med filtre, sider og CSV-eksport. */
export function AuditPage() {
  const { data: meta } = useApi<Meta>(['audit-meta'], '/api/audit/meta');
  const [range, setRange] = useState('30');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [user, setUser] = useState<string | null>(null);
  const [action, setAction] = useState<string | null>(null);
  const [machine, setMachine] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [dq] = useDebouncedValue(q, 300);
  const [page, setPage] = useState(1);

  const params = useMemo(() => {
    const p = new URLSearchParams();
    if (range === 'custom') {
      if (from) p.set('from', new Date(`${from}T00:00:00`).toISOString());
      if (to) p.set('to', new Date(new Date(`${to}T00:00:00`).getTime() + 86_400_000).toISOString());
    } else if (range !== 'all') p.set('from', new Date(Date.now() - Number(range) * 86_400_000).toISOString());
    if (user) p.set('user', user);
    if (action) p.set('action', action);
    if (machine) p.set('machineId', machine);
    if (dq.trim()) p.set('q', dq.trim());
    return p;
  }, [range, from, to, user, action, machine, dq]);
  const key = params.toString();
  useEffect(() => setPage(1), [key]);
  const { data } = useApi<Page>(['audit', key, page], `/api/audit?${key}${key ? '&' : ''}page=${page}&pageSize=${PAGE}`);

  if (!meta) return <Loading />;
  const pages = data ? Math.max(1, Math.ceil(data.total / PAGE)) : 1;
  return (
    <>
      <PageHeader title="Revisionslog" sub="Hvem gjorde hvad og hvornår">
        <Button component="a" href={`api/audit/export.csv?${key}`} download variant="default" leftSection={<IconDownload size={16} />}>Eksportér CSV</Button>
      </PageHeader>
      <Panel>
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 5 }} spacing="xs">
          <Select label="Periode" data={ranges} value={range} onChange={(v) => setRange(v ?? '30')} allowDeselect={false} />
          <Select label="Bruger" placeholder="Alle" data={meta.users} value={user} onChange={setUser} searchable clearable />
          <Select label="Handling" placeholder="Alle" value={action} onChange={setAction} searchable clearable
            data={meta.actions.map((a) => ({ value: a, label: ACTIONS[a] ? `${ACTIONS[a]} (${a})` : a }))} />
          <Select label="Maskine" placeholder="Alle" value={machine} onChange={setMachine} searchable clearable
            data={meta.machines.map((m) => ({ value: String(m.id), label: m.label }))} />
          <TextInput label="Søg" leftSection={<IconSearch size={16} />} placeholder="Tekst i detaljer…" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        </SimpleGrid>
        {range === 'custom' && (
          <Group mt="xs" gap="xs">
            <TextInput type="date" label="Fra" value={from} onChange={(e) => setFrom(e.currentTarget.value)} />
            <TextInput type="date" label="Til og med" value={to} onChange={(e) => setTo(e.currentTarget.value)} />
          </Group>
        )}
      </Panel>
      <Panel p={0}>
        {!data ? <Loading /> : <>
          <ScrollArea type="auto"><Table highlightOnHover miw={760}>
            <Table.Thead><Table.Tr><Table.Th>Tid</Table.Th><Table.Th>Bruger</Table.Th><Table.Th>Handling</Table.Th><Table.Th>Maskine</Table.Th><Table.Th>Detaljer</Table.Th></Table.Tr></Table.Thead>
            <Table.Tbody>{data.rows.map((r) => (
              <Table.Tr key={r.id}>
                <Table.Td c="dimmed" style={{ whiteSpace: 'nowrap' }}>{fmt(r.ts)}</Table.Td>
                <Table.Td>{r.user}</Table.Td>
                <Table.Td><Code color={r.action.endsWith('denied') || r.action.includes('failed') ? 'red' : r.action.startsWith('recovery_key.reveal') ? 'orange' : undefined}>{r.action}</Code></Table.Td>
                <Table.Td>{r.machine ? <Badge variant="light" color="gray">{r.machine}</Badge> : null}</Table.Td>
                <Table.Td c="dimmed" style={{ overflowWrap: 'anywhere' }}>{r.detail}</Table.Td>
              </Table.Tr>))}</Table.Tbody>
          </Table></ScrollArea>
          {!data.rows.length && <Empty>Intet fundet</Empty>}
          <Group justify="space-between" p="sm">
            <Text size="sm" c="dimmed">{data.total.toLocaleString('da-DK')} rækker</Text>
            {pages > 1 && <Pagination size="sm" total={pages} value={page} onChange={setPage} />}
          </Group>
        </>}
      </Panel>
      <RetentionPanel days={meta.retentionDays} min={meta.minRetention} />
    </>
  );
}

function RetentionPanel({ days, min }: { days: number; min: number }) {
  const [v, setV] = useState<number | string>(days);
  useEffect(() => setV(days), [days]);
  const save = useAction((d: number) => put('/api/audit/retention', { days: d }), 'Gemt');
  const n = typeof v === 'number' ? v : Number(v) || 0;
  return (
    <Panel title="Hvor længe gemmes loggen?">
      <Group align="flex-end" gap="xs">
        <NumberInput label="Dage" description={`0 = altid. Mindst ${min} dage.`} value={v} onChange={setV} min={0} max={3650} w={200} />
        <Button onClick={() => save.mutate(n)} loading={save.isPending} disabled={n === days}>Gem</Button>
      </Group>
      <Text size="xs" c="dimmed" mt="xs">Ældre rækker slettes automatisk én gang i døgnet.</Text>
    </Panel>
  );
}
