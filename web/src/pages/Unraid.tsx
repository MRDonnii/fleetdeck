import { useMemo, useState } from 'react';
import {
  Anchor, Avatar, Badge, Button, Card, Group, Menu, Paper, Progress, RingProgress, SegmentedControl, SimpleGrid, Stack, Text, TextInput, ThemeIcon, Title, Tooltip,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import {
  IconBrandDocker, IconBrandUbuntu, IconBrandWindows, IconChevronDown, IconCpu, IconDatabase, IconDeviceDesktop, IconExternalLink, IconHome,
  IconPlayerPlay, IconPlayerStop, IconRefresh, IconSearch, IconServer2, IconShieldCheck, IconTemperature, IconDownload,
} from '@tabler/icons-react';
import { post, useAction } from '../api';
import { useMe } from '../auth';
import { ago } from '../format';
import type { MachineDetail, UnraidContainer, UnraidDisk, UnraidInfo, UnraidVm } from '../types';

/** Danske navne for Unraids rå tilstande. Ukendte værdier vises uændret. */
const ARRAY_STATE: Record<string, string> = {
  STARTED: 'Kører', STOPPED: 'Stoppet', STARTING: 'Starter', STOPPING: 'Stopper',
  NEW_ARRAY: 'Nyt array', RECON_DISK: 'Genopbygger disk', DISABLE_DISK: 'Disk slået fra', SWAP_DSBL: 'Disk byttes',
  INVALID_EXPANSION: 'Ugyldig udvidelse', PARITY_NOT_BIGGEST: 'Paritetsdisken er ikke størst', TOO_MANY_MISSING_DISKS: 'For mange diske mangler',
  NEW_DISK_TOO_SMALL: 'Ny disk er for lille', NO_DATA_DISKS: 'Ingen datadiske',
};
const DISK_STATUS: Record<string, string> = {
  DISK_OK: 'OK', DISK_NP: 'Mangler', DISK_NP_MISSING: 'Mangler', DISK_DSBL: 'Slået fra', DISK_NP_DSBL: 'Slået fra (mangler)',
  DISK_INVALID: 'Ugyldig', DISK_WRONG: 'Forkert disk', DISK_EMULATED: 'Emuleret', DISK_DSBL_NEW: 'Ny (slået fra)', DISK_NEW: 'Ny',
};
const VM_STATE: Record<string, string> = {
  running: 'Kører', paused: 'På pause', 'shut off': 'Slukket', shutoff: 'Slukket', 'in shutdown': 'Lukker ned', crashed: 'Gået ned',
  pmsuspended: 'Dvale', idle: 'Inaktiv', blocked: 'Blokeret', 'no state': 'Ukendt',
};
const CONTAINER_STATE: Record<string, string> = {
  running: 'Kører', exited: 'Stoppet', paused: 'På pause', restarting: 'Genstarter', created: 'Oprettet', removing: 'Fjernes', dead: 'Gået ned',
};
const diskStatus = (s: string) => DISK_STATUS[s] ?? DISK_STATUS[`DISK_${s}`] ?? s;

/** Unraid-fanen: array, diske, Docker og VM'er – med handlinger, der bliver til jobs på serveren. */
export function UnraidTab({ m }: { m: MachineDetail }) {
  const u = m.health.unraid as UnraidInfo;
  const { isAdmin } = useMe();
  const act = useAction((b: { kind: string; name?: string; action?: string }) => post(`/api/machines/${m.id}/unraid`, b), 'Sendt til serveren – vises her om lidt');
  const run = (kind: string, action: string, name?: string) => act.mutate({ kind, action, name });
  const confirm = (title: string, text: string, label: string, go: () => void, color = 'blue') =>
    modals.openConfirmModal({ title, children: <Text size="sm">{text}</Text>, labels: { confirm: label, cancel: 'Annullér' }, confirmProps: { color }, onConfirm: go });

  const running = u.containers.filter((c) => c.state === 'running').length;
  const updates = u.containers.filter((c) => c.updateAvailable).length;
  const vmsOn = u.vms.filter((v) => v.state === 'running').length;
  const array = u.disks.filter((d) => d.type === 'Data' || d.type === 'Parity');
  const pools = u.disks.filter((d) => d.type !== 'Data' && d.type !== 'Parity');

  return (
    <Stack gap="lg">
      <SimpleGrid cols={{ base: 2, sm: 3, lg: 5 }} spacing="md">
        <Kpi icon={<IconServer2 size={20} />} color={u.arrayState === 'STARTED' ? 'green' : 'red'} label="Array"
          value={u.arrayState ? ARRAY_STATE[u.arrayState] ?? u.arrayState : 'Ukendt'} title={u.arrayState || undefined} sub={`Unraid ${u.version}`} />
        <Kpi icon={<IconShieldCheck size={20} />} color={u.syncErrors ? 'red' : u.parityProgress != null ? 'blue' : 'green'} label="Paritet"
          value={u.parityProgress != null ? `${u.parityProgress} %` : u.syncErrors ? `${u.syncErrors} fejl` : 'OK'}
          sub={u.parityProgress != null ? 'tjek kører' : u.parityLast ? `tjekket ${ago(u.parityLast)}` : 'intet tjek endnu'} />
        <Kpi icon={<IconBrandDocker size={20} />} color="blue" label="Containere" value={`${running} / ${u.containers.length}`} sub="kører" />
        <Kpi icon={<IconDeviceDesktop size={20} />} color="violet" label="VM'er" value={`${vmsOn} / ${u.vms.length}`} sub="kører" />
        <Kpi icon={<IconDownload size={20} />} color={updates ? 'orange' : 'green'} label="Opdateringer" value={String(updates)} sub={updates ? 'containere klar' : 'alt er nyt'} />
      </SimpleGrid>

      <Section title="Diske" right={<Text size="xs" c="dimmed">data fra {ago(u.collected)}</Text>}>
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="md">
          {[...array, ...pools].map((d) => <DiskCard key={d.name} d={d} />)}
        </SimpleGrid>
      </Section>

      <Containers u={u} isAdmin={isAdmin} run={run} confirm={confirm} />

      {u.vms.length > 0 && (
        <Section title="Virtuelle maskiner">
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md">
            {u.vms.map((v) => <VmCard key={v.name} v={v} isAdmin={isAdmin} run={run} confirm={confirm} />)}
          </SimpleGrid>
        </Section>
      )}

      {isAdmin && <Group justify="flex-end"><Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => run('refresh', 'refresh')}>Opdatér visningen</Button></Group>}
    </Stack>
  );
}

function Section({ title, right, children }: { title: string; right?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div>
      <Group justify="space-between" mb="sm"><Title order={4}>{title}</Title>{right}</Group>
      {children}
    </div>
  );
}

function Kpi({ icon, color, label, value, sub, title }: { icon: React.ReactNode; color: string; label: string; value: string; sub: string; title?: string }) {
  return (
    <Paper withBorder radius="lg" p="md">
      <Group gap="sm" wrap="nowrap">
        <ThemeIcon size={40} radius="md" variant="light" color={color}>{icon}</ThemeIcon>
        <div style={{ minWidth: 0 }}>
          <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
          <Text fw={700} fz={20} lh={1.2} c={color === 'red' ? 'red' : undefined} title={title}>{value}</Text>
          <Text size="xs" c="dimmed" lineClamp={1}>{sub}</Text>
        </div>
      </Group>
    </Paper>
  );
}

const tempColor = (t?: number | null) => (t == null ? 'gray' : t >= 55 ? 'red' : t >= 45 ? 'orange' : 'teal');

function DiskCard({ d }: { d: UnraidDisk }) {
  const pct = d.sizeGb && d.usedGb != null ? Math.round((d.usedGb / d.sizeGb) * 100) : null;
  const ok = d.status === 'DISK_OK';
  return (
    <Paper withBorder radius="lg" p="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          <RingProgress size={56} thickness={6} roundCaps sections={pct != null ? [{ value: pct, color: pct > 90 ? 'red' : pct > 75 ? 'orange' : 'blue' }] : []}
            label={<Text ta="center" size="xs" fw={700}>{pct != null ? `${pct} %` : '–'}</Text>} />
          <div style={{ minWidth: 0 }}>
            <Text fw={600} lineClamp={1}>{d.name}</Text>
            <Text size="xs" c="dimmed">{d.type}{d.ssd != null ? ` · ${d.ssd ? 'SSD' : 'HDD'}` : ''}{d.fsType ? ` · ${d.fsType}` : ''}</Text>
            <Text size="xs" c="dimmed">{d.usedGb != null && d.sizeGb ? `${fmtGb(d.usedGb)} af ${fmtGb(d.sizeGb)}` : d.sizeGb ? fmtGb(d.sizeGb) : ''}</Text>
          </div>
        </Group>
        <Stack gap={4} align="flex-end">
          {d.tempC != null && <Badge size="sm" variant="light" color={tempColor(d.tempC)} leftSection={<IconTemperature size={12} />}>{d.tempC} °C</Badge>}
          <Badge size="xs" variant="dot" color={ok ? 'green' : 'red'} title={d.status}>{diskStatus(d.status)}</Badge>
        </Stack>
      </Group>
    </Paper>
  );
}

const fmtGb = (g: number) => (g >= 1000 ? `${(g / 1024).toFixed(1)} TB` : `${Math.round(g)} GB`);

type Run = (kind: string, action: string, name?: string) => void;
type Confirm = (title: string, text: string, label: string, go: () => void, color?: string) => void;

function Containers({ u, isAdmin, run, confirm }: { u: UnraidInfo; isAdmin: boolean; run: Run; confirm: Confirm }) {
  const [q, setQ] = useState('');
  const [show, setShow] = useState('all');
  const list = useMemo(() => u.containers.filter((c) => {
    if (q && !`${c.name} ${c.image}`.toLowerCase().includes(q.toLowerCase())) return false;
    if (show === 'running') return c.state === 'running';
    if (show === 'stopped') return c.state !== 'running';
    if (show === 'updates') return c.updateAvailable;
    return true;
  }), [u.containers, q, show]);
  const updates = u.containers.filter((c) => c.updateAvailable).length;
  return (
    <Section title="Docker" right={isAdmin && updates > 0 && (
      <Button size="xs" color="orange" leftSection={<IconDownload size={14} />}
        onClick={() => confirm('Opdatér alle containere?', `${updates} container(e) har en ny version. De opdateres én ad gangen og genstartes; plugins med en ny version opdateres også.`, 'Opdatér alle', () => run('docker', 'update_all'), 'orange')}>
        Opdatér alle ({updates})</Button>)}>
      <Group mb="sm" gap="sm">
        <TextInput placeholder="Søg i containere" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} w={240} />
        <SegmentedControl size="xs" value={show} onChange={setShow} data={[{ value: 'all', label: 'Alle' }, { value: 'running', label: 'Kører' },
          { value: 'stopped', label: 'Stoppet' }, { value: 'updates', label: `Opdateringer${updates ? ` (${updates})` : ''}` }]} />
      </Group>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 3, xl: 4 }} spacing="md">
        {list.map((c) => <ContainerCard key={c.name} c={c} isAdmin={isAdmin} run={run} confirm={confirm} />)}
      </SimpleGrid>
      {list.length === 0 && <Text c="dimmed" size="sm">Ingen containere matcher.</Text>}
    </Section>
  );
}

function ContainerCard({ c, isAdmin, run, confirm }: { c: UnraidContainer; isAdmin: boolean; run: Run; confirm: Confirm }) {
  const on = c.state === 'running';
  return (
    <Card withBorder radius="lg" padding="md" style={{ opacity: on ? 1 : 0.75 }}>
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          <Avatar src={c.icon} radius="md" size={44} color="blue">{c.name.slice(0, 2).toUpperCase()}</Avatar>
          <div style={{ minWidth: 0 }}>
            <Text fw={600} lineClamp={1} title={c.name}>{c.name}</Text>
            <Text size="xs" c="dimmed" lineClamp={1} title={c.image}>{c.image}</Text>
          </div>
        </Group>
      </Group>
      <Group gap={6} mt="sm">
        <Badge size="xs" variant="dot" color={on ? 'green' : c.state === 'paused' ? 'yellow' : 'gray'} title={c.state}>{CONTAINER_STATE[c.state] ?? c.state}</Badge>
        {c.updateAvailable && <Badge size="xs" color="orange" variant="filled">Opdatering klar</Badge>}
        {c.autostart && <Badge size="xs" variant="outline" color="gray">Autostart</Badge>}
        {on && c.started && <Text size="xs" c="dimmed">oppe {ago(c.started).replace(' siden', '')}</Text>}
      </Group>
      {on && (c.cpu != null || c.memMb != null) && (
        <SimpleGrid cols={2} spacing="xs" mt="sm">
          <Tooltip label="CPU"><div><Group gap={4}><IconCpu size={12} /><Text size="xs">{c.cpu ?? 0} %</Text></Group>
            <Progress size="xs" value={Math.min(100, c.cpu ?? 0)} color="blue" mt={2} /></div></Tooltip>
          <Tooltip label="RAM"><div><Group gap={4}><IconDatabase size={12} /><Text size="xs">{c.memMb != null ? (c.memMb >= 1024 ? `${(c.memMb / 1024).toFixed(1)} GB` : `${c.memMb} MB`) : '–'}</Text></Group>
            <Progress size="xs" value={Math.min(100, ((c.memMb ?? 0) / 4096) * 100)} color="violet" mt={2} /></div></Tooltip>
        </SimpleGrid>
      )}
      <Group gap={6} mt="md" justify="space-between">
        {c.webUi && on ? <Anchor size="sm" href={c.webUi} target="_blank"><Group gap={4}><IconExternalLink size={14} />Åbn</Group></Anchor> : <span />}
        {isAdmin && (
          <Group gap={6}>
            {c.updateAvailable && <Button size="compact-xs" color="orange" onClick={() => run('docker', 'update', c.name)}>Opdatér</Button>}
            {on ? (
              <Menu position="bottom-end" withinPortal>
                <Menu.Target><Button size="compact-xs" variant="default" rightSection={<IconChevronDown size={12} />}>Handling</Button></Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconRefresh size={14} />} onClick={() => run('docker', 'restart', c.name)}>Genstart</Menu.Item>
                  <Menu.Item color="red" leftSection={<IconPlayerStop size={14} />}
                    onClick={() => confirm(`Stop ${c.name}?`, 'Containeren stoppes, og det den leverer, er utilgængeligt, til den startes igen.', 'Stop', () => run('docker', 'stop', c.name), 'red')}>Stop</Menu.Item>
                </Menu.Dropdown>
              </Menu>
            ) : <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlayerPlay size={12} />} onClick={() => run('docker', 'start', c.name)}>Start</Button>}
          </Group>
        )}
      </Group>
    </Card>
  );
}

function VmCard({ v, isAdmin, run, confirm }: { v: UnraidVm; isAdmin: boolean; run: Run; confirm: Confirm }) {
  const on = v.state === 'running';
  const n = v.name.toLowerCase();
  const icon = n.includes('windows') ? <IconBrandWindows size={24} /> : n.includes('ubuntu') || n.includes('linux') ? <IconBrandUbuntu size={24} />
    : n.includes('home assistant') ? <IconHome size={24} /> : <IconDeviceDesktop size={24} />;
  const important = n.includes('home assistant');
  return (
    <Card withBorder radius="lg" padding="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap="sm" wrap="nowrap">
          <ThemeIcon size={44} radius="md" variant="light" color={on ? 'violet' : 'gray'}>{icon}</ThemeIcon>
          <div>
            <Text fw={600}>{v.name}</Text>
            <Text size="xs" c="dimmed">{v.vcpus ?? '?'} vCPU · {v.memoryMb ? `${Math.round(v.memoryMb / 1024)} GB RAM` : '? RAM'}</Text>
          </div>
        </Group>
        <Badge size="sm" variant="light" color={on ? 'green' : v.state === 'paused' ? 'yellow' : 'gray'} title={v.state}>{VM_STATE[v.state] ?? v.state}</Badge>
      </Group>
      <Group gap={6} mt="sm">{v.autostart && <Badge size="xs" variant="outline" color="gray">Autostart</Badge>}</Group>
      {isAdmin && (
        <Group gap={6} mt="md" justify="flex-end">
          {on ? (
            <>
              <Button size="compact-xs" variant="default"
                onClick={() => confirm(`Genstart ${v.name}?`, important ? `${v.name} er utilgængelig, mens den genstarter (et par minutter).` : 'VM\'en genstartes.', 'Genstart', () => run('vm', 'reboot', v.name))}>Genstart</Button>
              <Button size="compact-xs" variant="light" color="red"
                onClick={() => confirm(`Luk ${v.name} ned?`, important ? `OBS: ${v.name} styrer huset. Alt den laver, stopper, til den startes igen.` : 'VM\'en lukkes pænt ned.', 'Luk ned', () => run('vm', 'shutdown', v.name), 'red')}>Luk ned</Button>
              <Menu position="bottom-end" withinPortal>
                <Menu.Target><Button size="compact-xs" variant="subtle" color="gray" px={4}><IconChevronDown size={14} /></Button></Menu.Target>
                <Menu.Dropdown><Menu.Item color="red" onClick={() => confirm(`Tving ${v.name} slukket?`, 'Svarer til at trække stikket. Brug kun, hvis "Luk ned" ikke virker.', 'Tving sluk', () => run('vm', 'destroy', v.name), 'red')}>Tving sluk</Menu.Item></Menu.Dropdown>
              </Menu>
            </>
          ) : v.state === 'paused'
            ? <Button size="compact-xs" variant="light" color="green" onClick={() => run('vm', 'resume', v.name)}>Genoptag</Button>
            : <Button size="compact-xs" variant="light" color="green" leftSection={<IconPlayerPlay size={12} />} onClick={() => run('vm', 'start', v.name)}>Start</Button>}
        </Group>
      )}
    </Card>
  );
}
