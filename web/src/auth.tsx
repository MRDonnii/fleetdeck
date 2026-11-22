import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { Anchor, Button, Center, Checkbox, Paper, PasswordInput, Stack, Text, TextInput, Title, Group, ThemeIcon } from '@mantine/core';
import { IconLayoutGrid } from '@tabler/icons-react';
import { ApiError, get, post, queryClient, setForbiddenHandler, setUnauthorizedHandler } from './api';
import { EnrollFlow } from './design/twofa';
import type { Me } from './types';
import { SetupWizard } from './pages/Setup';
import { isFramed, sessionLostText } from './design/loginSession';

/** refresh: hent sessionen igen (fx efter totrinsbekræftelse er slået til/fra, eller politikken er ændret). */
interface AuthCtx { me: Me; isAdmin: boolean; logout: () => Promise<void>; refresh: () => Promise<void> }
const Ctx = createContext<AuthCtx | null>(null);
export const useMe = () => {
  const c = useContext(Ctx);
  if (!c) throw new Error('useMe uden AuthProvider');
  return c;
};

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const last = useRef('');
  const lastForbidden = useRef(0);
  /** true = logget ind (sessionen er hentet) */
  const load = () => get<Me>('/api/auth/me').then((m) => {
    // kun ny værdi, når noget faktisk er ændret (ellers tegnes hele appen igen)
    const j = JSON.stringify(m);
    if (j !== last.current) { last.current = j; setMe(m); }
    return true;
  }).catch(() => { last.current = ''; setMe(null); return false; });
  useEffect(() => {
    setUnauthorizedHandler(() => { queryClient.clear(); last.current = ''; setMe(null); });
    // 403 kan betyde, at totrinsbekræftelse er blevet påkrævet, mens siden var åben: tjek sessionen (højst hvert 10. sekund)
    setForbiddenHandler(() => { if (Date.now() - lastForbidden.current > 10_000) { lastForbidden.current = Date.now(); load(); } });
    load();
  }, []);
  if (me === undefined) return null;
  const logout = async () => { await post('/api/auth/logout'); queryClient.clear(); last.current = ''; setMe(null); };
  if (me === null) return <Login onDone={load} />;
  if (me.twoFactor?.mustEnroll) return <ForcedEnroll me={me} onDone={() => { queryClient.clear(); void load(); }} logout={logout} />;
  return <Ctx.Provider value={{ me, isAdmin: me.role === 'admin', logout, refresh: async () => { await load(); } }}>{children}</Ctx.Provider>;
}

function Brand() {
  return <Group gap="sm"><ThemeIcon size="lg" radius="md"><IconLayoutGrid size={20} /></ThemeIcon><Title order={3}>Fleetdeck</Title></Group>;
}

/** Politikken kræver totrinsbekræftelse, og brugeren har den ikke: opsætningen er det eneste, der kan bruges. */
function ForcedEnroll({ me, onDone, logout }: { me: Me; onDone: () => void; logout: () => Promise<void> }) {
  return (
    <Center mih="100dvh" p="md">
      <Paper withBorder radius="lg" p="xl" w={520} maw="100%">
        <Stack>
          <Brand />
          <Title order={4}>Sæt totrinsbekræftelse op</Title>
          <Text size="sm" c="dimmed">Hej {me.username}. Totrinsbekræftelse er påkrævet for din konto, før du kan fortsætte.</Text>
          <EnrollFlow onDone={onDone} />
          <Group justify="flex-end"><Button variant="subtle" color="gray" onClick={logout}>Log ud</Button></Group>
        </Stack>
      </Paper>
    </Center>
  );
}

const loginError = (x: unknown) =>
  x instanceof ApiError && x.status === 429 && !x.message.startsWith('For mange') ? 'For mange forsøg. Vent et par minutter.' : (x as Error).message;

/** Efter et vellykket login: hent sessionen. Holder den ikke (browseren gemte ikke cookien), siges det i stedet for ingenting. */
async function afterLogin(onDone: () => Promise<boolean>, setErr: (e: string) => void, setFramed: (f: boolean) => void) {
  if (await onDone()) return;
  const framed = isFramed(window);
  setFramed(framed);
  setErr(sessionLostText(framed));
}

/** Indlejret i en anden side: login kan kun holde i et vindue for sig selv. */
const NewWindow = () => (
  <Button component="a" href={window.location.href} target="_blank" rel="noopener" variant="default" fullWidth>Åbn i et nyt vindue</Button>
);

function Login({ onDone }: { onDone: () => Promise<boolean> }) {
  const [setup, setSetup] = useState<boolean | null>(null);
  const [u, setU] = useState('');
  const [p, setP] = useState('');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [framed, setFramed] = useState(false);
  // trin 2: adgangskoden var rigtig, nu mangler koden fra appen
  const [ticket, setTicket] = useState<string | null>(null);
  useEffect(() => { get<{ needed: boolean }>('/api/auth/setup-needed').then((r) => setSetup(r.needed)).catch(() => setSetup(false)); }, []);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr('');
    try {
      const r = await post<{ twoFactor?: string; ticket?: string }>('/api/auth/login', { username: u.trim(), password: p });
      if (r?.twoFactor === 'code' && r.ticket) { setTicket(r.ticket); setP(''); }
      else await afterLogin(onDone, setErr, setFramed);
    }
    catch (x) { setErr(loginError(x)); }
    finally { setBusy(false); }
  };
  if (setup === null) return null;
  // Ingen brugere endnu: opsætningsguiden (administrator, adresse, sprog, notifikationer, evt. gendan fra backup)
  if (setup) return <SetupWizard onDone={onDone} />;
  if (ticket) return <CodeStep ticket={ticket} onDone={onDone} onBack={() => setTicket(null)} />;
  return (
    <Center mih="100dvh" p="md">
      <Paper withBorder radius="lg" p="xl" w={380} maw="100%" component="form" onSubmit={submit}>
        <Stack>
          <Brand />
          <Title order={4}>Log ind</Title>
          <TextInput label="Brugernavn" value={u} onChange={(e) => setU(e.currentTarget.value)} autoComplete="username" required autoFocus />
          <PasswordInput label="Adgangskode" value={p} onChange={(e) => setP(e.currentTarget.value)} autoComplete="current-password" required />
          {err && <Text c="red" size="sm">{err}</Text>}
          <Button type="submit" loading={busy} fullWidth>Log ind</Button>
          {framed && <NewWindow />}
        </Stack>
      </Paper>
    </Center>
  );
}

/** Login trin 2: 6-cifret kode fra appen eller en gendannelseskode, evt. "Husk denne enhed i 30 dage". */
function CodeStep({ ticket, onDone, onBack }: { ticket: string; onDone: () => Promise<boolean>; onBack: () => void }) {
  const [code, setCode] = useState('');
  const [recovery, setRecovery] = useState(false);
  const [remember, setRemember] = useState(false);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [framed, setFramed] = useState(false);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setBusy(true); setErr('');
    try { await post('/api/auth/2fa', { ticket, code, remember }); await afterLogin(onDone, setErr, setFramed); }
    catch (x) {
      if (x instanceof ApiError && x.message.includes('udløbet')) { onBack(); return; }
      setErr(loginError(x)); setCode('');
    }
    finally { setBusy(false); }
  };
  return (
    <Center mih="100dvh" p="md">
      <Paper withBorder radius="lg" p="xl" w={380} maw="100%" component="form" onSubmit={submit}>
        <Stack>
          <Brand />
          <Title order={4}>Totrinsbekræftelse</Title>
          <Text size="sm" c="dimmed">{recovery
            ? 'Skriv en af dine gendannelseskoder (fx abcde-12345). Hver kode virker én gang.'
            : 'Skriv den 6-cifrede kode fra autentificerings-appen på din telefon.'}</Text>
          {recovery
            ? <TextInput key="r" label="Gendannelseskode" value={code} onChange={(e) => setCode(e.currentTarget.value)} autoComplete="off" required autoFocus />
            : <TextInput key="c" label="Kode" value={code} onChange={(e) => setCode(e.currentTarget.value)} inputMode="numeric" autoComplete="one-time-code"
                placeholder="123456" maxLength={7} required autoFocus />}
          <Checkbox label="Husk denne enhed i 30 dage" checked={remember} onChange={(e) => setRemember(e.currentTarget.checked)} />
          {err && <Text c="red" size="sm">{err}</Text>}
          <Button type="submit" loading={busy} fullWidth>Log ind</Button>
          {framed && <NewWindow />}
          <Group justify="space-between">
            <Anchor component="button" type="button" size="sm" onClick={() => { setRecovery(!recovery); setCode(''); setErr(''); }}>
              {recovery ? 'Brug koden fra appen' : 'Brug en gendannelseskode'}</Anchor>
            <Anchor component="button" type="button" size="sm" c="dimmed" onClick={onBack}>Tilbage</Anchor>
          </Group>
        </Stack>
      </Paper>
    </Center>
  );
}
