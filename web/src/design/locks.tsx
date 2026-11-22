import { Badge, Button, Group, Stack, Text, Tooltip } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconLock, IconLockOpen } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { post, useAction } from '../api';

export type LockScope = 'alle' | 'maskine' | 'profil' | null | undefined;
const LABEL: Record<string, string> = { alle: 'alle maskiner', maskine: 'denne maskine', profil: 'profilen' };

/** "Opdatér aldrig": lås et program på denne maskine eller alle – eller vis låsen med "Lås op". */
export function LockControl({ machineId, machineName, wingetId, name, locked, compact }: {
  machineId: number; machineName?: string; wingetId: string; name: string; locked: LockScope; compact?: boolean;
}) {
  const qc = useQueryClient();
  const lock = useAction((scope: string) => post('/api/upgrades/lock', { machineId, wingetId, name, scope, locked: true }), `${name} opdateres ikke længere`);
  const unlock = useAction(() => post('/api/upgrades/lock', { machineId, wingetId, name, locked: false }), `${name} opdateres igen`);
  const done = () => qc.invalidateQueries();
  if (locked) {
    return (
      <Group gap={6} wrap="nowrap" justify="flex-end">
        <Tooltip label={`Opdateres aldrig – låst på ${LABEL[locked]}. "Opdatér alt" springer den over.`}>
          <Badge size="sm" variant="light" color="gray" leftSection={<IconLock size={11} />}>Opdateres aldrig{compact ? '' : ` (${LABEL[locked]})`}</Badge>
        </Tooltip>
        {locked === 'profil'
          ? <Tooltip label="Låst i profilen – ret det under Profiler"><Text size="xs" c="dimmed">i profil</Text></Tooltip>
          : <Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconLockOpen size={12} />} loading={unlock.isPending}
              onClick={() => unlock.mutate(undefined, { onSuccess: done })}>Lås op</Button>}
      </Group>
    );
  }
  const ask = () => modals.open({
    title: `Opdatér aldrig ${name}?`,
    children: (
      <Stack gap="sm">
        <Text size="sm">Programmet bliver på sin nuværende version. "Opdatér alt", vedligeholdelse og automatiske opdateringer springer det over, og det tæller ikke med som ventende. Du kan låse det op igen når som helst.</Text>
        <Button leftSection={<IconLock size={16} />} onClick={() => { modals.closeAll(); lock.mutate('machine', { onSuccess: done }); }}>
          Kun på {machineName ?? 'denne maskine'}</Button>
        <Button variant="default" leftSection={<IconLock size={16} />} onClick={() => { modals.closeAll(); lock.mutate('all', { onSuccess: done }); }}>
          På alle maskiner</Button>
      </Stack>
    ),
  });
  return <Tooltip label="Opdatér aldrig dette program"><Button size="compact-xs" variant="subtle" color="gray" loading={lock.isPending} onClick={ask}><IconLock size={14} /></Button></Tooltip>;
}
