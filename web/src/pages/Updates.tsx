import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Badge, Button, Checkbox, Group, Paper, SegmentedControl, Stack, Table, Tabs, Text, TextInput, Title } from '@mantine/core';
import { IconDownload, IconSearch } from '@tabler/icons-react';
import { post, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading } from '../components';
import { KindIcon, kindOf } from '../design/platform';
import { LockControl, type LockScope } from '../design/locks';
import { askUpdateAll, type UpdateMode } from '../design/updateAll';
import { BiosFleetTab, useBiosFleet } from '../design/bios';
import { fleetCounts } from '../design/biosText';

type Up = { machineId: number; machine: string; platform: string; online: boolean; name: string; wingetId: string; current: string | null; available: string | null; locked?: LockScope };

/** Alle ventende opdateringer på tværs af flåden – pr. program eller pr. maskine, med ét klik. */
export function UpdatesPage() {
  const { data } = useApi<Up[]>(['upgrades'], '/api/upgrades', { refetchInterval: 20_000 });
  const { isAdmin } = useMe();
  const [params, setParams] = useSearchParams();
  const tab = params.get('fane') === 'bios' ? 'bios' : 'programmer';
  const { data: bios } = useBiosFleet();
  const behind = bios ? fleetCounts(bios).behind : 0;
  const [q, setQ] = useState('');
  const [by, setBy] = useState<'program' | 'machine'>('program');
  const [sel, setSel] = useState<Set<string>>(new Set());
  const run = useAction((jobs: { machineId: number; wingetId?: string; name?: string }[]) =>
    Promise.all(jobs.map((j) => post('/api/machines/actions', { machineIds: [j.machineId], action: 'upgrade', params: j.wingetId ? { wingetId: j.wingetId, name: j.name } : {} }))),
    (r) => `${r.length} opdatering(er) sat i gang`);
  const all = useAction((b: { ids: number[]; mode: UpdateMode }) => post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: b.ids, action: 'update_all', params: { mode: b.mode } }),
    (r) => `Opdatér alt er sat i gang (${r.jobsCreated} job)`);
  const list = useMemo(() => (data ?? []).filter((u) => !q || `${u.name} ${u.machine} ${u.wingetId}`.toLowerCase().includes(q.toLowerCase())), [data, q]);
  const groups = useMemo(() => {
    const g = new Map<string, Up[]>();
    for (const u of list) { const k = by === 'program' ? u.wingetId : u.machine; g.set(k, [...(g.get(k) ?? []), u]); }
    return [...g.entries()].sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]));
  }, [list, by]);
  if (!data) return <Loading />;
  const key = (u: Up) => `${u.machineId}|${u.wingetId}`;
  const selected = list.filter((u) => sel.has(key(u)));
  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-end">
        <div><Title order={2} style={{ letterSpacing: '-0.02em' }}>Opdateringer</Title>
          <Text c="dimmed" size="sm" mt={2}>{data.filter((u) => !u.locked).length} opdateringer på {new Set(data.filter((u) => !u.locked).map((u) => u.machineId)).size} maskiner{behind ? ` · BIOS på ${behind} maskine${behind > 1 ? 'r' : ''}` : ''}{data.some((u) => u.locked) ? ` · ${data.filter((u) => u.locked).length} låst (opdateres aldrig)` : ''}</Text></div>
        {isAdmin && <Group>
          {selected.length > 0 && <Button variant="default" onClick={() => { run.mutate(selected.map((u) => ({ machineId: u.machineId, wingetId: u.wingetId, name: u.name }))); setSel(new Set()); }}>
            Opdatér valgte ({selected.length})</Button>}
          <Button className="fd-primary" leftSection={<IconDownload size={16} />} disabled={!data.length}
            onClick={() => {
              const ids = [...new Set(data.filter((u) => u.online && !u.locked).map((u) => u.machineId))];
              const ts = ids.map((id) => { const ms = data.filter((u) => u.machineId === id && !u.locked); return { name: ms[0].machine, platform: ms[0].platform, upgrades: ms.length }; });
              askUpdateAll(ts, (mode) => all.mutate({ ids, mode }));
            }}>Opdatér alt</Button>
        </Group>}
      </Group>
      <Tabs value={tab} onChange={(v) => setParams(v === 'bios' ? { fane: 'bios' } : {}, { replace: true })}>
        <Tabs.List>
          <Tabs.Tab value="programmer" rightSection={data.length ? <Badge size="xs" color="yellow">{data.filter((u) => !u.locked).length}</Badge> : undefined}>Programmer</Tabs.Tab>
          <Tabs.Tab value="bios" rightSection={behind ? <Badge size="xs" color="yellow">{behind}</Badge> : undefined}>BIOS</Tabs.Tab>
        </Tabs.List>
      </Tabs>
      {tab === 'bios' ? <BiosFleetTab /> : <>
      <Group>
        <TextInput placeholder="Søg program eller maskine" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} w={280} />
        <SegmentedControl value={by} onChange={(v) => setBy(v as 'program' | 'machine')} data={[{ value: 'program', label: 'Pr. program' }, { value: 'machine', label: 'Pr. maskine' }]} />
      </Group>
      {groups.length === 0 ? <Empty>Alt er opdateret 🎉</Empty> : groups.map(([g, ups]) => (
        <Paper key={g} p={0}>
          <Group justify="space-between" px="md" py="sm" style={{ borderBottom: '1px solid var(--fd-border)' }}>
            <Group gap="sm">
              {by === 'machine' && <KindIcon kind={kindOf({ platform: ups[0].platform })} size={28} />}
              <div><Text fw={650}>{by === 'program' ? ups[0].name : g}</Text>
                <Text size="xs" c="dimmed">{by === 'program' ? `${g} · ${ups.length} maskine${ups.length > 1 ? 'r' : ''}` : `${ups.length} opdatering${ups.length > 1 ? 'er' : ''}`}</Text></div>
            </Group>
            {isAdmin && <Button size="compact-sm" variant="light" onClick={() => run.mutate(ups.filter((u) => u.online && !u.locked).map((u) => ({ machineId: u.machineId, wingetId: u.wingetId, name: u.name })))}>
              Opdatér {by === 'program' ? 'på alle' : 'alle'}</Button>}
          </Group>
          <Table verticalSpacing={8} horizontalSpacing="md" layout="fixed"><Table.Tbody>
            {ups.map((u) => (
              <Table.Tr key={key(u)}>
                {isAdmin && <Table.Td w={32}>{!u.locked && <Checkbox size="xs" checked={sel.has(key(u))} onChange={() => setSel((s) => { const n = new Set(s); if (n.has(key(u))) n.delete(key(u)); else n.add(key(u)); return n; })} />}</Table.Td>}
                <Table.Td>{by === 'program' ? <Link to={`/maskiner/${u.machineId}/opdateringer`}>{u.machine}</Link> : u.name}</Table.Td>
                <Table.Td w={360}><Text size="sm" ff="monospace" lineClamp={1}><Text span c="dimmed" inherit>{u.current}</Text> → <Text span fw={700} inherit>{u.available}</Text></Text></Table.Td>
                <Table.Td w={300} ta="right">
                  {u.locked ? <LockControl machineId={u.machineId} machineName={u.machine} wingetId={u.wingetId} name={u.name} locked={u.locked} />
                    : <Group gap={4} justify="flex-end" wrap="nowrap">
                      {!u.online ? <Badge variant="light" color="gray">Offline</Badge>
                        : isAdmin && <Button size="compact-xs" variant="default" onClick={() => run.mutate([{ machineId: u.machineId, wingetId: u.wingetId, name: u.name }])}>Opdatér</Button>}
                      {isAdmin && <LockControl machineId={u.machineId} machineName={u.machine} wingetId={u.wingetId} name={u.name} locked={null} />}
                    </Group>}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody></Table>
        </Paper>
      ))}
      </>}
    </Stack>
  );
}
