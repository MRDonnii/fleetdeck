import { useState } from 'react';
import { Badge, Button, Group, Stack, Text, Tooltip } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconAlertCircle, IconAlertTriangle, IconBan, IconCpu, IconDownload, IconEye, IconEyeOff, IconLock, IconQuestionMark, IconUser } from '@tabler/icons-react';
import { post, useAction } from '../api';
import { ago } from '../format';
import type { MachineDetail, Upgrade } from '../types';
import { Panel } from '../components';
import { askAmdDriver } from './amdDrivers';
import { canHide, groupSkipped, hideAllLabel, hideText, skippedButton, skippedHideable, type SkippedGroup, type SkippedItem, type UpgradeAllResult } from './upgradeSkippedLogic';

export { canHide };
export type { UpgradeAllResult };

const ICONS: Record<SkippedGroup, typeof IconCpu> = { silent: IconCpu, user: IconUser, fail: IconAlertTriangle, blocked: IconBan, separate: IconDownload, other: IconQuestionMark, ignored: IconLock };

/** Skjul (eller vis igen): Windows Update via et job på pc'en (IsHidden); alt andet i Fleetdeck selv, med det samme (done). */
export function useHideUpdates(machineId: number) {
  return useAction(({ ids, hide }: { ids: string[]; hide: boolean }) => post<{ title: string; done?: boolean }>(`/api/machines/${machineId}/updates/hide`, { ids, hide }),
    (r) => r.done ? r.title : `${r.title} – sendt til pc'en`);
}

export function askHide(names: string[], ids: string[], onConfirm: () => void) {
  const one = names.length === 1;
  modals.openConfirmModal({
    title: one ? `Skjul ${names[0]}?` : `Skjul ${names.length} opdateringer?`,
    children: (
      <Stack gap="xs">
        {!one && <Text size="sm">{names.join(', ')}</Text>}
        <Text size="sm">{hideText(ids)}</Text>
        <Text size="sm" c="dimmed">Du kan vise {one ? 'den' : 'dem'} igen under "Skjulte opdateringer".</Text>
      </Stack>
    ),
    labels: { confirm: 'Skjul', cancel: 'Annullér' },
    onConfirm,
  });
}

/** "Anbefales ikke" med grunden som værktøjstip. */
export function NotRecommended({ advice }: { advice: string }) {
  return (
    <Tooltip label={advice} multiline w={280}>
      <Badge size="xs" variant="light" color="orange" leftSection={<IconAlertCircle size={11} />}>Anbefales ikke</Badge>
    </Tooltip>
  );
}

/** Efter "Opdatér alt": det, der ikke blev installeret, grupperet efter grund – med en knap til at gøre det enkeltvis. */
export function SkippedPanel({ m, result, isAdmin, queued, onUpgrade, onOpen }: {
  m: MachineDetail; result: UpgradeAllResult; isAdmin: boolean; queued: (id: string) => boolean;
  onUpgrade: (id: string, name: string) => void; onOpen: (jobId: number) => void;
}) {
  const hide = useHideUpdates(m.id);
  if (!result.skipped.length) return null;
  const n = result.skipped.length;
  const byId = (id: string): Upgrade | undefined => m.upgradeList.find((u) => u.wingetId === id);
  const hideable = result.skipped.filter((x) => result.hideable.includes(x.wingetId));
  const run = (x: SkippedItem) => {
    const u = byId(x.wingetId);
    if (x.action === 'amd' && u) askAmdDriver(u, () => onUpgrade(x.wingetId, x.name));
    else onUpgrade(x.wingetId, x.name);
  };
  return (
    <Panel title={`${n} blev sprunget over i Opdatér alt`} right={isAdmin && hideable.length > 0 && (
      <Button size="xs" variant="default" leftSection={<IconEyeOff size={14} />} loading={hide.isPending}
        onClick={() => askHide(hideable.map((x) => x.name), hideable.map((x) => x.wingetId), () => hide.mutate({ ids: hideable.map((x) => x.wingetId), hide: true }))}>
        {hideAllLabel(hideable.length)}</Button>)}>
      <Text size="xs" c="dimmed" mt={-8} mb="xs" style={{ cursor: 'pointer' }} onClick={() => onOpen(result.jobId)}>
        {result.finished ? `${ago(result.finished)} · ` : ''}{result.updated != null ? `${result.updated} opdateret · ` : ''}vis resultat</Text>
      <Stack gap="sm">
        {groupSkipped(result.skipped).map((g) => { const Icon = ICONS[g.group]; return (
          <div key={g.group}>
            <Group gap={6} mb={4}><Icon size={14} color="var(--mantine-color-dimmed)" />
              <Text size="xs" fw={700} c="dimmed" tt="uppercase" style={{ letterSpacing: '.04em' }}>{g.label} ({g.items.length})</Text></Group>
            <Stack gap={0}>
              {g.items.map((x) => {
                const b = skippedButton(x);
                return (
                  <Group key={x.wingetId} justify="space-between" wrap="wrap" gap="xs" py={6} align="flex-start"
                    style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                    <div style={{ flex: 1, minWidth: 220 }}>
                      <Group gap={6} wrap="wrap"><Text size="sm" fw={500}>{x.name}</Text>{x.advice && <NotRecommended advice={x.advice} />}</Group>
                      <Text size="xs" c="dimmed">{x.reason}{x.advice ? ` ${x.advice}` : ''}</Text>
                    </div>
                    {isAdmin && (queued(x.wingetId) ? <Badge size="sm" color="gray" variant="light">I kø</Badge> : (
                      <Group gap={4} wrap="nowrap">
                        {b && <Button size="compact-xs" variant="default" onClick={() => run(x)}>{b}</Button>}
                        {skippedHideable(x, m.platform) && <Button size="compact-xs" variant="subtle" color="gray" loading={hide.isPending}
                          onClick={() => askHide([x.name], [x.wingetId], () => hide.mutate({ ids: [x.wingetId], hide: true }))}>Skjul</Button>}
                      </Group>))}
                  </Group>
                );
              })}
            </Stack>
          </div>); })}
      </Stack>
    </Panel>
  );
}

/** Skjulte opdateringer (i Windows Update eller i Fleetdeck): klappet sammen, med "Vis igen" pr. linje. */
export function HiddenUpdatesPanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const [open, setOpen] = useState(false);
  const show = useHideUpdates(m.id);
  const list = m.hiddenUpgrades ?? [];
  if (!list.length) return null;
  return (
    <Panel>
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}><IconEyeOff size={16} color="var(--mantine-color-dimmed)" />
          <Text size="sm">{list.length === 1 ? '1 skjult opdatering' : `${list.length} skjulte opdateringer`} – de tilbydes ikke længere</Text></Group>
        <Button size="compact-xs" variant="subtle" onClick={() => setOpen((o) => !o)}>{open ? 'Skjul listen' : 'Vis listen'}</Button>
      </Group>
      {open && <Stack gap={0} mt="xs">{list.map((u) => (
        <Group key={u.wingetId} justify="space-between" wrap="wrap" gap="xs" py={6} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <div style={{ flex: 1, minWidth: 200 }}>
            <Group gap={6} wrap="wrap"><Text size="sm">{u.name}</Text>{u.advice && <NotRecommended advice={u.advice} />}</Group>
            {u.available && <Text size="xs" c="dimmed">{u.driver ? 'Driver' : 'Opdatering'} · {u.available}</Text>}
          </div>
          {isAdmin && <Button size="compact-xs" variant="default" leftSection={<IconEye size={13} />} loading={show.isPending}
            onClick={() => show.mutate({ ids: [u.wingetId], hide: false })}>Vis igen</Button>}
        </Group>))}
      </Stack>}
    </Panel>
  );
}
