import { useEffect, useState } from 'react';
import { Alert, Anchor, Badge, Button, Code, FileButton, Group, List, Modal, PasswordInput, Select, SimpleGrid, Stack, Switch, Table, Text, TextInput, Title } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router';
import { IconDownload, IconLock, IconQrcode, IconRefresh, IconTrash } from '@tabler/icons-react';
import { del, notifyErr, notifyOk, post, put, useAction, useApi } from '../api';
import { ago, fmt } from '../format';
import { CopyButton } from '../design/copy';
import { Qr } from '../design/family';
import { Panel, StatusBadge } from '../components';

// ---------------------------------------------------------------- typer (kun til Apple-styring)
interface MdmDevice {
  state: string; approved: boolean; approvalNote: string | null; expectedSerial: string | null; serial: string | null; udid: string | null;
  deviceName: string | null; productName: string | null; modelName: string | null; osVersion: string | null; buildVersion: string | null;
  supervised: boolean | null; activationLock: boolean | null; batteryLevel: number | null; storageTotalGb: number | null; storageFreeGb: number | null;
  lastCheckin: string | null; lastInventory: string | null; enrolledAt: string | null; checkedOutAt: string | null; identityExpires: string | null;
  lockedViaMdm: boolean; lockedInstalledVersion: number | null; lockedCurrent: boolean; lockedVersion: number; staticInstalled: number | null;
  osTargetVersion: string | null; osTargetBuild: string | null; osTargetLocal: string | null; pushError: string | null;
}
interface MdmCmd { commandUuid: string; requestType: string; status: string; requestedBy: string; detail: string | null; created: string; completed: string | null; error: string | null }
interface MdmInfo {
  configured: boolean; pushExpires: string | null; pushWarn: boolean; device: MdmDevice | null;
  link: { expires: string; usedAt: string | null; fetches: number; active: boolean } | null;
  commands: MdmCmd[]; ddm: { path: string; value: string; at: string | null }[] | { error: string } | null; can: string[]; admin: boolean;
}
interface MdmConf {
  publicHost: string; hostLocked: boolean; org: string; appleId: string | null;
  machineId: number | null; network: string; ip: string; container: string; image: string; defaultImage: string; dataDir: string;
  apiUrl: string; webhookBase: string; effectiveApiUrl: string | null; fleetdeckUrl: string | null;
  machines: { id: number; name: string; online: boolean; unraid: boolean }[];
  push: { topic: string | null; expires: string | null; hasCert: boolean; hasPendingCsr: boolean; hasEncryption: boolean; hasSignedRequest: boolean; subject: string | null; daysLeft: number | null; warn: boolean };
  ca: { thumbprint: string; started: boolean } | null;
  server: { ok: boolean | null; version: string | null; error: string | null; checkedAt: string | null; lastError: string | null };
  lastHook: string | null; hmac: boolean; devices: Record<string, number>;
  cloudflare: { hostname: string; path: string; service: string; what: string }[];
}
interface MdmStatus {
  machine: { id: number; name: string } | null; job: { id: number; title: string; status: string; output?: string } | null;
  suggest: { id: number; status: string; ip: string | null } | null; container: { reported: boolean; found: boolean; running: boolean; state: string | null; ip: string | null } | null;
}
type Manual = { apiKey: string; webhookUrl: string; hmacKey: string };
type Steps = { ok: boolean; steps: { ok: boolean; text: string }[] };

const STATE: Record<string, { label: string; color: string }> = {
  pending: { label: 'Venter på tilmelding', color: 'gray' },
  authenticated: { label: 'Profil installeret – venter', color: 'yellow' },
  enrolled: { label: 'Tilmeldt', color: 'green' },
  checked_out: { label: 'Fjernet fra iPad\'en', color: 'red' },
};
const CMD: Record<string, { label: string; color: string }> = {
  queued: { label: 'I kø', color: 'gray' }, sent: { label: 'Sendt', color: 'blue' }, acknowledged: { label: 'Udført', color: 'green' },
  error: { label: 'Fejl', color: 'red' }, failed: { label: 'Ikke sendt', color: 'red' }, notnow: { label: 'Venter – iPad\'en er optaget/låst', color: 'yellow' },
  unknown: { label: 'Intet svar', color: 'gray' }, declared: { label: 'Sendt (DDM)', color: 'teal' },
};
const base = () => document.baseURI.replace(/\/$/, '');
const readFile = (f: File) => f.text();
const localInput = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

function Fact({ label, value }: { label: string; value: React.ReactNode }) {
  return <div><Text size="xs" c="dimmed">{label}</Text><Text size="sm" fw={500}>{value ?? '–'}</Text></div>;
}

// ---------------------------------------------------------------- fanen "Apple-styring" på en iPad
export function MdmIpadTab({ id, name, kind }: { id: number; name: string; kind: string }) {
  const { data: m } = useApi<MdmInfo>(['ipad-mdm', id], `/api/ipads/${id}/mdm`, { refetchInterval: 15_000 });
  const nav = useNavigate();
  if (!m) return null;
  const d = m.device;
  const enrolled = d && (d.state === 'enrolled' || d.state === 'authenticated');
  return (
    <Stack gap="lg">
      {m.pushWarn && <Alert color="orange">Apple-styringens push-certifikat udløber {fmt(m.pushExpires)}. {m.admin ? 'Forny det under Indstillinger → Apple-styring.' : 'Bed en administrator forny det.'}</Alert>}
      {!m.configured && (m.admin
        ? <Alert color="blue" title="Apple-styring er ikke sat op">Sæt den op under <Anchor onClick={() => nav('/indstillinger?fane=apple')}>Indstillinger → Apple-styring</Anchor> (offentlig adresse, MDM-server og push-certifikat). iPad'en styres stadig via AdGuard uden.</Alert>
        : <Alert color="gray">Apple-styring er ikke sat op for denne husstand. iPad'en styres via AdGuard.</Alert>)}
      {d && !d.approved && <Alert color="red" title="Ukendt enhed – venter på godkendelse">
        {d.approvalNote} {m.admin && <Button size="xs" ml="sm" onClick={async () => { try { await post(`/api/ipads/${id}/mdm/approve`); notifyOk('Godkendt'); } catch (e) { notifyErr(e); } }}>Godkend enheden</Button>}
      </Alert>}
      {enrolled ? <Enrolled id={id} name={name} kind={kind} m={m} d={d!} /> : m.admin && m.configured && <Enroll id={id} kind={kind} m={m} />}
      {!enrolled && !m.admin && <Text size="sm" c="dimmed">iPad'en er ikke tilmeldt Apple-styring.</Text>}
      {m.commands.length > 0 && <History cmds={m.commands} />}
      {m.admin && d && <AdminDanger id={id} name={name} d={d} />}
    </Stack>
  );
}

function Enroll({ id, kind, m }: { id: number; kind: string; m: MdmInfo }) {
  const [serial, setSerial] = useState(m.device?.expectedSerial ?? '');
  const [link, setLink] = useState<{ url: string; expires: string } | null>(null);
  const mk = useAction(() => post<{ url: string; expires: string }>(`/api/ipads/${id}/mdm/enroll-link`, { expectedSerial: serial }), () => 'Linket er klar');
  const d = m.device;
  return (
    <div>
      <Title order={5} mb={4}>Tilmeld iPad'en til Apple-styring</Title>
      {d?.state === 'checked_out' && <Alert color="red" mb="sm">Apple-styring blev fjernet på iPad'en {ago(d.checkedOutAt)}. Lav et nyt link for at tilmelde den igen.</Alert>}
      <Text size="sm" c="dimmed" mb="sm">Med Apple-styring kan Fleetdeck se batteri, lager og iOS-version, låse skærmen, sende den låste profil trådløst og kræve opdateringer. AdGuard-styringen virker som før.</Text>
      {kind === 'child' && <Alert color="yellow" variant="light" mb="sm">Børne-iPad: Fjernes Apple-styring på iPad'en, forsvinder alt, der er installeret via MDM. Læg derfor den låste profil (DNS-lås og begrænsninger) på med Mac'en (statisk tilstand) – den kan ikke fjernes på en overvåget iPad.</Alert>}
      <Group align="flex-end" gap="sm" mb="sm">
        <TextInput label="Forventet serienummer (valgfrit)" description="Melder en anden enhed sig med linket, skal du godkende den" value={serial} onChange={(e) => setSerial(e.currentTarget.value)} w={280} />
        <Button leftSection={<IconQrcode size={16} />} loading={mk.isPending} onClick={() => mk.mutate(undefined, { onSuccess: setLink })}>Lav tilmeldingslink</Button>
        <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base()}/api/ipads/${id}/mdm/enroll-profile`}>Profil til Apple Configurator</Button>
      </Group>
      {link && <Group align="flex-start" gap="lg">
        <Qr text={link.url} />
        <Stack gap={6} style={{ flex: 1, minWidth: 240 }}>
          <Text size="sm" fw={600}>Sådan gør du på iPad'en:</Text>
          <Text size="sm">1. Scan QR-koden med kameraet (eller åbn linket i Safari) og tryk "Hent profil".</Text>
          <Text size="sm">2. Indstillinger → "Profil hentet" → Installér, og skriv iPad'ens kode.</Text>
          <Text size="sm">3. Bekræft "Fjernadministration" (Har du tillid).</Text>
          <Text size="sm">4. Her skifter status til "Tilmeldt" inden for et minut.</Text>
          <Group gap={6} mt={4}><TextInput size="xs" readOnly value={link.url} style={{ flex: 1 }} />
            <CopyButton value={link.url}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton></Group>
          <Text size="xs" c="dimmed">Linket virker i 30 minutter og holder op, så snart iPad'en har meldt sig. En forhåndsvisning i Beskeder bruger det ikke op.</Text>
        </Stack>
      </Group>}
      {!link && m.link?.active && <Text size="xs" c="dimmed">Et link er aktivt til {fmt(m.link.expires)} (hentet {m.link.fetches} gang(e)). Et nyt link gør det gamle ugyldigt.</Text>}
    </div>
  );
}

function Enrolled({ id, name, kind, m, d }: { id: number; name: string; kind: string; m: MdmInfo; d: MdmDevice }) {
  const can = (p: string) => m.can.includes(p);
  const [lockOpen, setLockOpen] = useState(false);
  const [eraseOpen, setEraseOpen] = useState(false);
  const refresh = useAction(() => post(`/api/ipads/${id}/mdm/refresh`), 'iPad\'en bliver bedt om nye oplysninger');
  const gb = (x: number | null) => x === null ? '–' : `${x.toFixed(x < 10 ? 1 : 0)} GB`;
  return (
    <>
      <div>
        <Group justify="space-between" mb="sm">
          <Group gap="xs"><Title order={5}>Enheden</Title><StatusBadge map={STATE} value={d.state} />{d.supervised && <Badge variant="light" color="grape">Overvåget</Badge>}</Group>
          <Group gap="xs">
            {can('update') && <Button size="xs" variant="default" leftSection={<IconRefresh size={14} />} loading={refresh.isPending} onClick={() => refresh.mutate(undefined)}>Opdatér oplysninger</Button>}
            {can('lock') && <Button size="xs" variant="default" leftSection={<IconLock size={14} />} onClick={() => setLockOpen(true)}>Lås skærmen</Button>}
            {can('erase') && <Button size="xs" variant="subtle" color="red" leftSection={<IconTrash size={14} />} onClick={() => setEraseOpen(true)}>Slet iPad'en</Button>}
          </Group>
        </Group>
        <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
          <Fact label="Model" value={d.modelName ? `${d.modelName}${d.productName ? ` (${d.productName})` : ''}` : d.productName} />
          <Fact label="iPadOS" value={d.osVersion ? `${d.osVersion}${d.buildVersion ? ` (${d.buildVersion})` : ''}` : null} />
          <Fact label="Batteri" value={d.batteryLevel !== null ? `${Math.round(d.batteryLevel * 100)} %` : null} />
          <Fact label="Lager" value={d.storageTotalGb !== null ? `${gb(d.storageFreeGb)} fri af ${gb(d.storageTotalGb)}` : null} />
          <Fact label="Sidst i kontakt" value={d.lastCheckin ? ago(d.lastCheckin) : null} />
          <Fact label="Oplysninger hentet" value={d.lastInventory ? ago(d.lastInventory) : null} />
          <Fact label="Serienummer" value={d.serial} />
          <Fact label="Navn på iPad'en" value={d.deviceName} />
          <Fact label="Aktiveringslås" value={d.activationLock === null ? null : d.activationLock ? 'Til' : 'Fra'} />
          <Fact label="Tilmeldt" value={d.enrolledAt ? fmt(d.enrolledAt) : null} />
          <Fact label="Identitet udløber" value={d.identityExpires ? fmt(d.identityExpires) : null} />
        </SimpleGrid>
        {d.pushError && <Alert color="orange" mt="sm">Push til iPad'en fejlede: {d.pushError}</Alert>}
      </div>
      <LockedViaMdm id={id} kind={kind} d={d} can={can('rules')} />
      <OsUpdate id={id} d={d} can={can('update')} />
      <LockModal id={id} opened={lockOpen} onClose={() => setLockOpen(false)} />
      <EraseModal id={id} name={name} opened={eraseOpen} onClose={() => setEraseOpen(false)} />
    </>
  );
}

function LockedViaMdm({ id, kind, d, can }: { id: number; kind: string; d: MdmDevice; can: boolean }) {
  const qc = useQueryClient();
  const set = async (on: boolean, force = false) => {
    try { await put(`/api/ipads/${id}/mdm/locked`, { viaMdm: on, force }); notifyOk(on ? 'Profilen sendes til iPad\'en' : 'Profilen fjernes fra iPad\'en'); qc.invalidateQueries(); }
    catch (e) {
      if ((e as { status?: number }).status === 409) modals.openConfirmModal({
        title: 'Den låste profil er installeret via Mac', children: <Text size="sm">{(e as Error).message}</Text>,
        labels: { confirm: 'Send alligevel', cancel: 'Annullér' }, onConfirm: () => set(on, true),
      });
      else notifyErr(e);
    }
  };
  const resend = useAction(() => post(`/api/ipads/${id}/mdm/locked/resend`), 'Profilen sendes igen');
  return (
    <div>
      <Title order={5} mb={4}>Låst profil via Apple-styring</Title>
      <Text size="sm" c="dimmed" mb="sm">Samme profil som i statisk tilstand (DNS-lås, skjulte apps, App Store, aldersgrænse), men sendt trådløst. Ændringer under "Apps og begrænsninger" sendes automatisk inden for et minut.</Text>
      {kind === 'child' && <Alert color="yellow" variant="light" mb="sm">Fjernes Apple-styring på iPad'en, forsvinder denne profil også. På børne-iPads bør DNS-låsen ligge i den statiske profil fra Mac'en.</Alert>}
      <Group gap="md">
        <Switch disabled={!can} checked={d.lockedViaMdm} onChange={(e) => set(e.currentTarget.checked)} label="Send den låste profil via Apple-styring" />
        {d.lockedViaMdm && (d.lockedCurrent
          ? <Badge color="green" variant="light">Installeret (v{d.lockedInstalledVersion})</Badge>
          : <Badge color="yellow" variant="light">{d.lockedInstalledVersion ? `v${d.lockedInstalledVersion} installeret – v${d.lockedVersion} på vej` : `v${d.lockedVersion} på vej`}</Badge>)}
        {d.lockedViaMdm && can && <Button size="xs" variant="subtle" loading={resend.isPending} onClick={() => resend.mutate(undefined)}>Send igen</Button>}
      </Group>
    </div>
  );
}

function OsUpdate({ id, d, can }: { id: number; d: MdmDevice; can: boolean }) {
  const { data: st } = useApi<MdmInfo>(['ipad-mdm-ddm', id], `/api/ipads/${id}/mdm?ddm=1`, { refetchInterval: 60_000 });
  const inAWeek = new Date(Date.now() + 7 * 864e5); inAWeek.setHours(20, 0, 0, 0);
  const [f, setF] = useState({ version: d.osTargetVersion ?? '', build: d.osTargetBuild ?? '', deadline: d.osTargetLocal?.slice(0, 16) ?? localInput(inAWeek) });
  const save = useAction(() => put(`/api/ipads/${id}/mdm/os-update`, f), 'Kravet er sendt til iPad\'en');
  const clear = useAction(() => del(`/api/ipads/${id}/mdm/os-update`), 'Kravet er fjernet');
  const ddm = st?.ddm && Array.isArray(st.ddm) ? st.ddm : [];
  const ddmErr = st?.ddm && !Array.isArray(st.ddm) ? st.ddm.error : null;
  return (
    <div>
      <Title order={5} mb={4}>Opdatering af iPadOS</Title>
      <Text size="sm" c="dimmed" mb="sm">iPad'en henter og installerer versionen selv og viser Apples besked "opdatering påkrævet". Når fristen er nået, installeres den automatisk. (Bruger Apples DDM – virker måske kun på overvågede iPads.)</Text>
      {d.osTargetVersion && <Text size="sm" mb="xs">Nu: iPadOS <b>{d.osTargetVersion}</b> senest <b>{d.osTargetLocal?.replace('T', ' kl. ').slice(0, 19)}</b> (iPad'ens tid).</Text>}
      {can && <Group align="flex-end" gap="sm">
        <TextInput label="Version" placeholder="26.1" value={f.version} onChange={(e) => setF({ ...f, version: e.currentTarget.value })} w={110} />
        <TextInput label="Build (valgfri)" placeholder="23B85" value={f.build} onChange={(e) => setF({ ...f, build: e.currentTarget.value })} w={130} />
        <TextInput label="Senest" type="datetime-local" value={f.deadline} onChange={(e) => setF({ ...f, deadline: e.currentTarget.value })} w={220} />
        <Button loading={save.isPending} onClick={() => save.mutate(undefined)}>Kræv opdatering</Button>
        {d.osTargetVersion && <Button variant="subtle" color="red" loading={clear.isPending} onClick={() => clear.mutate(undefined)}>Fjern kravet</Button>}
      </Group>}
      {ddm.length > 0 && <Table mt="sm" withRowBorders={false}><Table.Tbody>{ddm.map((x) => (
        <Table.Tr key={x.path}><Table.Td><Text size="xs" c="dimmed">{x.path.replace('.StatusItems.', '')}</Text></Table.Td><Table.Td><Text size="xs">{x.value}</Text></Table.Td>
          <Table.Td><Text size="xs" c="dimmed">{x.at ? ago(x.at) : ''}</Text></Table.Td></Table.Tr>))}</Table.Tbody></Table>}
      {ddmErr && <Text size="xs" c="dimmed" mt="xs">Status kunne ikke hentes: {ddmErr}</Text>}
    </div>
  );
}

function LockModal({ id, opened, onClose }: { id: number; opened: boolean; onClose: () => void }) {
  const [f, setF] = useState({ message: '', phone: '' });
  const go = useAction(() => post(`/api/ipads/${id}/mdm/lock`, f), 'iPad\'en låses');
  return (
    <Modal opened={opened} onClose={onClose} title="Lås skærmen">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">iPad'en låses med det samme og kan låses op med sin egen kode. Beskeden vises på låseskærmen.</Text>
        <TextInput label="Besked (valgfri)" maxLength={200} value={f.message} onChange={(e) => setF({ ...f, message: e.currentTarget.value })} />
        <TextInput label="Telefonnummer (valgfrit)" maxLength={40} value={f.phone} onChange={(e) => setF({ ...f, phone: e.currentTarget.value })} />
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button loading={go.isPending} onClick={() => go.mutate(undefined, { onSuccess: () => { onClose(); setF({ message: '', phone: '' }); } })}>Lås nu</Button></Group>
      </Stack>
    </Modal>
  );
}

function EraseModal({ id, name, opened, onClose }: { id: number; name: string; opened: boolean; onClose: () => void }) {
  const [f, setF] = useState({ confirmName: '', password: '' });
  const go = useAction(() => post(`/api/ipads/${id}/mdm/erase`, f), 'Sletningen er sendt til iPad\'en');
  return (
    <Modal opened={opened} onClose={onClose} title={`Slet ${name}?`}>
      <Stack gap="sm">
        <Alert color="red">Alt indhold og alle indstillinger slettes. iPad'en skal sættes op forfra (og overvåges igen med Apple Configurator), og Apple-styring forsvinder. Aktiveringslås kræver barnets Apple-konto.</Alert>
        <TextInput label={`Skriv "${name}" for at bekræfte`} value={f.confirmName} onChange={(e) => setF({ ...f, confirmName: e.currentTarget.value })} />
        <PasswordInput label="Din adgangskode til Fleetdeck" value={f.password} onChange={(e) => setF({ ...f, password: e.currentTarget.value })} autoComplete="current-password" />
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color="red" disabled={f.confirmName.trim().toLowerCase() !== name.trim().toLowerCase() || !f.password} loading={go.isPending}
            onClick={() => go.mutate(undefined, { onSuccess: () => { onClose(); setF({ confirmName: '', password: '' }); } })}>Slet iPad'en</Button></Group>
      </Stack>
    </Modal>
  );
}

function History({ cmds }: { cmds: MdmCmd[] }) {
  return (
    <div>
      <Title order={5} mb={4}>Historik</Title>
      <Table.ScrollContainer minWidth={560}>
        <Table striped>
          <Table.Thead><Table.Tr><Table.Th>Tid</Table.Th><Table.Th>Handling</Table.Th><Table.Th>Status</Table.Th><Table.Th>Af</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>{cmds.map((c) => (
            <Table.Tr key={c.commandUuid}>
              <Table.Td><Text size="xs">{fmt(c.created)}</Text></Table.Td>
              <Table.Td><Text size="sm">{c.detail ?? c.requestType}</Text>{c.error && <Text size="xs" c="red">{c.error}</Text>}</Table.Td>
              <Table.Td><StatusBadge map={CMD} value={c.status} /></Table.Td>
              <Table.Td><Text size="xs" c="dimmed">{c.requestedBy}</Text></Table.Td>
            </Table.Tr>))}</Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </div>
  );
}

function AdminDanger({ id, name, d }: { id: number; name: string; d: MdmDevice }) {
  const qc = useQueryClient();
  const enrolled = d.state === 'enrolled' || d.state === 'authenticated';
  return (
    <Group gap="xs">
      {enrolled && <Button size="xs" variant="subtle" color="red" onClick={() => modals.openConfirmModal({
        title: `Fjern Apple-styring fra ${name}?`, children: <Text size="sm">iPad'en afmeldes, og alt, der er installeret via Apple-styring (også den låste profil, hvis den er sendt via MDM), fjernes. AdGuard-styringen fortsætter.</Text>,
        labels: { confirm: 'Fjern', cancel: 'Annullér' }, confirmProps: { color: 'red' },
        onConfirm: async () => { try { await post(`/api/ipads/${id}/mdm/unenroll`); notifyOk('iPad\'en afmeldes'); qc.invalidateQueries(); } catch (e) { notifyErr(e); } },
      })}>Fjern Apple-styring</Button>}
      <Button size="xs" variant="subtle" color="gray" onClick={() => modals.openConfirmModal({
        title: 'Glem iPad\'en i Apple-styring?', children: <Text size="sm">Fleetdeck sletter sine oplysninger og historikken for Apple-styring på denne iPad. Selve iPad'en ændres ikke – brug "Fjern Apple-styring" først, hvis den stadig er tilmeldt.</Text>,
        labels: { confirm: 'Glem', cancel: 'Annullér' },
        onConfirm: async () => { try { await del(`/api/ipads/${id}/mdm`); qc.invalidateQueries(); } catch (e) { notifyErr(e); } },
      })}>Glem i Fleetdeck</Button>
    </Group>
  );
}

// ---------------------------------------------------------------- Indstillinger → Apple-styring
export function MdmSettingsTab() {
  const qc = useQueryClient();
  const { data: c } = useApi<MdmConf>(['mdm'], '/api/mdm', { refetchInterval: 30_000 });
  const { data: st } = useApi<MdmStatus>(['mdm-status'], '/api/mdm/status', { refetchInterval: 10_000 });
  const [f, setF] = useState({ publicHost: '', org: '', appleId: '', machineId: '', network: 'br0', ip: '', container: 'fleetdeck-mdm', image: '', dataDir: '', apiUrl: '', webhookBase: '' });
  const [test, setTest] = useState<Steps | null>(null);
  const [email, setEmail] = useState('');
  const [manual, setManual] = useState<Manual | null>(null);
  useEffect(() => {
    if (c) setF({ publicHost: c.publicHost, org: c.org, appleId: c.appleId ?? '', machineId: c.machineId ? String(c.machineId) : '', network: c.network, ip: c.ip, container: c.container,
      image: c.image === c.defaultImage ? '' : c.image, dataDir: c.dataDir, apiUrl: c.apiUrl, webhookBase: c.webhookBase });
  }, [c]);
  const body = (unlockHost = false) => ({ ...f, machineId: f.machineId ? Number(f.machineId) : 0, unlockHost });
  const run = async (fn: () => Promise<unknown>, ok?: string) => { try { await fn(); if (ok) notifyOk(ok); qc.invalidateQueries(); } catch (e) { notifyErr(e); } };
  const saveAll = async (unlockHost = false) => {
    try { await put('/api/mdm', body(unlockHost)); notifyOk('Gemt'); qc.invalidateQueries(); }
    catch (e) {
      if ((e as { status?: number }).status === 409) modals.openConfirmModal({
        title: 'Skift den offentlige adresse?', children: <Text size="sm">{(e as Error).message}</Text>, labels: { confirm: 'Skift alligevel', cancel: 'Annullér' }, confirmProps: { color: 'red' },
        onConfirm: () => saveAll(true),
      });
      else notifyErr(e);
    }
  };
  const runTest = useAction(() => post<Steps>('/api/mdm/test'));
  if (!c) return null;
  const p = c.push;
  const upload = async (file: File | null, url: string, ok: string, extra: Record<string, string> = {}) => {
    if (!file) return;
    await run(async () => { const r = await post<{ msg?: string }>(url, { text: await readFile(file), ...extra }); if (r?.msg) notifyOk(r.msg); }, ok);
  };
  const dl = (path: string) => `${base()}${path}`;
  return (
    <Stack gap={0}>
      <Panel title="Apple-styring (MDM)" right={c.server.ok === null ? <Badge color="gray" variant="light">Ikke sat op</Badge>
        : c.server.ok ? <Badge color="green" variant="light">MDM-serveren svarer{c.server.version ? ` (${c.server.version})` : ''}</Badge> : <Badge color="red" variant="light">MDM-serveren svarer ikke</Badge>}>
        <Text size="sm" c="dimmed">Valgfrit pr. iPad: Apple-styring giver batteri/lager/version, lås af skærmen, trådløs låst profil og krav om iPadOS-opdatering. AdGuard-styringen og statisk tilstand virker uændret uden. Kræver et push-certifikat fra Apple til en organisation (CVR) – det er gratis via mdmcert.download.</Text>
        <SimpleGrid cols={{ base: 2, sm: 4 }} mt="sm">
          <Fact label="Push-certifikat" value={p.expires ? <Text span size="sm" c={p.warn ? 'red' : undefined}>udløber {fmt(p.expires)} ({p.daysLeft} dage)</Text> : 'mangler'} />
          <Fact label="Tilmeldte iPads" value={String(c.devices.enrolled ?? 0)} />
          <Fact label="Seneste besked fra MDM-serveren" value={c.lastHook ? ago(c.lastHook) : 'ingen endnu'} />
          <Fact label="Enheds-CA" value={c.ca ? <Code>{c.ca.thumbprint.slice(0, 16)}…</Code> : 'laves ved første start'} />
        </SimpleGrid>
        {c.server.error && <Text size="sm" c="red" mt="xs">{c.server.error}</Text>}
        {c.server.lastError && <Text size="sm" c="red" mt="xs">Seneste fejl: {c.server.lastError}</Text>}
        {c.ca && !c.ca.started && <Alert color="yellow" mt="sm">Enheds-CA'en er ny – start MDM-serveren igen, så den kender den.</Alert>}
        {p.warn && <Alert color="orange" mt="sm">Push-certifikatet skal fornyes (trin 3). Brug "Renew" hos Apple med samme Apple-konto, så iPad'erne ikke skal tilmeldes igen.</Alert>}
      </Panel>

      <Panel title="1. Offentlig adresse (Cloudflare-tunnel)">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">iPad'erne skal kunne nå MDM-serveren overalt. Brug den samme Cloudflare-tunnel som til krypteret DNS (Indstillinger → iPads) med et ekstra "public hostname".</Text>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <TextInput label="Offentlig MDM-adresse" description={c.hostLocked ? 'Låst: iPads er tilmeldt med denne adresse' : 'Fx mdm.ditdomæne.dk – vælges én gang'} placeholder="mdm.eksempel.dk"
              value={f.publicHost} onChange={(e) => setF({ ...f, publicHost: e.currentTarget.value })} />
            <TextInput label="Husstand/organisation" description="Vises i profilerne på iPad'erne" value={f.org} onChange={(e) => setF({ ...f, org: e.currentTarget.value })} />
          </SimpleGrid>
          <Text size="sm" fw={600}>I Cloudflare Zero Trust → Networks → Tunnels → din tunnel → Public hostnames: tilføj to regler (samme hostname, hver sin sti):</Text>
          <Table withRowBorders={false}>
            <Table.Thead><Table.Tr><Table.Th>Hostname</Table.Th><Table.Th>Path</Table.Th><Table.Th>Service</Table.Th><Table.Th /></Table.Tr></Table.Thead>
            <Table.Tbody>{c.cloudflare.map((r) => (
              <Table.Tr key={r.path}>
                <Table.Td><Code>{r.hostname}</Code></Table.Td>
                <Table.Td><Group gap={4} wrap="nowrap"><Code>{r.path}</Code><CopyButton value={r.path}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? '✓' : 'Kopiér'}</Button>}</CopyButton></Group></Table.Td>
                <Table.Td><Group gap={4} wrap="nowrap"><Code>{r.service}</Code><CopyButton value={r.service}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? '✓' : 'Kopiér'}</Button>}</CopyButton></Group>
                  <Text size="xs" c="dimmed">{r.what}</Text></Table.Td>
              </Table.Tr>))}</Table.Tbody>
          </Table>
          <List size="xs" c="dimmed" spacing={2}>
            <List.Item>Tilføj ingen regel uden sti – Fleetdecks brugerflade og MDM-serverens API må ikke være offentlige (Fleetdeck svarer kun på /enroll/ på denne adresse).</List.Item>
            <List.Item>"Bot Fight Mode" skal være slået fra for domænet (Security → Bots). På gratis-planen kan den ikke undtages for én adresse – iPads kan ikke løse udfordringer.</List.Item>
            <List.Item>Lav en "Configuration Rule" for hostnavnet med Browser Integrity Check slået fra og Security Level "Essentially Off".</List.Item>
            <List.Item>Cloudflare kan se MDM-trafikken (også tilmeldingsprofilen). Linkene virker kun én gang i 30 minutter.</List.Item>
          </List>
          {test && <Stack gap={2}>{test.steps.map((x, i) => <Text key={i} size="sm" c={x.ok ? 'green' : 'red'}>{x.ok ? '✓' : '✗'} {x.text}</Text>)}</Stack>}
          <Group gap="xs"><Button onClick={() => saveAll()}>Gem</Button>
            <Button variant="default" loading={runTest.isPending} onClick={() => runTest.mutate(undefined, { onSuccess: setTest })}>Test adressen</Button></Group>
        </Stack>
      </Panel>

      <Panel title="2. MDM-server (NanoHUB i Docker)">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">Fleetdeck starter, opdaterer og stopper selv MDM-serveren på den valgte server – som tunnelen. Nøglerne hentes af jobbet med en engangskode og står aldrig i jobbet.</Text>
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <Select label="Server" placeholder="Vælg server med Docker" value={f.machineId || null} onChange={(v) => setF({ ...f, machineId: v ?? '' })}
              data={c.machines.map((m) => ({ value: String(m.id), label: `${m.name}${m.unraid ? ' (Unraid)' : ''}${m.online ? '' : ' – offline'}` }))} />
            <TextInput label="Docker-netværk" description="fx br0 på Unraid" value={f.network} onChange={(e) => setF({ ...f, network: e.currentTarget.value })} />
            <Group align="flex-end" gap={6} wrap="nowrap"><TextInput style={{ flex: 1 }} label="Fast IP" description="Ledig adresse på netværket" value={f.ip} onChange={(e) => setF({ ...f, ip: e.currentTarget.value })} />
              <Button variant="default" onClick={() => run(async () => { await put('/api/mdm', body()); await post('/api/mdm/suggest-ip'); }, 'Leder efter en ledig adresse…')}>Find</Button></Group>
            <TextInput label="Containerens navn" value={f.container} onChange={(e) => setF({ ...f, container: e.currentTarget.value })} />
            <TextInput label="Datamappe (valgfri)" description="Tom: /mnt/user/appdata/<navn> på Unraid" value={f.dataDir} onChange={(e) => setF({ ...f, dataDir: e.currentTarget.value })} />
            <TextInput label="Billede (valgfrit)" description="Tom: den version, Fleetdeck er testet med" placeholder={c.defaultImage.split('@')[0]} value={f.image} onChange={(e) => setF({ ...f, image: e.currentTarget.value })} />
            <TextInput label="Fleetdecks adresse på hjemmenettet" description={`MDM-serveren sender beskeder hertil${c.fleetdeckUrl ? ` (nu ${c.fleetdeckUrl})` : ''}`} placeholder="http://192.168.1.50:8585" value={f.webhookBase} onChange={(e) => setF({ ...f, webhookBase: e.currentTarget.value })} />
            <TextInput label="MDM-serverens interne adresse (valgfri)" description={c.effectiveApiUrl ? `Nu: ${c.effectiveApiUrl}` : 'Udfyldes automatisk ud fra den faste IP'} value={f.apiUrl} onChange={(e) => setF({ ...f, apiUrl: e.currentTarget.value })} />
          </SimpleGrid>
          {st?.suggest?.ip && !f.ip && <Text size="sm">Forslag: <Code>{st.suggest.ip}</Code> <Button size="compact-xs" variant="subtle" onClick={() => setF({ ...f, ip: st.suggest!.ip! })}>Brug den</Button></Text>}
          <Group gap="xs">
            <Button onClick={() => run(async () => { await put('/api/mdm', body()); await post('/api/mdm/start'); }, 'MDM-serveren startes')}>Gem og start/opdatér</Button>
            <Button variant="default" onClick={() => saveAll()}>Gem</Button>
            <Button variant="subtle" color="red" onClick={() => modals.openConfirmModal({
              title: 'Stop MDM-serveren?', children: <Text size="sm">iPad'erne kan ikke nås, før den startes igen. Data (push-certifikat, kø) bevares i datamappen.</Text>,
              labels: { confirm: 'Stop', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: () => run(() => post('/api/mdm/stop'), 'MDM-serveren stoppes'),
            })}>Stop</Button>
          </Group>
          {st && <Stack gap={2}>
            {st.job && <Text size="sm">Seneste: {st.job.title} – <b>{st.job.status}</b></Text>}
            {st.job?.output && <Code block style={{ maxHeight: 180, overflow: 'auto', fontSize: 11 }}>{st.job.output}</Code>}
            {st.container?.reported && <Text size="sm" c={st.container.running ? 'green' : 'red'}>{st.container.running ? '✓ MDM-containeren kører' : `✗ MDM-containeren ${st.container.found ? `er ${st.container.state}` : 'findes ikke'}`}{st.container.ip ? ` (${st.container.ip})` : ''}</Text>}
          </Stack>}
          <Group gap="xs">
            <Text size="xs" c="dimmed">Uden agent på serveren: brug deploy/compose/mdm.yml eller deploy/unraid/fleetdeck-mdm.xml og udfyld den interne adresse ovenfor.</Text>
            <Button size="compact-xs" variant="subtle" onClick={() => run(async () => setManual(await post<Manual>('/api/mdm/manual')))}>Værdier til manuel opsætning</Button>
            <Button size="compact-xs" variant="subtle" component="a" href={dl('/api/mdm/ca.pem')}>Hent fleetdeck-ca.pem</Button>
          </Group>
          {manual && <Table withRowBorders={false}><Table.Tbody>{([['NANOHUB_API_KEY', manual.apiKey], ['NANOHUB_WEBHOOK_URL', manual.webhookUrl], ['NANOHUB_WEBHOOK_HMAC_KEY', manual.hmacKey]] as const).map(([k, v]) => (
            <Table.Tr key={k}><Table.Td w={230}><Code>{k}</Code></Table.Td><Table.Td><Code style={{ wordBreak: 'break-all' }}>{v}</Code></Table.Td>
              <Table.Td w={80}><CopyButton value={v}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? '✓' : 'Kopiér'}</Button>}</CopyButton></Table.Td></Table.Tr>))}</Table.Tbody></Table>}
        </Stack>
      </Panel>

      <Panel title="3. Push-certifikat fra Apple">
        <Stack gap="sm">
          {p.topic ? <Alert color={p.warn ? 'orange' : 'green'} variant="light">Topic <Code>{p.topic}</Code> – udløber {fmt(p.expires)}{c.appleId ? ` · Apple-konto: ${c.appleId}` : ''}</Alert>
            : <Text size="sm" c="dimmed">Apple kræver et push-certifikat, før iPads kan styres. Det fornyes hvert år – Fleetdeck minder dig om det 30 dage før.</Text>}
          <Text size="sm" fw={600}>a) Lav en anmodning</Text>
          <Group gap="xs">
            <Button variant={p.hasPendingCsr ? 'default' : 'filled'} onClick={() => p.hasPendingCsr
              ? modals.openConfirmModal({ title: 'Lav en ny anmodning?', children: <Text size="sm">Den forrige anmodning (og svaret på den) kan så ikke længere bruges.</Text>, labels: { confirm: 'Lav ny', cancel: 'Annullér' }, onConfirm: () => run(() => post('/api/mdm/push/csr'), 'Ny anmodning lavet') })
              : run(() => post('/api/mdm/push/csr'), 'Anmodningen er lavet')}>{p.hasPendingCsr ? 'Lav ny anmodning' : 'Lav anmodning'}</Button>
            {p.hasPendingCsr && <Button variant="subtle" component="a" href={dl('/api/mdm/push/csr')} leftSection={<IconDownload size={14} />}>CSR</Button>}
            {p.hasEncryption && <Button variant="subtle" component="a" href={dl('/api/mdm/push/encrypt')} leftSection={<IconDownload size={14} />}>Krypteringscertifikat</Button>}
          </Group>
          <Text size="sm" fw={600}>b) Få den underskrevet hos mdmcert.download</Text>
          <Text size="sm" c="dimmed">Registrér husstandens organisation (med CVR) på mdmcert.download én gang og vent på godkendelse. Send derefter anmodningen herfra – svaret kommer som en fil (…plist.b64.p7) pr. e-mail.</Text>
          <Group align="flex-end" gap="xs">
            <TextInput label="E-mail registreret hos mdmcert.download" value={email} onChange={(e) => setEmail(e.currentTarget.value)} w={320} />
            <Button disabled={!p.hasPendingCsr || !email} onClick={() => run(async () => { const r = await post<{ msg: string }>('/api/mdm/push/mdmcert', { email }); notifyOk(r.msg); })}>Send anmodningen</Button>
          </Group>
          <Text size="sm" fw={600}>c) Pak svaret ud</Text>
          <Group gap="xs">
            <FileButton onChange={(file) => upload(file, '/api/mdm/push/decrypt', 'Svaret er pakket ud')} accept=".p7,.b64,.txt,*/*">
              {(props) => <Button {...props} variant="default" disabled={!p.hasEncryption}>Vælg filen fra mdmcert.download</Button>}
            </FileButton>
            {p.hasSignedRequest && <Button component="a" href={dl('/api/mdm/push/request')} leftSection={<IconDownload size={14} />}>Hent anmodningen til Apple</Button>}
          </Group>
          <Text size="sm" fw={600}>d) Hent certifikatet hos Apple</Text>
          <Text size="sm" c="dimmed">Log ind på identity.apple.com/pushcert med husstandens faste Apple-konto (skriv den ned – fornyelse kræver samme konto). Vælg "Create a Certificate" første gang – ellers "Renew" ud for det eksisterende – upload anmodningen fra c) og hent .pem-filen.</Text>
          <Text size="sm" fw={600}>e) Upload certifikatet</Text>
          <Group align="flex-end" gap="xs">
            <TextInput label="Apple-konto brugt hos Apple (huskeseddel)" value={f.appleId} onChange={(e) => setF({ ...f, appleId: e.currentTarget.value })} w={320} />
            <FileButton onChange={(file) => upload(file, '/api/mdm/push/cert', 'Push-certifikatet er gemt', { appleId: f.appleId })} accept=".pem,.cer,.crt,*/*">
              {(props) => <Button {...props}>Vælg .pem fra Apple</Button>}
            </FileButton>
          </Group>
        </Stack>
      </Panel>
    </Stack>
  );
}
