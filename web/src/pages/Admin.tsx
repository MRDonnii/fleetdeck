import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Paper, SegmentedControl, Switch, useMantineColorScheme } from '@mantine/core';
import { useSkin } from '../design/skin';
import { Anchor, Badge, Button, Code, FileInput, Group, Image, Modal, MultiSelect, NumberInput, PasswordInput, ScrollArea, Select, SimpleGrid, Stack, Table, Tabs, Text, TextInput } from '@mantine/core';
import { CopyButton } from '../design/copy';
import { ROLE_LABELS, UserModal } from '../design/family';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { api, del, notifyErr, notifyOk, post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { ago, fmt, STEPS } from '../format';
import type { FamilySettings, Profile, Settings, UserRow, WinSetupStatus } from '../types';
import { HaApiPanel } from '../design/power';
import { AgentDownloads, Empty, Loading, MachinePicker, PageHeader, Panel } from '../components';
import { MdmSettingsTab } from './IpadMdm';
import { NodkortPanel } from './Nodkort';
import { BiosSettingsTab } from '../design/bios';
import { SelfUpdatePanel } from '../design/selfupdate';
import { FileServerLoginPanel } from '../design/drives';
import { FILESERVER_LINK, FILESERVER_TAB } from '../design/fileserver-login';
import { BackupPanel } from './Backup';
import { SecuritySettingsForm, TwoFactorSettings } from '../design/twofa';
import { AgentSecurityTab, askRotateKey } from '../design/agentsec';
import { ProgramAccessTab } from '../design/apikeys';

// ---------------------------------------------------------------- WinSetup
export function WinSetupPage() {
  const { isAdmin } = useMe();
  const qc = useQueryClient();
  const { data: s } = useApi<WinSetupStatus>(['winsetup'], '/api/winsetup');
  const [step, setStep] = useState<string | null>(null);
  const [log, setLog] = useState<{ name: string; text: string } | null>(null);
  const imp = useAction(() => post<{ added: number }>('/api/winsetup/import'), (r) => `${r.added} nye pakker importeret`);
  if (!s) return <Loading />;
  if (!s.available) return <><PageHeader title="WinSetup" /><Panel>WinSetup-sharen er ikke monteret i containeren. Tilføj stien <Code>/mnt/user/PC-Reinstall/WinSetup</Code> → <Code>/winsetup</Code>.</Panel></>;
  const a = s.appearance;
  const upload = async (f: File | null) => {
    if (!f) return;
    const fd = new FormData(); fd.append('file', f);
    try { notifyOk(`Uploader ${f.name}…`); await api('POST', '/api/installers', fd); notifyOk('Uploadet. Tilknyt filen til en pakke under Pakker.'); qc.invalidateQueries(); } catch (e) { notifyErr(e); }
  };
  return (
    <>
      <PageHeader title="WinSetup" sub={`Sharen ${s.share}: baggrund, drivere, installationsfiler og backup`}>
        {isAdmin && <Button variant="default" onClick={() => imp.mutate(undefined)}>Importér apps.json igen</Button>}
      </PageHeader>
      <SimpleGrid cols={{ base: 1, lg: 2 }}>
        <Panel title="Baggrund og opsætning" right={isAdmin && <Button size="xs" variant="default" onClick={() => setStep('Capture')}>Gem fra en maskine…</Button>}>
          {a?.wallpaperFile ? <>
            <Image src={`api/winsetup/wallpaper?t=${encodeURIComponent(a.captured ?? '')}`} radius="md" mah={260} fit="contain" bg="dark.9" />
            <Text size="sm" c="dimmed" mt="xs">Gemt {a.captured} fra {a.capturedFrom}{a.wallpaperStyle === '22' ? ' · span over skærme' : ''}{a.lockScreen ? ' · også låseskærm' : ''}</Text>
          </> : <Empty>Ingen opsætning gemt endnu</Empty>}
        </Panel>
        <Stack gap={0}>
          <Panel title="Drivere" right={isAdmin && <Button size="xs" variant="default" onClick={() => setStep('CaptureDrivers')}>Gem fra en maskine…</Button>}>
            <Text>{s.drivers?.count ?? 0} drivere gemt {s.drivers?.date && <Text span c="dimmed">({s.drivers.date})</Text>}</Text>
          </Panel>
          <Panel title="Backup" right={isAdmin && <Button size="xs" variant="default" onClick={() => setStep('Backup')}>Tag backup af…</Button>} p={0}>
            {s.backups?.length ? <Table><Table.Tbody>{s.backups.map((b) => <Table.Tr key={b.name}><Table.Td>{b.name}</Table.Td><Table.Td>{b.date}</Table.Td>
              <Table.Td c="dimmed">{b.ageDays !== null ? `${Math.floor(b.ageDays)} dage` : ''}</Table.Td></Table.Tr>)}</Table.Tbody></Table> : <Empty>Ingen backup endnu</Empty>}
          </Panel>
        </Stack>
        <Panel title="Installationsfiler" p={0}>
          {s.installers?.length ? <Table><Table.Tbody>{s.installers.map((f) => <Table.Tr key={f.name}><Table.Td><Code>{f.name}</Code></Table.Td><Table.Td ta="right" c="dimmed">{f.sizeMb} MB</Table.Td></Table.Tr>)}</Table.Tbody></Table> : <Empty>Ingen filer</Empty>}
          {isAdmin && <FileInput m="md" placeholder="Upload .exe eller .msi" accept=".exe,.msi" onChange={upload} />}
        </Panel>
        <Panel title="Logfiler fra Run.ps1" p={0}>
          {s.logs?.length ? <ScrollArea h={300}><Table highlightOnHover><Table.Tbody>{s.logs.map((l) => (
            <Table.Tr key={l} style={{ cursor: 'pointer' }} onClick={async () => setLog({ name: l, text: await fetch(`api/winsetup/logs/${encodeURIComponent(l)}`).then((r) => r.text()) })}>
              <Table.Td><Code>{l}</Code></Table.Td></Table.Tr>))}</Table.Tbody></Table></ScrollArea> : <Empty>Ingen logfiler</Empty>}
        </Panel>
      </SimpleGrid>
      <MachinePicker opened={step !== null} onClose={() => setStep(null)} title={step ? STEPS[step] : ''}
        onRun={async (ids) => { const r = await post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: ids, action: 'winsetup', params: { step } }); notifyOk(`${r.jobsCreated} job(s) sat i kø`); }} />
      <Modal opened={log !== null} onClose={() => setLog(null)} title={log?.name} size="xl"><Code block style={{ whiteSpace: 'pre-wrap', maxHeight: '70vh', overflow: 'auto' }}>{log?.text}</Code></Modal>
    </>
  );
}

// ---------------------------------------------------------------- Log
// ---------------------------------------------------------------- Indstillinger
const NUMS: [string, string][] = [['poll_seconds', 'Agent tjekker ind hvert (sek.)'], ['health_minutes', 'Helbredstjek hvert (min.)'], ['inventory_hours', 'Fuldt inventar hver (timer)']];
const ALERTS: [string, string][] = [['alert_disk_pct', 'Diskalarm under (% fri)'], ['alert_cpu_temp', 'CPU-temp. alarm (°C)'],
  ['alert_disk_temp', 'Disk-temp. alarm (°C)'], ['alert_backup_days', 'Backup-alarm efter (dage)'], ['alert_battery_pct', 'Batterialarm under (% af ny)']];

export function SettingsPage() {
  const { isAdmin, me, refresh } = useMe();
  const qc = useQueryClient();
  const { data: s } = useApi<Settings>(['settings'], '/api/settings');
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], '/api/profiles');
  const { data: users = [] } = useApi<UserRow[]>(['users'], isAdmin ? '/api/users' : null);
  const [f, setF] = useState<Record<string, string>>({});
  const [editUser, setEditUser] = useState<{ u: UserRow | null } | null>(null);
  const [params, setParams] = useSearchParams();
  // ?fane=kontrolrum (før 3.38.0) åbner den nye fane Adgang for programmer
  const tab = (params.get('fane') ?? 'generelt').replace(/^kontrolrum$/, 'programmer');
  const setTab = (t: string) => setParams({ fane: t }, { replace: true });
  const [pw, setPw] = useState({ current: '', next: '' });
  useEffect(() => { if (s) setF(Object.fromEntries(Object.entries(s).filter(([, v]) => typeof v === 'string')) as Record<string, string>); }, [s]);
  const save = useAction(() => put('/api/settings', f), 'Indstillingerne er gemt');
  const test = useAction(() => post('/api/alerts/test-webhook'), 'Testbesked sendt');
  if (!s) return <Loading />;
  const ro = !isAdmin;
  const set = (k: string, v: string) => setF((x) => ({ ...x, [k]: v }));
  const cmd = `irm "${s.server_url}/install.ps1?key=${s.enrollKey ?? '…'}" | iex`;
  const rotate = () => askRotateKey(qc);
  return (
    <>
      <PageHeader title="Indstillinger" sub={`Fleetdeck ${s.version} · agent ${s.agentVersion || 'ikke bygget'}`}>{isAdmin && <Button loading={save.isPending} onClick={() => save.mutate(undefined)}>Gem indstillinger</Button>}</PageHeader>
      <Tabs value={tab} onChange={(t) => setTab(t ?? 'generelt')} keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="generelt">Generelt</Tabs.Tab>
          <Tabs.Tab value="beskeder">Alarmer og beskeder</Tabs.Tab>
          <Tabs.Tab value="konto">Min konto</Tabs.Tab>
          {isAdmin && <Tabs.Tab value="brugere">Brugere og sikkerhed</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="ipads">iPads</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="apple">Apple-styring</Tabs.Tab>}
          <Tabs.Tab value={FILESERVER_TAB}>Netværk og drev</Tabs.Tab>
          <Tabs.Tab value="winsetup">WinSetup</Tabs.Tab>
          {isAdmin && <Tabs.Tab value="sikkerhed">Agent-sikkerhed</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="programmer">Adgang for programmer</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="nodkort">Nødkort</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="bios">BIOS</Tabs.Tab>}
          {isAdmin && <Tabs.Tab value="backup">Backup</Tabs.Tab>}
        </Tabs.List>
        <Tabs.Panel value="generelt">
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            <Stack gap={0}>
          <LookPanel />
          <Panel title="Server og agent">
            <Stack>
              <TextInput label="Serverens adresse (som agenterne bruger)" disabled={ro} value={f.server_url ?? ''} onChange={(e) => set('server_url', e.currentTarget.value)} />
              <SimpleGrid cols={{ base: 1, sm: 3 }}>{NUMS.map(([k, l]) => <NumberInput key={k} label={l} disabled={ro} value={f[k] ?? ''} onChange={(v) => set(k, String(v))} />)}</SimpleGrid>
              <Select label="Forvalgt profil ved udrulning fra netværket (WinSetup)" description="Ved almindelig installation vælges profilen altid under installationen." clearable disabled={ro} data={profiles.map((p) => ({ value: String(p.id), label: p.name }))}
                value={f.default_profile_id || null} onChange={(v) => set('default_profile_id', v ?? '')} />
            </Stack>
          </Panel>
          <Panel title="Nye maskiner">
            <MultiSelect label="Udvidelser, der installeres automatisk" disabled={ro}
              description="Gælder maskiner, der installeres med kommandoen fra Tilføj maskine. I installationsvinduerne er de samme slået til fra start."
              data={[{ value: 'fjernskrivebord', label: 'Fjernskrivebord (RustDesk) – forbind med ét klik' }, { value: 'diskhelbred', label: 'Diskhelbred (SMART)' },
                { value: 'temperatur', label: 'Temperaturmåling (Windows og Mac)' }, { value: 'sensorer', label: 'Sensorer (Linux)' }, { value: 'winget', label: 'Programinstallation (winget)' },
                { value: 'flatpak', label: 'Flatpak (Linux)' }, { value: 'homebrew', label: 'Homebrew (Mac)' }]}
              value={(f.default_extensions ?? '').split(',').filter(Boolean)} onChange={(v) => set('default_extensions', v.join(','))} />
          </Panel>
            </Stack>
            <Stack gap={0}>
      {isAdmin && <SelfUpdatePanel />}
      {isAdmin && (
        <Panel title="Tilføj maskiner">
          <Text size="sm" c="dimmed" mb="xs">Kør i Terminal (administrator) på maskinen:</Text>
          <Group wrap="nowrap" align="stretch"><Code block style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{cmd}</Code>
            <CopyButton value={cmd}>{({ copied, copy }) => <Button onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
            <Button variant="default" color="red" onClick={rotate}>Skift tilmeldingsnøgle</Button></Group>
          {s.enroll_key_enabled === '0' && <Text size="xs" c="orange" mt="xs">Den globale tilmeldingsnøgle er slået fra – brug en engangskode under Agent-sikkerhed.</Text>}
          <Text size="xs" c="dimmed" mt="xs">Nye pc'er fra WinSetup-USB'en installerer agenten automatisk under Windows-installationen (agent.json + agent på sharen).</Text>
          <Text size="sm" fw={600} mt="md" mb={6}>Hent agenten og opsætningsprogrammet</Text>
          <AgentDownloads />
        </Panel>
      )}
            </Stack>
          </SimpleGrid>
        </Tabs.Panel>
        <Tabs.Panel value="beskeder">
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            <Stack gap={0}>
          <Panel title="Alarmer og beskeder">
            <Stack>
              <Group align="flex-end" wrap="nowrap"><TextInput style={{ flex: 1 }} label="Webhook (Discord-webhook eller ntfy, fx https://ntfy.sh/mit-emne)" disabled={ro}
                value={f.webhook_url ?? ''} onChange={(e) => set('webhook_url', e.currentTarget.value)} />
                {isAdmin && <Button variant="default" onClick={() => test.mutate(undefined)}>Test</Button>}</Group>
              <Group align="flex-end" wrap="nowrap">
                <Switch label="Advar når en maskine ikke har meldt sig" disabled={ro} checked={f.alert_offline_enabled === '1'}
                  onChange={(e) => set('alert_offline_enabled', e.currentTarget.checked ? '1' : '0')} />
                <NumberInput label="Efter (dage)" min={1} disabled={ro || f.alert_offline_enabled !== '1'} value={f.alert_offline_days ?? ''}
                  onChange={(v) => set('alert_offline_days', String(v))} w={140} />
              </Group>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>{ALERTS.map(([k, l]) => <NumberInput key={k} label={l} disabled={ro} value={f[k] ?? ''} onChange={(v) => set(k, String(v))} />)}</SimpleGrid>
            </Stack>
          </Panel>
            </Stack>
            <Stack gap={0}>
          <Panel title="Home Assistant (MQTT)" right={<Badge variant="light" color={String(s.mqttStatus ?? '').startsWith('Forbundet') ? 'teal' : String(s.mqttStatus ?? '').startsWith('Fejl') ? 'red' : 'gray'}>
            {String(s.mqttStatus ?? 'Slået fra')}</Badge>}>
            <Stack>
              <Text size="sm" c="dimmed">Hver maskine bliver en enhed i Home Assistant med sensorer (online, opdateringer, alarmer, helbred, CPU, RAM, temperatur,
                kørende job, planlagt genstart) og knapper (genstart, opdatér alt, tjek helbred, tænd, annullér genstart). Brug samme broker som Home Assistant –
                fx Mosquitto-tilføjelsen – og et login, der kun bruges af Fleetdeck. Tom adresse = slået fra.</Text>
              <SimpleGrid cols={{ base: 1, sm: 3 }}>
                <TextInput label="Broker (IP eller navn)" placeholder="fx 192.168.1.20" disabled={ro} value={f.mqtt_host ?? ''} onChange={(e) => set('mqtt_host', e.currentTarget.value)} />
                <NumberInput label="Port" disabled={ro} value={f.mqtt_port ?? '1883'} onChange={(v) => set('mqtt_port', String(v))} />
                <TextInput label="Discovery-præfiks" disabled={ro} value={f.mqtt_prefix ?? 'homeassistant'} onChange={(e) => set('mqtt_prefix', e.currentTarget.value)} />
              </SimpleGrid>
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <TextInput label="Brugernavn" disabled={ro} value={f.mqtt_user ?? ''} onChange={(e) => set('mqtt_user', e.currentTarget.value)} />
                <PasswordInput label="Adgangskode" disabled={ro} placeholder={s.hasMqttPass ? '(uændret)' : ''} value={f.mqtt_pass ?? ''} onChange={(e) => set('mqtt_pass', e.currentTarget.value)} />
              </SimpleGrid>
            </Stack>
          </Panel>
          <HaApiPanel isAdmin={isAdmin} />
            </Stack>
          </SimpleGrid>
        </Tabs.Panel>
        {isAdmin && <Tabs.Panel value="brugere">
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            <Stack gap={0}>
          {isAdmin && (
            <Panel title="Brugere" right={<Button size="xs" onClick={() => setEditUser({ u: null })}>+ Ny bruger</Button>} p={0}>
              <Table><Table.Tbody>{users.map((u) => (
                <Table.Tr key={u.id}>
                  <Table.Td><Group gap={6} wrap="nowrap"><Text fw={600} size="sm">{u.username}</Text>
                    {u.twoFactor && <Badge size="xs" variant="light" color="teal" title="Totrinsbekræftelse slået til">2FA</Badge>}</Group><Text size="xs" c="dimmed">{u.lastLogin ? `Sidst logget ind ${ago(u.lastLogin)}` : 'Aldrig logget ind'}</Text></Table.Td>
                  <Table.Td><Badge variant="light" color={u.role === 'admin' ? 'blue' : u.role === 'parent' ? 'grape' : 'gray'}>{ROLE_LABELS[u.role]}</Badge>
                    {u.role === 'parent' && <Text size="xs" c="dimmed" mt={2}>{u.ipadAll ? 'Alle iPads' : `${u.ipads.length} iPad${u.ipads.length === 1 ? '' : 's'}`}</Text>}</Table.Td>
                  <Table.Td ta="right"><Group gap={4} justify="flex-end" wrap="nowrap">
                    <Button size="compact-xs" variant="subtle" onClick={() => setEditUser({ u })}>Ret</Button>
                    {u.twoFactor && u.id !== me.id && <Button size="compact-xs" variant="subtle" color="orange" onClick={() => modals.openConfirmModal({
                      title: `Nulstil totrinsbekræftelse for ${u.username}?`,
                      children: <Text size="sm">Brug det, hvis {u.username} har mistet sin telefon og sine gendannelseskoder. {u.username} logges ud overalt,
                        huskede enheder glemmes, og totrinsbekræftelse skal sættes op igen (med det samme, hvis den er påkrævet).</Text>,
                      labels: { confirm: 'Nulstil', cancel: 'Annullér' }, confirmProps: { color: 'orange' },
                      onConfirm: async () => { try { await post(`/api/users/${u.id}/2fa/reset`); notifyOk('Totrinsbekræftelse er nulstillet'); qc.invalidateQueries(); } catch (e) { notifyErr(e); } } })}>Nulstil 2FA</Button>}
                    {u.id !== me.id && <Button size="compact-xs" variant="subtle" color="red" onClick={() => modals.openConfirmModal({
                      title: `Slet ${u.username}?`, labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
                      onConfirm: async () => { try { await del(`/api/users/${u.id}`); qc.invalidateQueries(); } catch (e) { notifyErr(e); } } })}>Slet</Button>}
                  </Group></Table.Td></Table.Tr>))}</Table.Tbody></Table>
            </Panel>
          )}
            </Stack>
            <Stack gap={0}>
          {isAdmin && <Panel title="Sikkerhed"><SecuritySettingsForm selfHasTwoFactor={me.twoFactor.enabled} onChanged={refresh} /></Panel>}
            </Stack>
          </SimpleGrid>
        </Tabs.Panel>}
        <Tabs.Panel value="konto">
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            <Stack gap={0}>
          <Panel title="Totrinsbekræftelse"><TwoFactorSettings onChanged={refresh} /></Panel>
            </Stack>
            <Stack gap={0}>
          <Panel title="Min adgangskode">
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <PasswordInput label="Nuværende" value={pw.current} onChange={(e) => setPw({ ...pw, current: e.currentTarget.value })} autoComplete="current-password" />
              <PasswordInput label="Ny (min. 8 tegn)" value={pw.next} onChange={(e) => setPw({ ...pw, next: e.currentTarget.value })} autoComplete="new-password" />
            </SimpleGrid>
            <Button mt="sm" variant="default" onClick={async () => { try { await post(`/api/users/${me.id}/password`, { current: pw.current, password: pw.next }); notifyOk('Adgangskoden er skiftet'); setPw({ current: '', next: '' }); } catch (e) { notifyErr(e); } }}>Skift adgangskode</Button>
          </Panel>
            </Stack>
          </SimpleGrid>
        </Tabs.Panel>
        {isAdmin && <Tabs.Panel value="ipads"><FamilyPanel /><PublicAccessPanel /><ReserveDnsPanel /></Tabs.Panel>}
        {isAdmin && <Tabs.Panel value="apple"><MdmSettingsTab /></Tabs.Panel>}
        <Tabs.Panel value={FILESERVER_TAB}>
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
            <FileServerLoginPanel s={s} f={f} set={set} ro={ro} />
          </SimpleGrid>
        </Tabs.Panel>
        <Tabs.Panel value="winsetup">
          <SimpleGrid cols={{ base: 1, lg: 2 }}>
          <Panel title="WinSetup-share">
            <Text size="sm" c="dimmed" mb="xs">Bruges, når agenten (som SYSTEM) skal hente eller gemme drivere på sharen.</Text>
            <Stack>
              <TextInput label="Sti" disabled={ro} value={f.share_path ?? ''} onChange={(e) => set('share_path', e.currentTarget.value)} />
              <Text size="sm">Login: {s.share_user ? <b>{s.share_user}</b> : <Text span c="orange">intet login sat</Text>} – <Anchor component={Link} to={FILESERVER_LINK} size="sm">ret under Netværk og drev</Anchor></Text>
            </Stack>
          </Panel>
          </SimpleGrid>
        </Tabs.Panel>
        {isAdmin && <Tabs.Panel value="sikkerhed"><AgentSecurityTab s={s} f={f} set={set} save={() => save.mutate(undefined)} saving={save.isPending} /></Tabs.Panel>}
        {isAdmin && <Tabs.Panel value="programmer"><ProgramAccessTab /></Tabs.Panel>}
        {isAdmin && <Tabs.Panel value="nodkort"><NodkortPanel /></Tabs.Panel>}
        {isAdmin && <Tabs.Panel value="bios"><BiosSettingsTab /></Tabs.Panel>}
        {isAdmin && <Tabs.Panel value="backup"><Stack><SiteConfigPanel /><BackupPanel /></Stack></Tabs.Panel>}
      </Tabs>
      <UserModal user={editUser?.u ?? null} opened={!!editUser} onClose={() => setEditUser(null)} />
    </>
  );
}


/** iPads: forbindelsen til AdGuard Home og den offentlige adresse til krypteret DNS. */
function FamilyPanel() {
  const { data: s } = useApi<FamilySettings>(['family-settings'], '/api/family/settings');
  const [f, setF] = useState({ adguardUrl: '', adguardUser: '', adguardPass: '', dohHost: '', watchHours: 24 });
  const [test, setTest] = useState<{ ok: boolean; steps: { ok: boolean; text: string }[] } | null>(null);
  useEffect(() => { if (s) setF({ adguardUrl: s.adguardUrl, adguardUser: s.adguardUser, adguardPass: '', dohHost: s.dohHost, watchHours: s.watchHours }); }, [s]);
  const save = useAction(() => put('/api/family/settings', f), 'Gemt');
  const run = useAction(() => post<{ ok: boolean; steps: { ok: boolean; text: string }[] }>('/api/family/test'));
  if (!s) return null;
  return (
    <Panel title="iPads – AdGuard og krypteret DNS">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">iPad-styringen bruger AdGuard Home. iPad'erne får en profil med krypteret DNS (DoH) til den offentlige adresse herunder, så reglerne også virker uden for hjemmet.</Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <TextInput label="AdGuard-adresse" placeholder="http://192.168.1.15" value={f.adguardUrl} onChange={(e) => setF({ ...f, adguardUrl: e.currentTarget.value })} />
          <TextInput label="Bruger" value={f.adguardUser} onChange={(e) => setF({ ...f, adguardUser: e.currentTarget.value })} autoComplete="off" />
          <PasswordInput label="Adgangskode" placeholder={s.hasPass ? '(gemt – udfyld for at skifte)' : ''} value={f.adguardPass} onChange={(e) => setF({ ...f, adguardPass: e.currentTarget.value })} autoComplete="new-password" />
        </SimpleGrid>
        <TextInput label="Offentlig DNS-adresse" description="Fx dns.ditdomæne.dk – peger (via Cloudflare-tunnel eller port 443) på AdGuards /dns-query" placeholder="dns.eksempel.dk" value={f.dohHost} onChange={(e) => setF({ ...f, dohHost: e.currentTarget.value })} />
        <NumberInput label="Advar, når en enhed ikke har brugt styringen i (timer)" description="Gælder enheder med advarsel slået til. Standard er 24 timer." min={1} max={720} w={320} value={f.watchHours} onChange={(v) => setF({ ...f, watchHours: Number(v) || 24 })} />
        {s.lastError && <Text size="sm" c="red">Seneste fejl: {s.lastError}</Text>}
        {test && <Stack gap={2}>{test.steps.map((x, i) => <Text key={i} size="sm" c={x.ok ? 'green' : 'red'}>{x.ok ? '✓' : '✗'} {x.text}</Text>)}</Stack>}
        <Group gap="xs"><Button onClick={() => save.mutate(undefined)} loading={save.isPending}>Gem</Button>
          <Button variant="default" loading={run.isPending} onClick={() => run.mutate(undefined, { onSuccess: setTest })}>Test forbindelsen</Button></Group>
      </Stack>
    </Panel>
  );
}

/** Udseende (gemmes i denne browser): menu i siden eller toppen, stil og tema. */
function LookPanel() {
  const { skin, setSkin, nav, setNav } = useSkin();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  return (
    <Panel title="Udseende">
      <Stack gap="sm">
        <div><Text size="sm" fw={500} mb={4}>Menu</Text>
          <SegmentedControl value={nav} onChange={(v) => setNav(v as typeof nav)} data={[{ value: 'side', label: 'I siden' }, { value: 'top', label: 'I toppen' }]} /></div>
        <div><Text size="sm" fw={500} mb={4}>Stil</Text>
          <SegmentedControl value={skin} onChange={(v) => setSkin(v as typeof skin)} data={[{ value: 'command', label: 'Command' }, { value: 'aurora', label: 'Aurora' }]} /></div>
        <div><Text size="sm" fw={500} mb={4}>Tema</Text>
          <SegmentedControl value={colorScheme} onChange={(v) => setColorScheme(v as typeof colorScheme)}
            data={[{ value: 'light', label: 'Lyst' }, { value: 'dark', label: 'Mørkt' }, { value: 'auto', label: 'Som systemet' }]} /></div>
        <Text size="xs" c="dimmed">Gemmes i denne browser – også når Fleetdeck vises inde i Kontrolrum.</Text>
      </Stack>
    </Panel>
  );
}

interface PaConf { hasToken: boolean; machineId: number | null; network: string; ip: string | null; container: string; image: string; dohHost: string; service: string | null;
  reserve: boolean; reserveService: string;
  machines: { id: number; name: string; online: boolean; unraid: boolean }[] }
interface PaStatus { machine: { id: number; name: string } | null; job: { id: number; title: string; status: string; output?: string } | null;
  suggest: { id: number; status: string; ip: string | null } | null; container: { reported: boolean; found: boolean; running: boolean; state: string | null; ip: string | null } | null;
  doh: { ok: boolean; text: string } | null }

/** Opsætningsfilen: alt det, der er skrevet ind i Fleetdeck (uden adgangskoder og nøgler), som én fil – til en privat kopi. */
function SiteConfigPanel() {
  const qc = useQueryClient();
  const [file, setFile] = useState<File | null>(null);
  const [res, setRes] = useState<{ settings: number; profilesChanged: number; profilesCreated: number; machines: number; printers?: number; skipped: string[] } | null>(null);
  const load = async () => {
    if (!file) return;
    try {
      const r = await post<typeof res>('/api/site-config', JSON.parse(await file.text()));
      setRes(r); notifyOk('Opsætningen er læst ind'); qc.invalidateQueries();
    } catch (e) { notifyErr(e); }
  };
  return (
    <Panel title="Opsætningsfil">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Alt det, du har skrevet ind – indstillinger, profiler og maskinernes profil, mærker og beskyttelse – som én fil. Adgangskoder, nøgler og tokens kommer ikke med. Gem filen privat, og læs den ind igen efter en geninstallation. Indlæsningen ændrer kun det, filen nævner.</Text>
        <Group gap="xs" align="flex-end">
          <Button component="a" href="/api/site-config" variant="default">Hent opsætning</Button>
          <FileInput placeholder="Vælg opsætningsfil (.json)" accept="application/json,.json" value={file} onChange={setFile} w={260} />
          <Button onClick={load} disabled={!file}>Læs ind</Button>
        </Group>
        {res && <Text size="sm">{res.settings} indstillinger, {res.profilesChanged} profiler ændret, {res.profilesCreated} oprettet, {res.machines} maskiner{res.printers ? `, ${res.printers} printere` : ''}.{res.skipped.length ? ` Sprunget over: ${res.skipped.join(', ')}.` : ''}</Text>}
      </Stack>
    </Panel>
  );
}

type InsideStatus = { on: boolean; status: { available: boolean; running: boolean; since: string | null; connections: number; error: string | null; lines: string[] } };

/** Tunnelen kørt i selve Fleetdeck-containeren (cloudflared som underproces) – så der ikke skal være en container mere. */
function TunnelInsideRow() {
  const qc = useQueryClient();
  const { data } = useApi<InsideStatus>(['public-access-inside'], '/api/public-access/inside', { refetchInterval: 10_000 });
  if (!data) return null;
  const st = data.status;
  const set = async (on: boolean) => {
    try { await put('/api/public-access/inside', { on }); notifyOk(on ? 'Tunnelen startes i Fleetdeck' : 'Tunnelen i Fleetdeck stoppes'); qc.invalidateQueries({ queryKey: ['public-access-inside'] }); }
    catch (e) { notifyErr(e); }
  };
  return (
    <Paper withBorder p="sm" radius="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <div>
          <Text size="sm" fw={600}>Kør tunnelen i selve Fleetdeck</Text>
          <Text size="xs" c="dimmed">Ingen ekstra container: Fleetdeck starter cloudflared selv og starter den igen, hvis den stopper. Når den er forbundet her, kan containeren ovenfor stoppes med Stop. Mens Fleetdeck opdateres, er tunnelen væk i omkring et halvt minut.</Text>
        </div>
        <Switch checked={data.on} onChange={(e) => set(e.currentTarget.checked)} disabled={!st.available && !data.on} />
      </Group>
      {data.on && <Text size="sm" mt={6} c={st.connections > 0 ? 'green' : 'orange'}>{st.connections > 0 ? `✓ Forbundet til Cloudflare (${st.connections} forbindelser)` : st.error ?? 'Starter …'}</Text>}
      {!st.available && <Text size="xs" c="dimmed" mt={4}>Kræver en Fleetdeck-container med cloudflared (3.32 eller nyere).</Text>}
    </Paper>
  );
}

/** Offentlig adgang: Cloudflare-tunnel startet af Fleetdeck på en Docker-vært – så iPads' krypterede DNS virker uden for hjemmet. */
function PublicAccessPanel() {
  const qc = useQueryClient();
  const { data: c } = useApi<PaConf>(['public-access'], '/api/public-access');
  const { data: st } = useApi<PaStatus>(['public-access-status'], '/api/public-access/status', { refetchInterval: 10_000 });
  const [f, setF] = useState({ token: '', machineId: '', network: 'br0', ip: '', container: 'fleetdeck-tunnel' });
  const [probe, setProbe] = useState<PaStatus['doh']>(null);
  useEffect(() => { if (c) setF((x) => ({ ...x, machineId: c.machineId ? String(c.machineId) : '', network: c.network || 'br0', ip: c.ip ?? '', container: c.container || 'fleetdeck-tunnel' })); }, [c]);
  const body = () => ({ token: f.token || null, machineId: f.machineId ? Number(f.machineId) : null, network: f.network, ip: f.ip || null, container: f.container });
  const run = async (fn: () => Promise<unknown>, ok: string) => { try { await fn(); notifyOk(ok); setF((x) => ({ ...x, token: '' })); qc.invalidateQueries(); } catch (e) { notifyErr(e); } };
  if (!c) return null;
  const host = c.dohHost || 'dns.ditdomæne.dk';
  const rows: [string, string][] = [['Tunnel-type', 'Cloudflared'], ['Public hostname', host], ['Path', '^/dns-query'], ['Service', c.reserve ? c.reserveService : (c.service ?? 'http://<AdGuard-adresse>:80')]];
  return (
    <Panel title="Offentlig adgang (Cloudflare-tunnel)">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Så virker iPad'ernes styring også uden for hjemmet (4G, skole, venner). Fleetdeck starter selv tunnelen på den valgte server – ingen porte åbnes i routeren.</Text>
        <Text size="sm" fw={600}>1. I Cloudflare Zero Trust → Networks → Tunnels: opret en tunnel og et "public hostname" med:</Text>
        <Table withRowBorders={false}><Table.Tbody>{rows.map(([k, v]) => (
          <Table.Tr key={k}><Table.Td w={150}><Text size="sm" c="dimmed">{k}</Text></Table.Td><Table.Td><Code>{v}</Code></Table.Td>
            <Table.Td w={90}><CopyButton value={v}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton></Table.Td></Table.Tr>))}</Table.Tbody></Table>
        {c.reserve && <Text size="xs" c="dimmed">Med Reserve-DNS sender begge connectorer til navnet <Code>fleetdeck-dns</Code>, som hver server selv peger på sin egen AdGuard. Start tunnelen igen her, før hostnamet i Cloudflare ændres.</Text>}
        <Text size="xs" c="dimmed">Vælg "Docker" ved connectoren og kopiér token'et (teksten efter --token). Kør ikke kommandoen – det gør Fleetdeck. "Bot Fight Mode" for domænet skal være slået fra.</Text>
        <Text size="sm" fw={600}>2. Indsæt token'et og vælg, hvor tunnelen skal køre:</Text>
        <PasswordInput label="Tunnel-token" placeholder={c.hasToken ? '(gemt – indsæt for at skifte)' : 'eyJh…'} value={f.token} onChange={(e) => setF({ ...f, token: e.currentTarget.value.replace(/^.*--token\s+/, '').trim() })} autoComplete="off" />
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <Select label="Server" placeholder="Vælg server med Docker" value={f.machineId || null} onChange={(v) => setF({ ...f, machineId: v ?? '' })}
            data={c.machines.map((m) => ({ value: String(m.id), label: `${m.name}${m.unraid ? ' (Unraid)' : ''}${m.online ? '' : ' – offline'}` }))} />
          <TextInput label="Docker-netværk" description="fx br0 på Unraid, eller host" value={f.network} onChange={(e) => setF({ ...f, network: e.currentTarget.value })} />
          <Group align="flex-end" gap={6} wrap="nowrap"><TextInput style={{ flex: 1 }} label="Fast IP" description="Ledig adresse på netværket" value={f.ip} onChange={(e) => setF({ ...f, ip: e.currentTarget.value })} />
            <Button variant="default" onClick={() => run(async () => { await put('/api/public-access', body()); await post('/api/public-access/suggest-ip'); }, 'Leder efter en ledig adresse…')}>Find</Button></Group>
        </SimpleGrid>
        {st?.suggest?.ip && !f.ip && <Text size="sm">Forslag: <Code>{st.suggest.ip}</Code> <Button size="compact-xs" variant="subtle" onClick={() => setF({ ...f, ip: st.suggest!.ip! })}>Brug den</Button></Text>}
        <Group gap="xs">
          <Button onClick={() => run(async () => { await put('/api/public-access', body()); await post('/api/public-access/start'); }, 'Tunnelen startes på serveren')}>Gem og start tunnel</Button>
          <Button variant="default" onClick={() => run(() => put('/api/public-access', body()), 'Gemt')}>Gem</Button>
          <Button variant="default" onClick={async () => { try { setProbe((await api<PaStatus>('GET', '/api/public-access/status?probe=true')).doh); } catch (e) { notifyErr(e); } }}>Test</Button>
          <Button variant="subtle" color="red" onClick={() => run(() => post('/api/public-access/stop'), 'Tunnelen stoppes')}>Stop</Button>
        </Group>
        <TunnelInsideRow />
        {st && <Stack gap={2}>
          {st.job && <Text size="sm">Seneste: {st.job.title} – <b>{st.job.status}</b></Text>}
          {st.container?.reported && <Text size="sm" c={st.container.running ? 'green' : 'red'}>{st.container.running ? '✓ Tunnel-containeren kører' : `✗ Tunnel-containeren ${st.container.found ? `er ${st.container.state}` : 'findes ikke'}`}{st.container.ip ? ` (${st.container.ip})` : ''}</Text>}
          {(probe ?? st.doh) && <Text size="sm" c={(probe ?? st.doh)!.ok ? 'green' : 'red'}>{(probe ?? st.doh)!.ok ? '✓' : '✗'} {(probe ?? st.doh)!.text}</Text>}
        </Stack>}
      </Stack>
    </Panel>
  );
}

interface RdConf { machineId: number | null; network: string; ip: string; container: string; tunnelContainer: string; dataDir: string; apiUrl: string; enabled: boolean;
  image: string; tunnelImage: string; port: number; service: string; alias: string; primaryHttps: boolean; hasToken: boolean; tunnelMachineId: number | null;
  machines: { id: number; name: string; online: boolean; unraid: boolean; ip: string | null }[] }
interface RdStatus { machine: { id: number; name: string } | null; enabled: boolean; apiUrl: string | null; job: { id: number; title: string; status: string; output?: string; stop: boolean } | null;
  suggest: { id: number; status: string; ip: string | null } | null;
  containers: { name: string; role: string; found: boolean; running: boolean; state: string | null; ip: string | null }[];
  health: { up: boolean; lastAttempt: string; lastOk: string | null; downSince: string | null; error: string | null; lastSync: string | null; syncError: string | null;
    lastChanges: number; lastChange: string | null; primaryOk: boolean; version: string | null; alarm: boolean } | null;
  primaryError: string | null }

/** Reserve-DNS: en ekstra AdGuard + tunnel-connector på en anden server, så iPads har internet, når hovedserveren er nede. */
function ReserveDnsPanel() {
  const qc = useQueryClient();
  const { data: c } = useApi<RdConf>(['reserve-dns'], '/api/reserve-dns');
  const { data: st } = useApi<RdStatus>(['reserve-dns-status'], '/api/reserve-dns/status', { refetchInterval: 10_000 });
  const [f, setF] = useState({ machineId: '', network: 'br0', ip: '', dataDir: '', apiUrl: '' });
  useEffect(() => { if (c) setF({ machineId: c.machineId ? String(c.machineId) : '', network: c.network || 'br0', ip: c.ip ?? '', dataDir: c.dataDir ?? '', apiUrl: c.apiUrl ?? '' }); }, [c]);
  const body = () => ({ machineId: f.machineId ? Number(f.machineId) : 0, network: f.network, ip: f.ip, dataDir: f.dataDir, apiUrl: f.apiUrl });
  const run = async (fn: () => Promise<unknown>, ok: string) => { try { await fn(); notifyOk(ok); qc.invalidateQueries(); } catch (e) { notifyErr(e); } };
  if (!c) return null;
  const h = st?.health;
  const rows: [string, string][] = [['Service (Type)', 'HTTP'], ['Service (URL)', `${c.alias}:${c.port}`]];
  return (
    <Panel title="Reserve-DNS (AdGuard på en anden server)">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Går hovedserveren ned, mister iPad'erne internettet, fordi al DNS går gennem AdGuard. Med en reserve starter Fleetdeck en ekstra AdGuard
          ({c.image.split(':')[1]}) og en ekstra connector til den samme Cloudflare-tunnel på en anden Linux-maskine med Docker. Fleetdeck holder reserven i takt med
          hoved-AdGuard: iPad-reglerne, klienter, egne regler, filterlister, spærrede tjenester og DNS-indstillinger.</Text>
        {!c.hasToken && <Text size="sm" c="orange">Sæt først den offentlige adgang (tunnelen) op ovenfor – reserven bruger den samme tunnel-nøgle.</Text>}
        {c.primaryHttps && <Text size="sm" c="orange">Hoved-AdGuard bruges via https. Begge AdGuards skal svare på almindelig http på samme port, for at Cloudflare kan sende til dem med samme adresse.</Text>}
        <Text size="sm" fw={600}>1. Vælg serveren til reserven og start den:</Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <Select label="Server" placeholder="Vælg en anden server med Docker" value={f.machineId || null} onChange={(v) => setF({ ...f, machineId: v ?? '' })} clearable
            data={c.machines.filter((m) => m.id !== c.tunnelMachineId).map((m) => ({ value: String(m.id), label: `${m.name}${m.unraid ? ' (Unraid)' : ''}${m.online ? '' : ' – offline'}` }))} />
          <TextInput label="Docker-netværk" description="br0 (macvlan, med fast IP), host, eller et eget netværk" value={f.network} onChange={(e) => setF({ ...f, network: e.currentTarget.value })} />
          <Group align="flex-end" gap={6} wrap="nowrap"><TextInput style={{ flex: 1 }} label="Fast IP" description="Kan også bruges som DNS nr. 2 i routeren" value={f.ip} onChange={(e) => setF({ ...f, ip: e.currentTarget.value })} />
            <Button variant="default" onClick={() => run(async () => { await put('/api/reserve-dns', body()); await post('/api/reserve-dns/suggest-ip'); }, 'Leder efter en ledig adresse…')}>Find</Button></Group>
        </SimpleGrid>
        {st?.suggest?.ip && !f.ip && <Text size="sm">Forslag: <Code>{st.suggest.ip}</Code> <Button size="compact-xs" variant="subtle" onClick={() => setF({ ...f, ip: st.suggest!.ip! })}>Brug den</Button></Text>}
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Datamappe (valgfri)" description="Tom = /mnt/user/appdata/… på Unraid, ellers /var/lib/…" value={f.dataDir} onChange={(e) => setF({ ...f, dataDir: e.currentTarget.value })} />
          <TextInput label="Reservens AdGuard-adresse (valgfri)" description="Tom = den faste IP eller serverens IP" placeholder={`http://<IP>:${c.port}`} value={f.apiUrl} onChange={(e) => setF({ ...f, apiUrl: e.currentTarget.value })} />
        </SimpleGrid>
        <Group gap="xs">
          <Button disabled={!c.hasToken} onClick={() => run(async () => { await put('/api/reserve-dns', body()); await post('/api/reserve-dns/start'); }, 'Reserven startes på serveren')}>Gem og start reserve</Button>
          <Button variant="default" onClick={() => run(() => put('/api/reserve-dns', body()), 'Gemt')}>Gem</Button>
          {st?.enabled && <Button variant="default" onClick={() => run(() => post('/api/reserve-dns/sync'), 'Synkroniserer…')}>Synkronisér nu</Button>}
          <Button variant="subtle" color="red" onClick={() => run(() => post('/api/reserve-dns/stop'), 'Reserven stoppes')}>Stop</Button>
        </Group>
        <Text size="sm" fw={600}>2. Når reserven kører: start tunnelen igen ovenfor, og ret derefter hostnamet i Cloudflare (Networks → Tunnels → din tunnel → Public hostnames → {'{'}DNS-adressen{'}'} → Edit):</Text>
        <Table withRowBorders={false}><Table.Tbody>{rows.map(([k, v]) => (
          <Table.Tr key={k}><Table.Td w={150}><Text size="sm" c="dimmed">{k}</Text></Table.Td><Table.Td><Code>{v}</Code></Table.Td>
            <Table.Td w={90}><CopyButton value={v}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton></Table.Td></Table.Tr>))}</Table.Tbody></Table>
        <Text size="xs" c="dimmed">Begge connectorer hører til den samme tunnel og ses under "Connectors"/"Replicas" i Cloudflare. Cloudflare sender hver forespørgsel til den nærmeste
          connector og skifter selv til den anden, hvis en connector forsvinder (fx når hovedserveren er slukket). Cloudflare tjekker ikke selve AdGuard: kører en servers
          tunnel, mens dens AdGuard er stoppet, fejler de forespørgsler, der lander der. Fleetdeck læser forespørgselsloggen fra begge AdGuards, så YouTube-tid tælles med.</Text>
        {st && <Stack gap={2}>
          {st.job && <Text size="sm">Seneste: {st.job.title} – <b>{st.job.status}</b></Text>}
          {st.containers.map((x) => <Text key={x.name} size="sm" c={x.running ? 'green' : 'red'}>{x.running ? '✓' : '✗'} {x.role} ({x.name}) {x.running ? 'kører' : x.found ? `er ${x.state}` : 'findes ikke'}{x.ip ? ` (${x.ip})` : ''}</Text>)}
          {st.enabled && !st.apiUrl && <Text size="sm" c="red">✗ Fleetdeck kender ikke reservens adresse – udfyld "Reservens AdGuard-adresse"</Text>}
          {st.enabled && st.apiUrl && !h && <Text size="sm" c="dimmed">Venter på første synkronisering med {st.apiUrl}…</Text>}
          {h && (h.up
            ? <Text size="sm" c="green">✓ Reservens AdGuard {h.version ?? ''} svarer – sidst synkroniseret {h.lastSync ? ago(h.lastSync) : '–'}{h.lastChange ? `, seneste ændring ${ago(h.lastChange)} (${h.lastChanges})` : ''}</Text>
            : <Text size="sm" c="red">✗ Reservens AdGuard svarer ikke{h.downSince ? ` (siden ${fmt(h.downSince)})` : ''}: {h.error}{h.alarm ? ' – der er slået alarm' : ''}</Text>)}
          {h?.up && !h.primaryOk && <Text size="sm" c="orange">! Hoved-AdGuard kunne ikke læses – kun iPad-reglerne er lagt på reserven{st.primaryError ? ` (${st.primaryError})` : ''}</Text>}
          {h?.syncError && <Text size="sm" c="orange">! En del af synkroniseringen fejlede: {h.syncError}</Text>}
          {st.job?.status === 'failed' && st.job.output && <Code block>{st.job.output.slice(-1200)}</Code>}
        </Stack>}
      </Stack>
    </Panel>
  );
}
