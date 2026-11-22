import { useEffect, useMemo, useState } from 'react';
import {
  Alert, Anchor, Button, Center, Group, List, NumberInput, Paper, PasswordInput, Progress, SegmentedControl, Select, Stack, Text, TextInput,
  ThemeIcon, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconCircleCheck, IconInfoCircle, IconLayoutGrid, IconPlus, IconShieldLock } from '@tabler/icons-react';
import { ApiError, get, post } from '../api';
import { EnrollFlow } from '../design/twofa';
import { FirstRunRestore } from './Backup';

type Opt = { id: string; name: string };
const STEPS = ['Start', 'Administrator', 'Husstand og adresse', 'Sprog og tidszone', 'Notifikationer', 'Overblik', 'Sikkerhed'];
interface SetupInfo {
  detectedUrl: string; fromEnv: boolean;
  suggested: { language: string; keyboard: string; timeZone: string };
  options: { languages: (Opt & { keyboard: string })[]; keyboards: Opt[]; serverTimeZone: string };
}
interface Form {
  username: string; password: string; password2: string; household: string; serverUrl: string;
  language: string; keyboard: string; timeZone: string;
  notify: 'none' | 'mqtt'; mqttHost: string; mqttPort: number; mqttUser: string; mqttPass: string;
}

const browserTz = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return ''; } };
const allTimeZones = (extra: string[]) => {
  let list: string[] = [];
  try { list = Intl.supportedValuesOf('timeZone'); } catch { /* ældre browser */ }
  return [...new Set([...extra.filter(Boolean), ...list])].sort();
};

/** Hvad adressen sandsynligvis betyder for agenterne (uden at serveren selv skal hente noget). */
export function urlHints(raw: string, current = window.location.origin): { color: 'red' | 'yellow' | 'green'; text: string }[] {
  const t = raw.trim().replace(/\/+$/, '');
  let u: URL;
  try { u = new URL(t.includes('://') ? t : `http://${t}`); } catch { return [{ color: 'red', text: 'Det ligner ikke en adresse. Skriv fx http://192.168.1.10:8585' }]; }
  const h = u.hostname.replace(/^\[|\]$/g, '');
  const out: { color: 'red' | 'yellow' | 'green'; text: string }[] = [];
  if (!/^https?:$/.test(u.protocol)) out.push({ color: 'red', text: 'Adressen skal starte med http:// eller https://' });
  if (h === 'localhost' || h.startsWith('127.') || h === '::1')
    out.push({ color: 'red', text: 'localhost virker kun på selve serveren. Brug serverens IP-adresse eller navn på hjemmenettet.' });
  else if (/^172\.(1[6-9]|2\d|3[01])\./.test(h))
    out.push({ color: 'yellow', text: 'Det ligner en intern Docker-adresse. Andre maskiner kan som regel ikke nå den – brug værtens IP-adresse.' });
  else if (u.origin === current) out.push({ color: 'green', text: 'Det er den adresse, du bruger lige nu.' });
  if (u.protocol === 'http:' && !/^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|169\.254\.)/.test(h) && !/\.(local|lan|home|internal)$/.test(h) && h.includes('.') && !/^\d+\.\d+\.\d+\.\d+$/.test(h))
    out.push({ color: 'yellow', text: 'Et offentligt navn over http: brug helst https (eller en adresse på hjemmenettet).' });
  return out;
}

function Brand() {
  return <Group gap="sm"><ThemeIcon size="lg" radius="md"><IconLayoutGrid size={20} /></ThemeIcon><Title order={3}>Fleetdeck</Title></Group>;
}

/**
 * Opsætningsguiden ved første start (ingen brugere endnu): administrator, husstand, serveradresse, sprog/tastatur/tidszone til
 * netværksinstallationer og valgfrie notifikationer. Intet gemmes, før man trykker "Opret" – derefter er /api/setup lukket.
 */
export function SetupWizard({ onDone }: { onDone: () => void }) {
  const [info, setInfo] = useState<SetupInfo | null>(null);
  const [loadErr, setLoadErr] = useState('');
  const [step, setStep] = useState(0);
  const [f, setF] = useState<Form | null>(null);
  const [kbTouched, setKbTouched] = useState(false);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [probe, setProbe] = useState<'idle' | 'busy' | 'ok' | 'fail' | 'mixed'>('idle');

  useEffect(() => {
    get<SetupInfo>(`/api/setup?lang=${encodeURIComponent(navigator.language || '')}`).then((i) => {
      setInfo(i);
      const tz = browserTz();
      setF({
        username: '', password: '', password2: '', household: '', serverUrl: i.detectedUrl,
        language: i.suggested.language, keyboard: i.suggested.keyboard,
        timeZone: tz && allTimeZones([]).includes(tz) ? tz : i.suggested.timeZone,
        notify: 'none', mqttHost: '', mqttPort: 1883, mqttUser: '', mqttPass: '',
      });
    }).catch((e) => setLoadErr(e instanceof ApiError && e.status === 404 ? 'Fleetdeck er allerede sat op. Genindlæs siden for at logge ind.' : (e as Error).message));
  }, []);

  const zones = useMemo(() => allTimeZones([f?.timeZone ?? '', info?.options.serverTimeZone ?? '']), [info, f?.timeZone]);
  if (loadErr) return <Shell><Alert color="yellow">{loadErr}</Alert></Shell>;
  if (!info || !f) return null;
  const set = (p: Partial<Form>) => { setF({ ...f, ...p }); setErr(''); };
  const hints = urlHints(f.serverUrl);

  const accountErr = f.username.trim().length < 2 ? 'Brugernavnet skal være mindst 2 tegn'
    : f.password.length < 8 ? 'Adgangskoden skal være mindst 8 tegn'
    : f.password !== f.password2 ? 'De to adgangskoder er ikke ens' : '';
  const addressErr = hints.some((h) => h.color === 'red') ? 'Ret serverens adresse først' : '';
  const notifyErr = f.notify === 'mqtt' && !f.mqttHost.trim() ? 'Skriv MQTT-brokerens adresse, eller vælg "Ingen"' : '';
  const next = (msg = '') => { if (msg) { setErr(msg); return; } setErr(''); setStep(step + 1); };

  const testUrl = async () => {
    const base = f.serverUrl.trim().replace(/\/+$/, '');
    const url = (base.includes('://') ? base : `http://${base}`) + '/api/health';
    if (window.location.protocol === 'https:' && url.startsWith('http:')) { setProbe('mixed'); return; }
    setProbe('busy');
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), 5000);
    try { await fetch(url, { mode: 'no-cors', cache: 'no-store', signal: ac.signal }); setProbe('ok'); }
    catch { setProbe('fail'); }
    finally { clearTimeout(timer); }
  };

  const create = async () => {
    setBusy(true); setErr('');
    try {
      await post('/api/setup', {
        username: f.username.trim(), password: f.password, household: f.household.trim(), serverUrl: f.serverUrl.trim(),
        language: f.language, keyboard: f.keyboard, timeZone: f.timeZone, notify: f.notify,
        ...(f.notify === 'mqtt' ? { mqttHost: f.mqttHost.trim(), mqttPort: f.mqttPort, mqttUser: f.mqttUser.trim(), mqttPass: f.mqttPass } : {}),
      });
      setStep(6);
    }
    catch (x) { setErr(x instanceof ApiError && x.status === 404 ? 'Fleetdeck er allerede sat op af en anden. Genindlæs siden og log ind.' : (x as Error).message); }
    finally { setBusy(false); }
  };

  const langName = info.options.languages.find((l) => l.id === f.language)?.name ?? f.language;
  const kbName = info.options.keyboards.find((k) => k.id === f.keyboard)?.name ?? f.keyboard;
  const nav = (onNext: () => void, label = 'Næste', back = true) => (
    <>
      {err && <Text c="red" size="sm">{err}</Text>}
      <Group justify="space-between" mt="sm">
        {back ? <Button variant="default" onClick={() => { setErr(''); setStep(step - 1); }}>Tilbage</Button> : <span />}
        <Button onClick={onNext} loading={busy}>{label}</Button>
      </Group>
    </>
  );

  return (
    <Shell wide>
      {step < STEPS.length && (
        <Stack gap={6}>
          <Group justify="space-between">
            <Text size="xs" c="dimmed" fw={600} tt="uppercase">Trin {step + 1} af {STEPS.length} · {STEPS[step]}</Text>
            {step + 1 < STEPS.length && <Text size="xs" c="dimmed">Næste: {STEPS[step + 1]}</Text>}
          </Group>
          <Progress value={((step + 1) / STEPS.length) * 100} size="sm" radius="xl" />
        </Stack>
      )}

      {step === 0 && (
        <Stack>
          <Title order={4}>Velkommen til Fleetdeck</Title>
          <Text size="sm">Fleetdeck holder styr på husstandens computere: opdateringer, backup, overvågning og geninstallation fra netværket.
            Guiden tager et par minutter, og alt kan ændres bagefter under Indstillinger.</Text>
          <Paper withBorder p="md" radius="md">
            <Stack gap={6}>
              <Text fw={600} size="sm">Har du brugt Fleetdeck før?</Text>
              <Text size="sm" c="dimmed">Flytter du til en ny server, eller er den geninstalleret, kan du gendanne maskiner, brugere, indstillinger og nøgler fra en backup i stedet.</Text>
              <Group><FirstRunRestore /></Group>
            </Stack>
          </Paper>
          {nav(() => next(), 'Start en ny opsætning', false)}
        </Stack>
      )}

      {step === 1 && (
        <Stack>
          <Title order={4}>Opret administratoren</Title>
          <Text size="sm" c="dimmed">Det er dig, der logger ind og styrer maskinerne. Du kan oprette flere brugere senere.</Text>
          <TextInput label="Brugernavn" value={f.username} onChange={(e) => set({ username: e.currentTarget.value })} autoComplete="username" autoFocus />
          <PasswordInput label="Adgangskode" description="Mindst 8 tegn" value={f.password} onChange={(e) => set({ password: e.currentTarget.value })} autoComplete="new-password" />
          <PasswordInput label="Gentag adgangskoden" value={f.password2} onChange={(e) => set({ password2: e.currentTarget.value })} autoComplete="new-password" />
          <Text size="xs" c="dimmed">Til sidst får du tilbudt totrinsbekræftelse med en app på telefonen.</Text>
          {nav(() => next(accountErr))}
        </Stack>
      )}

      {step === 2 && (
        <Stack>
          <Title order={4}>Husstand og serverens adresse</Title>
          <TextInput label="Husstandens navn" description="Vises som organisation, når iPads gøres overvågede. Kan stå tomt." placeholder="Familien"
            value={f.household} onChange={(e) => set({ household: e.currentTarget.value })} maxLength={64} />
          <TextInput label="Adressen, agenterne skal bruge" value={f.serverUrl} onChange={(e) => { set({ serverUrl: e.currentTarget.value }); setProbe('idle'); }}
            description={info.fromEnv ? 'Udfyldt fra SERVER_URL i containerens opsætning.' : 'Udfyldt ud fra den adresse, du bruger lige nu. Den skal kunne nås fra alle maskiner på hjemmenettet.'} />
          {hints.map((h) => (
            <Alert key={h.text} color={h.color} p="xs" icon={h.color === 'green' ? <IconCircleCheck size={16} /> : <IconAlertTriangle size={16} />}>
              <Text size="sm">{h.text}</Text>
            </Alert>
          ))}
          <Group gap="sm">
            <Button variant="default" size="xs" onClick={testUrl} loading={probe === 'busy'}>Test adressen</Button>
            {probe === 'ok' && <Text size="sm" c="green">Svarer fra denne browser.</Text>}
            {probe === 'fail' && <Text size="sm" c="red">Kunne ikke nås fra denne browser. Tjek IP-adresse, port og firewall.</Text>}
            {probe === 'mixed' && <Text size="sm" c="dimmed">Kan ikke testes fra en https-side. Prøv adressen i en ny fane.</Text>}
          </Group>
          {nav(() => next(addressErr))}
        </Stack>
      )}

      {step === 3 && (
        <Stack>
          <Title order={4}>Sprog, tastatur og tidszone</Title>
          <Text size="sm" c="dimmed">Bruges, når Fleetdeck installerer Windows eller Ubuntu fra netværket og gør iPads overvågede. Forslagene kommer fra din browser.</Text>
          <Select label="Sprog" data={info.options.languages.map((l) => ({ value: l.id, label: l.name }))} value={f.language} allowDeselect={false}
            onChange={(v) => { if (!v) return; const l = info.options.languages.find((x) => x.id === v); set({ language: v, ...(kbTouched || !l ? {} : { keyboard: l.keyboard }) }); }} />
          <Select label="Tastaturlayout" data={info.options.keyboards.map((k) => ({ value: k.id, label: k.name }))} value={f.keyboard} allowDeselect={false}
            onChange={(v) => { if (v) { setKbTouched(true); set({ keyboard: v }); } }} />
          <Select label="Tidszone" data={zones} value={f.timeZone} searchable allowDeselect={false} limit={200}
            onChange={(v) => v && set({ timeZone: v })} />
          {info.options.serverTimeZone !== f.timeZone && (
            <Alert color="blue" p="xs" icon={<IconInfoCircle size={16} />}>
              <Text size="sm">Serverens egen tidszone er {info.options.serverTimeZone}. Planlagte opdateringer og backup følger den – sæt TZ i containerens opsætning, hvis den skal være en anden.</Text>
            </Alert>
          )}
          {nav(() => next())}
        </Stack>
      )}

      {step === 4 && (
        <Stack>
          <Title order={4}>Notifikationer</Title>
          <Text size="sm" c="dimmed">Fleetdeck kan sende maskinernes status og advarsler til Home Assistant via MQTT. Det er valgfrit og kan sættes op senere.</Text>
          <SegmentedControl value={f.notify} onChange={(v) => set({ notify: v as Form['notify'] })}
            data={[{ value: 'none', label: 'Ingen' }, { value: 'mqtt', label: 'Home Assistant (MQTT)' }]} />
          {f.notify === 'mqtt' && (
            <>
              <Group grow align="flex-start">
                <TextInput label="MQTT-broker" placeholder="192.168.1.20" value={f.mqttHost} onChange={(e) => set({ mqttHost: e.currentTarget.value })} />
                <NumberInput label="Port" value={f.mqttPort} min={1} max={65535} onChange={(v) => set({ mqttPort: Number(v) || 1883 })} maw={120} />
              </Group>
              <Group grow>
                <TextInput label="Brugernavn" value={f.mqttUser} onChange={(e) => set({ mqttUser: e.currentTarget.value })} autoComplete="off" />
                <PasswordInput label="Adgangskode" value={f.mqttPass} onChange={(e) => set({ mqttPass: e.currentTarget.value })} autoComplete="new-password" />
              </Group>
            </>
          )}
          {nav(() => next(notifyErr))}
        </Stack>
      )}

      {step === 5 && (
        <Stack>
          <Title order={4}>Klar til at oprette</Title>
          <List size="sm" spacing={4}>
            <List.Item>Administrator: <b>{f.username.trim()}</b></List.Item>
            <List.Item>Husstand: {f.household.trim() || <Text span c="dimmed">(ikke angivet)</Text>}</List.Item>
            <List.Item>Serverens adresse: <b>{f.serverUrl.trim()}</b></List.Item>
            <List.Item>Installationer: {langName} · {kbName} · {f.timeZone}</List.Item>
            <List.Item>Notifikationer: {f.notify === 'mqtt' ? `Home Assistant via ${f.mqttHost.trim()}:${f.mqttPort}` : 'ingen'}</List.Item>
          </List>
          {nav(create, 'Opret og log ind')}
        </Stack>
      )}

      {step === 6 && (
        <Stack>
          <Group gap="xs"><IconShieldLock size={20} /><Title order={4}>Totrinsbekræftelse (anbefales)</Title></Group>
          <Text size="sm" c="dimmed">Med en kode fra en app på telefonen (fx Google Authenticator eller 1Password) kan ingen logge ind med adgangskoden alene.</Text>
          <EnrollFlow onDone={() => setStep(7)} />
          <Group justify="flex-end"><Button variant="subtle" color="gray" onClick={() => setStep(7)}>Spring over – det kan gøres senere</Button></Group>
        </Stack>
      )}

      {step === 7 && (
        <Stack>
          <Group gap="xs"><IconCircleCheck size={22} color="var(--mantine-color-green-6)" /><Title order={4}>Fleetdeck er klar</Title></Group>
          <Text size="sm">Næste skridt er at installere agenten på den første computer. Du får en kommando at kopiere, eller et link du kan sende til den, der skal bruge computeren.</Text>
          <Button leftSection={<IconPlus size={16} />} onClick={() => { window.history.replaceState(null, '', new URL('./?tilfoej=1', document.baseURI)); onDone(); }}>
            Tilføj din første maskine</Button>
          <Group justify="space-between">
            <Anchor size="sm" href={new URL('./hent', document.baseURI).href} target="_blank">Åbn downloadsiden</Anchor>
            <Anchor component="button" size="sm" onClick={onDone}>Gå til oversigten</Anchor>
          </Group>
        </Stack>
      )}
    </Shell>
  );
}

function Shell({ children, wide }: { children: React.ReactNode; wide?: boolean }) {
  return (
    <Center mih="100dvh" p="md">
      <Paper withBorder radius="lg" p="xl" w={wide ? 640 : 420} maw="100%">
        <Stack><Brand />{children}</Stack>
      </Paper>
    </Center>
  );
}
