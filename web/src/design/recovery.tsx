import { Badge, Button, Group, Modal, Stack, Text, ThemeIcon, Tooltip } from '@mantine/core';
import { useState } from 'react';
import { modals } from '@mantine/modals';
import { IconArchive, IconCircleCheck, IconCircleX, IconRestore } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { post, useAction, useApi } from '../api';
import { Panel } from '../components';
import { ago } from '../format';
import type { MachineDetail } from '../types';

interface KitFile { name: string; size: number; created: string; machineId: number; ok: boolean | null; files: number | null; units: number | null; error: string | null }
interface KitGroup { machineId: number; machine: string; kits: KitFile[] }
const mb = (b: number) => b > 1e9 ? `${(b / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(b / 1e6))} MB`;

/** Linux: "Hold kørende"-tjenester og gendannelsespunkter (gemt i Fleetdeck; gendan overskriver aldrig noget). */
export function RecoveryPanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const qc = useQueryClient();
  const { data: kits = [] } = useApi<KitFile[]>(['kits', m.id], `/api/machines/${m.id}/kits`, { refetchInterval: 60_000 });
  const act = useAction((b: { action: string; params?: object }) => post('/api/machines/actions', { machineIds: [m.id], ...b }), 'Sat i kø');
  const kept = m.health.kept ?? [];
  const [other, setOther] = useState(false);
  const restore = (k: KitFile, from?: string) => modals.openConfirmModal({
    title: from ? `Gendan ${m.name} fra ${from}s punkt?` : `Gendan fra ${k.name}?`,
    children: <Text size="sm">Lægger kun filer tilbage, der mangler på maskinen, installerer manglende pakker og starter kun de tjenester, der kørte, da punktet blev taget.
      Eksisterende filer overskrives aldrig, og tjenester, der kører, røres ikke – en maskine, der virker, forstyrres ikke.</Text>,
    labels: { confirm: 'Gendan', cancel: 'Annullér' }, onConfirm: () => { setOther(false); act.mutate({ action: 'kit_restore', params: { name: k.name, fromMachineId: k.machineId } }); },
  });
  if (!kept.length && !kits.length && !m.profile) return null;
  return (
    <Panel title="Drift og gendannelse" right={isAdmin && <Button size="xs" variant="light" leftSection={<IconArchive size={14} />} loading={act.isPending}
      onClick={() => act.mutate({ action: 'kit_backup' }, { onSuccess: () => setTimeout(() => qc.invalidateQueries({ queryKey: ['kits', m.id] }), 60_000) })}>Tag gendannelsespunkt nu</Button>}>
      <Stack gap="sm">
        {kept.length > 0 && <div>
          <Text size="sm" fw={600} mb={4}>Hold kørende</Text>
          <Stack gap={4}>{kept.map((k) => (
            <Group key={k.name} gap="xs" wrap="nowrap">
              <ThemeIcon size="sm" variant="light" color={k.active ? 'teal' : k.skipped ? 'gray' : 'red'} radius="xl">{k.active || k.skipped ? <IconCircleCheck size={13} /> : <IconCircleX size={13} />}</ThemeIcon>
              <Text size="sm" style={{ flex: 1 }}>{k.name}</Text>
              {k.restarts24h > 0 && <Badge size="xs" variant="light" color="orange">startet igen {k.restarts24h}× i døgnet</Badge>}
              <Text size="xs" c={k.active || k.skipped ? 'dimmed' : 'red'} title={k.skipped ? 'Tjenestens betingelse er ikke opfyldt på denne maskine, så den springer bevidst over sig selv. Det er ikke en fejl.' : undefined}>{k.active ? 'kører' : k.skipped ? 'springer over (betingelse ikke opfyldt)' : k.state}</Text>
            </Group>))}</Stack>
        </div>}
        <div>
          <Text size="sm" fw={600} mb={4}>Gendannelsespunkter</Text>
          {kits.length === 0 ? <Text size="sm" c="dimmed">Ingen endnu. Sæt stier og plan i profilen, eller tag et nu.</Text>
            : <Stack gap={4}>{kits.slice(0, 8).map((k) => (
              <Group key={k.name} gap="xs" wrap="nowrap">
                <Text size="sm" ff="monospace" style={{ flex: 1 }} lineClamp={1}>{k.name}</Text>
                <KitCheck k={k} />
                <Text size="xs" c="dimmed">{mb(k.size)} · {ago(k.created)}</Text>
                {isAdmin && <Button size="compact-xs" variant="subtle" leftSection={<IconRestore size={12} />} onClick={() => restore(k)}>Gendan…</Button>}
              </Group>))}</Stack>}
          <Text size="xs" c="dimmed" mt={4}>Gemmes i Fleetdeck (de 14 nyeste + én pr. uge den seneste måned), og hver pakke kontrolleres, når den modtages.</Text>
          {isAdmin && <Button mt="xs" size="xs" variant="default" leftSection={<IconRestore size={14} />} onClick={() => setOther(true)}>Gendan fra en anden maskine…</Button>}
        </div>
      </Stack>
      <OtherKits opened={other} onClose={() => setOther(false)} self={m.id} onPick={(k, from) => restore(k, from)} />
    </Panel>
  );
}

function KitCheck({ k }: { k: KitFile }) {
  if (k.ok == null) return null;
  return k.ok
    ? <Tooltip label={`Pakken er åbnet og kontrolleret: ${k.files} filer, ${k.units} tjenester i manifestet`}><Badge size="xs" variant="light" color="teal">kontrolleret</Badge></Tooltip>
    : <Tooltip label={k.error ?? 'Fejl'}><Badge size="xs" variant="light" color="red">defekt</Badge></Tooltip>;
}

/** Ny maskine (fx en ny Pi efter et nedbrud): vælg den gamle maskines gendannelsespunkt. */
function OtherKits({ opened, onClose, self, onPick }: { opened: boolean; onClose: () => void; self: number; onPick: (k: KitFile, from: string) => void }) {
  const { data = [] } = useApi<KitGroup[]>(['kits-all'], opened ? '/api/kits' : null);
  const groups = data.filter((g) => g.machineId !== self);
  return (
    <Modal opened={opened} onClose={onClose} title="Gendan fra en anden maskine" size="lg">
      <Text size="sm" c="dimmed" mb="sm">Bruges når en maskine er udskiftet, fx en ny Pi: installér Fleetdeck-agenten på den nye, vælg profilen, og gendan den gamles opsætning her. Intet på den nye maskine overskrives.</Text>
      {groups.length === 0 ? <Text size="sm">Ingen andre maskiner har gendannelsespunkter.</Text> : groups.map((g) => (
        <div key={g.machineId}>
          <Text fw={600} size="sm" mt="sm" mb={4}>{g.machine}</Text>
          <Stack gap={4}>{g.kits.slice(0, 6).map((k) => (
            <Group key={k.name} gap="xs" wrap="nowrap">
              <Text size="sm" ff="monospace" style={{ flex: 1 }} lineClamp={1}>{k.name}</Text>
              <KitCheck k={k} />
              <Text size="xs" c="dimmed">{mb(k.size)} · {ago(k.created)}</Text>
              <Button size="compact-xs" variant="light" disabled={k.ok === false} onClick={() => onPick(k, g.machine)}>Vælg</Button>
            </Group>))}</Stack>
        </div>))}
    </Modal>
  );
}
