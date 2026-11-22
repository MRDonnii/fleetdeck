import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Badge, Button, Group, Indicator, Paper, Popover, Progress, Stack, Text, ThemeIcon, UnstyledButton } from '@mantine/core';
import { IconLoader2, IconPlayerPause, IconPower, IconRefresh, IconX } from '@tabler/icons-react';
import { post, useAction } from '../api';
import type { MachineRow } from '../types';

const utc = (s: string) => new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(s) ? s : s + 'Z').getTime();

/** Sekundtæller, så nedtællinger opdaterer sig selv. */
export function useNow(active = true) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!active) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [active]);
  return now;
}

export const clock = (ms: number) => {
  const s = Math.max(0, Math.round(ms / 1000));
  return s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}` : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
};

const verb = (mode: string | null) => (mode === 'reboot' ? 'genstarter' : 'slukker');

function usePowerAction() {
  return useAction((a: { id: number; delay?: number }) =>
    post('/api/machines/actions', { machineIds: [a.id], action: 'cancel_power', params: a.delay ? { delay: a.delay } : {} }),
  'Sendt til maskinen');
}

/** Knapper til en planlagt genstart/slukning. */
export function PowerButtons({ m, size = 'xs' }: { m: MachineRow; size?: 'xs' | 'compact-xs' | 'compact-sm' }) {
  const act = usePowerAction();
  const stop = (e: React.MouseEvent) => { e.preventDefault(); e.stopPropagation(); };
  return (
    <Group gap={6} wrap="nowrap" onClick={stop}>
      <Button size={size} variant="default" leftSection={<IconPlayerPause size={13} />} loading={act.isPending}
        onClick={(e) => { stop(e); act.mutate({ id: m.id, delay: 3600 }); }}>Udsæt 1 t.</Button>
      <Button size={size} variant="light" color="red" leftSection={<IconX size={13} />} loading={act.isPending}
        onClick={(e) => { stop(e); act.mutate({ id: m.id }); }}>Annullér</Button>
    </Group>
  );
}

/** "Genstarter om 3:12" med levende nedtælling. */
export function PowerCountdown({ m, now }: { m: MachineRow; now: number }) {
  if (!m.power) return null;
  const left = utc(m.power.at) - now;
  return (
    <span className="fd-power" data-soon={left < 5 * 60_000 || undefined}>
      <IconPower size={13} />
      {left > 0 ? <>{verb(m.power.mode)} om <b>{clock(left)}</b></> : <>{verb(m.power.mode)} nu …</>}
    </span>
  );
}

/** Det job, der kører lige nu på maskinen, med bjælke. */
export function RunningLine({ m }: { m: MachineRow }) {
  if (!m.running) return null;
  const p = m.running.progress;
  return (
    <div className="fd-running">
      <Group gap={6} wrap="nowrap">
        <IconLoader2 size={13} className="fd-spin" />
        <Text size="xs" fw={600} lineClamp={1} style={{ flex: 1 }} title={m.running.step ?? undefined}>{m.running.title}</Text>
        {p != null && <Text size="xs" c="dimmed">{p}{'\u00A0'}%</Text>}
      </Group>
      <Progress value={p ?? 100} size={4} striped animated mt={4} color={p == null ? 'gray' : 'blue'} />
    </div>
  );
}

/** Banner øverst på alle sider, når en maskine snart genstarter eller slukker. */
export function PowerBanner({ machines, isAdmin }: { machines: MachineRow[]; isAdmin: boolean }) {
  const list = machines.filter((m) => m.power);
  const now = useNow(list.length > 0);
  if (!list.length) return null;
  return (
    <Stack gap={8} mb="md">
      {list.map((m) => {
        const left = utc(m.power!.at) - now;
        return (
          <Paper key={m.id} className="fd-power-banner" p="sm" px="md" data-soon={left < 5 * 60_000 || undefined}>
            <Group gap="sm" wrap="wrap">
              <ThemeIcon variant="light" color="orange" radius="xl"><IconPower size={16} /></ThemeIcon>
              <div style={{ flex: 1, minWidth: 180 }}>
                <Text size="sm" fw={650}><Link to={`/maskiner/${m.id}`}>{m.name}</Link> {verb(m.power!.mode)} {left > 0 ? <>om <span className="fd-tabnum">{clock(left)}</span></> : 'nu'}</Text>
                <Text size="xs" c="dimmed">Kl. {new Date(utc(m.power!.at)).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' })} · brugeren ser en nedtælling på skærmen</Text>
              </div>
              {isAdmin && left > 0 && <PowerButtons m={m} size="compact-sm" />}
            </Group>
          </Paper>
        );
      })}
    </Stack>
  );
}

/** Lille indikator i topbjælken: hvad kører lige nu, på tværs af maskinerne. */
export function ActivityIndicator({ machines }: { machines: MachineRow[] }) {
  const busy = machines.filter((m) => m.running);
  if (!busy.length) return null;
  return (
    <Popover position="bottom-end" width={340} shadow="md" withinPortal>
      <Popover.Target>
        <UnstyledButton className="fd-activity" aria-label="Aktivitet">
          <Indicator processing color="blue" size={8} offset={2}><IconRefresh size={16} className="fd-spin" /></Indicator>
          <span>{busy.length} i gang</span>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="sm">
          <Group justify="space-between"><Text fw={650} size="sm">Kører lige nu</Text><Badge size="xs" variant="light">{busy.length}</Badge></Group>
          {busy.map((m) => (
            <Link key={m.id} to={`/maskiner/${m.id}`} style={{ color: 'inherit', textDecoration: 'none' }}>
              <Text size="xs" c="dimmed">{m.name}</Text>
              <RunningLine m={m} />
            </Link>
          ))}
          <Button component={Link} to="/jobs" size="compact-xs" variant="subtle">Alle jobs</Button>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}
