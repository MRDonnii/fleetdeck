import { useState } from 'react';
import { Accordion, Alert, Badge, Button, Code, Group, List, Paper, Radio, Select, SimpleGrid, Stack, Stepper, Switch, TagsInput, Text, TextInput, Title, UnstyledButton } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconDeviceTablet, IconDownload, IconHelp, IconKey, IconRefresh, IconRestore, IconShieldCheck, IconUsb } from '@tabler/icons-react';
import { get, notifyErr, post, useAction, useApi } from '../api';
import { ago, JOB } from '../format';
import { CopyButton } from '../design/copy';
import { JobLive, Loading, StatusBadge } from '../components';
import type { IosDevice, IosMac, IpadBackupSet, IpadDetail, IpadLocked, IpadRow, IpadSupervisionInfo, JobStatus } from '../types';

// ------------------------------------------------------------------------------------------ apps og begrænsninger
/** Kendte apps (bundle-id'er fra App Store). Apple skelner mellem store og små bogstaver. */
const APP_PRESETS: { id: string; name: string }[] = [
  { id: 'com.google.ios.youtube', name: 'YouTube' },
  { id: 'com.google.ios.youtubekids', name: 'YouTube Kids' },
  { id: 'com.zhiliaoapp.musically', name: 'TikTok' },
  { id: 'com.toyopagroup.picaboo', name: 'Snapchat' },
  { id: 'com.burbn.instagram', name: 'Instagram' },
  { id: 'com.facebook.Facebook', name: 'Facebook' },
  { id: 'com.facebook.Messenger', name: 'Messenger' },
  { id: 'net.whatsapp.WhatsApp', name: 'WhatsApp' },
  { id: 'ph.telegra.Telegraph', name: 'Telegram' },
  { id: 'com.hammerandchisel.discord', name: 'Discord' },
  { id: 'com.roblox.robloxmobile', name: 'Roblox' },
  { id: 'com.epicgames.FortniteGame', name: 'Fortnite' },
  { id: 'com.netflix.Netflix', name: 'Netflix' },
  { id: 'tv.twitch', name: 'Twitch' },
  { id: 'com.apple.mobilesafari', name: 'Safari' },
  { id: 'com.apple.games', name: 'Spil (Apple)' },
];
const AGES = [
  { value: '0', label: 'Ingen grænse' }, { value: '4', label: '4+' }, { value: '9', label: '9+' },
  { value: '13', label: '13+' }, { value: '16', label: '16+' }, { value: '18', label: '18+' },
];
const BUNDLE = /^[A-Za-z0-9][A-Za-z0-9-]*(\.[A-Za-z0-9-]+)+$/;

type AppsDraft = Pick<IpadDetail, 'hiddenApps' | 'allowAppInstall' | 'allowInAppPurchases' | 'allowAccountChanges' | 'ageRatingApps'>;

/** "iPad'en har version X af Y" – til fanen og opsætningen. */
export function VersionStatus({ locked }: { locked: IpadLocked }) {
  if (locked.applied === null)
    return <Text size="sm" c="dimmed">Den låste profil (v{locked.version}) er ikke installeret – eller Mac'en har ikke set iPad'en endnu.</Text>;
  if (locked.applied === locked.version) return <Text size="sm" c="green">iPad'en har den nyeste låste profil (v{locked.version}).</Text>;
  return <Text size="sm" c="orange">iPad'en har version {locked.applied || '(ukendt)'} af {locked.version} – installér den nye version via Mac'en under Opsætning.</Text>;
}

export function AppsTab({ cur, edit, ro, kind, locked }: { cur: AppsDraft; edit: (p: Partial<AppsDraft>) => void; ro: boolean; kind: IpadRow['kind']; locked: IpadLocked }) {
  const known = new Set(APP_PRESETS.map((a) => a.id));
  const toggle = (id: string) => edit({ hiddenApps: cur.hiddenApps.includes(id) ? cur.hiddenApps.filter((x) => x !== id) : [...cur.hiddenApps, id] });
  const others = cur.hiddenApps.filter((x) => !known.has(x));
  const bad = others.filter((x) => !BUNDLE.test(x));
  return (
    <Stack gap="lg">
      <Alert color="blue" variant="light">
        <Text size="sm">Disse indstillinger ligger i iPad'ens <b>låste profil</b> og virker først, når profilen er installeret på en <b>overvåget</b> iPad via Mac'en (se Opsætning).
          Hver ændring giver en ny version af profilen, som skal installeres igen.</Text>
        <VersionStatus locked={locked} />
      </Alert>
      <div>
        <Title order={5}>Skjulte apps</Title>
        <Text size="xs" c="dimmed" mb={6}>Skjules på hjemmeskærmen og kan ikke åbnes. Det virker uanset hvilket netværk iPad'en er på.</Text>
        <SimpleGrid cols={{ base: 2, sm: 3, lg: 4 }} spacing={6}>
          {APP_PRESETS.map((a) => (
            <UnstyledButton key={a.id} className="fd-svc" data-on={cur.hiddenApps.includes(a.id) || undefined} disabled={ro} onClick={() => toggle(a.id)} title={a.id}>
              <Text size="sm" truncate style={{ flex: 1 }}>{a.name}</Text>
              <Switch size="xs" checked={cur.hiddenApps.includes(a.id)} readOnly tabIndex={-1} />
            </UnstyledButton>))}
        </SimpleGrid>
        <TagsInput mt="sm" label="Andre apps (bundle-id)" description="Fx com.google.ios.youtubemusic. Store og små bogstaver skal passe præcist." placeholder="Skriv et bundle-id og tryk Enter"
          value={others} disabled={ro} error={bad.length ? `Ikke et bundle-id: ${bad.join(', ')}` : undefined}
          onChange={(v) => edit({ hiddenApps: [...cur.hiddenApps.filter((x) => known.has(x)), ...v.map((x) => x.trim()).filter(Boolean)] })} />
        <Text size="xs" c="dimmed" mt={4}>Indstillinger kan ikke skjules. Skjules Apples egne apps (fx Safari), kan anden funktionalitet holde op med at virke.</Text>
      </div>
      <div>
        <Title order={5} mb={6}>App Store og køb</Title>
        <Stack gap="xs">
          <Switch label="Må hente apps i App Store" disabled={ro} checked={cur.allowAppInstall} onChange={(e) => edit({ allowAppInstall: e.currentTarget.checked })}
            description="Slået fra: App Store skjules (også alternative markedspladser). Apps kan stadig installeres og opdateres fra Mac'en med Apple Configurator." />
          <Switch label="Køb i apps" disabled={ro} checked={cur.allowInAppPurchases} onChange={(e) => edit({ allowInAppPurchases: e.currentTarget.checked })} />
          <Switch label="Må ændre Apple-konto og andre konti" disabled={ro} checked={cur.allowAccountChanges} onChange={(e) => edit({ allowAccountChanges: e.currentTarget.checked })}
            description="Slået fra: konti kan ikke tilføjes, ændres eller logges ud. Log ind på Apple-kontoen, før profilen installeres." />
        </Stack>
      </div>
      <div>
        <Select w={220} label="Aldersgrænse for apps" data={AGES} allowDeselect={false} disabled={ro}
          value={String(cur.ageRatingApps ?? 0)} onChange={(v) => edit({ ageRatingApps: v && v !== '0' ? Number(v) : null })} />
        <Text size="xs" c="dimmed" mt={4}>Apps med en højere aldersgrænse i App Store skjules. Fra iPadOS 26.2 kan grænsen også ramme enkelte af Apples egne apps – prøv det af på iPad'en.</Text>
      </div>
      {kind === 'child' && <div>
        <Title order={5} mb={4}>Altid på børne-iPads</Title>
        <List size="sm" spacing={2}>
          <List.Item>"Slet alt indhold og indstillinger" er fjernet</List.Item>
          <List.Item>Ingen VPN (så DNS-filteret ikke kan omgås)</List.Item>
          <List.Item>Ingen andre profiler kan installeres</List.Item>
          <List.Item>iCloud Privat relæ er slået fra</List.Item>
          <List.Item>Profilen kan kun fjernes med fjernelseskoden eller fra Mac'en</List.Item>
        </List>
      </div>}
    </Stack>
  );
}

// ------------------------------------------------------------------------------------------ opsætning: låst profil og Mac'en
export function LockedSection({ r, d, can, admin }: { r: IpadRow; d: IpadDetail; can: string[]; admin: boolean }) {
  const l = d.locked;
  const [pw, setPw] = useState<string | null>(null);
  const rotate = useAction(() => post<{ password: string; version: number }>(`/api/ipads/${r.id}/locked-profile/rotate-password`), (x) => `Ny kode – profilen er nu version ${x.version}`);
  const base = document.baseURI.replace(/\/$/, '');
  const show = async () => { try { setPw((await get<{ password: string }>(`/api/ipads/${r.id}/removal-password`)).password); } catch (e) { notifyErr(e); } };
  return (
    <div>
      <Title order={5} mb={4}>Låst profil (statisk tilstand)</Title>
      <Text size="sm" c="dimmed" mb="sm">
        Uden firma og MDM kan iPad'en låses med en profil, der installeres fra en Mac med Apple Configurator. Den låser DNS-filteret, så det ikke kan slås fra,
        skjuler de valgte apps og App Store og kan kun fjernes med en kode eller fra Mac'en. Sengetid, pause, YouTube og aktivitet virker som før.
        Det kræver, at iPad'en er <b>overvåget</b> – og det sletter iPad'en én gang (se vejledningen nedenfor).
      </Text>
      <Group gap="xs" mb="xs">
        {l.ecid ? (l.supervised ? <Badge color="green" variant="light">Overvåget</Badge> : <Badge color="yellow" variant="light">Ikke overvåget</Badge>)
          : <Badge color="gray" variant="light">Ikke linket til en enhed</Badge>}
        <Badge variant="light">Profil v{l.version}</Badge>
        {l.osVersion && <Badge color="gray" variant="light">iPadOS {l.osVersion}</Badge>}
        {l.serial && <Badge color="gray" variant="light">Serienr. {l.serial}</Badge>}
        {l.deviceSeen && <Text size="xs" c="dimmed">set på Mac'en {ago(l.deviceSeen)}</Text>}
      </Group>
      <VersionStatus locked={l} />
      {!l.dns && <Alert color="yellow" mt="sm">Den offentlige DNS-adresse er ikke sat op (Indstillinger → iPads). Så kommer DNS-låsen ikke med i profilen.</Alert>}
      {admin && <Stack gap="xs" mt="sm">
        <Group gap="xs">
          <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/ipads/${r.id}/locked-profile`}>Hent profilen (v{l.version})</Button>
          {pw === null
            ? <Button variant="default" leftSection={<IconKey size={16} />} onClick={show}>Vis fjernelseskode</Button>
            : <Group gap={6}>
                <TextInput size="sm" readOnly value={pw} w={190} styles={{ input: { fontFamily: 'monospace' } }} />
                <CopyButton value={pw}>{({ copied, copy }) => <Button size="sm" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
              </Group>}
          <Button variant="subtle" leftSection={<IconRefresh size={16} />} loading={rotate.isPending} onClick={() => modals.openConfirmModal({
            title: 'Lav en ny fjernelseskode?', labels: { confirm: 'Lav ny kode', cancel: 'Annullér' },
            children: <Text size="sm">Profilen får en ny version. Den gamle kode virker på iPad'en, indtil den nye version er installeret via Mac'en.</Text>,
            onConfirm: () => rotate.mutate(undefined, { onSuccess: (x) => setPw(x.password) }),
          })}>Ny kode</Button>
        </Group>
        <Text size="xs" c="dimmed">Filen indeholder fjernelseskoden i klartekst – del den ikke, og slet den, når den er installeret. I Apple Configurator: vælg iPad'en → Tilføj → Profiler.
          På en låst børne-iPad kan der ikke installeres andre profiler, så QR-koden ovenfor virker ikke længere – nye versioner installeres via Mac'en.</Text>
      </Stack>}
      {can.includes('update') && <MacSection r={r} d={d} admin={admin} canErase={admin && can.includes('erase')} />}
      <SuperviseGuide />
    </div>
  );
}


function MacSection({ r, d, admin, canErase }: { r: IpadRow; d: IpadDetail; admin: boolean; canErase: boolean }) {
  const { data: macs } = useApi<IosMac[]>(['ios-macs'], '/api/ipads/macs', { refetchInterval: 15_000 });
  const [macId, setMacId] = useState<string | null>(null);
  const act = useAction((a: { macId: number; action: string; confirm?: string; order?: string; backupSet?: string }) => post<{ msg: string }>(`/api/ipads/${r.id}/mac-action`, a), (x) => x.msg);
  const link = useAction((ecid: string) => post<{ msg: string }>(`/api/ipads/${r.id}/link`, { ecid }), (x) => x.msg);
  if (!macs) return <Loading />;
  const mac = macs.find((m) => String(m.id) === macId) ?? macs.find((m) => m.online) ?? macs[0];
  const run = (action: string) => mac && act.mutate({ macId: mac.id, action });
  const confirm = (action: string, title: string, text: string) => modals.openConfirmModal({
    title, children: <Text size="sm">{text}</Text>, labels: { confirm: 'Start', cancel: 'Annullér' }, onConfirm: () => run(action),
  });
  const busy = !!mac?.job && ['pending', 'running'].includes(mac.job.status);
  return (
    <Paper withBorder radius="md" p="md" mt="md">
      <Group gap={8} mb="xs"><IconUsb size={18} /><Title order={6}>Via Mac'en</Title></Group>
      {macs.length === 0
        ? <Text size="sm" c="dimmed">Ingen Mac har Fleetdeck-agenten. Installér agenten på Mac'en (Hent agent) og Apple Configurator fra App Store.</Text>
        : <Stack gap="sm">
            <Group align="flex-end" gap="xs">
              <Select label="Mac" w={240} allowDeselect={false} value={mac ? String(mac.id) : null} onChange={setMacId}
                data={macs.map((m) => ({ value: String(m.id), label: m.online ? m.name : `${m.name} (offline)` }))} />
              <Button leftSection={<IconDeviceTablet size={16} />} loading={act.isPending} disabled={!mac || busy} onClick={() => run('list')}>Find tilsluttede iPads</Button>
            </Group>
            {mac?.job && <Paper withBorder radius="sm" p="xs">
              <Group gap="xs" justify="space-between" wrap="nowrap">
                <Text size="sm" fw={600} truncate>{mac.job.title}</Text>
                <Group gap={6} wrap="nowrap"><Text size="xs" c="dimmed">{ago(mac.job.finished ?? mac.job.created)}</Text><StatusBadge map={JOB} value={mac.job.status} /></Group>
              </Group>
              <JobLive job={{ id: mac.job.id, status: mac.job.status as JobStatus, progress: mac.job.progress, step: mac.job.step, stepSince: mac.job.stepSince }} />
              {mac.job.action === 'supervise' && <SuperviseProgress job={mac.job} />}
              {mac.job.output && <Code block mt={6} style={{ whiteSpace: 'pre-wrap', maxHeight: 220, overflow: 'auto' }}>{mac.job.output}</Code>}
            </Paper>}
            {mac && <Text size="xs" c="dimmed">{mac.devicesAt ? `Tilsluttede iPads, sidst tjekket ${ago(mac.devicesAt)}:` : 'Tryk "Find tilsluttede iPads", når iPad\'en er forbundet med kabel og låst op.'}</Text>}
            {mac?.devices.map((dev) => <DeviceRow key={dev.ecid} dev={dev} r={r} d={d} admin={admin} busy={busy}
              onLink={(ecid) => link.mutate(ecid)}
              onInstall={() => confirm('install_profile', `Installér låst profil v${d.locked.version}?`,
                `Profilen installeres på ${dev.name ?? 'iPad\'en'}. Den gamle version og den gamle DNS-profil fra QR-koden erstattes. iPad'en skal være låst op.`)}
              onBackup={() => run('backup')}
              canErase={canErase}
              onSupervise={() => mac && modals.open({
                title: `Overvåg ${r.name} (sletter – uden gendannelse)`, size: 'lg',
                children: <SuperviseConfirm name={r.name} device={dev.name} onStart={(confirm, order) => { modals.closeAll(); act.mutate({ macId: mac.id, action: 'supervise', confirm, order }); }} />,
              })}
              onRestore={() => mac && modals.open({
                title: `Gendan backup på ${r.name}`, size: 'lg',
                children: <RestoreConfirm ipadId={r.id} name={r.name} onStart={(confirm, backupSet) => { modals.closeAll(); act.mutate({ macId: mac.id, action: 'restore_backup', confirm, backupSet }); }} />,
              })}
              onUpdate={() => confirm('update', 'Opdatér iPadOS?', 'Mac\'en henter den nyeste iPadOS og installerer den. iPad\'en genstarter, og det kan tage op til en time. Mac\'en skal have internet. Tag gerne backup først.')} />)}
            <BackupPanel r={r} admin={admin} onHelp={mac ? () => run('cfgutil_help') : undefined} busy={busy} />
          </Stack>}
    </Paper>
  );
}

function DeviceRow({ dev, r, d, admin, busy, onLink, onInstall, onBackup, onUpdate, canErase, onSupervise, onRestore }: {
  dev: IosDevice; r: IpadRow; d: IpadDetail; admin: boolean; busy: boolean; onLink: (ecid: string) => void; onInstall: () => void; onBackup: () => void; onUpdate: () => void;
  canErase: boolean; onSupervise: () => void; onRestore: () => void;
}) {
  const mine = d.locked.ecid === dev.ecid;
  const installed = dev.profiles?.find((p) => p.identifier === d.locked.identifier);
  return (
    <Paper withBorder radius="sm" p="xs" style={mine ? { borderColor: 'var(--mantine-color-blue-5)' } : undefined}>
      <Group justify="space-between" gap="xs" align="flex-start">
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" fw={600}>{dev.name ?? 'Uden navn'} <Text span size="xs" c="dimmed">{[dev.deviceType, dev.firmware && `iPadOS ${dev.firmware}`, dev.serial && `serienr. ${dev.serial}`].filter(Boolean).join(' · ')}</Text></Text>
          <Group gap={4} mt={2}>
            {dev.supervised === true && <Badge size="xs" color="green" variant="light">Overvåget</Badge>}
            {dev.supervised === false && <Badge size="xs" color="yellow" variant="light">Ikke overvåget</Badge>}
            {dev.paired === false && <Badge size="xs" color="red" variant="light">Ikke parret – tryk "Stol på"</Badge>}
            {dev.battery !== null && <Badge size="xs" color="gray" variant="light">Batteri {dev.battery} %</Badge>}
            {installed && <Badge size="xs" variant="light">{installed.name ?? 'Låst profil'}</Badge>}
            {dev.ipadName && !mine && <Badge size="xs" color="grape" variant="light">Linket til {dev.ipadName}</Badge>}
            {mine && <Badge size="xs" color="blue" variant="light">Linket til denne iPad</Badge>}
          </Group>
        </div>
        <Group gap={6}>
          {admin && !mine && <Button size="compact-sm" variant="default" onClick={() => dev.ipadName
            ? modals.openConfirmModal({ title: 'Flyt koblingen?', children: <Text size="sm">Enheden er linket til {dev.ipadName}. Link den til {r.name} i stedet?</Text>,
                labels: { confirm: 'Link', cancel: 'Annullér' }, onConfirm: () => onLink(dev.ecid) })
            : onLink(dev.ecid)}>Link til denne iPad</Button>}
          {mine && <>
            {canErase && dev.supervised !== true && <Button size="compact-sm" color="orange" leftSection={<IconShieldCheck size={14} />} disabled={busy} onClick={onSupervise}>Overvåg (sletter – uden gendannelse)</Button>}
            {admin && <Button size="compact-sm" disabled={busy} onClick={onInstall}>Installér låst profil</Button>}
            <Button size="compact-sm" variant="default" disabled={busy} onClick={onBackup}>Tag backup</Button>
            {canErase && <Button size="compact-sm" variant="default" leftSection={<IconRestore size={14} />} disabled={busy} onClick={onRestore}>Gendan backup</Button>}
            {canErase && dev.supervised === true && <Button size="compact-sm" variant="subtle" color="orange" disabled={busy} onClick={onSupervise}>Overvåg igen</Button>}
            <Button size="compact-sm" variant="default" disabled={busy} onClick={onUpdate}>Opdatér iPadOS</Button>
            {admin && <Button size="compact-sm" variant="subtle" color="gray" onClick={() => onLink('')}>Fjern kobling</Button>}
          </>}
        </Group>
      </Group>
    </Paper>
  );
}

/** Den manuelle vej i Apple Configurator – hvis "Gør overvåget via Mac'en" ikke kan bruges. */
function SuperviseGuide() {
  const cmd = (s: string) => <Code block style={{ whiteSpace: 'pre-wrap' }}>{s}</Code>;
  return (
    <Accordion variant="contained" mt="md">
      <Accordion.Item value="guide">
        <Accordion.Control><Text size="sm" fw={600}>Manuelt med Apple Configurator (hvis "Gør overvåget via Mac'en" ikke virker – sletter iPad'en)</Text></Accordion.Control>
        <Accordion.Panel>
          <Stack gap="xs">
            <Alert color="red" variant="light">Overvågning sletter alt på iPad'en. Tag backup først, og sørg for at kende koden til Apple-kontoen.</Alert>
            <List type="ordered" size="sm" spacing={6}>
              <List.Item><b>Backup:</b> på iPad'en: Indstillinger → [navn] → iCloud → iCloud-backup → Sikkerhedskopiér nu. Eller forbind den til Mac'en og tryk "Tag backup" ovenfor.</List.Item>
              <List.Item><b>Find:</b> slå Find min iPad fra (Indstillinger → [navn] → Find), ellers kræver iPad'en Apple-kontoens kode efter sletningen.</List.Item>
              <List.Item><b>Apple Configurator:</b> installér den fra App Store på Mac'en, åbn den (også efter hver opdatering), og vælg menuen Apple Configurator → Installér automatiseringsværktøjer.</List.Item>
              <List.Item><b>Forbind:</b> sæt iPad'en til Mac'en med USB-kabel, lås den op og tryk "Stol på".</List.Item>
              <List.Item><b>Forbered:</b> vælg iPad'en → Forbered (Prepare) → Manuel konfiguration → sæt flueben i "Overvåg enheder" → MDM-server: "Tilmeld ikke i MDM" →
                Organisation: opret en ny med husstandens navn → vælg de opsætningstrin, der skal springes over → Forbered. iPad'en slettes og starter forfra.</List.Item>
              <List.Item><b>Gendan:</b> under opsætningen på iPad'en: vælg "Fra iCloud-backup" (eller i Apple Configurator: Handlinger → Gendan fra backup) og log ind på Apple-kontoen.</List.Item>
              <List.Item><b>Gem overvågningsidentiteten:</b> Apple Configurator → Indstillinger → Organisationer → vælg organisationen → tandhjulet → Eksportér overvågningsidentitet.
                Gem filen (.p12) et sikkert sted – uden den kan en låst profil kun fjernes med koden eller ved at slette iPad'en.</List.Item>
              <List.Item><b>Giv Fleetdeck identiteten</b> (kun ved manuel overvågning – med knappen bruger Fleetdeck sin egen). I Terminal på Mac'en, i mappen med .p12-filen:
                {cmd('openssl pkcs12 -in identitet.p12 -clcerts -nokeys | openssl x509 -outform DER -out supervision-cert.der\nopenssl pkcs12 -in identitet.p12 -nocerts -nodes | openssl rsa -outform DER -out supervision-key.der\nsudo install -d -m 700 /usr/local/fleetdeck/ios\nsudo install -m 600 -o root -g wheel supervision-cert.der supervision-key.der /usr/local/fleetdeck/ios/\nrm supervision-key.der')}
                <Text size="xs" c="dimmed">Med OpenSSL 3 (Homebrew) skal der måske -legacy med efter pkcs12.</Text></List.Item>
              <List.Item><b>Fleetdeck:</b> tryk "Find tilsluttede iPads" → "Link til denne iPad" → "Installér låst profil".</List.Item>
            </List>
            <Text size="xs" c="dimmed">Ændrer du indstillingerne senere, får profilen en ny version: forbind iPad'en til Mac'en og installér den igen.
              Mac'en kan ikke nå iPad'en, når den har været låst i over en time (USB-begrænset tilstand) – lås den op først.</Text>
          </Stack>
        </Accordion.Panel>
      </Accordion.Item>
    </Accordion>
  );
}

// ------------------------------------------------------------------------------------------ overvågning og backup via Mac'en
const SUPERVISE_STEPS: Record<string, { label: string; tap?: string }> = {
  backup: { label: 'Krypteret backup', tap: "Lås iPad'en op. Spørger den, så tryk \"Stol på\" og skriv koden." },
  upload: { label: 'Sendes til Fleetdeck' },
  erase: { label: "Slet iPad'en", tap: 'Rør den ikke, og tag ikke kablet ud.' },
  wait: { label: 'Genstart', tap: 'Lad den stå på "Hej"-skærmen.' },
  restore: { label: 'Gendan backuppen', tap: 'Rør den ikke – den genstarter bagefter.' },
  prepare: { label: 'Gør overvåget' },
  profile: { label: 'Låst profil' },
  verify: { label: 'Tjek' },
};
const ORDER: Record<string, string[]> = {
  restore_first: ['backup', 'upload', 'erase', 'wait', 'restore', 'prepare', 'profile', 'verify'],
  prepare_first: ['backup', 'upload', 'erase', 'wait', 'prepare', 'restore', 'profile', 'verify'],
};

/** Trinene i et kørende "Gør overvåget"-job (agenten skriver "Trin 3/8: …" i trinteksten). */
function SuperviseProgress({ job }: { job: NonNullable<IosMac['job']> }) {
  const m = /Trin (\d+)\/(\d+): ([^–]+)(?:– (.*))?/.exec(job.step ?? '');
  const n = m ? Number(m[1]) - 1 : -1;
  const done = job.status === 'success';
  const ids = ORDER[job.order === 'prepare_first' ? 'prepare_first' : 'restore_first'];
  return (
    <Stack gap={6} mt="xs">
      <Stepper active={done ? ids.length : Math.max(n, 0)} size="xs" iconSize={22} allowNextStepsSelect={false} wrap>
        {ids.map((id) => <Stepper.Step key={id} label={SUPERVISE_STEPS[id].label} />)}
      </Stepper>
      {m?.[4] && ['pending', 'running'].includes(job.status) && <Alert color="orange" variant="light" p="xs"><Text size="sm"><b>Nu:</b> {m[4]}</Text></Alert>}
    </Stack>
  );
}

/** Bekræftelse før overvågning: hvad der sker, hvad man skal gøre på iPad'en, og iPad'ens navn skal skrives. */
function SuperviseConfirm({ name, device, onStart }: { name: string; device: string | null; onStart: (confirm: string, order: string) => void }) {
  const [typed, setTyped] = useState('');
  const [order, setOrder] = useState('restore_first');
  const ok = typed.trim().toLocaleLowerCase('da') === name.trim().toLocaleLowerCase('da');
  return (
    <Stack gap="sm">
      <Alert color="red" variant="light" title="iPad'en bliver slettet">
        <Text size="sm">Fleetdeck tager først en krypteret backup og sender den til Fleetdeck. Først når backuppen er tjekket og gemt begge steder,
          slettes iPad'en, backuppen lægges tilbage, og iPad'en gøres overvåget. Det tager typisk 30–90 minutter.</Text>
      </Alert>
      <div>
        <Text size="sm" fw={600} mb={4}>Før du starter – på iPad'en{device ? ` (${device})` : ''}:</Text>
        <List size="sm" spacing={2} type="ordered">
          <List.Item>Slå <b>Find min iPad</b> fra: Indstillinger → [navn] → Find → Find min iPad. Ellers kan den ikke slettes.</List.Item>
          <List.Item>Sæt den til strøm, forbind den til Mac'en med kabel, og <b>lås den op</b>.</List.Item>
          <List.Item>Hav koden til <b>Apple-kontoen</b> klar – den skal bruges, når iPad'en er gendannet.</List.Item>
          <List.Item>Rør ikke iPad'en og tag ikke kablet ud, mens jobbet kører. Fleetdeck siger til, hvis du skal trykke på noget.</List.Item>
        </List>
      </div>
      <List size="sm" spacing={2}>
        {ORDER[order].map((id, i) => <List.Item key={id}>{i + 1}. {SUPERVISE_STEPS[id].label}{SUPERVISE_STEPS[id].tap ? ` – ${SUPERVISE_STEPS[id].tap}` : ''}</List.Item>)}
      </List>
      <Radio.Group label="Rækkefølge" value={order} onChange={setOrder}>
        <Stack gap={4} mt={4}>
          <Radio value="restore_first" label="Gendan backuppen og overvåg bagefter (anbefalet – går overvågningen galt, har iPad'en stadig sine data)" />
          <Radio value="prepare_first" label="Overvåg først og gendan bagefter (prøv denne, hvis den anbefalede ikke giver en overvåget iPad)" />
        </Stack>
      </Radio.Group>
      <TextInput label={`Skriv iPad'ens navn for at bekræfte: ${name}`} value={typed} onChange={(e) => setTyped(e.currentTarget.value)} data-autofocus
        error={typed.length > 0 && !ok ? 'Navnet passer ikke' : undefined} />
      <Group justify="flex-end">
        <Button variant="default" onClick={() => modals.closeAll()}>Annullér</Button>
        <Button color="red" leftSection={<IconShieldCheck size={16} />} disabled={!ok} onClick={() => onStart(typed, order)}>Slet og gør overvåget</Button>
      </Group>
    </Stack>
  );
}

/** Bekræftelse før gendannelse: vælg Mac'ens egen backup eller en kopi i Fleetdeck. */
function RestoreConfirm({ ipadId, name, onStart }: { ipadId: number; name: string; onStart: (confirm: string, backupSet?: string) => void }) {
  const { data: sets } = useApi<IpadBackupSet[]>(['ipad-backups', ipadId], `/api/ipads/${ipadId}/backups`);
  const [src, setSrc] = useState('mac');
  const [typed, setTyped] = useState('');
  const ok = typed.trim().toLocaleLowerCase('da') === name.trim().toLocaleLowerCase('da');
  const complete = (sets ?? []).filter((x) => x.complete);
  return (
    <Stack gap="sm">
      <Alert color="red" variant="light" title="iPad'en bliver slettet">
        <Text size="sm">Alt på iPad'en slettes og erstattes af backuppen. Slå Find min iPad fra først, og lad iPad'en være tilsluttet og låst op.</Text>
      </Alert>
      <Radio.Group label="Backup" value={src} onChange={setSrc}>
        <Stack gap={4} mt={4}>
          <Radio value="mac" label="Den seneste backup på Mac'en" />
          {complete.map((x) => <Radio key={x.set} value={x.set} label={`Fra Fleetdeck: ${fmtSet(x)}`} />)}
        </Stack>
      </Radio.Group>
      <TextInput label={`Skriv iPad'ens navn for at bekræfte: ${name}`} value={typed} onChange={(e) => setTyped(e.currentTarget.value)}
        error={typed.length > 0 && !ok ? 'Navnet passer ikke' : undefined} />
      <Group justify="flex-end">
        <Button variant="default" onClick={() => modals.closeAll()}>Annullér</Button>
        <Button color="red" leftSection={<IconRestore size={16} />} disabled={!ok} onClick={() => onStart(typed, src === 'mac' ? undefined : src)}>Slet og gendan</Button>
      </Group>
    </Stack>
  );
}

const size = (b: number) => (b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(1)} GB` : `${Math.round(b / (1 << 20))} MB`);
const fmtSet = (x: IpadBackupSet) => `${ago(x.created)} · ${size(x.bytes)}${x.firmware ? ` · iPadOS ${x.firmware}` : ''}`;

/** Backups i Fleetdeck, backup-adgangskoden og overvågningsidentiteten. */
function BackupPanel({ r, admin, onHelp, busy }: { r: IpadRow; admin: boolean; onHelp?: () => void; busy: boolean }) {
  const { data: sets } = useApi<IpadBackupSet[]>(['ipad-backups', r.id], `/api/ipads/${r.id}/backups`, { refetchInterval: 30_000 });
  const { data: sv } = useApi<IpadSupervisionInfo>(['ipad-supervision'], admin ? '/api/ipads/supervision' : null);
  const [pw, setPw] = useState<string | null>(null);
  const [org, setOrg] = useState<string | null>(null);
  const saveOrg = useAction((o: string) => post<{ organization: string }>('/api/ipads/supervision/organization', { organization: o }), (x) => `Organisation: ${x.organization}`);
  const show = async () => { try { setPw((await get<{ password: string }>('/api/ipads/backup-password')).password); } catch (e) { notifyErr(e); } };
  const last = sets?.find((x) => x.complete);
  return (
    <Stack gap="xs" mt="xs">
      <Title order={6}>Backup</Title>
      {last
        ? <Group gap={6}>
            <Text size="sm">Seneste backup i Fleetdeck: {fmtSet(last)}{last.machine ? ` fra ${last.machine}` : ''}</Text>
            {last.encrypted ? <Badge size="xs" color="green" variant="light">Krypteret</Badge> : <Badge size="xs" color="yellow" variant="light">Ikke krypteret</Badge>}
            {last.passwordVerified === true && <Badge size="xs" color="green" variant="light">Koden tjekket</Badge>}
          </Group>
        : <Text size="sm" c="dimmed">Ingen backup i Fleetdeck endnu. "Tag backup" gemmer en krypteret backup på Mac'en og en kopi i Fleetdeck.</Text>}
      {sets && sets.length > 1 && <Text size="xs" c="dimmed">Fleetdeck gemmer de {sets.filter((x) => x.complete).length} nyeste ({sets.filter((x) => x.complete).map((x) => ago(x.created)).join(', ')}).</Text>}
      {admin && <Group gap="xs" align="flex-end">
        {pw === null
          ? <Button size="xs" variant="default" leftSection={<IconKey size={14} />} onClick={show}>Vis backup-adgangskode</Button>
          : <Group gap={6}>
              <TextInput size="xs" readOnly value={pw} w={230} styles={{ input: { fontFamily: 'monospace' } }} />
              <CopyButton value={pw}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
            </Group>}
        {onHelp && <Button size="xs" variant="subtle" color="gray" leftSection={<IconHelp size={14} />} disabled={busy} onClick={onHelp}>Vis cfgutil-hjælp</Button>}
      </Group>}
      {admin && <Text size="xs" c="dimmed">Backups krypteres med husstandens backup-adgangskode (den samme via Mac og USB). Den skal bruges, hvis en backup gendannes i Finder eller Apple Configurator.</Text>}
      {admin && sv && <Group gap="xs" align="flex-end">
        <TextInput size="xs" w={220} label="Organisation (vises på iPad'en)" value={org ?? sv.organization} onChange={(e) => setOrg(e.currentTarget.value)} />
        <Button size="xs" variant="default" disabled={org === null || org.trim() === sv.organization} loading={saveOrg.isPending}
          onClick={() => org !== null && saveOrg.mutate(org, { onSuccess: () => setOrg(null) })}>Gem</Button>
        <Text size="xs" c="dimmed">{sv.identity ? `Overvågningscertifikat gyldigt til ${new Date(sv.identity.notAfter).toLocaleDateString('da-DK')} · ${sv.identity.thumbprint.slice(0, 16)}…`
          : 'Overvågningscertifikatet laves første gang, en iPad overvåges.'}</Text>
      </Group>}
      {admin && sv?.previousUsbIdentity && <Text size="xs" c="orange">Bemærk: "iPad via USB" havde tidligere sin egen overvågningsnøgle ({sv.previousUsbIdentity.slice(0, 16)}…). Mac og USB bruger nu husstandens fælles identitet ovenfor – en iPad, der blev overvåget med den gamle via USB, kan kun styres lydløst efter en sletning.</Text>}
    </Stack>
  );
}
