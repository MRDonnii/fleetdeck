// Reservekort på Raspberry Pi (forslag B: statuspanel på Overblik og et vindue til at vælge og bekræfte kortet).
// Server: src/Fleetdeck.Server/Services/SpareCards.cs. Slået fra som standard: uden et valgt og bekræftet kort laves der intet.
import { useEffect, useState, type ReactNode } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Modal, Paper, Radio, Stack, Switch, Text, TextInput } from '@mantine/core';
import { IconCopy, IconDeviceSdCard, IconRefresh, IconSwitchHorizontal, IconTrash } from '@tabler/icons-react';
import { del, post, put, useApi } from '../api';
import { KeyValue } from '../components';
import { ago } from '../format';

type Disk = { id: string; path: string; size: number; model: string; vendor: string; serial: string; partitions: number; mounted: boolean; empty: boolean; label: string };
type View = {
  machineId: number; isPi: boolean; reported: boolean; candidates: Disk[];
  chosen: boolean; enabled: boolean; time: string; netbootOn: boolean; diskId: string; diskLabel: string; serial: string; size: number;
  confirmedAt: string | null; confirmedBy: string; present: boolean | null;
  lastRun: string | null; lastOk: string | null; status: '' | 'kører' | 'ok' | 'fejl'; message: string; kernel: string | null; bytes: number;
  ageHours: number | null; stale: boolean; next: string | null;
  job: { id: number; status: string; progress: number; step: string | null } | null;
  netbootSync: { status: string; message: string; at: string | null; signature: string | null };
};

const errText = (e: unknown) => e instanceof Error ? e.message : String(e);

function statusBadge(v: View) {
  if (!v.chosen) return <Badge variant="light" color="gray">Ikke valgt</Badge>;
  if (v.job) return <Badge variant="light" color="blue">Kopierer</Badge>;
  if (!v.enabled) return <Badge variant="light" color="gray">Slået fra</Badge>;
  if (v.stale) return <Badge variant="light" color="orange">For gammel</Badge>;
  if (v.status === 'fejl') return <Badge variant="light" color="red">Fejl</Badge>;
  if (v.status === 'ok') return <Badge variant="light" color="green">OK</Badge>;
  return <Badge variant="light" color="gray">Venter</Badge>;
}

/** Panelet på Pi'ens Overblik (kun administratorer). */
export function SpareCardPanel({ machineId }: { machineId: number }) {
  const { data: v, refetch } = useApi<View>(['sparecard', machineId], `/api/sparecard/${machineId}`, { refetchInterval: 30000 });
  const [pick, setPick] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [time, setTime] = useState('');
  useEffect(() => { if (v?.time) setTime(v.time); }, [v?.time]);
  if (!v || !v.isPi) return null;
  const act = async (f: () => Promise<unknown>) => {
    setBusy(true); setErr(null);
    try { await f(); await refetch(); } catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  };
  return (
    <Paper withBorder p="md" radius="md">
      <Group justify="space-between" mb="xs" wrap="nowrap">
        <Group gap={8} wrap="nowrap"><IconDeviceSdCard size={18} /><Text fw={600}>Reservekort</Text></Group>
        {statusBadge(v)}
      </Group>
      {!v.chosen ? (
        <Stack gap="xs">
          <Text size="xs" c="dimmed">En kopi af hele Pi'en til et SD-kort i en USB-kortlæser hver nat. Dør Pi'ens kort, flytter du reservekortet over og starter igen. Slået fra, til du har valgt et kort.</Text>
          {!v.reported && <Alert color="gray" p="xs">Agenten har ikke meldt USB-diske endnu. Opdatér agenten, eller vent på næste melding.</Alert>}
          {v.reported && v.candidates.length === 0 && <Alert color="gray" p="xs">Pi'en kan ikke se nogen kortlæser. Sæt en USB-kortlæser med et SD-kort i Pi'en.</Alert>}
          <Group justify="flex-end"><Button size="xs" leftSection={<IconDeviceSdCard size={14} />} disabled={!v.candidates.length} onClick={() => setPick(true)}>Vælg kort…</Button></Group>
        </Stack>
      ) : (
        <Stack gap="xs">
          <KeyValue rows={[
            ['Kort', <>{v.diskLabel}{v.present === false && <Badge ml={6} size="xs" variant="light" color="orange">ikke i Pi'en</Badge>}</>],
            ['Seneste kopi', v.lastOk ? `${ago(v.lastOk)}${v.ageHours != null ? ` (${v.ageHours} timer)` : ''}` : 'Ingen endnu'],
            ...(v.status === 'fejl' && v.message ? [['Sidste forsøg', <Text size="sm" c="red">{v.message}</Text>] as [string, ReactNode]] : []),
            ...(v.job ? [['I gang', `${v.job.step ?? v.message ?? 'Venter på Pi\'en'}`] as [string, ReactNode]] : []),
            ['Næste kopi', v.enabled ? (v.next ?? '–') : 'Slået fra'],
            ...(v.kernel ? [['Kerne', <Code>{v.kernel}</Code>] as [string, ReactNode]] : []),
            ...(v.netbootSync.status ? [['Netboot', v.netbootSync.message || v.netbootSync.status] as [string, ReactNode]] : []),
          ]} />
          <Group gap="sm" wrap="wrap" align="flex-end">
            <Switch label="Kopiér hver nat" checked={v.enabled} disabled={busy} onChange={(e) => { const on = e.currentTarget.checked; act(() => put(`/api/sparecard/${machineId}`, { enabled: on })); }} />
            <TextInput size="xs" w={90} label="Kl." value={time} onChange={(e) => setTime(e.currentTarget.value)}
              onBlur={() => { if (time !== v.time) act(() => put(`/api/sparecard/${machineId}`, { time })); }} />
          </Group>
          <Checkbox size="xs" label="Hold netboot-nødsystemet på samme kerne som Pi'en" checked={v.netbootOn} disabled={busy}
            onChange={(e) => { const on = e.currentTarget.checked; act(() => put(`/api/sparecard/${machineId}`, { netboot: on })); }} />
          {err && <Alert color="red" p="xs">{err}</Alert>}
          <Group justify="flex-end" gap="xs">
            <Button size="xs" variant="default" color="red" leftSection={<IconTrash size={14} />} disabled={busy || !!v.job} onClick={() => act(() => del(`/api/sparecard/${machineId}/disk`))}>Fravælg</Button>
            <Button size="xs" variant="default" leftSection={<IconSwitchHorizontal size={14} />} disabled={busy || !!v.job} onClick={() => setPick(true)}>Skift kort…</Button>
            <Button size="xs" leftSection={<IconCopy size={14} />} loading={busy} disabled={!!v.job || v.present === false} onClick={() => act(() => post(`/api/sparecard/${machineId}/run`))}>Kopiér nu</Button>
          </Group>
        </Stack>
      )}
      {!v.chosen && err && <Alert color="red" p="xs" mt="xs">{err}</Alert>}
      <PickModal v={v} opened={pick} onClose={() => setPick(false)} onDone={() => { setPick(false); refetch(); }} refresh={refetch} />
    </Paper>
  );
}

function PickModal({ v, opened, onClose, onDone, refresh }: { v: View; opened: boolean; onClose: () => void; onDone: () => void; refresh: () => unknown }) {
  const usable = (d: Disk) => !d.empty && !d.mounted;
  const [id, setId] = useState<string | null>(null);
  const [ok, setOk] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => { if (opened) { setId(v.candidates.find((d) => d.id === v.diskId && usable(d))?.id ?? v.candidates.find(usable)?.id ?? null); setOk(false); setErr(null); } }, [opened]); // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    if (!id) return;
    setBusy(true); setErr(null);
    try { await put(`/api/sparecard/${v.machineId}/disk`, { diskId: id, confirm: ok }); onDone(); }
    catch (e) { setErr(errText(e)); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={onClose} title={<Group gap={8}><IconDeviceSdCard size={18} /><Text fw={600}>Vælg reservekort</Text></Group>} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Sæt en USB-kortlæser med et SD-kort i Pi'en. Kortet skal være mindst lige så stort som det, Pi'en bruger.</Text>
        {v.candidates.length === 0 ? <Alert color="gray" p="xs">Pi'en kan ikke se nogen USB-kortlæser lige nu.</Alert> : (
          <Radio.Group value={id} onChange={setId}>
            <Paper withBorder radius="md">
              {v.candidates.map((d, i) => (
                <Group key={d.id} gap="sm" wrap="nowrap" p="sm" style={{ borderTop: i ? '1px solid var(--mantine-color-default-border)' : undefined, opacity: usable(d) ? 1 : 0.55 }}>
                  <Radio value={d.id} disabled={!usable(d)} aria-label={d.label} />
                  <IconDeviceSdCard size={18} />
                  <Stack gap={0} style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600}>{d.label}</Text>
                    <Text size="xs" c="dimmed" ff="monospace" style={{ overflowWrap: 'anywhere' }}>{d.id}{d.serial ? ` · serienr. ${d.serial}` : ''}</Text>
                    {d.empty && <Text size="xs" c="orange">Intet kort i læseren</Text>}
                    {d.mounted && <Text size="xs" c="orange">Monteret på Pi'en – afmonter det først</Text>}
                  </Stack>
                </Group>
              ))}
            </Paper>
          </Radio.Group>
        )}
        <Checkbox checked={ok} onChange={(e) => setOk(e.currentTarget.checked)}
          label="Alt på kortet slettes ved første kopi. Pi'ens eget kort (mmcblk0) og det kort, Pi'en kører fra, kan ikke vælges." />
        {err && <Alert color="red" p="xs">{err}</Alert>}
        <Group justify="space-between">
          <Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={() => refresh()}>Opdatér listen</Button>
          <Group gap="xs">
            <Button variant="default" onClick={onClose}>Annullér</Button>
            <Button loading={busy} disabled={!id || !ok} onClick={save}>Brug kortet</Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
