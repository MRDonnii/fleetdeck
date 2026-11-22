import { Alert, Anchor, Badge, Button, Checkbox, Group, Menu, Paper, Stack, Switch, Text, Title } from '@mantine/core';
import { IconChevronDown } from '@tabler/icons-react';
import { put, useAction } from '../api';
import { ago } from '../format';
import { devMeta } from '../design/devices';
import type { IpadDetail, IpadRow } from '../types';

const seenText = (ts: string | null) => { const a = ago(ts); return a === 'lige nu' ? 'set lige nu' : `set for ${a}`; };
const dateText = (ts: string) => new Date(ts).toLocaleDateString('da-DK', { day: 'numeric', month: 'long' });
const SNOOZE = [3, 7, 14, 30];

/** Status for styringen: bygger på DNS-opslag med enhedens klient-id (iPad/iPhone) eller dens IP-adresse (Apple TV/Android). */
export function controlText(r: IpadRow) {
  const c = r.control; const ios = devMeta(r.deviceType).ios;
  if (c.state === 'active') return { color: 'green', text: `Styringen er aktiv · ${seenText(c.seen)}` };
  if (c.state === 'stale') return { color: 'yellow', text: `Ikke set i et stykke tid · ${seenText(c.seen)}` };
  return { color: ios ? 'red' : 'gray', text: ios ? 'Profil ikke installeret' : 'Ikke set endnu' };
}

export function ControlLine({ r }: { r: IpadRow }) {
  const c = r.control; const s = controlText(r);
  return (
    <Stack gap={2} mt={8} mb={4}>
      <Group gap={6} wrap="nowrap"><span className="fd-control-dot" style={{ background: `var(--mantine-color-${s.color}-5)` }} /><Text size="sm" fw={600}>{s.text}</Text></Group>
      {c.state !== 'active' && c.lanSeen && <Text size="xs" c="dimmed">Set på hjemmets netværk for {ago(c.lanSeen).replace('lige nu', 'et øjeblik siden')}, men ikke via profilen.</Text>}
      {c.snoozeUntil && <Text size="xs" c="dimmed">Ingen advarsler før {dateText(c.snoozeUntil)}.</Text>}
    </Stack>
  );
}

function SnoozeMenu({ r, size = 'xs' }: { r: IpadRow; size?: string }) {
  const act = useAction((days: number) => put(`/api/ipads/${r.id}/watch`, { snoozeDays: days }), 'Advarslen er sat på pause');
  return (
    <Menu withinPortal position="bottom-start">
      <Menu.Target><Button size={size} variant="default" rightSection={<IconChevronDown size={14} />}>Tager på ferie</Button></Menu.Target>
      <Menu.Dropdown>{SNOOZE.map((d) => <Menu.Item key={d} onClick={() => act.mutate(d)}>{d} dage</Menu.Item>)}</Menu.Dropdown>
    </Menu>
  );
}

/** Den åbne advarsel. Teksten kommer fra serveren og påstår ikke, at profilen er fjernet. */
export function WatchAlert({ r, can }: { r: IpadRow; can: string[] }) {
  if (!r.control.alertSince || !r.control.alertText) return null;
  return (
    <Alert color="orange" mb="sm" title="Styringen bruges ikke">
      <Text size="sm" mb={can.includes('rules') ? 8 : 0}>{r.control.alertText}</Text>
      {can.includes('rules') && <SnoozeMenu r={r} />}
    </Alert>
  );
}

/** Advarsel til/fra og pause for én enhed. */
export function WatchPanel({ r, can }: { r: IpadRow; can: string[] }) {
  const c = r.control; const ro = !can.includes('rules');
  const set = useAction((b: { enabled?: boolean; snoozeDays?: number }) => put(`/api/ipads/${r.id}/watch`, b), 'Gemt');
  return (
    <div>
      <Title order={5} mb={6}>Advarsel</Title>
      <Switch disabled={ro} checked={c.watch} onChange={(e) => set.mutate({ enabled: e.currentTarget.checked })} label="Advar, hvis styringen ikke bliver brugt"
        description={`Du får besked efter ${c.hours} timer uden brug. Fleetdeck kan ikke se forskel på en slukket enhed og en fjernet profil.`} />
      {!ro && c.watch && <Group gap={6} mt="xs">
        {c.snoozeUntil
          ? <><Text size="sm">Ingen advarsler før {dateText(c.snoozeUntil)}.</Text><Button size="xs" variant="default" onClick={() => set.mutate({ snoozeDays: 0 })}>Stop pausen</Button></>
          : <SnoozeMenu r={r} />}
      </Group>}
    </div>
  );
}

/** Tjeklisten pr. barn. Kun profilen kan registreres automatisk; resten er markeret af en voksen og er aldrig kontrolleret. */
export function SetupChecklist({ r, d, can }: { r: IpadRow; d: IpadDetail; can: string[] }) {
  const mark = useAction((a: { key: string; done: boolean }) => put(`/api/ipads/${r.id}/setup-steps/${a.key}`, { done: a.done }));
  if (d.setup.length === 0) return null;
  const ro = !can.includes('rules');
  return (
    <div>
      <Title order={5} mb={4}>Tjekliste til {r.name}</Title>
      <Text size="sm" c="dimmed" mb="sm">Fleetdeck kan kun selv se, om DNS-profilen bruges. De andre trin markerer du selv, og de bliver ikke kontrolleret.</Text>
      <Stack gap="xs">
        {d.setup.map((s, i) => (
          <Paper key={s.key} withBorder radius="md" p="sm">
            <Group justify="space-between" wrap="nowrap" align="flex-start" gap="sm">
              <Checkbox checked={s.done} disabled={s.auto || ro} onChange={(e) => mark.mutate({ key: s.key, done: e.currentTarget.checked })} label={<Text fw={650} size="sm">{i + 1}. {s.title}</Text>} />
              {s.source === 'auto' && <Badge color="green" variant="light" style={{ flexShrink: 0 }}>Registreret af Fleetdeck</Badge>}
              {s.source === 'manual' && <Badge color="gray" variant="light" style={{ flexShrink: 0 }}>Markeret af dig</Badge>}
            </Group>
            <Stack gap={3} mt={6} ml={30}>
              {s.lines.map((l) => <Text key={l} size="sm">{l}</Text>)}
              {s.note && <Text size="xs" c="dimmed">{s.note}</Text>}
              {s.links.length > 0 && <Group gap="md">{s.links.map((l) => <Anchor key={l.url} size="xs" href={l.url} target="_blank" rel="noreferrer">{l.label}</Anchor>)}</Group>}
            </Stack>
          </Paper>))}
      </Stack>
    </div>
  );
}
