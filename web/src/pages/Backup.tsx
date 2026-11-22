import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { Alert, Anchor, Badge, Button, Checkbox, Code, Group, Loader, Modal, NumberInput, PasswordInput, Progress, SimpleGrid, Stack, Switch, Table, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, del, get, notifyErr, notifyOk, post, put, useApi } from '../api';
import { ago, fmt } from '../format';
import { Empty, Loading, Panel } from '../components';

interface BackupSchedule { enabled: boolean; hour: number; keep: number; dir: string; extraPath: string; includeKits: boolean; includeIpad: boolean; hasPassword: boolean }
interface BackupStatus { lastOk: string | null; lastAttempt: string | null; lastError: string; lastName: string | null; alert: string | null; nextRun: string | null }
interface BackupFile { name: string; kind: string; size: number; created: string; appVersion: string; encryption: 'password' | 'local'; contents: string[]; folder: string }
interface RestorePlan { staged: string; source: string; kind: string; backupCreated: string; backupVersion: string; contents: string[] }
interface RestoreResult { ok: boolean; at: string; message: string; preRestore: string | null }
interface BackupInfo {
  schedule: BackupSchedule; status: BackupStatus; files: BackupFile[]; sizes: { kits: number; ipad: number };
  pending: RestorePlan | null; lastRestore: RestoreResult | null; dataDir: string; minPassword: number; confirmWord: string;
}
interface UploadInfo { id: string; kind: string; created: string; appVersion: string; contents: string[]; encryption: 'password' | 'local' }

const KIND: Record<string, string> = { manual: 'Manuel', auto: 'Automatisk', 'pre-migration': 'Før opdatering', 'pre-restore': 'Før gendannelse' };
const CONTENT: Record<string, string> = { db: 'database', keys: 'nøgler', extensions: 'egne udvidelser', kits: 'gendannelsessæt', 'ipad-backups': 'iPad-backups' };
const CONFIRM = 'GENDAN';

export const bytes = (n: number) => {
  if (n < 1024) return `${n} B`;
  const u = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v < 10 ? 1 : 0)} ${u[i]}`;
};
const contents = (c: string[]) => c.map((x) => CONTENT[x] ?? x).join(', ');
const fileUrl = (name: string) => `api/backup/files/${encodeURIComponent(name)}`;

function download(name: string) {
  const a = document.createElement('a');
  a.href = fileUrl(name); a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
}

/** Upload af en rå fil med fremdrift (fetch kan ikke vise fremdrift for uploads). */
function upload(url: string, file: File, onProgress: (pct: number) => void): Promise<UploadInfo> {
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest();
    x.open('POST', url.replace(/^\//, ''));
    x.setRequestHeader('Content-Type', 'application/octet-stream');
    x.setRequestHeader('X-Fleetdeck', '1');   // CSRF-beskyttelsen kræver headeren på alle ændringer
    x.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(Math.round((e.loaded / e.total) * 100)); };
    x.onload = () => {
      let d: { detail?: string; title?: string } | null = null;
      try { d = JSON.parse(x.responseText); } catch { /* ikke json */ }
      if (x.status >= 200 && x.status < 300) resolve(d as unknown as UploadInfo);
      else reject(new ApiError(x.status, d?.detail ?? d?.title ?? `Fejl ${x.status}`));
    };
    x.onerror = () => reject(new Error('Uploaden blev afbrudt'));
    x.send(file);
  });
}

/** Venter på at serveren er genstartet efter en gendannelse og genindlæser så siden. */
function useRestartWait() {
  const [state, setState] = useState<'idle' | 'waiting' | 'slow'>('idle');
  const start = () => {
    setState('waiting');
    const began = Date.now();
    const tick = async () => {
      if (Date.now() - began > 3000) {
        try { await get('/api/health'); window.location.reload(); return; } catch { /* stadig nede */ }
      }
      if (Date.now() - began > 90_000) setState('slow');
      window.setTimeout(tick, 2000);
    };
    window.setTimeout(tick, 3000);
  };
  const view = state === 'idle' ? null : (
    <Modal opened onClose={() => {}} withCloseButton={false} centered title="Gendanner Fleetdeck">
      <Stack align="center" gap="sm">
        <Loader />
        <Text size="sm" ta="center">Serveren genstarter og lægger backuppen ind. Siden genindlæses af sig selv, når den er tilbage – log så ind med en bruger fra backuppen.</Text>
        {state === 'slow' && <Alert color="yellow">Fleetdeck er ikke startet igen af sig selv. Start containeren (eller tjenesten) igen – gendannelsen lægges ind, når den starter.</Alert>}
      </Stack>
    </Modal>
  );
  return { start, view };
}

/** Bekræftelse af en gendannelse: adgangskode (hvis nødvendig) og ordet GENDAN. */
function RestoreModal({ info, onClose, submit }: {
  info: { created: string; appVersion: string; contents: string[]; encryption: 'password' | 'local'; kind: string } | null;
  onClose: () => void; submit: (password: string, confirm: string) => Promise<void>;
}) {
  const [pw, setPw] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  useEffect(() => { setPw(''); setConfirm(''); setErr(''); }, [info]);
  if (!info) return null;
  const go = async () => {
    setBusy(true); setErr('');
    try { await submit(pw, confirm); onClose(); }
    catch (e) { setErr(e instanceof ApiError && e.status === 429 ? 'For mange forsøg. Vent et par minutter.' : (e as Error).message); }
    finally { setBusy(false); }
  };
  return (
    <Modal opened onClose={onClose} title="Gendan fra backup" size="lg">
      <Stack gap="sm">
        <Text size="sm">Backup fra <b>{fmt(info.created)}</b> ({KIND[info.kind] ?? info.kind}), Fleetdeck v{info.appVersion}. Indhold: {contents(info.contents)}.</Text>
        <Alert color="red" title="Alt i Fleetdeck erstattes">
          Maskiner, brugere, indstillinger, nøgler og historik bliver som på det tidspunkt, backuppen blev taget. Ændringer siden går tabt.
          Serveren genstarter og er væk et øjeblik. De nuværende data gemmes først som en backup af typen "Før gendannelse", så du kan fortryde.
        </Alert>
        {info.encryption === 'password'
          ? <PasswordInput label="Backuppens adgangskode" value={pw} onChange={(e) => setPw(e.currentTarget.value)} autoComplete="off" data-autofocus />
          : <Text size="sm" c="dimmed">Backuppen er låst med serverens egen nøgle – der skal ingen adgangskode til her.</Text>}
        <TextInput label={`Skriv ${CONFIRM} for at bekræfte`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} autoComplete="off" />
        {err && <Text c="red" size="sm">{err}</Text>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color="red" loading={busy} disabled={confirm.trim() !== CONFIRM || (info.encryption === 'password' && !pw)} onClick={go}>Gendan og genstart</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Vælg en .fdbackup-fil, upload den og bekræft gendannelsen. Bruges både i Backup-fanen og ved første start. */
function RestoreFromFile({ base, onRestarting }: { base: string; onRestarting: () => void }) {
  const ref = useRef<HTMLInputElement>(null);
  const [pct, setPct] = useState<number | null>(null);
  const [info, setInfo] = useState<UploadInfo | null>(null);
  const pick = async (f: File | undefined) => {
    if (!f) return;
    setPct(0);
    try { setInfo(await upload(`${base}/upload`, f, setPct)); }
    catch (e) { notifyErr(e); }
    finally { setPct(null); if (ref.current) ref.current.value = ''; }
  };
  return (
    <>
      <input ref={ref} type="file" accept=".fdbackup" hidden onChange={(e) => pick(e.currentTarget.files?.[0])} />
      {pct === null
        ? <Button variant="default" onClick={() => ref.current?.click()}>Vælg backupfil (.fdbackup)…</Button>
        : <Stack gap={4}><Text size="sm">Sender filen… {pct} %</Text><Progress value={pct} animated /></Stack>}
      <RestoreModal info={info} onClose={() => setInfo(null)} submit={async (password, confirm) => {
        await post(`${base}/uploads/${info!.id}/restore`, { password, confirm });
        onRestarting();
      }} />
    </>
  );
}

/** Login-siden på en tom server: "Gendan fra backup" i stedet for at oprette den første administrator. */
export function FirstRunRestore() {
  const [open, setOpen] = useState(false);
  const wait = useRestartWait();
  return (
    <>
      <Button variant="subtle" size="xs" onClick={() => setOpen(true)}>Gendan fra backup</Button>
      <Modal opened={open} onClose={() => setOpen(false)} title="Gendan fra backup" size="md">
        <Stack gap="sm">
          <Text size="sm">Flytter du Fleetdeck til en ny server, eller er den geninstalleret? Vælg en <Code>.fdbackup</Code>-fil, så kommer maskiner, brugere, indstillinger og nøgler tilbage – agenterne fortsætter som før.</Text>
          <Text size="sm" c="dimmed">Du skal bruge den adgangskode, backuppen blev lavet med.</Text>
          <RestoreFromFile base="/api/backup/first-run" onRestarting={() => { setOpen(false); wait.start(); }} />
        </Stack>
      </Modal>
      {wait.view}
    </>
  );
}

/** Rødt banner øverst for administratorer, når backuppen fejler eller er for gammel. */
export function BackupAlertBanner() {
  const { data } = useApi<{ alert: string | null }>(['backup-alert'], '/api/backup/alert', { refetchInterval: 300_000 });
  if (!data?.alert) return null;
  return (
    <Alert color="red" mb="md" title="Backup af Fleetdeck">
      {data.alert} <Anchor component={Link} to="/indstillinger?fane=backup" size="sm">Åbn Backup</Anchor>
    </Alert>
  );
}

/** Indstillinger → Backup. */
export function BackupPanel() {
  const qc = useQueryClient();
  const { data: b } = useApi<BackupInfo>(['backup'], '/api/backup', { refetchInterval: 30_000 });
  const [f, setF] = useState<BackupSchedule & { password: string }>();
  const [busy, setBusy] = useState<string | null>(null);
  const [create, setCreate] = useState<{ pw: string; pw2: string; useStored: boolean; kits: boolean; ipad: boolean } | null>(null);
  const [restoreFile, setRestoreFile] = useState<BackupFile | null>(null);
  const wait = useRestartWait();
  const schedKey = b ? JSON.stringify(b.schedule) : '';
  useEffect(() => { if (schedKey) setF({ ...(JSON.parse(schedKey) as BackupSchedule), password: '' }); }, [schedKey]);
  if (!b || !f) return <Loading />;
  const refresh = () => qc.invalidateQueries({ queryKey: ['backup'] }).then(() => qc.invalidateQueries({ queryKey: ['backup-alert'] }));
  const run = async (key: string, fn: () => Promise<unknown>, ok?: string) => {
    setBusy(key);
    try { await fn(); if (ok) notifyOk(ok); await refresh(); } catch (e) { notifyErr(e); } finally { setBusy(null); }
  };
  const saveSchedule = (password: string | null) => run('save', async () => {
    await put('/api/backup/schedule', { ...f, password });
    setF((x) => x && { ...x, password: '' });
  }, password === '' ? 'Adgangskoden er fjernet' : 'Gemt');
  const st = b.status;
  const remove = (x: BackupFile) => modals.openConfirmModal({
    title: 'Slet backup?', children: <Text size="sm">{x.name}</Text>, labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: () => run('del', () => del(`/api/backup/files/${encodeURIComponent(x.name)}`), 'Backuppen er slettet'),
  });
  const pwOk = create && (create.useStored || (create.pw.length >= b.minPassword && create.pw === create.pw2));

  return (
    <>
      {wait.view}
      {b.lastRestore && (
        <Alert color={b.lastRestore.ok ? 'green' : 'red'} mb="md" withCloseButton onClose={() => run('clr', () => del('/api/backup/last-restore'))} title={b.lastRestore.ok ? 'Gendannet' : 'Gendannelsen fejlede'}>
          {b.lastRestore.message} {b.lastRestore.preRestore && <>De tidligere data ligger i backuppen <Code>{b.lastRestore.preRestore}</Code>.</>} <Text span size="xs" c="dimmed">({fmt(b.lastRestore.at)})</Text>
        </Alert>
      )}
      {b.pending && (
        <Alert color="yellow" mb="md" title="En gendannelse venter">
          Backuppen fra {fmt(b.pending.backupCreated)} lægges ind, næste gang Fleetdeck starter. <Button size="compact-xs" variant="subtle" color="red" onClick={() => run('cancel', () => del('/api/backup/pending'), 'Gendannelsen er annulleret')}>Annullér</Button>
        </Alert>
      )}
      {st.alert && <Alert color="red" mb="md" title="Backup">{st.alert}</Alert>}

      <Panel title="Tag backup nu" right={<Button onClick={() => setCreate({ pw: '', pw2: '', useStored: b.schedule.hasPassword, kits: false, ipad: false })}>Tag backup nu</Button>}>
        <Text size="sm" c="dimmed">
          Én fil (<Code>.fdbackup</Code>) med databasen, krypteringsnøglerne (bl.a. til agenternes signering og gemte adgangskoder) og egne udvidelser – krypteret med en adgangskode, du vælger.
          Med den kan Fleetdeck genskabes på en ny server, uden at maskinerne skal tilmeldes igen. Installationsfiler og WinSetup-mappen er ikke med.
        </Text>
      </Panel>

      <Panel title="Automatisk backup" right={<Switch label="Til" checked={f.enabled} onChange={(e) => setF({ ...f, enabled: e.currentTarget.checked })} />}>
        <Stack gap="sm">
          <Text size="sm">
            Seneste vellykkede: <b>{st.lastOk ? `${fmt(st.lastOk)} (${ago(st.lastOk)})` : 'ingen endnu'}</b>
            {st.nextRun && b.schedule.enabled && <> · næste: {fmt(st.nextRun)}</>}
          </Text>
          {st.lastError && <Text size="sm" c="red">Seneste forsøg fejlede: {st.lastError}</Text>}
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <NumberInput label="Tidspunkt (kl.)" description="Serverens tid" min={0} max={23} value={f.hour} onChange={(v) => setF({ ...f, hour: Number(v) || 0 })} />
            <NumberInput label="Behold" description="De nyeste N automatiske" min={1} max={100} value={f.keep} onChange={(v) => setF({ ...f, keep: Number(v) || 1 })} />
            <TextInput label="Mappe i datamappen" description={`${b.dataDir}/…`} value={f.dir} onChange={(e) => setF({ ...f, dir: e.currentTarget.value })} />
          </SimpleGrid>
          <TextInput label="Ekstra kopi til (valgfri)" description="Fuld sti, fx et share eller en USB-disk monteret i containeren. Samme antal gemmes dér." placeholder="/mnt/backup/fleetdeck"
            value={f.extraPath} onChange={(e) => setF({ ...f, extraPath: e.currentTarget.value })} />
          <Group gap="lg">
            <Checkbox label={`Gendannelsessæt (${bytes(b.sizes.kits)})`} checked={f.includeKits} onChange={(e) => setF({ ...f, includeKits: e.currentTarget.checked })} />
            <Checkbox label={`iPad-backups (${bytes(b.sizes.ipad)})`} checked={f.includeIpad} onChange={(e) => setF({ ...f, includeIpad: e.currentTarget.checked })} />
          </Group>
          <Group align="flex-end" gap="xs">
            <PasswordInput style={{ flex: 1 }} label="Backup-adgangskode" description={`Mindst ${b.minPassword} tegn`} placeholder={b.schedule.hasPassword ? '(gemt – skriv en ny for at skifte)' : 'Vælg en adgangskode'}
              value={f.password} onChange={(e) => setF({ ...f, password: e.currentTarget.value })} autoComplete="new-password" />
            {b.schedule.hasPassword && <Button variant="subtle" color="red" onClick={() => saveSchedule('')}>Fjern</Button>}
          </Group>
          {b.schedule.hasPassword
            ? <Alert color="blue">Adgangskoden gemmes krypteret med serverens egen nøgle, så backuppen kan køre af sig selv. <b>Skal du gendanne på en ny server</b> (fx efter et diskbrud), skal du bruge den adgangskode, du har valgt her – skriv den ned et sikkert sted, fx i en adgangskode-manager.</Alert>
            : <Alert color="yellow">Der er ikke valgt en backup-adgangskode. De automatiske backups låses derfor med serverens egen nøgle og <b>kan kun gendannes på denne server</b> – ikke på en ny server efter et diskbrud.</Alert>}
          <Group gap="xs">
            <Button loading={busy === 'save'} onClick={() => saveSchedule(f.password ? f.password : null)}>Gem</Button>
            <Button variant="default" loading={busy === 'run'} onClick={() => run('run', () => post('/api/backup/run'), 'Backuppen er taget')}>Kør nu</Button>
          </Group>
        </Stack>
      </Panel>

      <Panel title="Gemte backups" p={0}>
        {b.files.length === 0 ? <Empty>Ingen backups endnu.</Empty> : (
          <Table.ScrollContainer minWidth={760}>
            <Table verticalSpacing="xs">
              <Table.Thead><Table.Tr><Table.Th>Tidspunkt</Table.Th><Table.Th>Type</Table.Th><Table.Th>Version</Table.Th><Table.Th>Størrelse</Table.Th><Table.Th>Låst med</Table.Th><Table.Th>Indhold</Table.Th><Table.Th /></Table.Tr></Table.Thead>
              <Table.Tbody>{b.files.map((x) => (
                <Table.Tr key={x.name}>
                  <Table.Td><Text size="sm">{fmt(x.created)}</Text><Text size="xs" c="dimmed">{ago(x.created)}</Text></Table.Td>
                  <Table.Td><Badge variant="light" color={x.kind === 'auto' ? 'blue' : x.kind === 'manual' ? 'teal' : 'gray'}>{KIND[x.kind] ?? x.kind}</Badge></Table.Td>
                  <Table.Td><Text size="sm">v{x.appVersion}</Text></Table.Td>
                  <Table.Td><Text size="sm">{bytes(x.size)}</Text></Table.Td>
                  <Table.Td><Text size="sm">{x.encryption === 'password' ? 'Adgangskode' : 'Serverens nøgle'}</Text></Table.Td>
                  <Table.Td><Text size="xs" c="dimmed">{contents(x.contents)}</Text></Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap" justify="flex-end">
                      <Button size="compact-xs" variant="subtle" onClick={() => download(x.name)}>Hent</Button>
                      <Button size="compact-xs" variant="subtle" color="orange" onClick={() => setRestoreFile(x)}>Gendan</Button>
                      <Button size="compact-xs" variant="subtle" color="red" onClick={() => remove(x)}>Slet</Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        <Text size="xs" c="dimmed" p="md" pt="xs">"Før opdatering" og "Før gendannelse" laves automatisk (de 3 nyeste gemmes) og er låst med serverens egen nøgle: de kan kun gendannes på denne server.</Text>
      </Panel>

      <Panel title="Gendan fra fil">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">Upload en <Code>.fdbackup</Code>-fil. Den kontrolleres (adgangskode, at intet er ændret, og at den passer til denne version), før noget røres. Derefter genstarter serveren og lægger den ind.</Text>
          <Group><RestoreFromFile base="/api/backup" onRestarting={wait.start} /></Group>
          <Text size="xs" c="dimmed">Genstarten kræver, at containeren har en genstartspolitik (fx <Code>--restart unless-stopped</Code>). Ellers lægges backuppen ind, næste gang du starter den.</Text>
        </Stack>
      </Panel>

      <Modal opened={!!create} onClose={() => setCreate(null)} title="Tag backup nu">
        {create && (
          <Stack gap="sm">
            {b.schedule.hasPassword && <Checkbox label="Brug den gemte backup-adgangskode" checked={create.useStored} onChange={(e) => setCreate({ ...create, useStored: e.currentTarget.checked })} />}
            {!create.useStored && <>
              <PasswordInput label="Adgangskode til backuppen" description={`Mindst ${b.minPassword} tegn. Uden den kan backuppen ikke gendannes – den kan ikke nulstilles.`}
                value={create.pw} onChange={(e) => setCreate({ ...create, pw: e.currentTarget.value })} autoComplete="new-password" data-autofocus />
              <PasswordInput label="Gentag adgangskoden" value={create.pw2} onChange={(e) => setCreate({ ...create, pw2: e.currentTarget.value })} autoComplete="new-password"
                error={create.pw2 && create.pw !== create.pw2 ? 'Adgangskoderne er ikke ens' : undefined} />
            </>}
            <Checkbox label={`Gendannelsessæt (${bytes(b.sizes.kits)})`} checked={create.kits} onChange={(e) => setCreate({ ...create, kits: e.currentTarget.checked })} />
            <Checkbox label={`iPad-backups (${bytes(b.sizes.ipad)})`} checked={create.ipad} onChange={(e) => setCreate({ ...create, ipad: e.currentTarget.checked })} />
            <Text size="xs" c="dimmed">De store dele kan laves igen (nye gendannelsessæt og iPad-backups), så de er som standard ikke med.</Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setCreate(null)}>Annullér</Button>
              <Button disabled={!pwOk} loading={busy === 'create'} onClick={() => run('create', async () => {
                const r = await post<{ name: string }>('/api/backup/create', { password: create.useStored ? null : create.pw, useStored: create.useStored, includeKits: create.kits, includeIpad: create.ipad });
                setCreate(null);
                download(r.name);
              }, 'Backuppen er taget og hentes nu')}>Tag backup og hent den</Button>
            </Group>
          </Stack>
        )}
      </Modal>

      <RestoreModal info={restoreFile} onClose={() => setRestoreFile(null)} submit={async (password, confirm) => {
        await post(`/api/backup/files/${encodeURIComponent(restoreFile!.name)}/restore`, { password: password || null, confirm });
        wait.start();
      }} />
    </>
  );
}
