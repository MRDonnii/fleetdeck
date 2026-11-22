import { useState, type FormEvent } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Modal, PasswordInput, Radio, SegmentedControl, SimpleGrid, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { notifyErr, notifyOk, post, put, useApi } from '../api';
import type { SecuritySettings, TwoFactorPolicy, TwoFactorStatus } from '../types';
import { Qr } from './family';
import { CopyButton } from './copy';

// Totrinsbekræftelse: opsætning (QR + manuel nøgle + bekræftelseskode), gendannelseskoder, slå fra, og administratorens politik.
// Importerer ikke auth/components, så login-siden kan bruge opsætningen uden cirkulære imports.

const APPS = 'fx Google Authenticator, Microsoft Authenticator, Aegis, 2FAS eller en adgangskodemanager';

/** Nøglen i grupper af 4, så den er nem at taste. */
const grouped = (s: string) => s.replace(/(.{4})/g, '$1 ').trim();

function download(text: string, name: string) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

/** Gendannelseskoderne – vises kun denne ene gang. */
export function RecoveryCodes({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const [saved, setSaved] = useState(false);
  const text = `Fleetdeck – gendannelseskoder (${location.host})\nHver kode virker én gang, hvis telefonen ikke er ved hånden.\n\n${codes.join('\n')}\n`;
  return (
    <Stack>
      <Alert color="yellow" title="Gem gendannelseskoderne nu">
        De vises kun denne ene gang. Brug én af dem, hvis du mister telefonen – hver kode virker én gang.
        Gem dem fx i en adgangskodemanager eller på papir et sikkert sted.
      </Alert>
      <SimpleGrid cols={2} spacing={6}>{codes.map((c) => <Code key={c} ta="center" fz="md">{c}</Code>)}</SimpleGrid>
      <Group>
        <CopyButton value={codes.join('\n')}>{({ copied, copy }) => <Button variant="default" onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
        <Button variant="default" onClick={() => download(text, 'fleetdeck-gendannelseskoder.txt')}>Hent som fil</Button>
      </Group>
      <Checkbox label="Jeg har gemt koderne" checked={saved} onChange={(e) => setSaved(e.currentTarget.checked)} />
      <Button disabled={!saved} onClick={onDone}>Færdig</Button>
    </Stack>
  );
}

/** Opsætning: start → scan QR (eller tast nøglen) → bekræft med en kode → gem gendannelseskoderne. */
export function EnrollFlow({ onDone }: { onDone: () => void }) {
  const [d, setD] = useState<{ secret: string; uri: string; token: string } | null>(null);
  const [code, setCode] = useState('');
  const [codes, setCodes] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const start = async () => {
    setBusy(true);
    try { setD(await post<{ secret: string; uri: string; token: string }>('/api/account/2fa/start')); } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const confirm = async (e: FormEvent) => {
    e.preventDefault();
    if (!d) return;
    setBusy(true);
    try { setCodes((await post<{ recoveryCodes: string[] }>('/api/account/2fa/confirm', { token: d.token, code })).recoveryCodes); }
    catch (x) { notifyErr(x); setCode(''); } finally { setBusy(false); }
  };
  if (codes) return <RecoveryCodes codes={codes} onDone={onDone} />;
  if (!d) return (
    <Stack align="flex-start">
      <Text size="sm">Du skal bruge en autentificerings-app på telefonen ({APPS}). Ved login skal du så både bruge adgangskoden og en 6-cifret kode fra appen.</Text>
      <Button loading={busy} onClick={start}>Sæt totrinsbekræftelse op</Button>
    </Stack>
  );
  return (
    <Stack component="form" onSubmit={confirm}>
      <Text size="sm"><b>1.</b> Scan QR-koden med appen.</Text>
      <Group align="flex-start" wrap="wrap" gap="lg">
        <Qr text={d.uri} size={200} />
        <Stack gap={4} style={{ flex: 1, minWidth: 200 }}>
          <Text size="sm" c="dimmed">Kan du ikke scanne? Tast nøglen manuelt (tidsbaseret, 6 cifre):</Text>
          <Code block fz="sm" style={{ wordBreak: 'break-all' }}>{grouped(d.secret)}</Code>
          <CopyButton value={d.secret}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy} w="fit-content">{copied ? 'Kopieret' : 'Kopiér nøglen'}</Button>}</CopyButton>
        </Stack>
      </Group>
      <Text size="sm"><b>2.</b> Skriv koden, appen viser, for at bekræfte.</Text>
      <Group align="flex-end">
        <TextInput label="Kode fra appen" value={code} onChange={(e) => setCode(e.currentTarget.value)} inputMode="numeric" autoComplete="one-time-code"
          placeholder="123456" maxLength={7} w={160} required autoFocus />
        <Button type="submit" loading={busy}>Bekræft og slå til</Button>
        <Button variant="subtle" onClick={() => { setD(null); setCode(''); }}>Annullér</Button>
      </Group>
    </Stack>
  );
}

/** Bevis før ændringer: kode fra appen (eller en gendannelseskode) eller adgangskoden. */
function ProofModal({ title, warning, action, opened, onClose, onProof }: {
  title: string; warning?: string; action: string; opened: boolean; onClose: () => void; onProof: (p: { code?: string; password?: string }) => Promise<void>;
}) {
  const [kind, setKind] = useState<'code' | 'password'>('code');
  const [v, setV] = useState('');
  const [busy, setBusy] = useState(false);
  const close = () => { setV(''); onClose(); };
  const submit = async (e: FormEvent) => {
    e.preventDefault(); setBusy(true);
    try { await onProof(kind === 'code' ? { code: v } : { password: v }); close(); } catch (x) { notifyErr(x); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={close} title={title}>
      <Stack component="form" onSubmit={submit}>
        {warning && <Alert color="yellow">{warning}</Alert>}
        <SegmentedControl value={kind} onChange={(k) => { setKind(k as 'code' | 'password'); setV(''); }}
          data={[{ value: 'code', label: 'Kode fra appen' }, { value: 'password', label: 'Adgangskode' }]} />
        {kind === 'code'
          ? <TextInput label="Kode fra appen eller en gendannelseskode" value={v} onChange={(e) => setV(e.currentTarget.value)} autoComplete="one-time-code" required autoFocus />
          : <PasswordInput label="Din adgangskode" value={v} onChange={(e) => setV(e.currentTarget.value)} autoComplete="current-password" required autoFocus />}
        <Group justify="flex-end"><Button variant="default" onClick={close}>Annullér</Button><Button type="submit" color="red" loading={busy}>{action}</Button></Group>
      </Stack>
    </Modal>
  );
}

/** "Min konto" → Totrinsbekræftelse. <paramref name="onChanged"> henter sessionen igen (den skifter ved til/fra). */
export function TwoFactorSettings({ onChanged }: { onChanged: () => void }) {
  const qc = useQueryClient();
  const { data: s } = useApi<TwoFactorStatus>(['twofa'], '/api/account/2fa');
  const [ask, setAsk] = useState<'off' | 'codes' | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const changed = () => { qc.invalidateQueries({ queryKey: ['twofa'] }); onChanged(); };
  if (!s) return null;
  if (codes) return <RecoveryCodes codes={codes} onDone={() => { setCodes(null); changed(); }} />;
  if (!s.enabled) return (
    <Stack>
      {s.required && <Alert color="orange">Totrinsbekræftelse er påkrævet for din konto.</Alert>}
      <EnrollFlow onDone={changed} />
    </Stack>
  );
  return (
    <Stack align="flex-start">
      <Group gap="xs"><Badge color="teal" variant="light">Slået til</Badge>
        {s.enabledAt && <Text size="sm" c="dimmed">siden {new Date(s.enabledAt).toLocaleDateString('da-DK')}</Text>}</Group>
      <Text size="sm" c={s.recoveryLeft <= 3 ? 'orange' : 'dimmed'}>
        {s.recoveryLeft} gendannelseskode{s.recoveryLeft === 1 ? '' : 'r'} tilbage{s.recoveryLeft <= 3 ? ' – lav nye snart' : ''}.
      </Text>
      <Text size="sm" c="dimmed">Ny telefon? Slå totrinsbekræftelse fra og sæt den op igen. Huskede enheder glemmes, når du skifter adgangskode eller slår den fra.</Text>
      <Group>
        <Button variant="default" onClick={() => setAsk('codes')}>Nye gendannelseskoder</Button>
        <Button variant="light" color="red" onClick={() => setAsk('off')}>Slå fra</Button>
      </Group>
      <ProofModal opened={ask === 'codes'} onClose={() => setAsk(null)} title="Nye gendannelseskoder" action="Lav nye koder"
        warning="De gamle gendannelseskoder holder op med at virke."
        onProof={async (p) => { setCodes((await post<{ recoveryCodes: string[] }>('/api/account/2fa/recovery-codes', p)).recoveryCodes); }} />
      <ProofModal opened={ask === 'off'} onClose={() => setAsk(null)} title="Slå totrinsbekræftelse fra" action="Slå fra"
        warning={s.required ? 'Totrinsbekræftelse er påkrævet for din konto: du skal sætte den op igen med det samme.' : 'Dine andre sessioner logges ud.'}
        onProof={async (p) => { await post('/api/account/2fa/disable', p); notifyOk('Totrinsbekræftelse er slået fra'); changed(); }} />
    </Stack>
  );
}

const POLICIES: { value: TwoFactorPolicy; label: string; hint: string }[] = [
  { value: 'optional', label: 'Valgfri', hint: 'Hver bruger vælger selv under Min konto.' },
  { value: 'admins', label: 'Påkrævet for administratorer', hint: 'Administratorer uden totrinsbekræftelse skal sætte den op lige efter adgangskoden.' },
  { value: 'all', label: 'Påkrævet for alle', hint: 'Gælder også læsere og forældre.' },
];

/** Administratoren: politik for totrinsbekræftelse og betroede proxyer. */
export function SecuritySettingsForm({ selfHasTwoFactor, onChanged }: { selfHasTwoFactor: boolean; onChanged: () => void }) {
  const qc = useQueryClient();
  const { data: s } = useApi<SecuritySettings>(['security'], '/api/settings/security');
  const [f, setF] = useState<{ twofaPolicy: TwoFactorPolicy; trustedProxies: string } | null>(null);
  const [busy, setBusy] = useState(false);
  if (!s) return null;
  const v = f ?? { twofaPolicy: s.twofaPolicy, trustedProxies: s.trustedProxies === s.trustedProxiesDefault ? '' : s.trustedProxies };
  const selfLocked = !selfHasTwoFactor && v.twofaPolicy !== 'optional';
  const save = async () => {
    setBusy(true);
    try { await put('/api/settings/security', v); notifyOk('Sikkerhedsindstillingerne er gemt'); setF(null); qc.invalidateQueries({ queryKey: ['security'] }); onChanged(); }
    catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Stack>
      <Radio.Group label="Totrinsbekræftelse" value={v.twofaPolicy} onChange={(p) => setF({ ...v, twofaPolicy: p as TwoFactorPolicy })}>
        <Stack gap={6} mt={6}>{POLICIES.map((p) => <Radio key={p.value} value={p.value} label={p.label} description={p.hint} />)}</Stack>
      </Radio.Group>
      {selfLocked && <Alert color="orange">Du har ikke selv totrinsbekræftelse endnu. Når du gemmer, skal du sætte den op med det samme.</Alert>}
      <Textarea label="Betroede proxyer" autosize minRows={2}
        description={<>Kun fra disse adresser læses klientens rigtige IP (X-Forwarded-For) – den bruges til begrænsning af login-forsøg og i loggen.
          Tilføj fx Kontrolrum eller cloudflared, hvis de kører på en anden maskine. IP-adresser eller net (CIDR), adskilt af komma. Tom = standard ({s.trustedProxiesDefault}).</>}
        placeholder={s.trustedProxiesDefault} value={v.trustedProxies} onChange={(e) => setF({ ...v, trustedProxies: e.currentTarget.value })} />
      <Text size="xs" c="dimmed">Serveren ser din forbindelse som {s.clientIp}{s.viaProxy ? ' (oplyst af en betroet proxy)' : ''}.</Text>
      <Group><Button loading={busy} onClick={save} disabled={!f}>Gem</Button></Group>
    </Stack>
  );
}
