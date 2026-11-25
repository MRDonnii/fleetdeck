import { useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { ActionIcon, Badge, Button, Code, Divider, Group, Menu, Modal, SegmentedControl, Select, SimpleGrid, Stack, Table, Tabs, TagsInput, Text, Textarea, TextInput, Tooltip, ScrollArea } from '@mantine/core';
import { CopyButton } from '../design/copy';
import { AmtPanel } from '../design/amt';
import { PowerPanel } from '../design/power';
import { KeepAwakePanel } from '../design/keepawake';
import { LockControl } from '../design/locks';
import { RecoveryPanel } from '../design/recovery';
import { modals } from '@mantine/modals';
import { IconDeviceSdCard } from '@tabler/icons-react';
import { IconBan, IconBolt, IconChevronDown, IconDownload, IconEyeOff, IconLock, IconLogout, IconMoon, IconPencil, IconPlayerPlay, IconPlayerStop, IconPower, IconRefresh, IconSearch, IconTrash, IconWorldOff } from '@tabler/icons-react';
import { useMediaQuery } from '@mantine/hooks';
import { ConnectButton, WebUrlField } from '../design/remote';
import { DrivesPanel } from '../design/drives';
import { MachinePrintersPanel } from '../design/printers';
import { del, notifyErr, post, put, queryClient, useAction, useApi, useJobProgress } from '../api';
import { useMe } from '../auth';
import { ago, COMP, fmt, INTENT, JOB, num, presence } from '../format';
import type { HelperFix, JobRow, MachineDetail, MachineRow, Metric, Profile, Settings, Software, Upgrade } from '../types';
import {
  AgentDownloads,
  AlertList, Empty, Gauge, HealthCell, JobLive, JobModal, JobTable, KeyValue, Loading, MachineTable, MetricsChart, OnlineDot, PageHeader, Panel, StatusBadge, UsageBar, useActionModal,
} from '../components';
import { HelperTab, useRunFix } from '../helper';
import { UnraidTab } from './Unraid';
import { askUpdateAll } from '../design/updateAll';
import { askAmdDriver, isAmdDriver } from '../design/amdDrivers';
import { askHide, canHide, HiddenUpdatesPanel, NotRecommended, SkippedPanel, useHideUpdates } from '../design/upgradeSkipped';
import { PendingAlert, TrustPanel } from '../design/agentsec';
import { AccessRequestsSection } from '../design/accessrequests';
import { DebloatTab, PowerPlanModal } from '../design/debloat';
import { EncryptionPanel } from '../design/encryption';
import { TpmSection } from '../design/tpm';
import { MaintenanceRunView } from '../design/updateRuns';
import { NetworkPanel } from '../design/network';
import { BatteryPanel, ChangeLogPanel, DiskPanel, TimelinePanel } from '../design/healthHistory';
import { LogsTab } from '../design/syslog';
import { PiCardStatus, PiCardWizard, PiNetbootPanel } from '../design/picard';
import { SpareCardPanel } from '../design/sparecard';
import { HelpdeskThread } from '../design/helpdesk';
import { BiosMachineSection, BiosUpgradeRow } from '../design/bios';

export function MachinesPage() {
  const { isAdmin } = useMe();
  const { data: ms } = useApi<MachineRow[]>(['machines'], '/api/machines', { refetchInterval: 30_000 });
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], '/api/profiles');
  const [q, setQ] = useState('');
  const [prof, setProf] = useState<string | null>(null);
  const [params] = useSearchParams();
  const [status, setStatus] = useState(params.get('vis') === 'opdateringer' ? 'updates' : 'all');
  const [sel, setSel] = useState<number[]>([]);
  const [enroll, setEnroll] = useState(false);
  const action = useActionModal();
  const filtered = useMemo(() => (ms ?? []).filter((m) => {
    const hay = [m.name, m.hostname, m.user, m.ip, m.profile, ...m.tags].join(' ').toLowerCase();
    if (q && !hay.includes(q.toLowerCase())) return false;
    if (prof && String(m.profileId) !== prof) return false;
    if (status === 'online') return m.online;
    if (status === 'offline') return !m.online;
    if (status === 'problem') return m.healthState !== 'ok' || m.compliance.failed > 0 || m.compliance.missing > 0;
    if (status === 'updates') return m.upgrades > 0;
    return true;
  }), [ms, q, prof, status]);
  if (!ms) return <Loading />;
  return (
    <>
      <PageHeader title="Maskiner" sub="Alle tilmeldte maskiner – Windows, Linux og Mac">
        {isAdmin && <>
          <Button variant="default" disabled={!sel.length} onClick={() => action.show(sel)}>{sel.length ? `Handling for ${sel.length} valgte…` : 'Handling for valgte…'}</Button>
          <Button onClick={() => setEnroll(true)}>+ Tilføj maskine</Button>
        </>}
      </PageHeader>
      <Group mb="md" gap="sm" wrap="wrap">
        <TextInput leftSection={<IconSearch size={16} />} placeholder="Søg navn, bruger, IP, tag…" value={q} onChange={(e) => setQ(e.currentTarget.value)} w={{ base: '100%', sm: 280 }} />
        <Select placeholder="Alle profiler" clearable data={profiles.map((p) => ({ value: String(p.id), label: p.name }))} value={prof} onChange={setProf} w={{ base: '100%', sm: 200 }} />
        <SegmentedControl value={status} onChange={setStatus} data={[{ value: 'all', label: 'Alle' }, { value: 'online', label: 'Online' }, { value: 'offline', label: 'Offline' },
          { value: 'problem', label: 'Problemer' }, { value: 'updates', label: 'Opdateringer' }]} size="xs" />
      </Group>
      {isAdmin && <AccessRequestsSection />}
      {isAdmin && ms.filter((m) => m.replacesId != null).map((m) => <PendingAlert key={m.id} m={m} compact />)}
      {(ms?.length ?? 0) > 0 && !filtered.length ? <Empty>Ingen maskiner matcher filteret</Empty>
        : <Panel p={0}><MachineTable machines={filtered} selectable={isAdmin} selected={sel} onSelect={setSel} /></Panel>}
      <EnrollModal opened={enroll} onClose={() => setEnroll(false)} />
      {action.node}
    </>
  );
}

export function EnrollModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { data: s } = useApi<Settings>(['settings'], opened ? '/api/settings' : null);
  const [piOpen, setPiOpen] = useState(false);
  const [os, setOs] = useState<'windows' | 'linux' | 'mac'>('windows');
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], opened ? '/api/profiles' : null);
  const [prof, setProf] = useState<string | null>(null);
  const pq = prof === null ? '' : `&profile=${prof}`;
  const cmd = s ? `irm "${s.server_url}/install.ps1?key=${s.enrollKey}${pq}" | iex` : '';
  const lcmd = s ? `curl -fsSL "${s.server_url}/install.sh?key=${s.enrollKey}${pq}" | sudo bash` : '';
  const copyRow = (c: string) => prof === null
    ? <Code block c="dimmed">Vælg profil ovenfor først – så vises kommandoen.</Code>
    : (<Group wrap="nowrap" align="stretch"><Code block style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{c}</Code>
      <CopyButton value={c}>{({ copied, copy }) => <Button onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton></Group>);
  return (
    <Modal opened={opened} onClose={onClose} title="Tilføj en maskine" size="xl">
      <Stack>
        {s?.enroll_key_enabled === '0' && <Text size="sm" c="orange">Den globale tilmeldingsnøgle er slået fra, så kommandoerne herunder virker ikke. Lav en engangskode under Indstillinger → Agent-sikkerhed.</Text>}
        <div>
          <Text size="sm" fw={500} mb={6}>Hvad kører maskinen?</Text>
          <SegmentedControl fullWidth value={os} onChange={(v) => setOs(v as 'windows' | 'linux' | 'mac')}
            data={[{ value: 'windows', label: 'Windows' }, { value: 'linux', label: 'Linux / Raspberry Pi' }, { value: 'mac', label: 'Mac' }]} />
        </div>
        <Select label="Profil" required placeholder="Vælg profil" value={prof} onChange={setProf} allowDeselect={false} comboboxProps={{ withinPortal: true }}
          description="Bestemmer, hvilke programmer og indstillinger maskinen får. Installeres der uden profil i kommandoen, spørger installationen selv."
          data={[{ value: '0', label: 'Ingen profil – kun overvågning og opdateringer' },
            ...profiles.map((p) => ({ value: String(p.id), label: p.name }))]} />
        {os === 'windows' ? <>
          <Text size="sm">1. Åbn <b>Terminal (administrator)</b> på pc'en (højreklik på Start).<br />2. Indsæt og kør:</Text>
          {copyRow(cmd)}
          <Text size="sm" c="dimmed">Agenten installeres som Windows-tjenesten "Fleetdeck Agent" og vises her inden for et minut med den valgte profil.
            Pc'er installeret fra WinSetup-USB'en eller netværket tilmelder sig automatisk.</Text>
          <Divider label="eller med et vindue" labelPosition="center" />
          <Text size="sm">Hent installationsprogrammet, dobbeltklik på det, og indsæt kommandoen ovenfor (eller adressen og nøglen) i vinduet. Profilen vælges i vinduet.
            Knappen <b>Find automatisk</b> finder serveren selv på hjemmenettet.</Text>
          <Group><Button component="a" href="/api/agent/download" variant="default">Hent Fleetdeck.exe</Button>
            <CopyButton value={s?.enrollKey ?? ''}>{({ copied, copy }) => <Button variant="subtle" onClick={copy}>{copied ? 'Nøglen er kopieret' : 'Kopiér kun nøglen'}</Button>}</CopyButton></Group>
        </> : os === 'linux' ? <>
          <Group justify="space-between" wrap="nowrap" p="sm" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8 }}>
            <div><Text size="sm" fw={600}>Ny Raspberry Pi?</Text><Text size="xs" c="dimmed">Fleetdeck laver et færdigt SD-kort. Pi'en melder sig selv og får profilens programmer.</Text></div>
            <Button variant="light" leftSection={<IconDeviceSdCard size={16} />} onClick={() => setPiOpen(true)}>Lav SD-kort</Button>
          </Group>
          <PiCardWizard opened={piOpen} onClose={() => setPiOpen(false)} />
          <Text size="sm">1. Åbn en terminal på maskinen (eller log ind med SSH).<br />2. Indsæt og kør. Du bliver bedt om din sudo-adgangskode:</Text>
          {copyRow(lcmd)}
          <Text size="sm" c="dimmed">Agenten kører som tjenesten <Code>fleetdeck-agent</Code> (systemd, OpenRC, runit eller SysV-init), starter selv ved opstart
            uden login og har lav prioritet, så skrivebordet eller kiosken altid går forrest. Den bruger distroens egen pakkehåndtering – apt, dnf/yum, zypper,
            pacman, apk, xbps, eopkg, rpm-ostree eller SteamOS – plus snap og flatpak, og virker på almindelige pc'er (x64), Raspberry Pi (ARM, 32- og 64-bit)
            og Alpine (musl). Kommandoen vælger selv. På Alpine: kør først <Code>apk add bash curl</Code> som root.</Text>
          <Text size="sm" fw={600} mt="xs">Selve Unraid-serveren, som Fleetdeck kører på</Text>
          <Text size="sm">Unraid kan ikke nå containerens egen IP, så den forbinder gennem en fil i appdata. Kør i Unraids terminal (&gt;_ øverst til højre):</Text>
          {copyRow(s ? `curl -fsSL --unix-socket /mnt/user/appdata/fleetdeck/fleetdeck.sock "http://localhost/install.sh?key=${s.enrollKey}${pq}" | bash` : '')}
          <Text size="sm" c="dimmed">Agenten gemmes på USB-stikket og starter selv efter genstart. Plugins og containere vises som programmer, og containere
            med en ny version kan opdateres herfra. Andre Unraid-servere bruger den almindelige kommando ovenfor.</Text>
          <Divider label="eller med et vindue" labelPosition="center" />
          <Text size="sm">Hent agenten, og kør <Code>chmod +x fleetdeck-agent && ./fleetdeck-agent setup</Code> i en terminal.
            På et skrivebord kommer der små vinduer; uden skærm stilles spørgsmålene i terminalen.</Text>
          <Group><Button component="a" href="hent" target="_blank" variant="default">Hent agenten (vælg x64 eller Raspberry Pi)</Button>
            <CopyButton value={s?.enrollKey ?? ''}>{({ copied, copy }) => <Button variant="subtle" onClick={copy}>{copied ? 'Nøglen er kopieret' : 'Kopiér kun nøglen'}</Button>}</CopyButton></Group>
        </> : <>
          <Text size="sm">1. Åbn <b>Terminal</b> på Mac'en (Programmer → Hjælpeværktøjer, eller søg med ⌘ + mellemrum).<br />
            2. Indsæt og kør. Du bliver bedt om din adgangskode:</Text>
          {copyRow(lcmd)}
          <Text size="sm" c="dimmed">Agenten kører som launchd-tjenesten <Code>com.fleetdeck.agent</Code> og virker på både Apple Silicon og Intel.
            Programmer installeres og opdateres med Homebrew og App Store (udvidelserne <i>Homebrew</i> og <i>App Store</i>), og macOS-opdateringer
            med Windows Update-handlingen.</Text>
          <Divider label="eller med appen" labelPosition="center" />
          <Text size="sm">Hent Fleetdeck til Mac, træk den over i Programmer og åbn den. Indsæt kommandoen ovenfor (eller adressen og nøglen) i vinduet,
            vælg profil og tryk <b>Installér og forbind</b>. Bagefter ligger Fleetdeck i menulinjen med status, <i>Synkronisér nu</i> og pause,
            og vinduet hjælper med at give <b>Fuld diskadgang</b>.</Text>
          <Group><Button component="a" href="/api/agent/download?platform=mac&kind=dmg&arch=arm64" variant="default">Hent Fleetdeck.dmg (Apple Silicon)</Button>
            <Button component="a" href="/api/agent/download?platform=mac&kind=dmg&arch=x64" variant="default">Intel-Mac</Button>
            <CopyButton value={s?.enrollKey ?? ''}>{({ copied, copy }) => <Button variant="subtle" onClick={copy}>{copied ? 'Nøglen er kopieret' : 'Kopiér kun nøglen'}</Button>}</CopyButton></Group>
          <Text size="sm" c="dimmed">Første gang siger macOS, at appen ikke kan kontrolleres (den er ikke købt-signeret hos Apple): på macOS 15 og nyere
            under <b>Systemindstillinger → Anonymitet og sikkerhed → Åbn alligevel</b>, ellers højreklik på appen → <b>Åbn</b>.</Text>
        </>}
        <Divider label="send et link til brugeren" labelPosition="center" />
        <Text size="sm">Downloadsiden viser kun installationsprogrammerne og en vejledning til Windows, Linux, Raspberry Pi og Mac – intet andet fra Fleetdeck.
          Med nøglen i linket kan modtageren forbinde med det samme; uden nøglen skal du give den på anden vis.</Text>
        {s && <Stack gap={6}>
          <ShareLink label="Link uden nøgle" url={`${s.server_url}/hent`} />
          <ShareLink label="Link med nøgle" url={`${s.server_url}/hent#k=${s.enrollKey}`} />
        </Stack>}
        {s && !s.agentAvailable && <Text c="red" size="sm">Agenten er ikke bygget ind i serveren. Byg Docker-billedet igen.</Text>}
      </Stack>
    </Modal>
  );
}

const TABS = [['overblik', 'Overblik'], ['helbred', 'Helbred'], ['logs', 'Logs'], ['overholdelse', 'Profil'], ['software', 'Software og spil'], ['opdateringer', 'Opdateringer'], ['hjaelper', 'Hjælper'], ['jobs', 'Jobs']] as const;

export function MachinePage() {
  const { id = '', tab = 'overblik' } = useParams();
  const nav = useNavigate();
  const { isAdmin } = useMe();
  const { data: m } = useApi<MachineDetail>(['machine', id], `/api/machines/${id}`, { refetchInterval: 20_000 });
  const action = useActionModal();
  const [renaming, setRenaming] = useState(false);
  const quick = useAction((b: { action: string; params?: object }) => post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: [Number(id)], ...b }),
    (r) => (r.jobsCreated ? `${r.jobsCreated} job(s) sat i kø` : 'Udført'));
  if (!m) return <Loading />;
  const f = m.facts;
  return (
    <>
      <PageHeader title={<Group gap="sm" wrap="nowrap"><OnlineDot online={m.online} />{m.name}
          {isAdmin && <Tooltip label="Omdøb"><ActionIcon variant="subtle" color="gray" onClick={() => setRenaming(true)} aria-label="Omdøb maskinen"><IconPencil size={18} /></ActionIcon></Tooltip>}</Group>}
        sub={`${m.renamed ? `Værtsnavn ${m.hostname} · ` : ''}${f.os ?? 'Ukendt OS'} ${f.osVersion ?? ''} · ${presence(m)}${m.user ? ` · ${m.user}` : ''}`}>
        {isAdmin && <MachineActions m={m} run={(b) => quick.mutate(b)} onAction={() => action.show([m.id])} />}
      </PageHeader>
      {isAdmin && m.replacesId != null && <PendingAlert m={m} onGone={(kept) => nav(kept ? `/maskiner/${kept}` : '/maskiner')} />}
      <Tabs value={tab} onChange={(v) => nav(`/maskiner/${id}/${v}`)} mb="md">
        <Tabs.List style={{ flexWrap: 'nowrap', overflowX: 'auto' }}>
          {[...(m.health.unraid ? [TABS[0], ['unraid', 'Unraid'] as const, ...TABS.slice(1)] : TABS).filter((t) => isAdmin || t[0] !== 'logs'), ...(isAdmin ? [['beskeder', 'Beskeder'] as const] : [])]
            .flatMap<readonly [string, string]>((t) => (t[0] === 'hjaelper' && m.platform === 'windows' && isAdmin ? [['debloat', 'Debloat & privatliv'] as const, t]
              : t[0] === 'software' && m.printers ? [t, ['printere', 'Printere'] as const] : [t])).map(([k, l]) => (
            <Tabs.Tab key={k} value={k} rightSection={k === 'helbred' && m.alerts ? <Badge size="xs" color="red" circle>{m.alerts}</Badge>
              : k === 'opdateringer' && m.upgrades ? <Badge size="xs" color="yellow">{m.upgrades}</Badge> : undefined}>{l}</Tabs.Tab>
          ))}
        </Tabs.List>
      </Tabs>
      {tab === 'overblik' && <Overview m={m} />}
      {tab === 'unraid' && m.health.unraid && <UnraidTab m={m} />}
      {tab === 'helbred' && <HealthTab m={m} />}
      {tab === 'logs' && isAdmin && <LogsTab m={m} />}
      {tab === 'overholdelse' && <ComplianceTab m={m} onInstall={(pid) => quick.mutate({ action: 'install', params: { packageId: pid } })} onEnforce={() => quick.mutate({ action: 'enforce' })} />}
      {tab === 'software' && <SoftwareTab m={m} />}
      {tab === 'printere' && m.printers && <MachinePrintersPanel machineId={m.id} rows={m.printers} isAdmin={isAdmin} online={m.online} />}
      {tab === 'opdateringer' && <UpgradesTab m={m} onUpgrade={(wingetId, name) => quick.mutate({ action: 'upgrade', params: { wingetId, name } })}
        onUpgradeAll={() => askUpdateAll([m], (mode) => quick.mutate({ action: 'update_all', params: { mode } }))}
        onShutdownWhenDone={() => shutdownWhenDone(m, (b) => quick.mutate(b))} />}
      {tab === 'hjaelper' && <HelperTab m={m} />}
      {tab === 'debloat' && m.platform === 'windows' && isAdmin && <DebloatTab machineId={m.id} />}
      {tab === 'jobs' && <Panel p={0}><JobTable jobs={m.jobs} withMachine={false} /></Panel>}
      {tab === 'beskeder' && isAdmin && <Panel p={0}><HelpdeskThread machineId={m.id} online={m.online} compact /></Panel>}
      {action.node}
      {renaming && <RenameModal m={m} onClose={() => setRenaming(false)} />}
    </>
  );
}

/** Omdøb maskinen: et visningsnavn, som agenten aldrig overskriver. Værtsnavnet bliver ved med at være maskinens identitet. */
/** Hvor temperaturerne kommer fra (Mac: Apple silicon-sensorer, SMC eller powermetrics; "off"/"none" = slået fra / ingen sensor). */
function tempSourceText(source?: string | null): string {
  switch (source) {
    case undefined: case null: case '': return 'Ingen temperaturmåling på maskinen endnu';
    case 'off': return "Temperaturmåling er slået fra på Mac'en";
    case 'none': return "Mac'en har ingen temperatursensor, Fleetdeck kan læse";
    case 'applesilicon': return 'Temperaturer fra Apple silicon-sensorerne';
    case 'smc': return 'Temperaturer fra SMC (Intel)';
    case 'ACPI': return 'Temperaturer fra ACPI (unøjagtig)';
    default: return `Temperaturer fra ${source}`;
  }
}

function RenameModal({ m, onClose }: { m: MachineRow; onClose: () => void }) {
  const [name, setName] = useState(m.name);
  const save = useAction((n: string) => put<{ name: string }>(`/api/machines/${m.id}/name`, { name: n }), (r) => `Maskinen hedder nu ${r.name}`);
  const clean = name.trim().replace(/\s+/g, ' ');
  const err = clean.length > 64 ? 'Højst 64 tegn' : undefined;
  const submit = (n: string) => save.mutate(n, { onSuccess: onClose });
  return (
    <Modal opened onClose={onClose} title={`Omdøb ${m.name}`} centered>
      <form onSubmit={(e) => { e.preventDefault(); if (clean && !err) submit(clean); }}>
        <Stack>
          <TextInput label="Navn i Fleetdeck" data-autofocus value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={80} error={err}
            description={<>Vises overalt i Fleetdeck, i Home Assistant og i beskeder. Agenten ændrer det aldrig. Værtsnavnet <Code>{m.hostname}</Code> er uændret.</>} />
          <Group justify="space-between">
            {m.renamed ? <Button variant="subtle" color="gray" onClick={() => submit(m.hostname)} loading={save.isPending}>Brug værtsnavnet</Button> : <span />}
            <Group gap="xs">
              <Button variant="default" onClick={onClose}>Annullér</Button>
              <Button type="submit" disabled={!clean || !!err || clean === m.name} loading={save.isPending}>Gem</Button>
            </Group>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

type Run = (b: { action: string; params?: object }) => void;

/** Bekræft før noget sker på maskinen. */
function confirmDo(o: { title: string; text: string; label: string; color?: string; run: () => void }) {
  modals.openConfirmModal({
    title: o.title, children: <Text size="sm">{o.text}</Text>,
    labels: { confirm: o.label, cancel: 'Annullér' }, confirmProps: { color: o.color }, onConfirm: o.run,
  });
}

type PowerItem = { key: string; label: string; icon: ReactNode; go: () => void };

/** Knapperne øverst på maskinsiden. Alt der ændrer noget på pc'en skal bekræftes. På mobil samles de i én menu. */
/** Agenten på en maskine, der aldrig opdateres automatisk: kun efter et udtrykkeligt OK. */
function askAgentUpdate(id: number, name: string) {
  modals.openConfirmModal({
    title: 'Opdatér agenten',
    children: <Text size="sm">Agenten på <b>{name}</b> opdateres ved næste check-in (inden for en time). Kun agenten skiftes – maskinen genstartes ikke, og programmer og system røres ikke.</Text>,
    labels: { confirm: 'Opdatér agenten', cancel: 'Annullér' },
    onConfirm: () => post(`/api/machines/${id}/agent-update`, {}).then(() => queryClient.invalidateQueries()).catch(notifyErr),
  });
}

function MachineActions({ m, run, onAction }: { m: MachineDetail; run: Run; onAction: () => void }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const win = m.platform === 'windows';
  const [planOpen, setPlanOpen] = useState(false);
  const [cardOpen, setCardOpen] = useState(false);
  const isPi = m.platform === 'linux' && /raspberry/i.test(m.model ?? m.facts?.model ?? '');
  const planModal = <>{planOpen ? <PowerPlanModal machineId={m.id} name={m.name} onClose={() => setPlanOpen(false)} /> : null}
    {isPi && <PiCardWizard machineId={m.id} opened={cardOpen} onClose={() => setCardOpen(false)} />}</>;
  const upgrade = () => askUpdateAll([m], (mode) => run({ action: 'update_all', params: { mode } }));
  const maint = () => maintNow(m, (after) => run({ action: 'maintenance', params: { after } }));
  const power: (PowerItem | null)[] = [
    !m.online && m.mac ? { key: 'wake', label: 'Tænd (Wake-on-LAN)', icon: <IconPower size={16} />, go: () => confirmDo({ title: `Tænd ${m.name}?`, label: 'Tænd',
      text: 'Sender et Wake-on-LAN-signal til netkortet. Det kræver, at Wake-on-LAN er slået til i BIOS.', run: () => run({ action: 'wake' }) }) } : null,
    { key: 'reboot', label: 'Genstart…', icon: <IconRefresh size={16} />, go: () => confirmDo({ title: `Genstart ${m.name}?`, label: 'Genstart', color: 'orange',
      text: "Pc'en genstarter om 1 minut med en besked på skærmen. Ikke gemt arbejde kan gå tabt.",
      run: () => run({ action: 'reboot', params: { delay: 60, message: "Fleetdeck: pc'en genstarter om 1 minut. Gem dit arbejde." } }) }) },
    { key: 'shutdown', label: 'Sluk…', icon: <IconPower size={16} />, go: () => confirmDo({ title: `Sluk ${m.name}?`, label: 'Sluk', color: 'red',
      text: "Pc'en slukker om 1 minut med en besked på skærmen. Ikke gemt arbejde kan gå tabt.",
      run: () => run({ action: 'shutdown', params: { delay: 60, message: "Fleetdeck: pc'en slukker om 1 minut. Gem dit arbejde." } }) }) },
    win ? { key: 'when_done', label: 'Sluk når færdig…', icon: <IconPlayerStop size={16} />, go: () => shutdownWhenDone(m, run) } : null,
    win ? { key: 'power_plan', label: 'Strømprofil…', icon: <IconBolt size={16} />, go: () => setPlanOpen(true) } : null,
    { key: 'headless', label: `Luk hovedløse browsere${m.headless ? ` (${m.headless})` : ''}…`, icon: <IconWorldOff size={16} />, go: () => confirmDo({
      title: `Luk hovedløse browsere på ${m.name}?`, label: 'Luk dem',
      text: 'Lukker browsere, der er startet uden vindue (--headless), fx efterladt af automatisering. Browsere med vindue – også kiosken – røres ikke.',
      run: () => run({ action: 'kill_headless' }) }) },
    { key: 'lock', label: 'Lås skærmen…', icon: <IconLock size={16} />, go: () => confirmDo({ title: `Lås skærmen på ${m.name}?`, label: 'Lås',
      text: 'Brugeren skal logge ind igen. Intet går tabt.', run: () => run({ action: 'lock' }) }) },
    win ? { key: 'logoff', label: 'Log brugeren af…', icon: <IconLogout size={16} />, go: () => confirmDo({ title: `Log brugeren af ${m.name}?`, label: 'Log af', color: 'red',
      text: 'Alle åbne programmer lukkes. Ikke gemt arbejde går tabt.', run: () => run({ action: 'logoff' }) }) } : null,
    !win && (m.tags.some((t) => t.toLowerCase() === 'kiosk') || /kiosk/i.test(m.profile ?? '')) ? { key: 'kiosk', label: 'Genstart kiosk-browser…', icon: <IconRefresh size={16} />, go: () => confirmDo({ title: `Genstart kiosk-browseren på ${m.name}?`, label: 'Genstart kiosk',
      text: 'Chrome-kiosken lukkes og startes igen. Det tager et par sekunder.', run: () => run({ action: 'script', params: { name: 'Genstart kiosk-browser', runAs: 'user',
        body: 'if systemctl --user cat kiosk-chrome.service >/dev/null 2>&1; then systemctl --user restart kiosk-chrome.service && echo "kiosk-chrome genstartet"; else echo "Ingen kiosk-chrome.service på denne maskine"; exit 1; fi' } }) }) } : null,
  ];
  const powerItems = power.filter((x): x is PowerItem => !!x).map((p) => <Menu.Item key={p.key} leftSection={p.icon} onClick={p.go}>{p.label}</Menu.Item>);

  if (mobile) return (
    <Group gap="xs" wrap="nowrap" w="100%">
      {planModal}
      <ConnectButton id={m.id} name={m.name} remote={m.health?.remote} web={m.webUrl} size="md" />
      <Button size="md" style={{ flex: 1 }} leftSection={<IconPlayerPlay size={18} />} onClick={onAction}>Handling…</Button>
      <Menu position="bottom-end" withinPortal width={270}>
        <Menu.Target><Button size="md" variant="default" style={{ flex: 1 }} rightSection={<IconChevronDown size={16} />}>Mere</Button></Menu.Target>
        <Menu.Dropdown>
          <Menu.Item leftSection={<IconRefresh size={16} />} onClick={() => run({ action: 'inventory' })}>Synkronisér</Menu.Item>
          <Menu.Item leftSection={<IconDownload size={16} />} onClick={upgrade}>Opdatér alt{m.upgrades ? ` (${m.upgrades})` : ''}…</Menu.Item>
          <Menu.Item leftSection={<IconMoon size={16} />} onClick={maint}>Vedligehold nu…</Menu.Item>
          {isPi && <Menu.Item leftSection={<IconDeviceSdCard size={16} />} onClick={() => setCardOpen(true)}>Lav SD-kort…</Menu.Item>}
          <Menu.Divider /><Menu.Label>Strøm og bruger</Menu.Label>
          {powerItems}
        </Menu.Dropdown>
      </Menu>
    </Group>
  );
  return (
    <>
      {planModal}
      <ConnectButton id={m.id} name={m.name} remote={m.health?.remote} web={m.webUrl} />
      <Button variant="default" leftSection={<IconRefresh size={16} />} onClick={() => run({ action: 'inventory' })}>Synkronisér</Button>
      <Button variant="default" leftSection={<IconDownload size={16} />} onClick={upgrade}>Opdatér alt{m.upgrades ? ` (${m.upgrades})` : ''}</Button>
      <Button variant="default" leftSection={<IconMoon size={16} />} onClick={maint}>Vedligehold nu</Button>
      {isPi && <Button variant="default" leftSection={<IconDeviceSdCard size={16} />} onClick={() => setCardOpen(true)}>Lav SD-kort</Button>}
      <Menu position="bottom-end" withinPortal width={240}>
        <Menu.Target><Button variant="default" leftSection={<IconPower size={16} />} rightSection={<IconChevronDown size={14} />}>Strøm</Button></Menu.Target>
        <Menu.Dropdown>{powerItems}</Menu.Dropdown>
      </Menu>
      <Button leftSection={<IconPlayerPlay size={16} />} onClick={onAction}>Handling…</Button>
    </>
  );
}

/** Sluk pc'en, når alt i køen (fx en stor opdatering) er færdigt – men ikke hvis nogen bruger den. */
export function shutdownWhenDone(m: { name: string }, run: Run) {
  confirmDo({
    title: `Sluk ${m.name}, når opgaverne er færdige?`, label: 'Sluk når færdig',
    text: "Pc'en slukker 2 minutter efter, at alt det der står i kø er færdigt (fx en stor opdatering). Den slukker ikke, hvis nogen har brugt den de sidste 15 minutter.",
    run: () => run({ action: 'shutdown_when_done', params: { onlyIfIdleMin: 15 } }),
  });
}


/** "Vedligehold nu": vælg hvad der skal ske bagefter, og kør profilens opgaver med det samme. */
function maintNow(m: MachineDetail, run: (after: string) => void) {
  let after = m.platform === 'windows' ? 'reboot' : 'none';
  modals.openConfirmModal({
    title: `Vedligehold ${m.name} nu`,
    children: (
      <Stack gap="sm">
        <Text size="sm">Kører profilens opgaver (Windows Update og programopdateringer, hvis intet andet er valgt). Du kan følge det under Opdateringer og Jobs.</Text>
        <Select label="Bagefter" defaultValue={after} allowDeselect={false} comboboxProps={{ withinPortal: true }} onChange={(v) => { after = v ?? after; }}
          data={[{ value: 'none', label: 'Ingenting' }, { value: 'reboot', label: 'Genstart, hvis det kræves' }, { value: 'shutdown', label: 'Genstart hvis nødvendigt, og sluk bagefter' }]} />
        <Text size="xs" c="dimmed">Genstart og sluk sker kun, hvis ingen har brugt pc'en de sidste 15 minutter.</Text>
      </Stack>
    ),
    labels: { confirm: 'Start', cancel: 'Annullér' },
    onConfirm: () => run(after),
  });
}

function Overview({ m }: { m: MachineDetail }) {
  const { isAdmin } = useMe();
  const isPi = m.platform === 'linux' && /raspberry/i.test(m.model ?? m.facts?.model ?? '');
  const nav = useNavigate();
  const f = m.facts;
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], '/api/profiles');
  const [profileId, setProfileId] = useState<string | null>(m.profileId ? String(m.profileId) : null);
  const [tags, setTags] = useState<string[]>(m.tags);
  const [notes, setNotes] = useState(m.notes);
  const action = useActionModal();
  const save = useAction(() => put<{ jobsCreated: number }>(`/api/machines/${m.id}`, { profileId: profileId ? Number(profileId) : null, tags, notes }),
    (r) => `Gemt${r.jobsCreated ? ` – ${r.jobsCreated} job(s) oprettet` : ''}`);
  const remove = () => modals.openConfirmModal({
    title: `Slet ${m.name}?`, children: <Text size="sm">Al historik slettes, og agenten på maskinen holder op med at virke.</Text>,
    labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: async () => { try { await del(`/api/machines/${m.id}`); nav('/maskiner'); } catch (e) { notifyErr(e); } },
  });
  return (
    <SimpleGrid cols={{ base: 1, lg: 2 }}>
      {isPi && <PiCardStatus machineId={m.id} />}
      {isPi && isAdmin && <PiNetbootPanel machineId={m.id} />}
      {isPi && isAdmin && <SpareCardPanel machineId={m.id} />}
      <Stack gap={0}>
      <Panel title="System">
        <KeyValue rows={[
          ['Model', `${f.manufacturer ?? ''} ${f.model ?? ''}`], ['Bundkort', f.board], ['BIOS', f.bios],
          ['CPU', f.cpuName ? `${f.cpuName} (${f.cores} kerner / ${f.threads} tråde)` : null], ['RAM', f.ramGb ? `${f.ramGb} GB` : null],
          ['Grafik', f.gpus?.join(', ')], ['Windows', f.os ? `${f.os} ${f.osVersion ?? ''} (build ${f.build})` : null], ['Udgave', f.edition],
          ['Installeret', f.installed], ['Sidste opstart', f.lastBoot ? `${f.lastBoot} (${num((m.health.live?.uptimeH ?? 0) / 24, 1)} dage)` : null],
          ['Firmware', f.firmware ? `${f.firmware}, Secure Boot ${f.secureBoot ? 'til' : f.secureBoot === false ? 'fra' : '–'}` : null],
          ['Netværk', <><Code>{m.ip ?? '–'}</Code> <Code>{m.mac ?? ''}</Code></>],
          ...(m.health.network ? [] : [['Netkort', <Stack gap={0}>{(f.nics ?? []).map((n) => <Text key={n.mac} size="sm">{n.name} <Text span c="dimmed" size="xs">{n.status} {n.speed}</Text></Text>)}</Stack>] as [string, ReactNode]]),
        ]} />
        <Text fw={600} mt="md" mb={4}>Drev</Text>
        {(m.health.live?.volumes ?? []).map((v) => (
          <Group key={v.drive} gap="sm" wrap="nowrap" mb={4}><Text fw={600} w={28}>{v.drive}</Text><UsageBar value={v.pctUsed} warn={85} bad={95} w={160} />
            <Text size="sm" c="dimmed">{num(v.freeGb)} GB fri af {num(v.sizeGb)} GB</Text></Group>
        ))}
      </Panel>
      <NetworkPanel net={m.health.network} />
      </Stack>
      <Stack gap={0}>
        <Panel title="Styring">
          <Stack>
            <Select label="Profil" clearable disabled={!isAdmin} data={profiles.map((p) => ({ value: String(p.id), label: p.name }))} value={profileId} onChange={setProfileId} />
            <TagsInput label="Tags" description="Fx barnets navn eller 'gaming'" disabled={!isAdmin} value={tags} onChange={setTags} />
            <Textarea label="Noter" autosize minRows={2} disabled={!isAdmin} value={notes} onChange={(e) => setNotes(e.currentTarget.value)} />
            {isAdmin && <Group><Button loading={save.isPending} onClick={() => save.mutate(undefined)}>Gem</Button></Group>}
            {isAdmin && <WebUrlField id={m.id} value={m.webUrl} auto={!!m.webUrl && /unraid/i.test(m.os ?? '')} />}
          </Stack>
        </Panel>
        <PowerPanel m={m} isAdmin={isAdmin} />
        {m.platform === 'windows' && m.health.amt?.present && <AmtPanel m={m} isAdmin={isAdmin} />}
        {m.platform === 'mac' && m.keepAwake && <KeepAwakePanel m={m} isAdmin={isAdmin} />}
        {m.platform !== 'windows' && <RecoveryPanel m={m} isAdmin={isAdmin} />}
        <DrivesPanel machineId={m.id} rows={m.drives} isAdmin={isAdmin} windows={m.platform === 'windows'} online={m.online} lastSeen={m.lastSeen} />
        <Panel title="Vedligeholdelse og opdateringer">
          <MaintenanceRunView run={m.maintenance} autoUpdate={m.autoUpdate} profile={m.profile} />
          <Text size="xs" c="dimmed" mt="xs">Planen styres af profilen{m.profile ? ` (${m.profile})` : ''}.</Text>
        </Panel>
        <ExtensionsPanel machineId={m.id} isAdmin={isAdmin} />
        <TrustPanel m={m} />
        <Panel title="Agent">
          <KeyValue rows={[
            ['Version', <Group gap={6}><Code>{m.agentVersion ?? '–'}</Code>{m.agentCurrent ? <Badge variant="light" color="green">Nyeste</Badge>
              : !m.agentUpdate?.blocked ? <Badge variant="light" color="yellow">Opdateres</Badge>
              : m.agentUpdate.requested ? <Badge variant="light" color="blue">Opdateres ved næste check-in</Badge>
              : <>
                <Tooltip label="Maskinen opdateres aldrig automatisk – heller ikke agenten."><Badge variant="light" color="gray">Opdateres ikke automatisk</Badge></Tooltip>
                {isAdmin && <Button size="compact-xs" variant="light" onClick={() => askAgentUpdate(m.id, m.name)}>Opdatér agenten</Button>}
              </>}</Group>],
            ['Tilmeldt', fmt(m.enrolled)], ['Sidste inventar', `${fmt(m.lastInventory)} (${ago(m.lastInventory)})`], ['Sidste helbredstjek', fmt(m.lastHealth)],
          ]} />
          <Text size="sm" fw={600} mt="md" mb={6}>Opsætningsprogram til denne maskine</Text>
          <AgentDownloads platform={m.platform} />
          {isAdmin && <Group mt="md"><Button variant="default" onClick={() => action.show([m.id], 'provision')}>Nyinstallations-sekvens…</Button>
            <Button variant="subtle" color="red" onClick={remove}>Slet maskine</Button></Group>}
        </Panel>
      </Stack>
      {action.node}
    </SimpleGrid>
  );
}

function HealthTab({ m }: { m: MachineDetail }) {
  const { isAdmin } = useMe();
  const [hours, setHours] = useState('24');
  const { data: metrics = [] } = useApi<Metric[]>(['metrics', m.id, hours], `/api/machines/${m.id}/metrics?hours=${hours}`, { refetchInterval: 60_000 });
  const ack = useAction((id: number) => post(`/api/alerts/${id}/ack`), 'Kvitteret');
  const ignore = useAction((b: { key: string; title: string; all: boolean }) => post(`/api/machines/${m.id}/ignore-alert`, b), 'Ignoreres fremover');
  const unignore = useAction((b: { key: string; all: boolean }) => del(`/api/machines/${m.id}/ignore-alert/${encodeURIComponent(b.key)}?all=${b.all}`), 'Vises igen');
  const runFix = useRunFix();
  const check = useAction(() => post('/api/machines/actions', { machineIds: [m.id], action: 'health' }), 'Helbredstjek sat i kø');
  const h = m.health; const live = h.live ?? { volumes: [], temps: {} }; const d = h.defender; const t = live.temps ?? {};
  const win = m.platform === 'windows';
  // Hoveddrevet: C: på Windows, / på Linux/Mac (ellers det mest fyldte)
  const c = live.volumes?.find((v) => win ? v.drive.startsWith('C') : v.drive === '/')
    ?? [...(live.volumes ?? [])].sort((a, b) => (b.pctUsed ?? 0) - (a.pctUsed ?? 0))[0];
  const diskLabel = `Disk ${c?.drive ?? (win ? 'C:' : '/')}`;
  const updLabel = win ? 'Windows Update' : m.platform === 'mac' ? 'macOS-opdateringer' : 'Systemopdateringer';
  const yn = (v: boolean | undefined, good = true) => v === undefined || v === null ? '–'
    : <Badge variant="light" color={v === good ? 'green' : 'red'}>{v ? (good ? 'Til' : 'Ja') : (good ? 'Fra' : 'Nej')}</Badge>;
  return (
    <>
      <Panel title="Lige nu" right={<Group gap="xs"><HealthCell state={m.healthState} score={m.healthScore} />{isAdmin && <Button size="xs" variant="default" onClick={() => check.mutate(undefined)}>Tjek nu</Button>}</Group>}>
        <SimpleGrid cols={{ base: 2, sm: 3, md: 5 }}>
          <Gauge value={live.cpu} label="CPU" />
          <Gauge value={live.ram} label={`RAM (${num(live.ramUsedGb, 1)}/${num(live.ramTotalGb)} GB)`} />
          <Gauge value={c?.pctUsed} label={diskLabel} />
          <Gauge value={t.cpu} label="CPU-temp." unit="°" max={105} />
          {t.gpu != null && <Gauge value={t.gpu} label="GPU-temp." unit="°" max={105} />}
        </SimpleGrid>
        <Group justify="center" gap="xs" mt="xs">
          <Text size="xs" c="dimmed">{tempSourceText(t.source)}</Text>
          {isAdmin && m.platform === 'windows' && t.source !== 'LibreHardwareMonitor' && (
            <Button size="compact-xs" variant="light" onClick={() => runFix.runTool(m.id, m.name, { id: 'temps_enable', title: 'Slå temperaturmåling til', risk: 'confirm',
              description: "Lægger LibreHardwareMonitor fast på pc'en og kører den skjult fra opstart, så CPU-, GPU- og disktemperaturer kan ses her." } as HelperFix)}>Slå temperaturmåling til</Button>
          )}
          {isAdmin && m.platform === 'mac' && t.cpu == null && t.source !== 'none' && (
            <Button size="compact-xs" variant="light" onClick={() => runFix.runTool(m.id, m.name, { id: 'temps_enable_mac', title: 'Slå temperaturmåling til (Mac)', risk: 'safe',
              description: "Slår Fleetdecks indbyggede temperaturmåling til på Mac'en (Apple silicon-sensorerne, på Intel SMC'en eller powermetrics). Intet ekstra program." } as HelperFix)}>Slå temperaturmåling til</Button>
          )}
        </Group>
      </Panel>
      <Panel title="Historik" right={<SegmentedControl size="xs" value={hours} onChange={setHours} data={[{ value: '6', label: '6 t' }, { value: '24', label: '24 t' }, { value: '168', label: '7 dage' }]} />}>
        <SimpleGrid cols={{ base: 1, md: 2 }}>
          <div><Text fw={600} size="sm" mb={4}>Belastning</Text>
            <MetricsChart data={metrics} unit="%" max={100} series={[{ name: 'cpu', label: 'CPU', color: 'blue.6' }, { name: 'ram', label: 'RAM', color: 'grape.6' }, { name: 'disk', label: diskLabel, color: 'orange.6' }]} /></div>
          <div><Text fw={600} size="sm" mb={4}>Temperatur</Text>
            <MetricsChart data={metrics} unit="°" series={[{ name: 'cpuTemp', label: 'CPU', color: 'red.6' }, { name: 'gpuTemp', label: 'GPU', color: 'teal.6' }]} /></div>
        </SimpleGrid>
      </Panel>
      <TimelinePanel machineId={m.id} />
      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <Panel title="Sikkerhed">
          {win ? <KeyValue rows={[
            ['Antivirus', <Stack gap={2}>{h.antivirus?.map((a) => <Group key={a.name} gap={6}>{a.name}{yn(a.enabled)}</Group>)}</Stack>],
            ['Defender', d ? <Group gap={6}>{yn(d.enabled)} realtid {yn(d.realtime)}<Text size="xs" c="dimmed">{d.mode}</Text></Group> : '–'],
            ['Definitioner', d?.sigAgeDays !== undefined && d?.sigAgeDays !== null ? `${d.sigAgeDays} dage gamle` : '–'],
            ['Trusler (30 dage)', d ? (d.threatsLast30Days ? <Badge color="red">{d.threatsLast30Days}</Badge> : '0') : '–'],
            ['Manipulationsbeskyttelse', yn(d?.tamper)],
            ['Firewall', <Group gap={6}>{Object.entries(h.firewall ?? {}).map(([k, v]) => <Group key={k} gap={4}>{k}{yn(v)}</Group>)}</Group>],
            ['BitLocker (C:)', h.bitLocker ?? '–'],
          ]} /> : (<>
            {(!!h.antivirus?.length || !!Object.keys(h.firewall ?? {}).length) && <KeyValue rows={[
              ...(h.antivirus?.length ? [['Antivirus', <Stack gap={2}>{h.antivirus.map((a) => <Group key={a.name} gap={6}>{a.name}{yn(a.enabled)}</Group>)}</Stack>] as [string, ReactNode]] : []),
              ...(Object.keys(h.firewall ?? {}).length ? [['Firewall', <Group gap={6}>{Object.entries(h.firewall ?? {}).map(([k, v]) => <Group key={k} gap={4}>{k}{yn(v)}</Group>)}</Group>] as [string, ReactNode]] : []),
            ]} />}
            <Text size="sm" c="dimmed">Defender, BitLocker og manipulationsbeskyttelse findes kun på Windows. Systemopdateringer og diske ses her til højre og nedenfor.</Text>
          </>)}
          <TpmSection tpm={h.tpm} encryption={h.encryption} platform={m.platform} />
        </Panel>
        <Panel title="Stabilitet og opdateringer">
          <KeyValue rows={[
            ['Nedbrud (7 dage)', h.events?.crashes7d ? <Group gap={6}><Badge color="red">{h.events.crashes7d}</Badge><Text size="xs" c="dimmed">senest {h.events.lastCrash}</Text></Group> : String(h.events?.crashes7d ?? '–')],
            ['Systemfejl (24 t)', num(h.events?.errors24h)], ['Venter på genstart', yn(live.pendingReboot, false)],
            ['Oppetid', `${num((live.uptimeH ?? 0) / 24, 1)} dage`],
            [updLabel, h.updates?.pending === undefined || h.updates?.pending === null ? '–'
              : h.updates.pending ? <Badge color="yellow" variant="light">{h.updates.pending} venter</Badge> : <Badge color="green" variant="light">Opdateret</Badge>],
            ['Seneste opdatering', h.lastUpdate],
          ]} />
          {!!h.updates?.titles?.length && <Stack gap={2} mt="xs">{h.updates.titles.map((x) => <Text key={x} size="xs" c="dimmed">• {x}</Text>)}</Stack>}
        </Panel>
      </SimpleGrid>
      <EncryptionPanel m={m} isAdmin={isAdmin} />
      <DiskPanel disks={h.disks} />
      <BatteryPanel batteries={h.batteries} />
      <ChangeLogPanel machineId={m.id} platform={m.platform} />
      <Panel title="Alarmer"><AlertList alerts={m.alertList.filter((a) => !a.acknowledged)} onAck={isAdmin ? (id) => ack.mutate(id) : undefined} onFix={isAdmin ? runFix.fixAlert : undefined}
        onIgnore={isAdmin ? (a, all) => ignore.mutate({ key: a.key, title: a.title, all }) : undefined} /></Panel>
      {m.ignoredAlerts.length > 0 && (
        <Panel title="Ignorerede alarmer">
          <Text size="xs" c="dimmed" mb="xs">Giver ingen alarm, besked eller fradrag i helbredet, og Hjælperen prøver ikke at rette dem.</Text>
          <Stack gap={6}>{m.ignoredAlerts.map((x) => (
            <Group key={`${x.all}-${x.key}`} justify="space-between" wrap="nowrap">
              <Group gap={6}><Text size="sm">{x.title}</Text><Badge size="xs" variant="light" color="gray">{x.all ? 'alle maskiner' : 'denne maskine'}</Badge>
                <Text size="xs" c="dimmed">af {x.by} {ago(x.at)}</Text></Group>
              {isAdmin && <Button size="compact-xs" variant="default" onClick={() => unignore.mutate({ key: x.key, all: x.all })}>Vis igen</Button>}
            </Group>))}
          </Stack>
        </Panel>
      )}
      {runFix.node}
    </>
  );
}

function ComplianceTab({ m, onInstall, onEnforce }: { m: MachineDetail; onInstall: (pid: number) => void; onEnforce: () => void }) {
  const { isAdmin } = useMe();
  return (
    <Panel title={`Profil: ${m.profile ?? 'ingen'}`} right={isAdmin && m.profileId ? <Button size="xs" variant="default" onClick={onEnforce}>Gennemtving nu</Button> : undefined} p={0}>
      {m.complianceItems.length ? (
        <ScrollArea type="auto"><Table highlightOnHover miw={600}><Table.Thead><Table.Tr><Table.Th>Pakke</Table.Th><Table.Th>Krav</Table.Th><Table.Th>Status</Table.Th><Table.Th>Seneste job</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>{m.complianceItems.map((i) => (
            <Table.Tr key={i.packageId}><Table.Td>{i.name} <Text span size="xs" c="dimmed">{i.type}</Text></Table.Td><Table.Td>{INTENT[i.intent]}</Table.Td>
              <Table.Td><StatusBadge map={COMP} value={i.state} /></Table.Td>
              <Table.Td>{i.lastJob ? <Group gap={6}><StatusBadge map={JOB} value={i.lastJob.status} /><Text size="xs" c="dimmed">{ago(i.lastJob.finished ?? i.lastJob.created)}</Text></Group> : '–'}</Table.Td>
              <Table.Td>{isAdmin && i.intent !== 'uninstall' && !['ok', 'installed', 'pending'].includes(i.state) && <Button size="compact-xs" variant="light" onClick={() => onInstall(i.packageId)}>Installér</Button>}</Table.Td>
            </Table.Tr>))}</Table.Tbody></Table></ScrollArea>
      ) : <Empty>{m.profileId ? 'Profilen har ingen pakker' : 'Maskinen har ingen profil. Vælg en under Overblik.'}</Empty>}
    </Panel>
  );
}

/** Ikon for et program: det rigtige fra maskinen, ellers et bogstav i en farve ud fra navnet. */
function ProgIcon({ s }: { s: Software }) {
  const [broken, setBroken] = useState(false);
  if (s.iconHash && !broken) return <img className="fd-prog-icon" src={`/api/icons/${s.iconHash}`} alt="" loading="lazy" onError={() => setBroken(true)} />;
  let h = 0; for (const ch of s.name) h = (h * 31 + ch.charCodeAt(0)) % 360;
  const letters = s.name.replace(/[^\p{L}\p{N} ]/gu, '').split(' ').filter(Boolean).slice(0, 2).map((w: string) => w[0]).join('').toUpperCase() || '?';
  return <span className="fd-prog-icon" style={{ background: `hsl(${h} 55% 42%)` }}>{letters}</span>;
}

const PAGE = 300;

function SoftwareTab({ m }: { m: MachineDetail }) {
  const [q, setQ] = useState('');
  const [scope, setScope] = useState('all');
  const [shown, setShown] = useState(PAGE);
  const { isAdmin } = useMe();
  const removeOne = useAction((s: Software) => post('/api/machines/actions', { machineIds: [m.id], action: 'remove_app', params: { name: s.name } }),
    'Afinstallationen er sat i gang');
  const askRemove = (s: Software) => modals.openConfirmModal({
    title: `Afinstallér ${s.name}?`, labels: { confirm: 'Afinstallér', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    children: <Text size="sm">{s.name} fjernes fra {m.name} uden spørgsmål. Programmet lukkes først, hvis det kører.
      {m.platform !== 'windows' && ' Fjerner det også andre pakker, eller er det en systempakke, stopper agenten og fortæller hvorfor.'}</Text>,
    onConfirm: () => removeOne.mutate(s),
  });
  const upgradeOne = useAction((u: Upgrade) => post('/api/machines/actions', { machineIds: [m.id], action: 'upgrade', params: { wingetId: u.wingetId, name: u.name } }),
    'Opdateringen er sat i gang');
  // opdateringer pr. program: samme navn, eller programnavnet begynder med opdateringens navn (winget vs. registreringsdatabasen)
  const upFor = (s: Software) => {
    const n = s.name.toLowerCase();
    return m.upgradeList.find((u) => u.name.toLowerCase() === n || u.wingetId.toLowerCase() === n || (u.name.length >= 4 && n.startsWith(u.name.toLowerCase())));
  };
  const withUp = m.software.filter((s) => upFor(s)).length;
  const games = m.software.filter((s) => s.scope === 'steam').length;
  const ql = q.toLowerCase();
  const rows = m.software.filter((s) => (scope === 'all' || (scope === 'updates' ? !!upFor(s) : scope === 'steam' ? s.scope === 'steam' : s.scope !== 'steam'))
    && (!q || `${s.name} ${s.publisher ?? ''} ${s.description ?? ''} ${s.installPath ?? ''}`.toLowerCase().includes(ql)));
  return (
    <>
      <Group mb="md">
        <TextInput leftSection={<IconSearch size={16} />} placeholder={`Søg i ${m.software.length} programmer…`} value={q} onChange={(e) => { setQ(e.currentTarget.value); setShown(PAGE); }} w={{ base: '100%', sm: 300 }} />
        <SegmentedControl size="xs" value={scope} onChange={(v) => { setScope(v); setShown(PAGE); }} data={[
          { value: 'all', label: `Alle (${m.software.length})` }, { value: 'updates', label: `Kan opdateres (${withUp})` },
          { value: 'apps', label: 'Programmer' }, ...(games ? [{ value: 'steam', label: `Steam-spil (${games})` }] : [])]} />
      </Group>
      <Panel p={0}>
        <div className="fd-prog fd-prog-head"><span /><span>Program</span><span>Installeret i</span><span>Version</span><span /></div>
        {rows.slice(0, shown).map((s, i) => {
          const up = upFor(s);
          const path = s.installPath ?? s.exe;
          return (
            <div className="fd-prog" key={`${s.name}-${i}`}>
              <ProgIcon s={s} />
              <div style={{ minWidth: 0 }}>
                <Group gap={6} wrap="nowrap">
                  <Text fw={650} size="sm" lineClamp={1}>{s.name}</Text>
                  {up ? <Badge size="xs" color="orange" variant="light">{up.available ? `${up.available} klar` : 'kan opdateres'}</Badge> : null}
                  {s.scope && s.scope !== 'machine' && <Badge size="xs" variant="outline" color="gray">{({ user: 'bruger', steam: 'Steam', store: 'Store' } as Record<string, string>)[s.scope] ?? s.scope}</Badge>}
                </Group>
                <Text size="xs" c="dimmed" lineClamp={1} title={s.description ?? undefined}>{s.description ?? s.publisher ?? '\u00A0'}</Text>
              </div>
              <div className="fd-prog-path">{path ? <Code title={s.exe ?? path}>{path}</Code> : <Text size="xs" c="dimmed">–</Text>}</div>
              <Text size="sm" className="fd-tabnum" lineClamp={1} title={s.version}>{s.version ?? '–'}</Text>
              <Group gap={6} justify="flex-end" wrap="nowrap">
                {up && isAdmin && up.wingetId && <Button size="compact-xs" variant="light" color="orange" loading={upgradeOne.isPending} onClick={() => upgradeOne.mutate(up)}>Opdatér</Button>}
                {path && <CopyButton value={s.exe ?? path}>{({ copied, copy }) => (
                  <Button size="compact-xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér sti'}</Button>)}</CopyButton>}
                {isAdmin && s.canRemove !== false && (
                  <Tooltip label="Afinstallér"><Button size="compact-xs" variant="subtle" color="red" px={6} onClick={() => askRemove(s)} aria-label={`Afinstallér ${s.name}`}>
                    <IconTrash size={14} /></Button></Tooltip>)}
              </Group>
            </div>
          );
        })}
        {rows.length > shown && <Group justify="center" p="sm"><Button variant="subtle" size="xs" onClick={() => setShown(shown + PAGE)}>Vis {Math.min(PAGE, rows.length - shown)} mere ({rows.length - shown} tilbage)</Button></Group>}
        {!rows.length && <Empty>Intet fundet</Empty>}
      </Panel>
    </>
  );
}

const UPDATE_TYPES = ['upgrade', 'windows_update', 'wu_hide'];
const ACTIVE = ['pending', 'running', 'waiting_user', 'restarting'];

/** Status for ét program, mens "Opdatér alt" kører: markeres, når agenten er i gang med netop det. */
function InAllRun({ job, name, id }: { job: JobRow; name: string; id: string }) {
  const p = useJobProgress(job.id, job.progress, job.step);
  const now = job.status === 'running' && !!p.step && (p.step.includes(name) || p.step.includes(`: ${id}`));
  // Et opdateret program forsvinder fra listen med det samme. Står det her stadig, efter at det var i gang, fejlede det eller blev sprunget over.
  const seen = useRef(false);
  if (now) seen.current = true;
  if (now) return <Badge size="sm" color="blue" variant="light">Opdateres nu</Badge>;
  if (seen.current) return <Badge size="sm" color="orange" variant="light" title="Se resultatet, når Opdatér alt er færdig">Fejlede – sprunget over</Badge>;
  return <Text size="xs" c="dimmed">Venter i "Opdatér alt"</Text>;
}


/** Kan ikke installeres (agenten meldte en fejl, det ikke hjælper at prøve igen): grunden, Skjul og et "Prøv alligevel" (fx efter en BIOS-opdatering). */
function BlockedControl({ u, isAdmin, platform, busy, onRetry, onHide }: {
  u: Upgrade; isAdmin: boolean; platform: string; busy: boolean; onRetry: () => void; onHide: () => void;
}) {
  return (
    <Group gap={4} wrap="nowrap">
      <Tooltip label={u.blocked} multiline w={300}><Badge size="sm" color="red" variant="light" leftSection={<IconBan size={11} />}>Kan ikke installeres</Badge></Tooltip>
      {isAdmin && canHide(u.wingetId, platform) && <Tooltip label="Skjul – kan vises igen"><Button size="compact-xs" variant="subtle" color="gray" loading={busy} onClick={onHide}><IconEyeOff size={14} /></Button></Tooltip>}
      {isAdmin && <Tooltip label="Prøv alligevel – fx efter en BIOS-opdatering"><Button size="compact-xs" variant="subtle" color="gray" onClick={onRetry}>Prøv igen</Button></Tooltip>}
    </Group>
  );
}

/** Hvor en opdatering kommer fra (præfikset i id'et, sat af agenten): den dybe scanning ud over winget, brew og pakkerne. */
const UPGRADE_SOURCES: [string, string, string][] = [
  ['wu:', 'Windows Update', 'blue'], ['office:', 'Office', 'orange'], ['gpu:', 'Grafikdriver', 'red'], ['chipset:', 'Chipset-driver', 'teal'], ['fwupd:', 'Firmware', 'grape'],
  ['mas:', 'App Store', 'cyan'], ['cask:', 'Homebrew', 'yellow'], ['macos:', 'macOS', 'gray'],
];
const upgradeSource = (id: string) => { const s = UPGRADE_SOURCES.find(([p]) => id.startsWith(p)); return s ? { label: s[1], color: s[2] } : null; };

function UpgradesTab({ m, onUpgrade, onUpgradeAll, onShutdownWhenDone }: {
  m: MachineDetail; onUpgrade: (id: string, name: string) => void; onUpgradeAll: () => void; onShutdownWhenDone: () => void;
}) {
  const { isAdmin } = useMe();
  const [open, setOpen] = useState<number | null>(null);
  const updates = m.jobs.filter((j) => UPDATE_TYPES.includes(j.type));   // nyeste først
  const active = updates.filter((j) => ACTIVE.includes(j.status)).reverse();
  const last = updates.find((j) => !ACTIVE.includes(j.status));
  const all = active.find((j) => j.type === 'upgrade' && !j.wingetId);
  const hide = useHideUpdates(m.id);
  return (
    <>
      <BiosMachineSection m={m} />
      <Panel title="Status" right={isAdmin && <Group gap="xs">
        {m.platform === 'windows' && <Button size="xs" variant="default" leftSection={<IconPlayerStop size={14} />} onClick={onShutdownWhenDone}>Sluk når færdig</Button>}
        <Button size="xs" variant="light" disabled={!!all} onClick={onUpgradeAll}>
          {all ? 'Opdatér alt er i gang' : `Opdatér alt${m.upgrades ? ` (${m.upgrades})` : ''}`}</Button></Group>}>
        {active.length ? (
          <Stack gap="sm">{active.map((j) => (
            <Group key={j.id} gap="sm" wrap="nowrap" align="flex-start" style={{ cursor: 'pointer' }} onClick={() => setOpen(j.id)}>
              <StatusBadge map={JOB} value={j.status} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" fw={500}>{j.title}</Text>
                {j.status === 'running' ? <JobLive job={j} /> : <Text size="xs" c="dimmed">{j.status === 'waiting_user' ? 'Venter på brugeren' : `I kø – starter, når maskinen henter sine jobs (oprettet ${ago(j.created)})`}</Text>}
                {j.started && <Text size="xs" c="dimmed">startet {ago(j.started)}</Text>}
              </div>
            </Group>))}
          </Stack>
        ) : <Text size="sm" c="dimmed">Ingen opdateringer kører lige nu.</Text>}
        {last && (
          <Group gap={6} mt="sm" style={{ cursor: 'pointer' }} onClick={() => setOpen(last.id)}>
            <Text size="sm" c="dimmed">Sidst:</Text><Text size="sm">{last.title}</Text><StatusBadge map={JOB} value={last.status} />
            <Text size="sm" c="dimmed">{ago(last.finished ?? last.created)} · vis resultat</Text>
          </Group>
        )}
      </Panel>
      {!all && m.upgradeAll && <SkippedPanel m={m} result={m.upgradeAll} isAdmin={isAdmin} queued={(id) => active.some((j) => j.wingetId === id)}
        onUpgrade={onUpgrade} onOpen={setOpen} />}
      <BiosUpgradeRow m={m} />
      <Panel p={0}>
        {m.upgradeList.length ? (
          <ScrollArea type="auto"><Table highlightOnHover miw={600}><Table.Thead><Table.Tr><Table.Th>Program</Table.Th><Table.Th>Id</Table.Th><Table.Th>Nu</Table.Th><Table.Th>Ny</Table.Th><Table.Th>Status</Table.Th></Table.Tr></Table.Thead>
            <Table.Tbody>{m.upgradeList.map((u) => {
              const job = active.find((j) => j.wingetId === u.wingetId);
              return (
                <Table.Tr key={u.wingetId}><Table.Td><Group gap={6} wrap="nowrap">{u.name}{upgradeSource(u.wingetId) && <Badge size="xs" variant="light" color={upgradeSource(u.wingetId)!.color}>{upgradeSource(u.wingetId)!.label}</Badge>}{u.advice && <NotRecommended advice={u.advice} />}</Group></Table.Td>
                  <Table.Td><Code>{u.wingetId.startsWith('wu:') ? u.wingetId.slice(0, 15) + '…' : u.wingetId}</Code></Table.Td><Table.Td><Code>{u.current}</Code></Table.Td><Table.Td><Code fw={700}>{u.available}</Code></Table.Td>
                  <Table.Td w={260}>
                    {u.locked ? <LockControl machineId={m.id} machineName={m.name} wingetId={u.wingetId} name={u.name} locked={u.locked} compact />
                      : job ? (job.status === 'running' ? <JobLive job={job} w={200} /> : <Badge size="sm" color="gray" variant="light">I kø</Badge>)
                      : u.blocked ? <BlockedControl u={u} isAdmin={isAdmin} platform={m.platform} busy={hide.isPending}
                          onRetry={() => onUpgrade(u.wingetId, u.name)} onHide={() => askHide([u.name], [u.wingetId], () => hide.mutate({ ids: [u.wingetId], hide: true }))} />
                      : all ? <InAllRun job={all} name={u.name} id={u.wingetId} />
                      : isAdmin && <Group gap={4} wrap="nowrap"><Button size="compact-xs" variant="light" onClick={() => isAmdDriver(u.wingetId) ? askAmdDriver(u, () => onUpgrade(u.wingetId, u.name))
                        : onUpgrade(u.wingetId, u.name)}>{u.wingetId.startsWith('fwupd:') ? 'Opdatér firmware' : isAmdDriver(u.wingetId) ? 'Installér driver' : 'Opdatér'}</Button>
                          {canHide(u.wingetId, m.platform) ? <Tooltip label={u.wingetId.startsWith('wu:') ? 'Skjul i Windows Update – kan vises igen' : 'Skjul – kan vises igen'}><Button size="compact-xs" variant="subtle" color="gray" loading={hide.isPending}
                            onClick={() => askHide([u.name], [u.wingetId], () => hide.mutate({ ids: [u.wingetId], hide: true }))}><IconEyeOff size={14} /></Button></Tooltip> : null}
                          <LockControl machineId={m.id} machineName={m.name} wingetId={u.wingetId} name={u.name} locked={null} /></Group>}
                  </Table.Td></Table.Tr>);
            })}</Table.Tbody></Table></ScrollArea>
        ) : <Empty>Alle programmer er opdaterede ✔</Empty>}
        <Text size="xs" c="dimmed" p="sm">Opdateret ved sidste inventar ({ago(m.lastInventory)}). Listen opdateres af sig selv, når en opdatering er færdig.</Text>
      </Panel>
      <HiddenUpdatesPanel m={m} isAdmin={isAdmin} />
      <JobModal id={open} onClose={() => setOpen(null)} />
    </>
  );
}

function ShareLink({ label, url }: { label: string; url: string }) {
  return (
    <Group wrap="nowrap" gap="xs">
      <Text size="sm" w={120} style={{ flexShrink: 0 }}>{label}</Text>
      <Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{url}</Code>
      <CopyButton value={url}>{({ copied, copy }) => <Button size="compact-sm" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
      <Button size="compact-sm" variant="subtle" component="a" href={url} target="_blank">Åbn</Button>
    </Group>
  );
}

type Extension = { id: string; name: string; description: string; gives: string; installed: boolean; pending: boolean };

/** Udvidelser: ekstra dele, som bestemte funktioner kræver – installeres med ét klik. */
function ExtensionsPanel({ machineId, isAdmin }: { machineId: number; isAdmin: boolean }) {
  const { data = [] } = useApi<Extension[]>(['machines', machineId, 'extensions'], `/api/machines/${machineId}/extensions`, { refetchInterval: 30_000 });
  const install = useAction((id: string) => post(`/api/machines/${machineId}/extensions/${id}`), 'Udvidelsen installeres – du kan følge det under Jobs');
  if (data.length === 0) return null;
  return (
    <Panel title="Udvidelser">
      <Stack gap="sm">
        {data.map((e) => (
          <Group key={e.id} justify="space-between" wrap="nowrap" align="flex-start">
            <div style={{ minWidth: 0 }}>
              <Text size="sm" fw={600}>{e.name}</Text>
              <Text size="xs" c="dimmed">{e.description}</Text>
              <Text size="xs" c="dimmed">Giver: {e.gives}</Text>
            </div>
            {e.installed ? <Badge color="green" variant="light">Installeret</Badge>
              : e.pending ? <Badge color="blue" variant="light">Installeres…</Badge>
              : isAdmin ? <Button size="compact-sm" variant="default" loading={install.isPending && install.variables === e.id} onClick={() => install.mutate(e.id)}>Installér</Button>
              : <Badge color="gray" variant="light">Mangler</Badge>}
          </Group>
        ))}
      </Stack>
    </Panel>
  );
}
