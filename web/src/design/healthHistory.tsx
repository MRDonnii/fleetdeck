import { useMemo, useState } from 'react';
import { Badge, Box, Group, Paper, Progress, SegmentedControl, SimpleGrid, Stack, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { useDebouncedValue, useMediaQuery } from '@mantine/hooks';
import { IconBattery2, IconSearch } from '@tabler/icons-react';
import { useApi } from '../api';
import { Empty, Loading, Panel } from '../components';
import { fmt } from '../format';
import { groupTitle } from './syslogText';
import {
  ACTIONS, batteryCapacity, batteryHealth, batteryNow, byDay, DISK_SOURCE, diskKind, diskLife, diskNotes, diskSize, diskState, diskTemp, dk, LANES, markerColor,
  pos, powerOn, SEG_STYLE, span, ticks, TIMELINE_RANGES, uptimeTone, versionText,
  type BatteryInfo, type ChangeLog, type DiskHealth, type Shown, type TimelineMarker, type TimelineView,
} from './healthHistoryText';

const ShownBadge = ({ s }: { s: Shown }) => <Badge variant="light" color={s.tone} style={{ flex: 'none' }}>{s.text}</Badge>;

/** Lille nøgle/værdi-celle i kortene. */
function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ minWidth: 0 }}>
      <Text size="xs" c="dimmed">{label}</Text>
      <Text size="sm" component="div">{children}</Text>
    </div>
  );
}

// ------------------------------------------------------------------ diske
/** Diskhelbred pr. fysisk disk: type, vurdering, levetid, temperatur, driftstid og det, man skal lægge mærke til. */
export function DiskPanel({ disks, tempLimit = 70 }: { disks?: DiskHealth[] | null; tempLimit?: number }) {
  return (
    <Panel title="Diske (SMART)">
      {disks?.length ? (
        <SimpleGrid cols={{ base: 1, md: disks.length > 1 ? 2 : 1 }}>
          {disks.map((d, i) => {
            const life = diskLife(d); const temp = diskTemp(d, tempLimit); const notes = diskNotes(d);
            return (
              <Paper key={d.id ?? `${d.name}-${i}`} withBorder radius="md" p="sm">
                <Group justify="space-between" wrap="nowrap" gap="xs" mb="xs">
                  <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
                    <Badge variant="outline" color="gray" size="sm" style={{ flex: 'none' }}>{diskKind(d)}</Badge>
                    <Text fw={600} size="sm" truncate>{d.name}</Text>
                    {diskSize(d.sizeGb) && <Text size="xs" c="dimmed" style={{ flex: 'none' }}>{diskSize(d.sizeGb)}</Text>}
                  </Group>
                  <ShownBadge s={diskState(d)} />
                </Group>
                <SimpleGrid cols={{ base: 2, xs: 4 }} spacing="xs">
                  <Fact label="Levetid">
                    {life ? <Stack gap={2}><Text size="sm">{life.text}</Text><Progress value={life.pct} color={life.tone} size="sm" aria-label={life.text} /></Stack> : d.kind === 'HDD' || d.media === 'HDD' ? 'Ikke relevant' : '–'}
                  </Fact>
                  <Fact label="Temperatur">{temp ? <><Text size="sm" c={temp.tone === 'green' ? undefined : temp.tone}>{temp.text}</Text>{temp.max && <Text size="xs" c="dimmed">{temp.max}</Text>}</> : '–'}</Fact>
                  <Fact label="I drift">{powerOn(d.powerOnHours)}</Fact>
                  <Fact label="Bus">{d.bus && d.bus !== '?' ? d.bus : '–'}</Fact>
                </SimpleGrid>
                {notes.length > 0 && (
                  <Group gap={6} mt="xs">{notes.map((n) => <Badge key={n.text} variant="light" color={n.tone} size="sm" tt="none">{n.text}</Badge>)}</Group>
                )}
                {d.source && <Text size="xs" c="dimmed" mt={6}>Kilde: {DISK_SOURCE[d.source] ?? d.source}</Text>}
              </Paper>
            );
          })}
        </SimpleGrid>
      ) : <Empty>Ingen data endnu</Empty>}
    </Panel>
  );
}

// ------------------------------------------------------------------ batteri
/** Batterihelbred på bærbare: kapacitet i forhold til som nyt, opladninger og status. Vises kun, når der er et batteri. */
export function BatteryPanel({ batteries, limit = 60 }: { batteries?: BatteryInfo[] | null; limit?: number }) {
  if (!batteries?.length) return null;
  return (
    <Panel title="Batteri">
      <Stack gap="sm">
        {batteries.map((b, i) => {
          const h = batteryHealth(b, limit);
          return (
            <Paper key={`${b.name}-${i}`} withBorder radius="md" p="sm">
              <Group justify="space-between" wrap="nowrap" mb="xs">
                <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
                  <IconBattery2 size={18} style={{ flex: 'none' }} />
                  <Text fw={600} size="sm" truncate>{b.name}</Text>
                  {b.chemistry && <Text size="xs" c="dimmed">{b.chemistry}</Text>}
                </Group>
                <ShownBadge s={h} />
              </Group>
              {b.healthPct != null && (
                <Box mb="xs">
                  <Group justify="space-between" mb={2}><Text size="xs" c="dimmed">Kapacitet i forhold til som nyt</Text><Text size="xs">{h.text}</Text></Group>
                  <Progress value={Math.min(100, b.healthPct)} color={h.tone} size="md" aria-label={`Batterihelbred ${h.text}`} />
                </Box>
              )}
              <SimpleGrid cols={{ base: 2, xs: 4 }} spacing="xs">
                <Fact label="Opladninger">{b.cycles != null ? dk(b.cycles) : '–'}</Fact>
                <Fact label="Fuld opladning">{batteryCapacity(b)}</Fact>
                <Fact label="Lige nu">{batteryNow(b)}</Fact>
                <Fact label="Tilstand">{b.condition ?? (h.tone === 'red' ? 'Bør skiftes' : h.tone === 'gray' ? '–' : 'Normal')}</Fact>
              </SimpleGrid>
            </Paper>
          );
        })}
      </Stack>
    </Panel>
  );
}

// ------------------------------------------------------------------ tidslinje
function MarkerTip({ m }: { m: TimelineMarker }) {
  const title = m.kind === 'event' ? groupTitle({ source: m.title, kind: m.ref }) : m.title;
  return (
    <Stack gap={2} maw={280}>
      <Text size="xs" c="dimmed">{fmt(m.time)}</Text>
      <Text size="sm" fw={600}>{title}{m.count > 1 && m.kind === 'event' ? ` (${m.count} gange)` : ''}</Text>
      {m.detail && <Text size="xs" style={{ whiteSpace: 'pre-line' }}>{m.detail}</Text>}
    </Stack>
  );
}

const LANE_H = 16;

/**
 * Tidslinjen på Helbred-fanen: statusbånd (online/offline/ukendt) og en række markører pr. type (hændelser, opdateringer, ændringer,
 * opstarter) med tooltip. Under båndet de seneste markører som liste, så alt også kan læses uden at holde musen over.
 */
export function TimelinePanel({ machineId }: { machineId: number }) {
  const [days, setDays] = useState('7');
  const { data: v, isLoading } = useApi<TimelineView>(['timeline', machineId, days], `/api/machines/${machineId}/timeline?days=${days}`, { refetchInterval: 60_000 });
  const compact = useMediaQuery('(max-width: 600px)') ?? false;
  const ticksList = useMemo(() => (v ? ticks(v.from, v.to, Number(days), compact) : []), [v, days, compact]);
  const present = useMemo(() => new Set(v?.segments.map((s) => s.state) ?? []), [v]);
  const recent = useMemo(() => (v ? [...v.markers].reverse().slice(0, 8) : []), [v]);
  return (
    <Panel title="Tidslinje" right={<SegmentedControl size="xs" value={days} onChange={setDays} data={TIMELINE_RANGES.map((r) => ({ ...r }))} />}>
      {isLoading || !v ? <Loading /> : (
        <>
          <Group gap={compact ? 'xs' : 'lg'} mb="sm" wrap="wrap" style={{ rowGap: 4 }}>
            <Group gap={6}><Text size="sm" c="dimmed">Oppetid</Text>
              <Badge variant="light" color={uptimeTone(v.onlinePct)}>{v.onlinePct != null ? `${dk(v.onlinePct, v.onlinePct % 1 ? 1 : 0)} %` : 'ukendt'}</Badge></Group>
            <Text size="sm"><Text span c="dimmed">Offline-perioder</Text> {v.offlineCount}</Text>
            <Text size="sm"><Text span c="dimmed">Opstarter</Text> {v.boots}</Text>
            <Text size="sm"><Text span c="dimmed">Opdateringer</Text> {v.updates}</Text>
            <Text size="sm"><Text span c="dimmed">Alvorlige hændelser</Text> {v.events}</Text>
            <Text size="sm"><Text span c="dimmed">Ændringer</Text> {v.changes}</Text>
          </Group>
          <div style={{ display: 'grid', gridTemplateColumns: 'minmax(70px, max-content) 1fr', columnGap: 10, rowGap: 4, alignItems: 'center' }}>
            {LANES.map((lane) => (
              <LaneRow key={lane.kind} label={lane.label} color={lane.color}>
                {v.markers.filter((m) => m.kind === lane.kind).map((m, i) => (
                  <Tooltip key={`${m.time}-${i}`} label={<MarkerTip m={m} />} withArrow multiline events={{ hover: true, focus: true, touch: true }}>
                    <button type="button" aria-label={`${lane.label}: ${m.title}, ${fmt(m.time)}`}
                      style={{
                        position: 'absolute', left: `calc(${pos(m.time, v.from, v.to)}% - 7px)`, top: 0, width: 14, height: LANE_H, padding: 0, border: 0,
                        background: 'transparent', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
                      }}>
                      <span style={{
                        width: m.kind === 'event' ? 10 : 8, height: m.kind === 'event' ? 10 : 8, background: markerColor(m),
                        borderRadius: m.kind === 'boot' ? '50%' : m.kind === 'event' ? 2 : 1, transform: m.kind === 'event' ? 'rotate(45deg)' : undefined,
                        boxShadow: '0 0 0 2px var(--mantine-color-body)',
                      }} />
                    </button>
                  </Tooltip>
                ))}
              </LaneRow>
            ))}
            <Text size="xs" c="dimmed" ta="right">Status</Text>
            <div style={{ position: 'relative', height: 18, borderRadius: 4, overflow: 'hidden', background: 'var(--mantine-color-default-border)' }}>
              {v.segments.map((s) => {
                const left = pos(s.from, v.from, v.to); const width = Math.max(0.3, pos(s.to, v.from, v.to) - left);
                const st = SEG_STYLE[s.state];
                return (
                  <Tooltip key={s.from} label={`${st.label.split(' (')[0]}: ${fmt(s.from)} – ${fmt(s.to)} (${span(s.from, s.to)})`} withArrow events={{ hover: true, focus: true, touch: true }}>
                    <div style={{
                      position: 'absolute', left: `${left}%`, width: `${width}%`, top: 0, bottom: 0, background: st.color,
                      backgroundImage: s.state === 'unknown' ? 'repeating-linear-gradient(45deg, transparent 0 4px, rgba(0,0,0,.18) 4px 6px)' : undefined,
                      opacity: s.state === 'unknown' ? 0.45 : 0.9, borderRight: '2px solid var(--mantine-color-body)',
                    }} />
                  </Tooltip>
                );
              })}
            </div>
            <span />
            <div style={{ position: 'relative', height: 16 }}>
              {ticksList.map((t) => (
                <Text key={t.pct} size="10px" c="dimmed" style={{ position: 'absolute', left: `${t.pct}%`, transform: 'translateX(-50%)', whiteSpace: 'nowrap' }}>{t.label}</Text>
              ))}
            </div>
          </div>
          <Group gap="md" mt="xs" wrap="wrap">
            {(['online', 'offline', 'unknown'] as const).filter((k) => present.has(k)).map((k) => (
              <Group key={k} gap={4}><span style={{ width: 12, height: 8, borderRadius: 2, background: SEG_STYLE[k].color, opacity: k === 'unknown' ? 0.45 : 0.9 }} />
                <Text size="xs" c="dimmed">{SEG_STYLE[k].label}</Text></Group>
            ))}
          </Group>
          {recent.length > 0 && (
            <Stack gap={4} mt="sm">
              <Text size="xs" fw={600} c="dimmed" tt="uppercase">Seneste</Text>
              {recent.map((m, i) => (
                <Group key={`${m.time}-${i}`} gap={8} wrap="nowrap">
                  <span style={{ width: 8, height: 8, borderRadius: m.kind === 'boot' ? '50%' : 2, background: markerColor(m), flex: 'none' }} />
                  <Text size="xs" c="dimmed" style={{ flex: 'none', minWidth: 110 }}>{fmt(m.time)}</Text>
                  <Text size="sm" truncate>{m.kind === 'event' ? groupTitle({ source: m.title, kind: m.ref }) : m.title}{m.count > 1 && m.kind === 'event' ? ` (${m.count} gange)` : ''}</Text>
                </Group>
              ))}
            </Stack>
          )}
          {v.knownSince && new Date(v.knownSince) > new Date(v.from) && (
            <Text size="xs" c="dimmed" mt="xs">Fleetdeck har ført tidslinje for maskinen siden {fmt(v.knownSince)}.</Text>
          )}
        </>
      )}
    </Panel>
  );
}

function LaneRow({ label, color, children }: { label: string; color: string; children: React.ReactNode }) {
  return (
    <>
      <Group gap={4} justify="flex-end" wrap="nowrap"><span style={{ width: 6, height: 6, borderRadius: 2, background: color }} /><Text size="xs" c="dimmed">{label}</Text></Group>
      <div style={{ position: 'relative', height: LANE_H, borderBottom: '1px dashed var(--mantine-color-default-border)' }}>{children}</div>
    </>
  );
}

// ------------------------------------------------------------------ ændringslog
/** Ændringsloggen: programmer og drivere, der er installeret, opdateret eller fjernet, pr. dag. */
export function ChangeLogPanel({ machineId, platform }: { machineId: number; platform: string }) {
  const [kind, setKind] = useState('all');
  const [days, setDays] = useState('30');
  const [q, setQ] = useState('');
  const [dq] = useDebouncedValue(q, 250);
  const url = `/api/machines/${machineId}/changes?days=${days}${kind !== 'all' ? `&kind=${kind}` : ''}${dq ? `&q=${encodeURIComponent(dq)}` : ''}`;
  const { data, isLoading } = useApi<ChangeLog>(['changes', machineId, kind, days, dq], url);
  const groups = useMemo(() => byDay(data?.entries ?? []), [data]);
  return (
    <Panel title="Ændringer" right={<SegmentedControl size="xs" value={days} onChange={setDays} data={[{ value: '7', label: '7 dage' }, { value: '30', label: '30 dage' }, { value: '180', label: '180 dage' }]} />}>
      <Group gap="xs" mb="sm" wrap="wrap">
        {platform === 'windows' && (
          <SegmentedControl size="xs" value={kind} onChange={setKind} data={[{ value: 'all', label: 'Alle' }, { value: 'program', label: 'Programmer' }, { value: 'driver', label: 'Drivere' }]} />
        )}
        <TextInput size="xs" placeholder="Søg program eller udgiver" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} style={{ flex: 1, minWidth: 160 }} />
        {data && (
          <Group gap={6}>{(['installed', 'updated', 'removed'] as const).map((a) => data.counts[a] ? (
            <Badge key={a} variant="light" color={ACTIONS[a].color}>{data.counts[a]} {ACTIONS[a].label.toLowerCase()}</Badge>) : null)}</Group>
        )}
      </Group>
      {isLoading || !data ? <Loading /> : groups.length === 0 ? (
        <Empty>{data.tracking ? 'Ingen ændringer i perioden. Fleetdeck sammenligner hvert inventar med det forrige.' : 'Ændringerne vises efter næste inventar.'}</Empty>
      ) : (
        <Stack gap="sm">
          {groups.map((g) => (
            <div key={g.day}>
              <Text size="xs" fw={600} c="dimmed" tt="uppercase" mb={4}>{g.label}</Text>
              <Table verticalSpacing={4} horizontalSpacing="xs" withRowBorders={false} layout="fixed">
                <Table.Tbody>
                  {g.rows.map((r) => (
                    <Table.Tr key={r.id}>
                      <Table.Td w={120}><Badge size="sm" variant="light" color={ACTIONS[r.action].color} fullWidth>{ACTIONS[r.action].label}</Badge></Table.Td>
                      <Table.Td style={{ overflow: 'hidden' }}>
                        <Group gap={6} wrap="nowrap">
                          <Text size="sm" truncate>{r.name}</Text>
                          {r.kind === 'driver' && <Badge size="xs" variant="outline" color="gray" style={{ flex: 'none' }}>Driver</Badge>}
                        </Group>
                        <Text size="xs" c="dimmed" truncate>{[versionText(r), r.publisher].filter(Boolean).join(' · ') || ' '}</Text>
                      </Table.Td>
                      <Table.Td w={52} ta="right"><Text size="xs" c="dimmed">{new Date(r.time).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' })}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </div>
          ))}
          {data.total > data.entries.length && <Text size="xs" c="dimmed">Viser de nyeste {data.entries.length} af {data.total}.</Text>}
        </Stack>
      )}
      {platform === 'windows' && data && <Text size="xs" c="dimmed" mt="xs">{data.drivers ? `${data.drivers} drivere følges (uden Windows' egne).` : 'Driverne følges fra næste inventar.'}</Text>}
    </Panel>
  );
}

