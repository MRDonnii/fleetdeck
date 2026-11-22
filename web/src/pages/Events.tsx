import { useState } from 'react';
import { useNavigate } from 'react-router';
import { Group, Paper, SegmentedControl, Select, SimpleGrid, Stack, Switch, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { GroupRow, useIgnore, type IgnoreFn } from '../design/syslog';
import { AREAS, byArea, countText, RANGES, summarize, type Area, type FleetSysLog } from '../design/syslogText';

/** Hændelser: de alvorlige fejl fra alle maskiner på én liste (blå skærme, hardware- og diskfejl, nedlukninger, hukommelse, opdateringer). */
export function EventsPage() {
  const nav = useNavigate();
  const { isAdmin } = useMe();
  const [days, setDays] = useState('7');
  const [level, setLevel] = useState<'serious' | 'errors'>('serious');
  const [machine, setMachine] = useState<string | null>(null);
  const [area, setArea] = useState<Area | ''>('');
  const [q, setQ] = useState('');
  const [dq] = useDebouncedValue(q, 300);
  const [hidden, setHidden] = useState(false);
  const p = new URLSearchParams({ days, level });
  if (machine) p.set('machineId', machine);
  if (dq.trim()) p.set('q', dq.trim());
  if (hidden) p.set('hidden', 'true');
  const { data } = useApi<FleetSysLog>(['syslog-events', p.toString()], `/api/syslog/events?${p}`, { refetchInterval: 60_000 });
  const ignore = useIgnore();
  const onIgnore: IgnoreFn | undefined = isAdmin ? (key, machineId, ign) => ignore.mutate({ key, machineId, ignore: ign }) : undefined;
  if (!data) return <Loading />;
  const stats = summarize(data.groups);
  const shown = byArea(data.groups, area);
  const affected = new Set(data.groups.filter((g) => !g.ignored).map((g) => g.machineId)).size;
  return (
    <>
      <PageHeader title="Hændelser" sub="Alvorlige fejl fra alle maskiners systemlog: blå skærme, hardware- og diskfejl, uventede nedlukninger, hukommelse og mislykkede opdateringer" />
      <SimpleGrid cols={{ base: 2, sm: 4, lg: Math.min(7, stats.length + 1) }} mb="md">
        <Paper withBorder p="md"><Text size="xs" c="dimmed" tt="uppercase" fw={600}>Maskiner berørt</Text><Text fz={28} fw={700} c={affected ? 'yellow' : 'green'}>{affected}</Text></Paper>
        {stats.map((s) => (
          <UnstyledButton key={s.area} onClick={() => setArea(area === s.area ? '' : s.area)} aria-pressed={area === s.area}>
            <Paper withBorder p="md" style={area === s.area ? { borderColor: 'var(--mantine-primary-color-filled)' } : undefined}>
              <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{AREAS[s.area]}</Text>
              <Text fz={28} fw={700} c={s.area === 'nedbrud' || s.area === 'hardware' ? 'red' : s.area === 'disk' ? 'orange' : 'yellow'}>{s.count}</Text>
              <Text size="xs" c="dimmed">{s.machines === 1 ? '1 maskine' : `${s.machines} maskiner`}</Text>
            </Paper>
          </UnstyledButton>
        ))}
      </SimpleGrid>
      <Panel title="Alvorlige hændelser">
        <Stack gap="sm">
          <Group gap="xs" wrap="wrap" align="flex-end">
            <SegmentedControl size="xs" value={level} onChange={(v) => setLevel(v as 'serious' | 'errors')} data={[{ value: 'serious', label: 'Alvorlige' }, { value: 'errors', label: 'Alle fejl' }]} />
            <Select size="xs" style={{ flex: '1 1 130px', maxWidth: 170 }} value={days} onChange={(v) => setDays(v ?? '7')} data={RANGES.map((r) => ({ ...r }))} allowDeselect={false} aria-label="Tidsrum" />
            <Select size="xs" style={{ flex: '1 1 150px', maxWidth: 220 }} placeholder="Alle maskiner" clearable searchable value={machine} onChange={setMachine}
              data={data.machines.map((m) => ({ value: String(m.id), label: m.name }))} aria-label="Maskine" />
            <Select size="xs" style={{ flex: '1 1 130px', maxWidth: 180 }} placeholder="Alle områder" clearable value={area || null} onChange={(v) => setArea((v ?? '') as Area | '')}
              data={(Object.keys(AREAS) as Area[]).filter((a) => a !== 'støj').map((a) => ({ value: a, label: AREAS[a] }))} aria-label="Område" />
            <TextInput size="xs" style={{ flex: '1 1 180px' }} placeholder="Søg i beskeder" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} />
          </Group>
          <Group justify="space-between" gap="xs">
            <Text size="xs" c="dimmed">{shown.length} {shown.length === 1 ? 'type' : 'typer'} · {countText(shown.reduce((s, g) => s + g.count, 0))}{data.truncated ? ' · kun de nyeste vises' : ''}</Text>
            {data.hiddenCount > 0 && <Switch size="xs" label={`Vis skjulte (${data.hiddenCount})`} checked={hidden} onChange={(e) => setHidden(e.currentTarget.checked)} />}
          </Group>
          {shown.length === 0
            ? <Empty>Ingen alvorlige hændelser i perioden 👍</Empty>
            : <Stack gap={6}>{shown.map((g) => (
                <GroupRow key={`${g.machineId}:${g.key}`} g={g} machineId={g.machineId} machineLabel={g.machine} onIgnore={onIgnore}
                  onMachine={() => nav(`/maskiner/${g.machineId}/logs`)} />
              ))}</Stack>}
        </Stack>
      </Panel>
    </>
  );
}
