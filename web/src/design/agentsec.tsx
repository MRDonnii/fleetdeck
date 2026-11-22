import { useState, type ReactNode } from 'react';
import { Alert, Badge, Button, Code, Group, Modal, NumberInput, SegmentedControl, Select, SimpleGrid, Stack, Table, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { del, notifyErr, notifyOk, post, useApi } from '../api';
import { ago, fmt } from '../format';
import type { AgentSecurity, EnrollCode, MachineRow, NewEnrollCode, Profile, Settings, SignedJobs } from '../types';
import { Empty, KeyValue, Panel } from '../components';
import { CopyButton } from './copy';

// ---------------------------------------------------------------- fælles

const copyBtn = (v: string, label = 'Kopiér') => (
  <CopyButton value={v}>{({ copied, copy }) => <Button size="compact-sm" variant="default" onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : label}</Button>}</CopyButton>);

export const TRUST: Record<string, { label: string; color: string }> = {
  pinned: { label: 'Pinnet', color: 'green' }, unpinned: { label: 'Ikke pinnet', color: 'yellow' }, mismatch: { label: 'Anden nøgle!', color: 'red' },
};
export const ENROLL_METHOD: Record<string, string> = {
  key: 'global nøgle', code: 'engangskode', token: 'gammelt token', deploy: 'geninstallation', approved: 'godkendt erstatning', new: 'ny maskine',
};
const pin = (p: string) => `sha256//${p.replace(/^sha256\/\//, '')}`;
const TRANSPORT: Record<string, string> = { http: 'http', https: 'https', socket: 'socket (lokal)' };

/** Skift den globale tilmeldingsnøgle (maskiner, der allerede er tilmeldt, bruger deres egne tokens). */
export function askRotateKey(qc: QueryClient) {
  modals.openConfirmModal({
    title: 'Skift tilmeldingsnøgle?',
    children: <Text size="sm">Den gamle nøgle kan ikke længere bruges til at tilmelde nye maskiner. Maskiner, der allerede er tilmeldt, virker som før, fordi de bruger deres egne tokens.
      Gør USB-nøglen og gemte installationskommandoer klar igen bagefter.</Text>,
    labels: { confirm: 'Skift nøgle', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: async () => { try { await post('/api/settings/rotate-key'); notifyOk('Tilmeldingsnøglen er skiftet'); qc.invalidateQueries(); } catch (e) { notifyErr(e); } },
  });
}

// ---------------------------------------------------------------- maskiner

/** Ny agent med et navn, der fandtes i forvejen: får hverken jobs eller adgang, før en administrator har taget stilling. */
export function PendingAlert({ m, onGone, compact }: { m: MachineRow; onGone?: (keptId: number | null) => void; compact?: boolean }) {
  const qc = useQueryClient();
  const old = m.replacesName ?? 'den gamle maskine';
  const go = (title: string, text: ReactNode, label: string, color: string, fn: () => Promise<unknown>, ok: string, kept: number | null) => modals.openConfirmModal({
    title, children: <Text size="sm">{text}</Text>, labels: { confirm: label, cancel: 'Annullér' }, confirmProps: { color },
    onConfirm: async () => { try { await fn(); notifyOk(ok); onGone?.(kept); qc.invalidateQueries(); } catch (e) { notifyErr(e); } },
  });
  const replace = () => go(`Godkend som erstatning for ${old}?`,
    <>{old} beholder sin profil, historik, gendannelsespakke og backup og overtager den nye agent. Den ventende post ({m.name}) forsvinder, og agenten fortsætter som {old}.</>,
    'Godkend som erstatning', 'blue', () => post(`/api/machines/${m.id}/approve-replacement`), `${old} er overtaget af den nye agent`, m.replacesId ?? null);
  const keep = () => go('Behold som ny maskine?',
    <>{m.name} bliver en selvstændig maskine ved siden af {old} og får jobs og adgang som alle andre maskiner.</>,
    'Behold som ny', 'blue', () => post(`/api/machines/${m.id}/approve-new`), 'Maskinen er godkendt som ny', m.id);
  const reject = () => go(`Afvis ${m.name}?`, <>Den ventende post slettes, og agenten kan ikke hente jobs eller oplysninger. {old} påvirkes ikke.</>,
    'Afvis', 'red', () => del(`/api/machines/${m.id}`), 'Agenten er afvist', null);
  return (
    <Alert color="orange" mb="md" title={compact ? `${m.name} venter på godkendelse` : 'Venter på godkendelse'}>
      <Stack gap="xs">
        <Text size="sm">En ny agent har tilmeldt sig med navnet <b>{m.hostname}</b>, som {m.replacesName ? <b>{m.replacesName}</b> : 'en anden maskine'} allerede har.
          Den får hverken jobs eller adgangsoplysninger, før du har valgt.{m.enrollMethod ? ` Tilmeldt via ${ENROLL_METHOD[m.enrollMethod] ?? m.enrollMethod}${m.ip ? ` fra ${m.ip}` : ''}.` : ''}</Text>
        <Group gap="xs">
          {m.replacesId != null && <Button size="xs" onClick={replace}>Godkend som erstatning for {old}</Button>}
          <Button size="xs" variant="default" onClick={keep}>Behold som ny maskine</Button>
          <Button size="xs" variant="subtle" color="red" onClick={reject}>Afvis</Button>
        </Group>
      </Stack>
    </Alert>
  );
}

/** Maskinsiden: om agenten stoler på serverens jobnøgle, hvordan den forbinder, og hvordan den blev tilmeldt. */
export function TrustPanel({ m }: { m: MachineRow }) {
  const t = m.jobTrust ? TRUST[m.jobTrust] : { label: 'Ældre agent', color: 'gray' };
  return (
    <Panel title="Tillid">
      <KeyValue rows={[
        ['Signerede jobs', <Badge variant="light" color={t.color}>{t.label}</Badge>],
        ['Forbindelse', m.transport ? <Group gap={6}><Code>{TRANSPORT[m.transport] ?? m.transport}</Code>{m.tlsPinned && <Badge variant="light" color="green">TLS fastlåst</Badge>}</Group> : '–'],
        ['Tilmeldt via', m.enrollMethod ? (ENROLL_METHOD[m.enrollMethod] ?? m.enrollMethod) : '–'],
        ...(m.trustError ? [['Afviste jobs', <Stack gap={0}><Text size="sm" c="red">{m.trustError}</Text>
          {m.trustErrorAt && <Text size="xs" c="dimmed">{fmt(m.trustErrorAt)} ({ago(m.trustErrorAt)})</Text>}</Stack>] as [string, ReactNode]] : []),
      ]} />
    </Panel>
  );
}

// ---------------------------------------------------------------- indstillinger

const MODES: { value: SignedJobs; label: string; text: string }[] = [
  { value: 'off', label: 'Fra', text: 'Serveren signerer ikke jobs (nødbremse). Agenter, der allerede har pinnet nøglen, afviser så jobs.' },
  { value: 'sign', label: 'Kun signér', text: 'Standard. Serveren signerer alt; agenter med pinnet nøgle kræver det, ældre agenter virker som før.' },
  { value: 'enforce', label: 'Kræv', text: 'Jobs leveres kun til agenter, der har pinnet nøglen. Ældre agenter får kun agentopdateringen.' },
];

/** Fanen "Agent-sikkerhed" under Indstillinger. f/set/save er sidens fælles indstillinger. */
export function AgentSecurityTab({ s, f, set, save, saving }: { s: Settings; f: Record<string, string>; set: (k: string, v: string) => void; save: () => void; saving: boolean }) {
  const qc = useQueryClient();
  const sec = (s as unknown as { security?: AgentSecurity }).security;
  const mode = (f.signed_jobs || sec?.mode || 'sign') as SignedJobs;
  const c = sec?.machines;
  const setMode = (v: string) => {
    const missing = c ? c.total - c.pending - c.pinned : 0;
    if (v === 'enforce' && missing > 0) modals.openConfirmModal({
      title: 'Kræv signerede jobs?',
      children: <Text size="sm">{missing} agent{missing === 1 ? '' : 'er'} har ikke pinnet jobnøglen endnu. De får kun agentopdateringen og ingen andre jobs, før de har opdateret og pinnet nøglen.</Text>,
      labels: { confirm: 'Kræv alligevel', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: () => set('signed_jobs', v),
    });
    else set('signed_jobs', v);
  };
  const enrollOn = (f.enroll_key_enabled ?? (sec?.enrollKeyEnabled === false ? '0' : '1')) !== '0';
  const ar = sec?.accessRequests;
  const access = f.access_requests || ar?.mode || 'lan';
  const h = sec?.https;
  return (
    <SimpleGrid cols={{ base: 1, lg: 2 }}>
      <Stack gap={0}>
        <Panel title="Agent-sikkerhed" right={<Button size="xs" loading={saving} onClick={save}>Gem</Button>}>
          <Stack gap="sm">
            <div><Text size="sm" fw={500} mb={4}>Kræv signerede jobs</Text>
              <SegmentedControl value={mode} onChange={setMode} data={MODES.map((x) => ({ value: x.value, label: x.label }))} />
              <Text size="xs" c="dimmed" mt={4}>{MODES.find((x) => x.value === mode)?.text}</Text>
              {sec && sec.mode !== mode && <Text size="xs" c="orange">Ikke gemt endnu – tryk Gem.</Text>}</div>
            {c && <Group gap={6}>
              <Badge variant="light" color="green">{c.pinned} pinnet</Badge><Badge variant="light" color="yellow">{c.unpinned} ikke pinnet</Badge>
              <Badge variant="light" color="gray">{c.old} ældre agent{c.old === 1 ? '' : 'er'}</Badge>
              {c.mismatch > 0 && <Badge variant="filled" color="red">{c.mismatch} anden nøgle</Badge>}
              {c.errors > 0 && <Badge variant="light" color="red">{c.errors} med afvisninger</Badge>}
              <Badge variant="light" color="blue">{c.https} på https</Badge>
              {c.pending > 0 && <Badge variant="light" color="orange">{c.pending} venter på godkendelse</Badge>}
            </Group>}
            {sec?.jobKey && <Group gap="xs" wrap="nowrap"><Text size="sm" c="dimmed">Jobnøgle</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{sec.jobKey}</Code>{copyBtn(sec.jobKey)}</Group>}
            <Text size="xs" c="dimmed">Se docs/sikkerhed-agent.md for den trinvise udrulning.</Text>
          </Stack>
        </Panel>
        <Panel title="Tilmelding">
          <Stack gap="sm">
            <SegmentedControl value={enrollOn ? '1' : '0'} onChange={(v) => set('enroll_key_enabled', v)}
              data={[{ value: '1', label: 'Tillad den globale tilmeldingsnøgle' }, { value: '0', label: 'Kun engangskoder' }]} />
            <Text size="xs" c="dimmed">Engangskoder virker altid, også når den globale nøgle er slået fra. Ændringen gælder, når du trykker Gem.</Text>
            <Group><Button variant="default" color="red" onClick={() => askRotateKey(qc)}>Skift tilmeldingsnøgle</Button></Group>
            <div><Text size="sm" fw={500} mb={4} mt="xs">Anmod om adgang (uden nøgle)</Text>
              <SegmentedControl value={access} onChange={(v) => set('access_requests', v)}
                data={[{ value: 'off', label: 'Fra' }, { value: 'lan', label: 'Kun hjemmenet og VPN' }, { value: 'on', label: 'Overalt' }]} />
              <Text size="xs" c="dimmed" mt={4}>{ACCESS_TEXT[access] ?? ACCESS_TEXT.lan}</Text></div>
            {access !== 'off' && <Group grow>
              <NumberInput label="Højst åbne anmodninger" min={1} max={100} value={Number(f.access_requests_max ?? ar?.max ?? 10)}
                onChange={(v) => set('access_requests_max', String(v || 10))} />
              <NumberInput label="Udløber efter (timer)" min={1} max={168} value={Number(f.access_requests_hours ?? ar?.hours ?? 24)}
                onChange={(v) => set('access_requests_hours', String(v || 24))} />
            </Group>}
          </Stack>
        </Panel>
        <Panel title="HTTPS til agenter">
          <Stack gap="sm">
            {h?.port ? <Group gap={6}><Text size="sm">https på port <b>{h.port}</b></Text>{h.selfSigned && <Badge variant="light" color="gray">Selvsigneret</Badge>}</Group>
              : !h?.error && <Text size="sm" c="dimmed">Start serveren med miljøvariablen <Code>HTTPS_PORT</Code> (fx 8586) for at slå https til. Certifikatet laves selv (selvsigneret) eller angives med <Code>HTTPS_CERT</Code>/<Code>HTTPS_KEY</Code>.</Text>}
            {h?.error && <Text size="sm" c="red">{h.error}</Text>}
            {h?.pin && <Group gap="xs" wrap="nowrap"><Text size="sm" c="dimmed">Fingeraftryk</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{pin(h.pin)}</Code>{copyBtn(pin(h.pin))}</Group>}
            <TextInput label="https-adresse til agenter" description="Kun agenter med pinnet jobnøgle flytter; installationer og ældre agenter bruger fortsat serveradressen."
              placeholder={h?.port ? `https://<server>:${h.port}` : 'https://…'} value={f.agent_https_url ?? ''} onChange={(e) => set('agent_https_url', e.currentTarget.value)} />
            <TextInput label="TLS-fingeraftryk (valgfrit)" description="Fingeraftryk, hvis https klares af en proxy med eget selvsigneret certifikat. Tom = serverens eget."
              placeholder={sec?.tlsPin ? pin(sec.tlsPin) : ''} value={f.tls_pin ?? ''} onChange={(e) => set('tls_pin', e.currentTarget.value)} />
            {c && <Text size="xs" c="dimmed">{c.tlsPinned} agent{c.tlsPinned === 1 ? '' : 'er'} har fastlåst TLS-certifikatet.</Text>}
          </Stack>
        </Panel>
      </Stack>
      <EnrollCodesPanel />
    </SimpleGrid>
  );
}

const ACCESS_TEXT: Record<string, string> = {
  off: 'Maskiner uden nøgle kan ikke bede om adgang. Brug en engangskode.',
  lan: 'En maskine på hjemmenettet eller VPN kan hente agenten, finde serveren og bede dig om lov. Du får besked, sammenligner koden med brugeren og vælger profilen. Maskiner, der forbinder via den offentlige adresse, regnes som udefra – også når de står derhjemme. Brug serverens adresse på hjemmenettet.',
  on: 'Også maskiner udefra (fx gennem Cloudflare) kan bede om adgang. Rate-limit og loftet begrænser misbrug, men bekræft altid koden med brugeren.',
};

const CODE_STATE: Record<EnrollCode['state'], { label: string; color: string }> = {
  active: { label: 'Aktiv', color: 'green' }, used: { label: 'Brugt', color: 'gray' }, expired: { label: 'Udløbet', color: 'gray' },
};

/** Engangskoder: én installation pr. kode, udløber af sig selv. */
function EnrollCodesPanel() {
  const qc = useQueryClient();
  const { data: codes } = useApi<EnrollCode[]>(['enroll-codes'], '/api/enroll-codes');
  const { data: ms = [] } = useApi<MachineRow[]>(['machines'], '/api/machines');
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], '/api/profiles');
  const [f, setF] = useState({ label: '', hours: 24 as number | string, replace: null as string | null, profile: null as string | null });
  const [busy, setBusy] = useState(false);
  const [made, setMade] = useState<NewEnrollCode | null>(null);
  const create = async () => {
    setBusy(true);
    try {
      const r = await post<NewEnrollCode>('/api/enroll-codes', { label: f.label || undefined, hours: Number(f.hours) || 24,
        replaceMachineId: f.replace ? Number(f.replace) : null, profileId: f.profile ? Number(f.profile) : null });
      setMade(r); setF({ label: '', hours: 24, replace: null, profile: null }); qc.invalidateQueries({ queryKey: ['enroll-codes'] });
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const remove = (x: EnrollCode) => modals.openConfirmModal({
    title: 'Slet engangskoden?', children: <Text size="sm">Koden {x.hint}{x.label ? ` (${x.label})` : ''} kan derefter ikke bruges.</Text>,
    labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: async () => { try { await del(`/api/enroll-codes/${x.id}`); qc.invalidateQueries({ queryKey: ['enroll-codes'] }); } catch (e) { notifyErr(e); } },
  });
  return (
    <Panel title="Engangskoder">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">En kode tilmelder én maskine og udløber af sig selv. Brug den i stedet for den globale nøgle, eller til at geninstallere en bestemt maskine.</Text>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Beskrivelse" placeholder="fx ny bærbar" value={f.label} onChange={(e) => setF({ ...f, label: e.currentTarget.value })} />
          <NumberInput label="Gyldighed (timer)" min={1} value={f.hours} onChange={(v) => setF({ ...f, hours: v })} />
          <Select label="Til geninstallation af" placeholder="Ingen (ny maskine)" clearable searchable value={f.replace} onChange={(v) => setF({ ...f, replace: v })}
            data={ms.filter((m) => m.replacesId == null).map((m) => ({ value: String(m.id), label: m.name }))} />
          <Select label="Profil" placeholder="Vælges ved installationen" clearable value={f.profile} onChange={(v) => setF({ ...f, profile: v })}
            data={profiles.map((p) => ({ value: String(p.id), label: p.name }))} />
        </SimpleGrid>
        <Group><Button loading={busy} onClick={create}>Ny engangskode</Button></Group>
        {!codes ? null : !codes.length ? <Empty>Ingen engangskoder endnu</Empty> : (
          <Table verticalSpacing={4}><Table.Tbody>{codes.map((x) => (
            <Table.Tr key={x.id}>
              <Table.Td><Text size="sm" fw={600}><Code>{x.hint}</Code> {x.label}</Text>
                <Text size="xs" c="dimmed">
                  {x.replaceName ? `Geninstallation af ${x.replaceName} · ` : ''}
                  {x.state === 'used' ? `Brugt ${ago(x.usedAt)}${x.usedByName ? ` af ${x.usedByName}` : ''}` : x.state === 'active' ? `Udløber ${fmt(x.expires)}` : `Udløb ${fmt(x.expires)}`}
                  {` · lavet af ${x.createdBy}`}</Text></Table.Td>
              <Table.Td w={80}><Badge variant="light" color={CODE_STATE[x.state].color}>{CODE_STATE[x.state].label}</Badge></Table.Td>
              <Table.Td w={60} ta="right">{x.state === 'active' && <Button size="compact-xs" variant="subtle" color="red" onClick={() => remove(x)}>Slet</Button>}</Table.Td>
            </Table.Tr>))}</Table.Tbody></Table>
        )}
      </Stack>
      <Modal opened={!!made} onClose={() => setMade(null)} title="Ny engangskode" size="lg">
        {made && <Stack gap="sm">
          <Alert color="yellow">Koden vises kun nu. Den kan bruges én gang og udløber {fmt(made.expires)}.</Alert>
          <Group gap="xs" wrap="nowrap"><Code fz="lg" style={{ flex: 1 }}>{made.code}</Code>{copyBtn(made.code, 'Kopiér kode')}</Group>
          {([['Linux/Mac (terminal som root)', made.linux], ['Windows (Terminal som administrator)', made.windows]] as const).map(([l, v]) => (
            <div key={l}><Text size="sm" fw={500} mb={4}>{l}</Text>
              <Group gap="xs" wrap="nowrap" align="stretch"><Code block style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{v}</Code>{copyBtn(v)}</Group></div>))}
          <Group justify="flex-end"><Button onClick={() => setMade(null)}>Luk</Button></Group>
        </Stack>}
      </Modal>
    </Panel>
  );
}
