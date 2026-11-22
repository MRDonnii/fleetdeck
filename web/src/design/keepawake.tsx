import { Badge, Stack, Switch, Text } from '@mantine/core';
import { put, useAction } from '../api';
import { Panel } from '../components';
import { ago, fmt } from '../format';
import type { KeepAwakeState, MachineDetail } from '../types';

/** Teksten for tilstanden lige nu (samme som i menulinjen på Mac'en). */
export function keepAwakeText(s: KeepAwakeState | null | undefined, on: boolean): { label: string; color: string } {
  if (!s) return on ? { label: 'Venter på Mac\'en', color: 'gray' } : { label: 'Fra', color: 'gray' };
  switch (s.state) {
    case 'held': return { label: 'Strøm · holdes vågen', color: 'teal' };
    case 'battery': return { label: 'Batteri · sover normalt', color: 'yellow' };
    case 'user': return { label: 'Sat af brugeren', color: 'blue' };
    case 'error': return { label: 'Fejl', color: 'red' };
    default: return { label: 'Fra', color: 'gray' };
  }
}

/** Mac: "Hold vågen på strøm (også med lukket låg)" – kan også slås til og fra i menulinjen på Mac'en; sidste ændring vinder. */
export function KeepAwakePanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const k = m.keepAwake!;
  const live = k.live;
  const set = useAction((on: boolean) => put(`/api/machines/${m.id}/keepawake`, { on }), (r) => ((r as { on: boolean }).on ? 'Holdes vågen på strøm' : 'Sover normalt igen'));
  const t = keepAwakeText(live, k.on);
  const changed = k.at ? (k.source === 'mac' ? `Ændret på Mac'en ${fmt(k.at)} (${ago(k.at)})` : `Ændret i Fleetdeck${k.by ? ` af ${k.by}` : ''} ${fmt(k.at)} (${ago(k.at)})`) : null;
  return (
    <Panel title="Strøm og dvale" right={<Badge variant="light" color={t.color}>{t.label}</Badge>}>
      <Stack gap="xs">
        <Switch label="Hold vågen på strøm (også med lukket låg)" checked={k.on} disabled={!isAdmin || set.isPending}
          onChange={(e) => set.mutate(e.currentTarget.checked)}
          description="Mac'en kan bruges til jobs, når den er sat til strøm – også med lukket låg. På batteri sover den altid normalt. Kan også slås til og fra i Fleetdeck-menuen på Mac'en." />
        {live?.state === 'user' && <Text size="xs" c="dimmed">Dvale er slået fra på Mac'en af brugeren selv (pmset). Fleetdeck lader den indstilling være.</Text>}
        {live?.state === 'error' && live.error && <Text size="xs" c="red">{live.error}</Text>}
        {live && live.onAc != null && <Text size="xs" c="dimmed">Strømkilde: {live.onAc ? 'lysnettet' : 'batteri'}</Text>}
        {changed && <Text size="xs" c="dimmed">{changed}</Text>}
        {live && live.setting !== k.on && <Text size="xs" c="dimmed">Mac'en har ikke fået ændringen endnu (den kommer ved næste kontakt).</Text>}
      </Stack>
    </Panel>
  );
}
