import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Box, Button, Code, Collapse, Group, Menu, Paper, ScrollArea, SegmentedControl, Select, Stack, Switch, Text, TextInput, Tooltip, UnstyledButton } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconBroadcast, IconChevronDown, IconChevronRight, IconCopy, IconEye, IconEyeOff, IconPlayerPause, IconPlayerPlay, IconPlayerStop, IconSearch } from '@tabler/icons-react';
import { del, notifyErr, post, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading, Panel } from '../components';
import {
  appendLive, countText, groupTitle, heartbeatFrame, HEARTBEAT_MS, known, LEVEL_FILTERS, LEVELS, liveMatches, liveText, logQuery, parseLiveFrame, RANGES, rangeText,
  sourceLabel, when, type LiveEvent, type MachineSysLog, type SysLevel, type SysLogGroup,
} from './syslogText';

export const LevelBadge = ({ level }: { level: SysLevel }) => (
  <Badge size="sm" variant="light" color={LEVELS[level]?.color ?? 'gray'} style={{ flex: 'none' }}>{LEVELS[level]?.label ?? level}</Badge>
);

/** Dansk forklaring og "hvad gør jeg" for en kendt hændelse. */
export function ExplainBox({ kind }: { kind?: string | null }) {
  const k = known(kind);
  if (!k) return null;
  return (
    <Paper p="sm" radius="sm" bg="var(--mantine-color-dark-6)" withBorder>
      <Text size="sm" fw={600}>{k.title}{k.noise && <Badge ml={8} size="xs" variant="light" color="gray">Kan ignoreres</Badge>}</Text>
      <Text size="sm" mt={4}>{k.explain}</Text>
      <Text size="xs" fw={600} mt="xs" c="dimmed" tt="uppercase">Hvad gør jeg</Text>
      <Stack component="ol" gap={2} mt={4} mb={0} pl={18} style={{ margin: 0 }}>
        {k.todo.map((t) => <Text component="li" size="sm" key={t}>{t}</Text>)}
      </Stack>
    </Paper>
  );
}

export type IgnoreFn = (key: string, machineId: number | null, ignore: boolean) => void;

/** Én gruppe af ens hændelser: niveau, titel, antal, første/seneste og (foldet ud) forklaring, besked og tidspunkter. */
export function GroupRow({ g, machineId, machineLabel, onIgnore, onMachine }: {
  g: SysLogGroup; machineId: number; machineLabel?: string; onIgnore?: IgnoreFn; onMachine?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const k = known(g.kind);
  return (
    <Paper withBorder p="xs" radius="sm" style={{ borderLeft: `3px solid var(--mantine-color-${LEVELS[g.level]?.color ?? 'gray'}-6)`, opacity: g.ignored ? 0.6 : 1 }}>
      <UnstyledButton onClick={() => setOpen((o) => !o)} style={{ width: '100%' }} aria-expanded={open}>
        <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
          <Group gap={6} wrap="nowrap" align="flex-start" style={{ minWidth: 0 }}>
            {open ? <IconChevronDown size={16} style={{ flex: 'none', marginTop: 3 }} /> : <IconChevronRight size={16} style={{ flex: 'none', marginTop: 3 }} />}
            <Box style={{ minWidth: 0 }}>
              <Group gap={6} wrap="wrap">
                <LevelBadge level={g.level} />
                {machineLabel && <Text fw={600} size="sm">{machineLabel}</Text>}
                <Text size="sm" fw={k ? 600 : 500}>{groupTitle(g)}</Text>
                {k && <Text size="xs" c="dimmed">{sourceLabel(g)}</Text>}
                {g.ignored && <Badge size="xs" variant="outline" color="gray">{g.ignored === 'global' ? 'Skjult på alle' : 'Skjult her'}</Badge>}
              </Group>
              {!open && <Text size="xs" c="dimmed" mt={2} lineClamp={1} style={{ wordBreak: 'break-word' }}>{g.message}</Text>}
            </Box>
          </Group>
          <Stack gap={0} align="flex-end" style={{ flex: 'none' }}>
            <Badge size="lg" variant="default" radius="sm">{g.count}×</Badge>
            <Text size="xs" c="dimmed" mt={2}>{when(g.last)}</Text>
          </Stack>
        </Group>
      </UnstyledButton>
      <Collapse expanded={open}>
        <Stack gap="xs" mt="xs" pl={22}>
          <ExplainBox kind={g.kind} />
          <Text size="xs" c="dimmed">{countText(g.count)} · {rangeText(g)} · {g.log}</Text>
          <Code block style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: 220, overflow: 'auto' }}>{g.message}</Code>
          {g.recent.length > 1 && <Text size="xs" c="dimmed">Seneste gange: {g.recent.slice(0, 6).map((t) => when(t)).join(' · ')}</Text>}
          <Group gap="xs">
            {onMachine && <Button size="compact-xs" variant="light" onClick={onMachine}>Åbn maskinens logs</Button>}
            {onIgnore && (g.ignored
              ? <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconEye size={12} />}
                  onClick={() => onIgnore(g.key, g.ignored === 'global' ? null : machineId, false)}>Vis igen</Button>
              : (
                <Menu position="bottom-start" withinPortal>
                  <Menu.Target><Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconEyeOff size={12} />}>Skjul denne type</Button></Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Label>Skjulte hændelser gemmes stadig, men giver ingen advarsler</Menu.Label>
                    <Menu.Item onClick={() => onIgnore(g.key, machineId, true)}>Kun på denne maskine</Menu.Item>
                    <Menu.Item onClick={() => onIgnore(g.key, null, true)}>På alle maskiner</Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              ))}
          </Group>
        </Stack>
      </Collapse>
    </Paper>
  );
}

export function useIgnore() {
  return useAction((a: { key: string; machineId: number | null; ignore: boolean }) => post('/api/syslog/ignore', a),
    'Gemt');
}

/** Fanen Logs på maskinens side: fejl og advarsler fra systemloggen, grupperet, med filtre, forklaringer og live-visning. */
export function LogsTab({ m }: { m: { id: number; platform: string; online: boolean } }) {
  const { isAdmin } = useMe();
  const [days, setDays] = useState('7');
  const [level, setLevel] = useState('');
  const [source, setSource] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [dq] = useDebouncedValue(q, 300);
  const [hidden, setHidden] = useState(false);
  const [view, setView] = useState<'groups' | 'all'>('groups');
  const [live, setLive] = useState(false);
  const qs = logQuery({ days, level, source, q: dq, hidden });
  const { data } = useApi<MachineSysLog>(['syslog', m.id, qs], `/api/machines/${m.id}/syslog?${qs}`, { refetchInterval: 60_000 });
  const ignore = useIgnore();
  const onIgnore: IgnoreFn | undefined = isAdmin ? (key, machineId, ign) => ignore.mutate({ key, machineId, ignore: ign }) : undefined;

  if (!data) return <Loading />;
  const sources = data.sources.map((s) => ({ value: s.source, label: `${s.source} (${s.count})` }));
  return (
    <Stack>
      {!data.collects && (
        <Alert color="yellow" variant="light" title="Fejlloggen indsamles ikke endnu">
          Agenten på maskinen skal opdateres til version 3.40.0 eller nyere, før den sender fejl og advarsler fra systemloggen.
        </Alert>
      )}
      {live && isAdmin && <LiveLogPanel machineId={m.id} onClose={() => setLive(false)} />}
      <Panel title="Fejllog" right={isAdmin && !live && (
        <Tooltip label={data.live ?? 'Se loggen, mens den skrives'} withArrow>
          <Button size="xs" leftSection={<IconBroadcast size={14} />} disabled={!!data.live || !m.online} onClick={() => setLive(true)}>Live</Button>
        </Tooltip>
      )}>
        <Stack gap="sm">
          <Group gap="xs" wrap="wrap" align="flex-end">
            <SegmentedControl size="xs" value={level} onChange={setLevel} data={LEVEL_FILTERS.map((l) => ({ ...l }))} />
            <Select size="xs" style={{ flex: '1 1 130px', maxWidth: 170 }} value={days} onChange={(v) => setDays(v ?? '7')} data={RANGES.map((r) => ({ ...r }))} allowDeselect={false} aria-label="Tidsrum" />
            <Select size="xs" style={{ flex: '1 1 150px', maxWidth: 220 }} placeholder="Alle kilder" clearable searchable value={source} onChange={setSource} data={sources} aria-label="Kilde" />
            <TextInput size="xs" style={{ flex: '1 1 180px' }} placeholder="Søg i beskeder, kilde eller id" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} />
          </Group>
          <Group justify="space-between" gap="xs">
            <Text size="xs" c="dimmed">
              {data.total} hændelser i {data.groups.length} {data.groups.length === 1 ? 'gruppe' : 'grupper'}
              {data.lastReceived ? ` · senest modtaget ${when(data.lastReceived)}` : ''}
            </Text>
            <Group gap="sm">
              {data.hiddenCount > 0 && <Switch size="xs" label={`Vis skjulte (${data.hiddenCount})`} checked={hidden} onChange={(e) => setHidden(e.currentTarget.checked)} />}
              <SegmentedControl size="xs" value={view} onChange={(v) => setView(v as 'groups' | 'all')} data={[{ value: 'groups', label: 'Grupperet' }, { value: 'all', label: 'Alle' }]} />
            </Group>
          </Group>
          {data.groups.length === 0
            ? <Empty>{data.collects ? 'Ingen fejl eller advarsler i perioden 👍' : 'Ingen hændelser endnu'}</Empty>
            : view === 'groups'
              ? <Stack gap={6}>{data.groups.map((g) => <GroupRow key={g.key} g={g} machineId={m.id} onIgnore={onIgnore} />)}</Stack>
              : <EntryList entries={data.entries} total={data.total} />}
        </Stack>
      </Panel>
    </Stack>
  );
}

function EntryList({ entries, total }: { entries: MachineSysLog['entries']; total: number }) {
  return (
    <Stack gap={4}>
      {entries.map((e) => (
        <Group key={e.id} gap={8} wrap="nowrap" align="flex-start" style={{ borderBottom: '1px solid var(--mantine-color-dark-5)', paddingBottom: 4 }}>
          <Text size="xs" c="dimmed" ff="monospace" style={{ flex: 'none', width: 88 }}>{when(e.time)}</Text>
          <LevelBadge level={e.level} />
          <Box style={{ minWidth: 0 }}>
            <Text size="xs" fw={600}>{groupTitle(e)}{known(e.kind) && <Text span size="xs" c="dimmed" fw={400}> · {sourceLabel(e)}</Text>}</Text>
            <Text size="xs" c="dimmed" lineClamp={2} style={{ wordBreak: 'break-word' }}>{e.message}</Text>
          </Box>
        </Group>
      ))}
      {total > entries.length && <Text size="xs" c="dimmed" ta="center">Viser de nyeste {entries.length} af {total}. Brug filtrene for at se andre.</Text>}
    </Stack>
  );
}

/** Live-visning: åbner en forbindelse gennem serveren til agenten og viser hændelserne, mens de skrives. */
export function LiveLogPanel({ machineId, onClose }: { machineId: number; onClose: () => void }) {
  const [level, setLevel] = useState<'warning' | 'all'>('warning');
  const [run, setRun] = useState(0);
  const [lines, setLines] = useState<LiveEvent[]>([]);
  const [status, setStatus] = useState('Starter …');
  const [state, setState] = useState<'connecting' | 'live' | 'stopped'>('connecting');
  const [paused, setPaused] = useState(false);
  const [q, setQ] = useState('');
  const view = useRef<HTMLDivElement>(null);
  const pausedRef = useRef(paused);
  pausedRef.current = paused;

  useEffect(() => {
    let ws: WebSocket | null = null; let id: string | null = null; let closed = false;
    const pending: LiveEvent[] = [];
    const flush = window.setInterval(() => { if (pending.length) setLines((l) => appendLive(l, pending.splice(0))); }, 250);
    const beat = window.setInterval(() => { if (document.visibilityState === 'visible' && ws?.readyState === WebSocket.OPEN) ws.send(heartbeatFrame()); }, HEARTBEAT_MS);
    setState('connecting'); setStatus('Starter …');
    (async () => {
      try {
        const s = await post<{ id: string; ticket: string }>('/api/syslog/live', { machineId, level });
        id = s.id;
        if (closed) { del(`/api/syslog/live/${s.id}`).catch(() => {}); return; }
        const u = new URL(`api/syslog/live/ws/${s.id}`, document.baseURI);
        u.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'; u.searchParams.set('ticket', s.ticket);
        ws = new WebSocket(u.toString()); ws.binaryType = 'arraybuffer';
        ws.onmessage = (msg) => {
          const f = parseLiveFrame(new Uint8Array(msg.data as ArrayBuffer));
          if (!f) return;
          if (f.type === 'event') pending.push(f.ev);
          else if (f.type === 'status') { if (f.text) setStatus(f.text); else { setStatus(''); setState('live'); } }
          else setState('stopped');
        };
        ws.onclose = () => { setState('stopped'); setStatus((t) => t || 'Live-loggen er stoppet.'); };
      } catch (e) { setState('stopped'); setStatus(e instanceof Error ? e.message : String(e)); }
    })();
    return () => {
      closed = true; window.clearInterval(flush); window.clearInterval(beat);
      ws?.close();
      if (id) del(`/api/syslog/live/${id}`).catch(() => {});
    };
  }, [machineId, level, run]);

  useEffect(() => { if (!pausedRef.current && view.current) view.current.scrollTop = view.current.scrollHeight; }, [lines]);
  const shown = useMemo(() => lines.filter((e) => liveMatches(e, q)), [lines, q]);
  const copy = () => navigator.clipboard?.writeText(shown.map(liveText).join('\n')).catch(notifyErr);

  return (
    <Panel title={<Group gap={8}><IconBroadcast size={18} color={state === 'live' ? 'var(--mantine-color-red-6)' : undefined} />Live-log
      <Badge size="sm" variant="dot" color={state === 'live' ? 'red' : state === 'connecting' ? 'yellow' : 'gray'}>{state === 'live' ? 'Live' : state === 'connecting' ? 'Forbinder' : 'Stoppet'}</Badge></Group>}
      right={<Group gap={6}>
        {state === 'stopped'
          ? <Button size="xs" variant="light" leftSection={<IconPlayerPlay size={14} />} onClick={() => setRun((r) => r + 1)}>Start igen</Button>
          : <Button size="xs" variant="light" color="gray" leftSection={paused ? <IconPlayerPlay size={14} /> : <IconPlayerPause size={14} />} onClick={() => setPaused((p) => !p)}>{paused ? 'Rul med' : 'Pause'}</Button>}
        <Button size="xs" variant="subtle" color="gray" leftSection={<IconPlayerStop size={14} />} onClick={onClose}>Luk</Button>
      </Group>}>
      <Stack gap="xs">
        <Group gap="xs" wrap="wrap">
          <SegmentedControl size="xs" value={level} onChange={(v) => { setLines([]); setLevel(v as 'warning' | 'all'); }}
            data={[{ value: 'warning', label: 'Fejl og advarsler' }, { value: 'all', label: 'Alt' }]} />
          <TextInput size="xs" style={{ flex: '1 1 160px' }} placeholder="Filtrér linjerne" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} />
          <Tooltip label="Kopiér de viste linjer"><Button size="xs" variant="subtle" color="gray" onClick={copy} aria-label="Kopiér"><IconCopy size={14} /></Button></Tooltip>
        </Group>
        {status && <Text size="xs" c={state === 'stopped' ? 'yellow' : 'dimmed'}>{status}</Text>}
        <ScrollArea.Autosize mah={420} viewportRef={view} type="auto" style={{ background: 'var(--mantine-color-dark-8)', borderRadius: 6 }}>
          <Box p="xs" ff="monospace" fz={12} style={{ lineHeight: 1.55 }}>
            {shown.length === 0 && <Text size="xs" c="dimmed">{state === 'live' ? 'Venter på nye linjer …' : ' '}</Text>}
            {shown.map((e, i) => (
              <Box key={i} style={{ display: 'flex', flexWrap: 'wrap', columnGap: 8, alignItems: 'baseline', marginBottom: 2 }}>
                <span style={{ color: 'var(--mantine-color-dimmed)', flex: 'none' }}>{new Date(e.time).toLocaleTimeString('da-DK')}</span>
                <span style={{ color: `var(--mantine-color-${LEVELS[e.level]?.color ?? 'gray'}-5)`, flex: 'none', width: 64 }}>{e.level === 'info' ? 'Info' : LEVELS[e.level]?.label ?? e.level}</span>
                <span style={{ color: 'var(--mantine-color-blue-4)', flex: 'none', maxWidth: 180, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={e.source}>{sourceLabel(e)}</span>
                <span style={{ wordBreak: 'break-word', minWidth: 0, flex: '1 1 300px' }}>
                  {known(e.kind) && <Badge size="xs" variant="light" color={LEVELS[e.level]?.color ?? 'gray'} mr={6}>{known(e.kind)!.title}</Badge>}
                  {e.message}
                </span>
              </Box>
            ))}
          </Box>
        </ScrollArea.Autosize>
        <Text size="xs" c="dimmed">Kun til læsning. Adgangskoder og tokens fjernes på maskinen, før linjerne sendes. Stopper af sig selv efter 30 minutter, eller når siden lukkes.</Text>
      </Stack>
    </Panel>
  );
}
