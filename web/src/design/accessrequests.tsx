import { useState } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Modal, Paper, SegmentedControl, Select, Stack, Table, Text, Textarea, TextInput } from '@mantine/core';
import { IconAlertTriangle, IconBan, IconKey } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { del, notifyErr, notifyOk, post, useApi } from '../api';
import { ago, fmt } from '../format';
import type { AccessBlockRow, AccessRequestRow, Profile, SentKey } from '../types';
import { Panel } from '../components';
import { CopyButton } from './copy';
import { approveBlocker, BLOCK_DURATIONS, blockAffects, blockLine, blockSummary, describe, expiresIn, isOpen, lastSeen, sortRows, STATE, type BlockChoice } from './accessRequestsText';

/** Alle anmodninger (administrator). Samme nøgle i App.tsx (tallet i menuen) og på Maskiner. */
export const useAccessRequests = (isAdmin: boolean) =>
  useApi<AccessRequestRow[]>(['access-requests'], isAdmin ? '/api/access-requests' : null, { refetchInterval: 30_000 });

const copyBtn = (v: string, label = 'Kopiér') => (
  <CopyButton value={v}>{({ copied, copy }) => <Button size="compact-sm" variant="default" onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : label}</Button>}</CopyButton>);

/** Den store kode, brugeren også ser på sin skærm. */
export const AccessCodeBox = ({ code, size = 28 }: { code: string; size?: number }) => (
  <Paper withBorder radius="md" px="md" py={6} className="fd-access-code" style={{ flexShrink: 0 }} aria-label={`Kode ${code}`}>
    <Text ff="monospace" fw={700} fz={size} lh={1.1} style={{ letterSpacing: '0.12em', whiteSpace: 'nowrap' }}>{code}</Text>
  </Paper>
);

function profileOptions(profiles: Profile[] | undefined) {
  return [{ value: '0', label: 'Ingen profil – kun overvågning og opdateringer' }, ...(profiles ?? []).map((p) => ({ value: String(p.id), label: p.name }))];
}

/** Øverst på Maskiner: anmodninger, der venter, og dem fra de sidste 7 dage. */
export function AccessRequestsSection() {
  const { data } = useAccessRequests(true);
  const [approve, setApprove] = useState<AccessRequestRow | null>(null);
  const [reject, setReject] = useState<AccessRequestRow | null>(null);
  const [sendKey, setSendKey] = useState<AccessRequestRow | null>(null);
  const [block, setBlock] = useState<AccessRequestRow | null>(null);
  const { data: blocks } = useApi<AccessBlockRow[]>(['access-requests', 'blocks'], '/api/access-blocks', { refetchInterval: 60_000 });
  const rows = sortRows(data ?? []);
  const open = rows.filter(isOpen);
  const done = rows.filter((r) => !isOpen(r));
  if (!rows.length && !blocks?.length) return null;
  return (
    <>
      {open.map((r) => <RequestCard key={r.id} r={r} onApprove={() => setApprove(r)} onReject={() => setReject(r)} onSendKey={() => setSendKey(r)} onBlock={() => setBlock(r)} />)}
      {done.length > 0 && <Panel title="Anmodninger om adgang – de sidste 7 dage" p={0}>
        <Table.ScrollContainer minWidth={560}>
          <Table verticalSpacing={6} fz="sm">
            <Table.Tbody>
              {done.map((r) => (
                <Table.Tr key={r.id}>
                  <Table.Td><Text size="sm" fw={500}>{r.approvedName ?? r.name}</Text><Text size="xs" c="dimmed">{describe(r)}</Text></Table.Td>
                  <Table.Td><Code>{r.code}</Code></Table.Td>
                  <Table.Td><Badge variant="light" color={STATE[r.state].color}>{STATE[r.state].label}</Badge>
                    {r.rejectReason && <Text size="xs" c="dimmed">{r.rejectReason}</Text>}</Table.Td>
                  <Table.Td><Text size="xs" c="dimmed">{r.decidedBy ? `${r.decidedBy} · ` : ''}{fmt(r.decidedAt ?? r.created)}</Text></Table.Td>
                  <Table.Td ta="right"><Button size="compact-xs" variant="subtle" color="gray" leftSection={<IconBan size={12} />} onClick={() => setBlock(r)}>Bloker…</Button></Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      </Panel>}
      <ApproveModal r={approve} onClose={() => setApprove(null)} />
      <RejectModal r={reject} onClose={() => setReject(null)} />
      {!!blocks?.length && <BlocksPanel blocks={blocks} />}
      <SendKeyModal r={sendKey} onClose={() => setSendKey(null)} />
      <BlockModal r={block} all={rows} onClose={() => setBlock(null)} />
    </>
  );
}

export function RequestCard({ r, onApprove, onReject, onSendKey, onBlock }: { r: AccessRequestRow; onApprove: () => void; onReject: () => void; onSendKey: () => void; onBlock?: () => void }) {
  const pending = r.state === 'pending';
  const seen = lastSeen(r);
  return (
    <Alert color={pending ? 'orange' : 'blue'} mb="md" icon={<IconKey size={20} />} className="fd-access-card"
      title={pending ? `${r.name} beder om adgang` : `${r.approvedName ?? r.name} er godkendt`}>
      <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
        <Stack gap={4} style={{ flex: '1 1 260px', minWidth: 0 }}>
          <Text size="sm">{describe(r)}</Text>
          <Text size="sm" c="dimmed">{r.requestedBy ? <>Beder: <b>{r.requestedBy}</b> · </> : ''}fra {r.ip} · {ago(r.created)} · udløber {expiresIn(r.expires)}</Text>
          {r.message && <Text size="sm" fs="italic">“{r.message}”</Text>}
          {r.conflict && <Text size="sm" c="orange.8">⚠ {r.conflict}</Text>}
          <Text size="xs" c={seen.silent ? 'orange.8' : 'dimmed'}>{seen.silent ? '⚠ ' : ''}{seen.text}</Text>
          {r.unencrypted
            ? <Text size="xs" c="orange.8">⚠ Ukrypteret forbindelse (http): andre på netværket kan se anmodningen. Nøglen sendes krypteret til maskinen, men bekræft koden med brugeren, inden du godkender.</Text>
            : !r.pinVerified && <Text size="xs" c="dimmed">Koden er ikke tjekket mod serverens TLS-certifikat (proxy uden kendt fingeraftryk). Bekræft hvem der beder, inden du godkender.</Text>}
          {!pending && <Text size="xs" c="dimmed">{STATE[r.state].label}{r.decidedBy ? ` · godkendt af ${r.decidedBy}` : ''}. Maskinen henter selv sin engangsnøgle.</Text>}
        </Stack>
        <Stack gap={6} align="center">
          <Text size="xs" c="dimmed">Kode på maskinen</Text>
          <AccessCodeBox code={r.code} />
        </Stack>
      </Group>
      <Group gap="xs" mt="sm">
        {pending && <Button size="xs" onClick={onApprove}>Godkend…</Button>}
        {pending && <Button size="xs" variant="default" onClick={onSendKey}>Send nøgle i stedet…</Button>}
        <Button size="xs" variant="subtle" color="red" onClick={onReject}>Afvis…</Button>
        {onBlock && <Button size="xs" variant="subtle" color="gray" leftSection={<IconBan size={14} />} onClick={onBlock}
          title="Afvis og bloker adressen, så enheden ikke kan optage de åbne pladser">Bloker…</Button>}
      </Group>
    </Alert>
  );
}

/** Godkend: profil skal vælges, navnet kan rettes, og koden skal være bekræftet med brugeren. */
export function ApproveModal({ r, onClose, profiles: given }: { r: AccessRequestRow | null; onClose: () => void; profiles?: Profile[] }) {
  const qc = useQueryClient();
  const { data: fetched } = useApi<Profile[]>(['profiles'], given || !r ? null : '/api/profiles');
  const profiles = given ?? fetched;
  const [profile, setProfile] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [forId, setForId] = useState<number | null>(null);
  if (r && forId !== r.id) { setForId(r.id); setProfile(null); setName(r.name); setConfirmed(false); }
  const blocker = approveBlocker({ profile, name, confirmed });
  const submit = async () => {
    if (!r || blocker) return;
    setBusy(true);
    try {
      await post(`/api/access-requests/${r.id}/approve`, { profileId: Number(profile), name: name.trim().toUpperCase() });
      notifyOk(`${name.trim().toUpperCase()} er godkendt – den tilmelder sig selv om lidt`);
      qc.invalidateQueries(); onClose();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={!!r} onClose={onClose} title={r ? `Godkend ${r.name}` : ''} centered>
      {r && <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap" align="center">
          <Text size="sm">Spørg brugeren{r.requestedBy ? ` (${r.requestedBy})` : ''}, hvilken kode der står på maskinen. Den skal være præcis den her:</Text>
          <AccessCodeBox code={r.code} size={24} />
        </Group>
        <Select label="Profil" description="Bestemmer, hvilke programmer og indstillinger maskinen får." placeholder="Vælg profil…" required
          data={profileOptions(profiles)} value={profile} onChange={setProfile} comboboxProps={{ withinPortal: true }} />
        <TextInput label="Navn i Fleetdeck" value={name} onChange={(e) => setName(e.currentTarget.value.toUpperCase())} maxLength={31}
          error={name && approveBlocker({ profile: '0', name, confirmed: true }) ? 'Kun A-Z, 0-9, - og _ (højst 31 tegn)' : undefined} />
        {r.conflict && <Text size="sm" c="orange.8">⚠ {r.conflict}</Text>}
        <Checkbox checked={confirmed} onChange={(e) => setConfirmed(e.currentTarget.checked)}
          label={<>Koden <b>{r.code}</b> er bekræftet med brugeren</>} />
        <Text size="xs" c="dimmed">Maskinen får en engangsnøgle, der kun virker for den. Den kan aldrig overtage en maskine, der findes i forvejen.</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button onClick={submit} loading={busy} disabled={!!blocker} title={blocker ?? undefined}>Godkend</Button>
        </Group>
      </Stack>}
    </Modal>
  );
}

function RejectModal({ r, onClose }: { r: AccessRequestRow | null; onClose: () => void }) {
  const qc = useQueryClient();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    if (!r) return;
    setBusy(true);
    try { await post(`/api/access-requests/${r.id}/reject`, { reason: reason.trim() || null }); notifyOk('Anmodningen er afvist'); qc.invalidateQueries(); setReason(''); onClose(); }
    catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={!!r} onClose={onClose} title={r ? `Afvis ${r.name}?` : ''} centered>
      <Stack gap="sm">
        <Text size="sm">Maskinen får besked og kan ikke komme på med denne anmodning. Begrundelsen vises på maskinen.</Text>
        <Textarea label="Begrundelse (valgfri)" maxLength={200} autosize minRows={2} value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color="red" onClick={submit} loading={busy}>Afvis</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** "Send nøgle i stedet": en almindelig engangskode, som du selv giver videre (vises kun én gang). */
function SendKeyModal({ r, onClose }: { r: AccessRequestRow | null; onClose: () => void }) {
  const qc = useQueryClient();
  const { data: profiles } = useApi<Profile[]>(['profiles'], r ? '/api/profiles' : null);
  const [profile, setProfile] = useState<string | null>(null);
  const [hours, setHours] = useState<string | null>('24');
  const [sent, setSent] = useState<SentKey | null>(null);
  const [busy, setBusy] = useState(false);
  const close = () => { setSent(null); setProfile(null); onClose(); };
  const submit = async () => {
    if (!r || profile == null) return;
    setBusy(true);
    try { setSent(await post<SentKey>(`/api/access-requests/${r.id}/send-key`, { profileId: Number(profile), hours: Number(hours ?? 24) })); qc.invalidateQueries(); }
    catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={!!r || !!sent} onClose={close} title={sent ? 'Engangsnøgle' : r ? `Send nøgle til ${r.name}` : ''} centered size="lg">
      {sent ? <Stack gap="sm">
        <Alert color="yellow">Nøglen vises kun nu. Giv den videre, fx som linket nedenfor (nøglen står efter #, så den ikke sendes til serveren).</Alert>
        <Group gap="xs" wrap="nowrap"><Text size="sm" w={70}>Nøgle</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{sent.code}</Code>{copyBtn(sent.code)}</Group>
        <Group gap="xs" wrap="nowrap"><Text size="sm" w={70}>Link</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{sent.link}</Code>{copyBtn(sent.link)}</Group>
        <Group gap="xs" wrap="nowrap"><Text size="sm" w={70}>Windows</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{sent.windows}</Code>{copyBtn(sent.windows)}</Group>
        <Group gap="xs" wrap="nowrap"><Text size="sm" w={70}>Linux/Mac</Text><Code style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{sent.linux}</Code>{copyBtn(sent.linux)}</Group>
        <Group justify="flex-end"><Button onClick={close}>Færdig</Button></Group>
      </Stack> : r && <Stack gap="sm">
        <Text size="sm">I stedet for at godkende anmodningen laver du en almindelig engangsnøgle, som du selv sender til brugeren. Anmodningen lukkes.</Text>
        <Select label="Profil" placeholder="Vælg profil…" required data={profileOptions(profiles)} value={profile} onChange={setProfile} comboboxProps={{ withinPortal: true }} />
        <Select label="Gyldig i" data={[{ value: '1', label: '1 time' }, { value: '24', label: '1 døgn' }, { value: '168', label: '7 dage' }]} value={hours} onChange={setHours} />
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>Annullér</Button>
          <Button onClick={submit} loading={busy} disabled={profile == null}>Lav nøgle</Button>
        </Group>
      </Stack>}
    </Modal>
  );
}

/** "Bloker…": IP-adressen (og evt. MAC-adresserne og navnet) i en valgt periode. Åbne anmodninger, der passer, afvises med det samme. */
function BlockModal({ r, all, onClose }: { r: AccessRequestRow | null; all: AccessRequestRow[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [c, setC] = useState<BlockChoice>({ ip: true, mac: false, name: false, hours: '24' });
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [forId, setForId] = useState<number | null>(null);
  // en delt IP (proxy, router, NAT) er ikke valgt på forhånd: en blokering ville ramme alle bag den
  if (r && forId !== r.id) { setForId(r.id); setC({ ip: r.ip !== 'socket' && !r.ipShared, mac: false, name: false, hours: '24' }); setReason(''); }
  const sum = r ? blockSummary(r, c) : { text: '', blocker: null };
  const affects = r ? blockAffects(r, all, c) : [];
  const submit = async () => {
    if (!r || sum.blocker) return;
    setBusy(true);
    try {
      const res = await post<{ blocks: number; closed: number }>(`/api/access-requests/${r.id}/block`, { ...c, hours: Number(c.hours), reason: reason.trim() || null });
      notifyOk(res.closed > 0 ? `Blokeret – ${res.closed} åben${res.closed === 1 ? '' : 'e'} anmodning${res.closed === 1 ? '' : 'er'} afvist` : 'Blokeret');
      qc.invalidateQueries(); onClose();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const macs = r?.macs ?? [];
  return (
    <Modal opened={!!r} onClose={onClose} title={r ? `Bloker ${r.name}` : ''} centered>
      {r && <Stack gap="sm">
        <Text size="sm">Enheden kan ikke anmode om adgang, mens blokeringen gælder. Åbne anmodninger, der passer, afvises med det samme,
          så de ikke optager pladserne. Enheden får samme svar som når anmodninger er slået fra.</Text>
        <Checkbox checked={c.ip} disabled={r.ip === 'socket'} onChange={(e) => setC({ ...c, ip: e.currentTarget.checked })}
          label={<>IP-adressen <Code>{r.ipKey || r.ip}</Code></>}
          description={(r.ipKey || r.ip).endsWith('/64') ? `Hele /64-nettet omkring ${r.ip} (ellers skifter enheden bare adresse).` : 'Den adresse, serveren så anmodningen komme fra.'} />
        {r.ipShared && <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} p="xs">
          <Text size="xs">{r.ipShared} Derfor er IP'en ikke valgt.</Text>
        </Alert>}
        <Checkbox checked={c.mac} disabled={!macs.length} onChange={(e) => setC({ ...c, mac: e.currentTarget.checked })}
          label={macs.length ? <>MAC-adresse{macs.length > 1 ? 'rne' : 'n'} {macs.slice(0, 3).map((m) => <Code key={m} mr={4}>{m}</Code>)}{macs.length > 3 ? `+${macs.length - 3}` : ''} <Text span size="xs" c="dimmed">(oplyst af enheden)</Text></> : 'MAC-adresse (ikke sendt med anmodningen)'}
          description="Enheden oplyser selv sine MAC-adresser – en anden enhed kan bruge dem for at komme uden om eller for at give sig ud for en rigtig maskine." />
        <Checkbox checked={c.name} onChange={(e) => setC({ ...c, name: e.currentTarget.checked })}
          label={<>Maskinnavnet <Code>{r.name}</Code> <Text span size="xs" c="dimmed">(oplyst af enheden)</Text></>}
          description="Navnet kan skiftes frit – og blokerer man det, rammer det også en rigtig maskine med samme navn." />
        <div>
          <Text size="sm" fw={500} mb={4}>Hvor længe</Text>
          <SegmentedControl fullWidth size="xs" data={BLOCK_DURATIONS} value={c.hours ?? ''} onChange={(v) => setC({ ...c, hours: v })} />
        </div>
        <Textarea label="Begrundelse (valgfri)" description="Vises på maskinen for anmodninger, der afvises nu, og i revisionsloggen." maxLength={200} autosize minRows={2}
          value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
        {sum.text && <Text size="sm" fw={500}>{sum.text}</Text>}
        {affects.length > 0 && <Alert color="red" variant="light" p="xs" title={affects.length === 1 ? 'Afviser også 1 anden åben anmodning' : `Afviser også ${affects.length} andre åbne anmodninger`}>
          <Text size="xs">{affects.slice(0, 6).map((o) => `${o.name} (${o.code})`).join(', ')}{affects.length > 6 ? ` og ${affects.length - 6} til` : ''}</Text>
        </Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color="red" leftSection={<IconBan size={16} />} onClick={submit} loading={busy} disabled={!!sum.blocker} title={sum.blocker ?? undefined}>Bloker</Button>
        </Group>
      </Stack>}
    </Modal>
  );
}

/** De blokeringer, der gælder nu – kan ophæves før tid. */
function BlocksPanel({ blocks }: { blocks: AccessBlockRow[] }) {
  const qc = useQueryClient();
  const [busy, setBusy] = useState<number | null>(null);
  const lift = async (b: AccessBlockRow) => {
    setBusy(b.id);
    try { await del(`/api/access-blocks/${b.id}`); notifyOk(`${b.label} er ikke længere blokeret`); qc.invalidateQueries(); }
    catch (e) { notifyErr(e); } finally { setBusy(null); }
  };
  return (
    <Panel title="Blokeret fra at anmode om adgang" p={0}>
      <Table.ScrollContainer minWidth={560}>
        <Table verticalSpacing={6} fz="sm">
          <Table.Tbody>
            {blocks.map((b) => (
              <Table.Tr key={b.id}>
                <Table.Td><Group gap={6} wrap="nowrap"><IconBan size={14} style={{ opacity: 0.6, flexShrink: 0 }} /><Text size="sm" fw={500} ff={b.kind === 'name' ? undefined : 'monospace'}>{b.label}</Text></Group>
                  <Text size="xs" c="dimmed">{b.requestName ? `Fra anmodningen ${b.requestName} · ` : ''}{b.createdBy} · {fmt(b.created)}{b.reason ? ` · ${b.reason}` : ''}</Text></Table.Td>
                <Table.Td><Text size="xs" c="dimmed">{blockLine(b)}</Text></Table.Td>
                <Table.Td ta="right"><Button size="compact-sm" variant="default" loading={busy === b.id} onClick={() => lift(b)}>Ophæv</Button></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Panel>
  );
}
