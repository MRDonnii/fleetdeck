import { useEffect, useRef, useState } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Modal, NumberInput, PasswordInput, Progress, ScrollArea, SegmentedControl, Select, SimpleGrid, Stack, Switch, Text, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { ApiError, notifyErr, notifyOk, post, put, useApi } from '../api';
import { ago, fmt } from '../format';
import { Panel } from '../components';

type Release = { version: string; name: string; notes: string; prerelease: boolean; published: string | null; url: string | null; newer: boolean; same: boolean; image: string | null };
type UpdateStatus = {
  current: string; inDocker: boolean;
  config: { source: 'github' | 'local'; repo: string; channel: 'stable' | 'beta'; image: string; localDir: string; auto: boolean; autoHour: number;
    machineId: number | null; container: string; healthMinutes: number; hasToken: boolean };
  checkedAt: string | null; error: string | null; latest: Release | null; updateAvailable: boolean; releases: Release[];
  /** onHost: containerens navn findes på værten, når opdateringen starter; note: hvorfor. */
  target: { machineId: number; machine: string; container: string; online: boolean; detected: boolean; onHost: boolean; note: string | null } | null;
  /** Hvorfor opdateringen ikke kan starte (fx ingen Docker-vært fundet) – vises i dialogen, før man trykker Opdatér nu. */
  problem: string | null;
  candidates: { machineId: number; machine: string; container: string; image: string; score: number; why: string }[];
  machines: { id: number; name: string; online: boolean; unraid: boolean }[];
  state: { jobId: number; from: string; to: string; by: string; started: string; auto: boolean; result: string | null } | null;
  job: { id: number; title: string; status: string; progress: number | null; step: string; created: string; started: string | null; finished: string | null; output: string; machine: string | null } | null;
};
type Health = { ok: boolean; version: string; release?: string };

const ACTIVE = ['pending', 'running', 'waiting_user'];
const STATUS: Record<string, [string, string]> = {
  pending: ['Venter på agenten', 'gray'], running: ['I gang', 'blue'], success: ['Opdateret', 'teal'], failed: ['Fejlede', 'red'],
  cancelled: ['Annulleret', 'gray'], expired: ['Udløbet', 'orange'], waiting_user: ['Venter', 'gray'],
};

async function health(): Promise<Health | null> {
  try {
    const r = await fetch('api/health', { cache: 'no-store' });
    return r.ok ? (await r.json()) as Health : null;
  } catch { return null; }
}

/** Indstillinger → Generelt: version, udgivelser og opdatering af selve Fleetdeck (kun admin). */
export function SelfUpdatePanel() {
  const qc = useQueryClient();
  const [restarting, setRestarting] = useState(false);
  const { data: st, error } = useApi<UpdateStatus>(['update'], '/api/update', { refetchInterval: restarting ? undefined : 4000 });
  const [f, setF] = useState<UpdateStatus['config'] & { token: string }>();
  const [confirm, setConfirm] = useState<Release | null>(null);
  const [allowOlder, setAllowOlder] = useState(false);
  const [busy, setBusy] = useState('');
  const [showSettings, setShowSettings] = useState(false);
  const [mark, setMark] = useState(false);
  const targetRef = useRef<HTMLDivElement>(null);
  const loaded = useRef<string | null>(null);

  useEffect(() => { if (st && !f) setF({ ...st.config, token: '' }); }, [st, f]);
  useEffect(() => { if (st && loaded.current === null) loaded.current = st.current; }, [st]);

  // Mens opdateringen kører, genstarter serveren: forespørgslerne fejler. Spørg /api/health, til den svarer igen,
  // og hent så siden igen (den nye version har en ny brugerflade).
  const active = !!st?.job && ACTIVE.includes(st.job.status);
  const serverDown = !!error && !(error instanceof ApiError && error.status < 500);
  useEffect(() => { if (serverDown && (active || restarting)) setRestarting(true); }, [serverDown, active, restarting]);
  useEffect(() => {
    if (!restarting) return;
    let stop = false;
    const tick = async () => {
      if (stop) return;
      const h = await health();
      if (h && loaded.current && (h.release ?? h.version) !== loaded.current) { window.location.reload(); return; }
      if (h) { setRestarting(false); qc.invalidateQueries({ queryKey: ['update'] }); return; }
      setTimeout(tick, 3000);
    };
    const t = setTimeout(tick, 3000);
    return () => { stop = true; clearTimeout(t); };
  }, [restarting, qc]);
  // ny version kører (fx en anden fane opdaterede): hent den nye brugerflade
  useEffect(() => { if (st && loaded.current && st.current !== loaded.current && !active) window.location.reload(); }, [st, active]);

  if (!st || !f) return restarting ? <RestartingPanel /> : null;
  const set = (k: keyof typeof f, v: unknown) => setF({ ...f, [k]: v } as typeof f);

  const saveConfig = async () => {
    await put('/api/update/config', { ...f, token: f.token || null, machineId: f.machineId ?? 0 });
    setF({ ...f, token: '' });
  };
  const run = async (name: string, fn: () => Promise<unknown>, ok?: string) => {
    setBusy(name);
    try { await fn(); if (ok) notifyOk(ok); await qc.invalidateQueries({ queryKey: ['update'] }); } catch (e) { notifyErr(e); } finally { setBusy(''); }
  };
  const check = () => run('check', async () => { await saveConfig(); await post('/api/update/check'); }, 'Udgivelserne er hentet');
  const start = (r: Release) => run('start', async () => {
    await post('/api/update/start', { version: r.version, allowOlder: !r.newer && allowOlder });
    setConfirm(null); setAllowOlder(false);
  }, `Opdateringen til ${r.version} er startet – Fleetdeck genstarter om lidt`);

  // Fra dialogen: luk den, åbn indstillingerne, markér feltet og rul hen til det
  const showTarget = () => {
    setConfirm(null); setShowSettings(true); setMark(true);
    setTimeout(() => targetRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }), 50);
  };
  const job = st.job;
  const latest = st.latest;
  const machineData = st.machines.map((m) => ({ value: String(m.id), label: `${m.name}${m.unraid ? ' (Unraid)' : ''}${m.online ? '' : ' – offline'}` }));

  return (
    <Panel title="Opdatering af Fleetdeck" right={st.updateAvailable ? <Badge color="teal" variant="light">Ny version: {latest?.version}</Badge> : <Badge color="gray" variant="light">Fleetdeck {st.current}</Badge>}>
      <Stack gap="sm">
        {restarting && <RestartingBar />}
        {!st.inDocker && <Alert color="orange">Fleetdeck kører ikke i Docker – opdatering herfra kræver, at serveren kører som container med agenten installeret på Docker-værten.</Alert>}
        <SimpleGrid cols={{ base: 2, sm: 3 }}>
          <div><Text size="xs" c="dimmed">Kører nu</Text><Text fw={600}>{st.current}</Text></div>
          <div><Text size="xs" c="dimmed">Nyeste ({f.channel === 'beta' ? 'beta' : 'stabil'})</Text>
            {/* Ingen nyere udgivelse (eller slet ingen endnu): den version, der kører, er den nyeste */}
            <Text fw={600} c={st.updateAvailable ? 'teal' : undefined}>{st.updateAvailable && latest ? `${latest.version}${latest.prerelease ? ' (beta)' : ''}` : st.checkedAt ? `${st.current} (denne)` : '–'}</Text></div>
          <div><Text size="xs" c="dimmed">Sidst søgt</Text><Text size="sm">{st.checkedAt ? ago(st.checkedAt) : 'aldrig'}</Text></div>
        </SimpleGrid>
        {st.error && <Text size="sm" c="red">{st.error}</Text>}
        {!st.error && st.checkedAt && !st.updateAvailable && <Text size="sm" c="teal">Du kører den nyeste version.</Text>}

        {job && (
          <Stack gap={4}>
            <Group gap="xs"><Text size="sm" fw={600}>{job.title}</Text><Badge size="sm" color={STATUS[job.status]?.[1] ?? 'gray'} variant="light">{STATUS[job.status]?.[0] ?? job.status}</Badge>
              {st.state?.auto && <Badge size="sm" variant="outline">automatisk</Badge>}
              <Text size="xs" c="dimmed">{job.machine ? `på ${job.machine} · ` : ''}{fmt(job.created)}</Text></Group>
            {active && !restarting && <>
              <Group gap="xs" wrap="nowrap"><Progress style={{ flex: 1 }} value={job.progress ?? 100} striped animated color={job.progress == null ? 'gray' : job.progress >= 100 ? 'teal' : 'blue'} />
                {job.progress != null && <Text size="xs" fw={600} w={36} ta="right">{job.progress} %</Text>}</Group>
              <Text size="sm">{job.step || (job.status === 'pending' ? 'Venter på agenten på Docker-værten …' : 'Arbejder …')}</Text>
            </>}
            {!active && job.output && <ScrollArea.Autosize mah={240}><Code block style={{ whiteSpace: 'pre-wrap' }}>{job.output}</Code></ScrollArea.Autosize>}
          </Stack>
        )}

        <Group gap="xs">
          <Button variant="default" loading={busy === 'check'} onClick={check}>Søg efter opdatering</Button>
          {latest && <Button disabled={!st.updateAvailable || active || !st.inDocker} onClick={() => { setAllowOlder(false); setConfirm(latest); }}>Opdatér nu</Button>}
          <Button variant="subtle" onClick={() => setShowSettings((x) => !x)}>{showSettings ? 'Skjul indstillinger' : 'Indstillinger og versioner'}</Button>
        </Group>

        <Group gap="md" align="flex-end">
          <SegmentedControl size="xs" value={f.channel} onChange={(v) => set('channel', v)} data={[{ value: 'stable', label: 'Stabil' }, { value: 'beta', label: 'Beta' }]} />
          <Switch label="Opdatér automatisk om natten" checked={f.auto} onChange={(e) => set('auto', e.currentTarget.checked)} />
          {f.auto && <NumberInput size="xs" w={110} label="Klokken" min={0} max={23} value={f.autoHour} onChange={(v) => set('autoHour', Number(v) || 0)} suffix=":00" />}
          <Button size="xs" variant="default" loading={busy === 'save'} onClick={() => run('save', saveConfig, 'Gemt')}>Gem</Button>
        </Group>
        {f.auto && <Text size="xs" c="dimmed">Kun nyere versioner på den valgte kanal. Lykkes en opdatering ikke, rulles den tilbage, og samme version prøves ikke automatisk igen.</Text>}

        <Text size="sm">
          {st.target
            ? <>Opdateres via agenten på <b>{st.target.machine}</b>, {st.target.onHost ? 'containeren findes automatisk på værten, når opdateringen starter'
              : <>container <Code>{st.target.container}</Code>{st.target.detected ? ' (fundet automatisk)' : ''}</>}{st.target.online ? '' : ' – offline!'}</>
            : <Text span c="orange">{st.problem ?? 'Docker-værten er ikke fundet – vælg maskinen og containeren under indstillinger.'}</Text>}
        </Text>
        {st.target?.onHost && st.target.note && <Text size="xs" c="dimmed" mt={-8}>{st.target.note[0].toUpperCase() + st.target.note.slice(1)}.</Text>}
        {st.target && st.problem && <Text size="sm" c="orange">{st.problem}</Text>}

        {showSettings && (
          <Stack gap="sm">
            <Select label="Kilde" value={f.source} onChange={(v) => set('source', v ?? 'github')}
              data={[{ value: 'github', label: 'GitHub-udgivelser (stabil/beta)' }, { value: 'local', label: 'Lokal build-mappe (udvikling)' }]} />
            {f.source === 'github' ? (
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <TextInput label="GitHub-projekt" description="ejer/navn" value={f.repo} onChange={(e) => set('repo', e.currentTarget.value)} />
                <PasswordInput label="GitHub-nøgle (valgfri)" description="Kun til private projekter. Bruges kun af serveren." placeholder={st.config.hasToken ? '(gemt – skriv - for at fjerne)' : ''}
                  value={f.token} onChange={(e) => set('token', e.currentTarget.value)} autoComplete="off" />
                <TextInput label="Docker-billede" description="{version} udfyldes. Tom = brug billedfilen i udgivelsen" value={f.image} onChange={(e) => set('image', e.currentTarget.value)} />
              </SimpleGrid>
            ) : (
              <TextInput label="Build-mappe (som Fleetdeck ser den)" description="Koden lægges her med deploy/deploy.sh --source-only; billedet bygges på Docker-værten" value={f.localDir} onChange={(e) => set('localDir', e.currentTarget.value)} />
            )}
            <SimpleGrid cols={{ base: 1, sm: 3 }} ref={targetRef}>
              <Select label="Docker-vært" placeholder="Find automatisk" clearable data={machineData} value={f.machineId ? String(f.machineId) : null}
                error={mark && !st.target && !f.machineId ? 'Vælg maskinen, Fleetdeck-containeren kører på' : undefined}
                onChange={(v) => { set('machineId', v ? Number(v) : null); setMark(false); }} />
              <TextInput label="Container" description="Tom = findes automatisk" placeholder={st.candidates[0] ? `fx ${st.candidates[0].container}` : 'fx Fleetdeck'} value={f.container}
                error={mark && st.target && st.problem && !f.container ? 'Skriv navnet fra docker ps' : undefined}
                onChange={(e) => { set('container', e.currentTarget.value); setMark(false); }} />
              <NumberInput label="Vent på ny version (min)" description="Ellers rulles tilbage" min={1} max={60} value={f.healthMinutes} onChange={(v) => set('healthMinutes', Number(v) || 5)} />
            </SimpleGrid>
            {st.candidates.length > 0 && <Text size="xs" c="dimmed">Fundet: {st.candidates.map((c) => `${c.machine}/${c.container} (${c.why})`).join(', ')}</Text>}
            <Group><Button size="xs" loading={busy === 'save'} onClick={() => run('save', saveConfig, 'Gemt')}>Gem indstillinger</Button></Group>

            <Text size="sm" fw={600} mt="xs">Versioner</Text>
            {st.releases.length === 0 && <Text size="sm" c="dimmed">{st.checkedAt ? 'Der er ingen udgivelser i GitHub-projektet endnu.' : 'Ingen udgivelser fundet endnu – klik Søg efter opdatering.'}</Text>}
            {st.releases.map((r) => (
              <Group key={r.version} justify="space-between" wrap="nowrap">
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600}>{r.name}{r.prerelease && <Badge ml={6} size="xs" color="orange" variant="light">beta</Badge>}{r.same && <Badge ml={6} size="xs" variant="light">kører nu</Badge>}</Text>
                  <Text size="xs" c="dimmed" truncate>{r.published ? fmt(r.published) : ''}{r.image ? ` · ${r.image}` : ' · intet Docker-billede'}</Text>
                </div>
                <Button size="compact-xs" variant={r.newer ? 'light' : 'subtle'} color={r.newer ? undefined : 'gray'} disabled={active || !r.image}
                  onClick={() => { setAllowOlder(false); setConfirm(r); }}>{r.newer ? 'Installér' : r.same ? 'Geninstallér' : 'Gå tilbage'}</Button>
              </Group>
            ))}
          </Stack>
        )}
      </Stack>

      <Modal opened={!!confirm} onClose={() => setConfirm(null)} title={confirm ? `Opdatér Fleetdeck til ${confirm.version}?` : ''} size="lg">
        {confirm && (
          <Stack gap="sm">
            <Text size="sm">Fleetdeck {st.current} stoppes, databasen og nøglerne kopieres til en backup, og containeren startes igen med {confirm.version}
              {st.target ? ` på ${st.target.machine}` : ''}. Svarer den nye version ikke inden for {f.healthMinutes} minutter, lægges den gamle version og databasen tilbage.
              Fleetdeck er væk i et minuts tid undervejs.</Text>
            {st.problem && (
              <Alert color="orange" p="xs" title="Opdateringen kan ikke starte endnu">
                <Text size="sm">{st.problem}</Text>
                <Button size="compact-sm" variant="light" color="orange" mt={6} onClick={showTarget}>Vælg Docker-vært og container</Button>
              </Alert>
            )}
            {!st.problem && st.target?.onHost && <Text size="xs" c="dimmed">Containerens navn er ikke skrevet – opdateringen finder Fleetdecks egen container på {st.target.machine} ud fra dens id.</Text>}
            {confirm.prerelease && <Alert color="orange" p="xs">Det er en beta-version.</Alert>}
            <Text size="sm" fw={600}>Udgivelsesnoter</Text>
            <ScrollArea.Autosize mah={300}><Code block style={{ whiteSpace: 'pre-wrap' }}>{confirm.notes || '(ingen noter)'}</Code></ScrollArea.Autosize>
            {confirm.url && <Text size="xs"><a href={confirm.url} target="_blank" rel="noreferrer">Se udgivelsen på GitHub</a></Text>}
            {!confirm.newer && (
              <Checkbox color="red" checked={allowOlder} onChange={(e) => setAllowOlder(e.currentTarget.checked)}
                label={confirm.same ? 'Ja, installér den samme version igen' : `Tillad ældre version (${confirm.version} er ældre end ${st.current}). En database, der er opgraderet af en nyere version, virker måske ikke.`} />
            )}
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirm(null)}>Annullér</Button>
              <Button color={confirm.newer ? undefined : 'red'} disabled={(!confirm.newer && !allowOlder) || !!st.problem} loading={busy === 'start'} onClick={() => start(confirm)}>
                {confirm.newer ? 'Opdatér nu' : 'Installér alligevel'}</Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Panel>
  );
}

function RestartingPanel() {
  return (
    <Panel title="Opdatering af Fleetdeck">
      <RestartingBar />
    </Panel>
  );
}

/** Alt er hentet og klar: bjælken står på 100 %, mens Fleetdeck genstarter og den nye version starter op. */
function RestartingBar() {
  return (
    <Stack gap={4}>
      <Group gap="xs" wrap="nowrap"><Progress style={{ flex: 1 }} value={100} striped animated color="teal" />
        <Text size="xs" fw={600} w={36} ta="right">100 %</Text></Group>
      <Text size="sm" fw={600}>Fleetdeck genstarter nu</Text>
      <Text size="xs" c="dimmed">Den nye version starter op. Siden hentes forfra af sig selv, når den svarer – det tager normalt under et minut.</Text>
    </Stack>
  );
}
