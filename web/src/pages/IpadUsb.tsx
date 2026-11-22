import { useState } from 'react';
import { Alert, Badge, Button, Code, Group, Modal, Paper, Select, Stack, Text, TextInput, Title } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useNavigate } from 'react-router';
import { IconAlertTriangle, IconKey, IconUsb } from '@tabler/icons-react';
import { get, notifyErr, post, useAction, useApi } from '../api';
import { ago, JOB } from '../format';
import { CopyButton } from '../design/copy';
import { JobLive, StatusBadge } from '../components';
import type { IpadDetail, IpadRow, JobStatus, UsbBackup, UsbDevice, UsbMachine } from '../types';

const PLATFORM: Record<string, string> = { windows: 'Windows', linux: 'Linux', mac: 'Mac' };
const useUsb = () => useApi<UsbMachine[]>(['ipads', 'usb'], '/api/ipads/usb', { refetchInterval: 10_000 });
const gb = (b: number) => `${(b / 1024 / 1024 / 1024).toLocaleString('da-DK', { maximumFractionDigits: 1 })} GB`;

/** Øverst på iPad-siden: "Annas iPad er sat i ARBEJDS-PC – vælg handling". */
export function UsbBanner() {
  const { data } = useUsb();
  const nav = useNavigate();
  const hits = (data ?? []).flatMap((m) => m.devices.map((d) => ({ m, d })));
  if (hits.length === 0) return null;
  return (
    <Stack gap="xs" mb="md">
      {hits.map(({ m, d }) => (
        <Alert key={`${m.id}-${d.udid}`} color={d.ipadId ? 'blue' : 'gray'} variant="light" icon={<IconUsb size={18} />}>
          <Group justify="space-between" gap="xs">
            <Text size="sm">
              {d.ipadId
                ? <><b>{d.ipadName}</b> er sat i <b>{m.name}</b> – vælg handling.</>
                : <>En iPad{d.name ? <> (<b>{d.name}</b>)</> : ''} er sat i <b>{m.name}</b>, men er ikke linket. Åbn den iPad, den hører til, og link den under Opsætning.</>}
              {d.paired === false && ' Lås iPad\'en op og tryk "Stol på".'}
            </Text>
            {d.ipadId && <Button size="compact-sm" onClick={() => nav(`/ipads/${d.ipadId}/opsaetning`)}>Vælg handling</Button>}
          </Group>
        </Alert>
      ))}
    </Stack>
  );
}

type Run = (m: UsbMachine, d: UsbDevice, action: string, extra?: { backup?: string; confirm?: string }) => void;

/** I Opsætning: pc'er, hvor iPad'en er sat i lige nu, med knapper. Virker med Windows-, Linux- og Mac-agenter. */
export function UsbSection({ r, d, can, admin }: { r: IpadRow; d: IpadDetail; can: string[]; admin: boolean }) {
  const { data: machines } = useUsb();
  const update = admin || can.includes('update');
  const { data: backups } = useApi<UsbBackup[]>(['ipads', r.id, 'usb-backups'], update ? `/api/ipads/${r.id}/usb/backups` : null);
  const [pw, setPw] = useState<string | null>(null);
  const act = useAction((a: { machineId: number; udid: string; action: string; backup?: string; confirm?: string }) =>
    post<{ msg: string }>(`/api/ipads/${r.id}/usb/action`, a), (x) => x.msg);
  const link = useAction((a: { machineId: number; udid: string }) => post<{ msg: string }>(`/api/ipads/${r.id}/usb/link`, a), (x) => x.msg);
  const run: Run = (m, dev, action, extra) => act.mutate({ machineId: m.id, udid: dev.udid, action, ...extra });
  const showPw = async () => { try { setPw((await get<{ password: string }>('/api/ipads/usb/backup-password')).password); } catch (e) { notifyErr(e); } };

  const list = machines ?? [];
  const mine = list.flatMap((m) => m.devices.filter((x) => x.ipadId === r.id).map((dev) => ({ m, dev })));
  const unlinked = admin ? list.flatMap((m) => m.devices.filter((x) => !x.ipadId).map((dev) => ({ m, dev }))) : [];
  const problems = list.filter((m) => m.problem);

  return (
    <Paper withBorder radius="md" p="md" mt="md">
      <Group gap={8} mb="xs"><IconUsb size={18} /><Title order={6}>Via USB på en pc</Title></Group>
      <Text size="sm" c="dimmed" mb="sm">
        Sæt iPad'en i kablet på den pc, du sidder ved, og styr den herfra. Pc'en skal have udvidelsen <b>iPad via USB</b> (maskinens side → Udvidelser) –
        det virker på Windows, Linux og Mac. Lås iPad'en op og tryk "Stol på" første gang.
      </Text>
      {problems.map((m) => <Alert key={m.id} color="yellow" mb="xs" icon={<IconAlertTriangle size={16} />}>{m.name}: {m.problem}</Alert>)}
      <Stack gap="sm">
        {mine.length === 0 && <Text size="sm" c="dimmed">
          {!admin ? 'iPad\'en er ikke sat i nogen pc lige nu.'
            : list.length === 0 ? 'Ingen pc har udvidelsen "iPad via USB" installeret (eller agenten er offline).'
            : `iPad'en er ikke sat i nogen pc lige nu. Pc'er med udvidelsen: ${list.map((m) => m.name).join(', ')}.`}
        </Text>}
        {mine.map(({ m, dev }) => <Connection key={`${m.id}-${dev.udid}`} m={m} dev={dev} r={r} d={d} admin={admin} update={update} backups={backups ?? []}
          run={run} busy={act.isPending} />)}
        {unlinked.map(({ m, dev }) => (
          <Paper key={`u-${m.id}-${dev.udid}`} withBorder radius="sm" p="xs">
            <Group justify="space-between" gap="xs">
              <Text size="sm">{dev.name ?? 'En iPad'} <Text span size="xs" c="dimmed">sat i {m.name} · ikke linket{dev.serial ? ` · serienr. ${dev.serial}` : ''}</Text></Text>
              <Button size="compact-sm" variant="default" loading={link.isPending} disabled={!dev.ecid}
                title={dev.ecid ? undefined : 'Lås iPad\'en op og tryk "Stol på" først'}
                onClick={() => link.mutate({ machineId: m.id, udid: dev.udid })}>Link til {r.name}</Button>
            </Group>
          </Paper>
        ))}
        {update && (backups?.length ?? 0) > 0 && <div>
          <Text size="sm" fw={600}>Krypterede backupper i Fleetdeck</Text>
          {backups!.map((b) => <Text key={b.set} size="xs" c="dimmed">{new Date(b.created).toLocaleString('da-DK')} · {gb(b.bytes)}{b.machine ? ` · fra ${b.machine}` : ''}</Text>)}
        </div>}
        {admin && <Group gap="xs">
          {pw === null
            ? <Button size="xs" variant="subtle" leftSection={<IconKey size={14} />} onClick={showPw}>Vis husstandens backup-kode</Button>
            : <Group gap={6}>
                <TextInput size="xs" readOnly value={pw} w={220} styles={{ input: { fontFamily: 'monospace' } }} />
                <CopyButton value={pw}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
              </Group>}
          <Text size="xs" c="dimmed">Bruges til krypterede backupper – også hvis en backup skal gendannes i Finder eller Apple Devices.</Text>
        </Group>}
      </Stack>
    </Paper>
  );
}

function Connection({ m, dev, r, d, admin, update, backups, run, busy }: {
  m: UsbMachine; dev: UsbDevice; r: IpadRow; d: IpadDetail; admin: boolean; update: boolean; backups: UsbBackup[]; run: Run; busy: boolean;
}) {
  const [restore, setRestore] = useState(false);
  const [typed, setTyped] = useState<{ action: 'supervise' | 'erase' } | null>(null);
  const job = m.job && (m.job.ipadId === r.id || m.job.udid === dev.udid) ? m.job : null;
  const running = !!m.job && ['pending', 'running'].includes(m.job.status);
  const off = busy || running || !m.online;
  const installed = dev.profiles?.find((p) => p.identifier === d.locked.identifier);
  const confirm = (action: string, title: string, text: string) => modals.openConfirmModal({
    title, children: <Text size="sm">{text}</Text>, labels: { confirm: 'Start', cancel: 'Annullér' }, onConfirm: () => run(m, dev, action),
  });
  return (
    <Paper withBorder radius="sm" p="xs" style={{ borderColor: 'var(--mantine-color-blue-5)' }}>
      <Group justify="space-between" gap="xs" align="flex-start">
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" fw={600}>Sat i {m.name} <Text span size="xs" c="dimmed">{[PLATFORM[m.platform] ?? m.platform, dev.name, dev.deviceType, dev.firmware && `iPadOS ${dev.firmware}`].filter(Boolean).join(' · ')}</Text></Text>
          <Group gap={4} mt={2}>
            {!m.online && <Badge size="xs" color="red" variant="light">Pc'en er offline</Badge>}
            {dev.paired === false && <Badge size="xs" color="red" variant="light">Ikke parret – lås op og tryk "Stol på"</Badge>}
            {dev.supervised === true && <Badge size="xs" color="green" variant="light">Overvåget</Badge>}
            {dev.supervised === false && <Badge size="xs" color="yellow" variant="light">Ikke overvåget</Badge>}
            {dev.battery !== null && <Badge size="xs" color="gray" variant="light">Batteri {dev.battery} %{dev.charging ? ' (lader)' : ''}</Badge>}
            {installed && <Badge size="xs" variant="light">{installed.name ?? 'Låst profil'}{installed.version ? ` v${installed.version}` : ''}</Badge>}
            <Text size="xs" c="dimmed">set {ago(m.at)}</Text>
          </Group>
        </div>
      </Group>
      <Group gap={6} mt="xs">
        {update && <Button size="compact-sm" variant="default" disabled={off} onClick={() => run(m, dev, 'info')}>Læs iPad'en</Button>}
        {admin && <Button size="compact-sm" disabled={off} onClick={() => confirm('install_profile', `Installér låst profil v${d.locked.version}?`,
          dev.supervised ? `Profilen installeres lydløst på ${r.name}. Den gamle DNS-profil fra QR-koden erstattes.`
            : `Profilen sendes til ${r.name} og skal godkendes på iPad'en (Indstillinger → Profil hentet). iPad'en er ikke overvåget, så de fleste begrænsninger virker ikke endnu.`)}>
          Installér låst profil</Button>}
        {update && <Button size="compact-sm" variant="default" disabled={off} onClick={() => confirm('backup', 'Tag krypteret backup?',
          `Pc'en tager en fuld, krypteret backup af ${r.name} og sender den til Fleetdeck (de 3 nyeste gemmes). Lås iPad'en op – den kan bede om koden. Det kan tage lang tid, og pc'en skal have plads til backuppen midlertidigt.`)}>
          Tag krypteret backup</Button>}
        {admin && <Button size="compact-sm" variant="default" disabled={off || backups.length === 0} onClick={() => setRestore(true)}>Gendan…</Button>}
      </Group>
      {admin && <Group gap={6} mt={6}>
        <Badge size="xs" color="orange" variant="light">Afprøves</Badge>
        <Button size="compact-xs" variant="subtle" color="orange" disabled={off || dev.supervised === true} onClick={() => setTyped({ action: 'supervise' })}>Overvåg (sletter – uden gendannelse)</Button>
        <Button size="compact-xs" variant="subtle" color="red" disabled={off} onClick={() => setTyped({ action: 'erase' })}>Slet iPad'en</Button>
      </Group>}
      {job && <Paper withBorder radius="sm" p="xs" mt="xs">
        <Group gap="xs" justify="space-between" wrap="nowrap">
          <Text size="sm" fw={600} truncate>{job.title}</Text>
          <Group gap={6} wrap="nowrap"><Text size="xs" c="dimmed">{ago(job.finished ?? job.created)}</Text><StatusBadge map={JOB} value={job.status} /></Group>
        </Group>
        <JobLive job={{ id: job.id, status: job.status as JobStatus, progress: job.progress, step: job.step, stepSince: job.stepSince }} />
        {job.output && <Code block mt={6} style={{ whiteSpace: 'pre-wrap', maxHeight: 220, overflow: 'auto' }}>{job.output}</Code>}
      </Paper>}
      <RestoreModal opened={restore} onClose={() => setRestore(false)} r={r} backups={backups} onStart={(set) => { setRestore(false); run(m, dev, 'restore', { backup: set }); }} />
      <TypedConfirm action={typed?.action ?? null} r={r} onClose={() => setTyped(null)} onStart={(action, confirm) => { setTyped(null); run(m, dev, action, { confirm }); }} />
    </Paper>
  );
}

function RestoreModal({ opened, onClose, r, backups, onStart }: { opened: boolean; onClose: () => void; r: IpadRow; backups: UsbBackup[]; onStart: (set: string) => void }) {
  const [set, setSet] = useState<string | null>(null);
  const pick = set ?? backups[0]?.set ?? null;
  return (
    <Modal opened={opened} onClose={onClose} title={`Gendan ${r.name}`}>
      <Stack gap="sm">
        <Select label="Backup" allowDeselect={false} value={pick} onChange={setSet}
          data={backups.map((b) => ({ value: b.set, label: `${new Date(b.created).toLocaleString('da-DK')} · ${gb(b.bytes)}${b.deviceName ? ` · ${b.deviceName}` : ''}` }))} />
        <Alert color="orange" variant="light">
          <Text size="sm">Alt på iPad'en erstattes af backuppen, og iPad'en genstarter. Slå <b>Find min iPad</b> fra først (Indstillinger → dit navn → Find) – ellers afviser iPad'en gendannelsen.
            Apps hentes igen fra App Store bagefter.</Text>
        </Alert>
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button color="orange" disabled={!pick} onClick={() => pick && onStart(pick)}>Gendan</Button></Group>
      </Stack>
    </Modal>
  );
}

/** Overvågning og sletning afprøves: iPad'ens navn skal skrives for at starte. */
function TypedConfirm({ action, r, onClose, onStart }: { action: 'supervise' | 'erase' | null; r: IpadRow; onClose: () => void; onStart: (action: 'supervise' | 'erase', confirm: string) => void }) {
  const [text, setText] = useState('');
  const ok = text.trim().toLocaleLowerCase('da-DK') === r.name.trim().toLocaleLowerCase('da-DK');
  const close = () => { setText(''); onClose(); };
  return (
    <Modal opened={action !== null} onClose={close} title={action === 'erase' ? `Slet ${r.name}? (afprøves)` : `Gør ${r.name} overvåget? (afprøves)`}>
      <Stack gap="sm">
        <Alert color={action === 'erase' ? 'red' : 'orange'} variant="light">
          {action === 'erase'
            ? <Text size="sm">Alt indhold og alle indstillinger på iPad'en slettes, og den starter i Opsætningsassistent. Er <b>Find min iPad</b> slået til, skal Apple-kontoens kode
                bruges bagefter – aktiveringslåsen kan ikke omgås. Tag en backup først. Funktionen afprøves stadig.</Text>
            : <Text size="sm">Virker kun på en nulstillet iPad, der står i Opsætningsassistent ("Hej"-skærmen). iPad'en bliver overvåget med husstandens overvågningsnøgle,
                så den låste profil kan installeres lydløst bagefter. Funktionen afprøves stadig.</Text>}
        </Alert>
        <TextInput label={`Skriv iPad'ens navn (${r.name}) for at bekræfte`} value={text} onChange={(e) => setText(e.currentTarget.value)} data-autofocus />
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>Annullér</Button>
          <Button color={action === 'erase' ? 'red' : 'orange'} disabled={!ok} onClick={() => { const t = text; setText(''); if (action) onStart(action, t); }}>
            {action === 'erase' ? 'Slet iPad\'en' : 'Gør overvåget'}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
