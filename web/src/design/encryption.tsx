import { useState, type FormEvent } from 'react';
import { Alert, Anchor, Badge, Button, Code, Group, Modal, PasswordInput, ScrollArea, SegmentedControl, Stack, Table, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconKey, IconLock, IconLockOpen } from '@tabler/icons-react';
import { ApiError, notifyErr, post, useAction, useApi } from '../api';
import { Panel } from '../components';
import { ago, fmt } from '../format';
import { CopyButton } from './copy';
import type { MachineDetail } from '../types';

interface EncVolume { mount: string; system: boolean; encrypted?: boolean | null; protected?: boolean | null; status?: string | null; method?: string | null; percent?: number | null; recoveryKeys: number }
interface EncInfo { kind: string; portable?: boolean | null; volumes: EncVolume[]; hasPersonalKey?: boolean | null; error?: string | null }
interface KeyView { id: number; kind: string; volume: string; protectorId: string; created: string; lastSeen: string; superseded: string | null; source: string }
interface EncData {
  encryption: EncInfo | null; legacy?: string | null; keys: KeyView[]; canRotate: boolean;
  rotateJob?: { id: number; status: string; created: string; finished?: string | null } | null;
}

const KIND: Record<string, string> = { bitlocker: 'BitLocker', filevault: 'FileVault', luks: 'LUKS' };
const ACTIVE = ['pending', 'running', 'waiting_user'];

/** Bekræftelsen (gælder 5 minutter) huskes kun i hukommelsen – aldrig i localStorage. */
let reauth: { token: string; until: number } | null = null;
const validReauth = () => (reauth && reauth.until > Date.now() + 5_000 ? reauth.token : null);

function statusBadge(v: EncVolume, kind: string) {
  const pct = v.percent != null ? ` ${Math.round(v.percent)} %` : '';
  if (kind === 'luks') return v.status === 'Locked' ? <Badge variant="light" color="gray">Låst</Badge>
    : v.encrypted ? <Badge variant="light" color="green">Krypteret</Badge> : <Badge variant="light" color="red">Ikke krypteret</Badge>;
  switch (v.status) {
    case 'EncryptionInProgress': return <Badge variant="light" color="yellow">Krypterer{pct}</Badge>;
    case 'DecryptionInProgress': return <Badge variant="light" color="orange">Dekrypterer{pct}</Badge>;
    case 'EncryptionPaused': return <Badge variant="light" color="orange">Kryptering på pause{pct}</Badge>;
    case 'DecryptionPaused': return <Badge variant="light" color="orange">Dekryptering på pause{pct}</Badge>;
    case 'FullyDecrypted': return <Badge variant="light" color="red">Ikke krypteret</Badge>;
  }
  if (v.encrypted && v.protected === false) return <Badge variant="light" color="orange">Krypteret, beskyttelse på pause</Badge>;
  if (v.encrypted || v.protected) return <Badge variant="light" color="green">Krypteret</Badge>;
  return <Badge variant="light" color="gray">Ukendt</Badge>;
}

/** Kryptering på maskinens side: status pr. drev, gemte gendannelsesnøgler og "Vis nøgle" (administrator + bekræftelse). */
export function EncryptionPanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const { data } = useApi<EncData>(['encryption', m.id], `/api/machines/${m.id}/encryption`);
  const [ask, setAsk] = useState<KeyView | null>(null);
  const [shown, setShown] = useState<{ k: KeyView; key: string } | null>(null);
  const [old, setOld] = useState(false);
  const rotate = useAction(() => post(`/api/machines/${m.id}/encryption/rotate`), "Sendt til Mac'en – brugeren skal skrive sin adgangskode");
  if (!data || (!data.encryption && !data.keys.length)) return null;
  const e = data.encryption;
  const kind = e?.kind ?? data.keys[0]?.kind ?? '';
  const escrowable = kind === 'bitlocker' || kind === 'filevault';
  const current = data.keys.filter((k) => !k.superseded);
  const previous = data.keys.filter((k) => k.superseded);
  const hasKey = (v: EncVolume) => current.some((k) => k.kind === kind && k.volume.toUpperCase() === v.mount.toUpperCase());
  const job = data.rotateJob && ACTIVE.includes(data.rotateJob.status) ? data.rotateJob : null;

  const reveal = async (k: KeyView, token: string) => {
    try {
      const r = await post<{ key: string }>(`/api/machines/${m.id}/recovery-keys/${k.id}/reveal`, { reauth: token });
      setShown({ k, key: r.key });
    } catch (x) {
      if (x instanceof ApiError && x.status === 428) { reauth = null; setAsk(k); } else notifyErr(x);
    }
  };
  const start = (k: KeyView) => modals.openConfirmModal({
    title: `Vis gendannelsesnøglen til ${k.volume}?`,
    children: <Text size="sm">Med nøglen kan disken låses op uden brugerens adgangskode. Visningen skrives i revisionsloggen, og der sendes en besked om den.</Text>,
    labels: { confirm: 'Fortsæt', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: () => { const t = validReauth(); if (t) void reveal(k, t); else setAsk(k); },
  });
  const askRotate = () => modals.openConfirmModal({
    title: 'Ny gendannelsesnøgle til Fleetdeck?',
    children: <Stack gap="xs">
      <Text size="sm">Brugeren, der er logget ind på {m.name}, får en dialog og skal skrive sin adgangskode til Mac&apos;en. Adgangskoden bliver på Mac&apos;en.</Text>
      <Text size="sm">FileVault laver så en ny personlig gendannelsesnøgle, som gemmes krypteret her. Den gamle nøgle holder op med at virke.</Text>
    </Stack>,
    labels: { confirm: 'Send til Mac\'en', cancel: 'Annullér' }, onConfirm: () => rotate.mutate(undefined),
  });

  const keyRow = (k: KeyView) => (
    <Group key={k.id} justify="space-between" wrap="nowrap" gap="xs">
      <Stack gap={0} style={{ minWidth: 0 }}>
        <Group gap={6}><IconKey size={14} /><Text size="sm" fw={500}>{KIND[k.kind] ?? k.kind} {k.volume}</Text>
          {k.superseded && <Badge size="xs" variant="light" color="gray">tidligere</Badge>}</Group>
        <Text size="xs" c="dimmed" truncate>
          {k.kind === 'bitlocker' ? `ID ${k.protectorId} · ` : ''}gemt {fmt(k.created)}{k.superseded ? ` · udskiftet ${fmt(k.superseded)}` : ` · senest set ${ago(k.lastSeen)}`}
        </Text>
      </Stack>
      {isAdmin && <Button size="compact-xs" variant="light" color={k.superseded ? 'gray' : 'red'} onClick={() => start(k)}>Vis nøgle</Button>}
    </Group>
  );

  return (
    <Panel title="Kryptering" right={isAdmin && data.canRotate && (
      <Button size="xs" variant="light" leftSection={<IconKey size={14} />} loading={rotate.isPending} disabled={!!job} onClick={askRotate}>Ny gendannelsesnøgle til Fleetdeck</Button>)}>
      <Stack gap="sm">
        {e?.error && <Alert color="yellow" p="xs">{e.error}</Alert>}
        {e && e.volumes.length > 0 && (
          <ScrollArea type="auto"><Table miw={520} verticalSpacing={4}>
            <Table.Thead><Table.Tr><Table.Th>Drev</Table.Th><Table.Th>{KIND[kind] ?? 'Kryptering'}</Table.Th><Table.Th>Metode</Table.Th>{escrowable && <Table.Th>Gendannelsesnøgle</Table.Th>}</Table.Tr></Table.Thead>
            <Table.Tbody>{e.volumes.map((v) => (
              <Table.Tr key={v.mount}>
                <Table.Td><Group gap={6} wrap="nowrap">{v.encrypted ? <IconLock size={14} /> : <IconLockOpen size={14} />}<Text size="sm">{v.mount}</Text>
                  {v.system && <Badge size="xs" variant="light" color="gray">system</Badge>}</Group></Table.Td>
                <Table.Td>{statusBadge(v, kind)}</Table.Td>
                <Table.Td c="dimmed"><Text size="sm">{v.method ?? '–'}</Text></Table.Td>
                {escrowable && <Table.Td>{hasKey(v) ? <Badge variant="light" color="green">Gemt i Fleetdeck</Badge>
                  : v.protected ? <Badge variant="light" color="orange">{kind === 'bitlocker' && v.recoveryKeys === 0 ? 'Drevet har ingen' : 'Mangler'}</Badge> : <Text size="sm" c="dimmed">–</Text>}</Table.Td>}
              </Table.Tr>))}</Table.Tbody>
          </Table></ScrollArea>
        )}
        {!e && data.legacy && <Text size="sm" c="dimmed">{data.legacy} (agenten er for gammel til at vise detaljer)</Text>}
        {e?.portable && e.volumes.some((v) => v.system && !v.protected) && (
          <Alert color="blue" p="xs">Bærbar uden kryptering af systemdisken. Se forslaget under Alarmer.</Alert>)}
        {kind === 'luks' && <Text size="xs" c="dimmed">LUKS-nøgler gemmes ikke i Fleetdeck – her vises kun status.</Text>}
        {kind === 'filevault' && e?.hasPersonalKey === false && <Text size="xs" c="dimmed">Mac&apos;en har ingen personlig gendannelsesnøgle (fx kun iCloud).</Text>}
        {job && <Text size="sm" c="dimmed">Venter på, at brugeren på Mac&apos;en godkender en ny nøgle (job {job.id}, {ago(job.created)}).</Text>}
        {current.length > 0 && <Stack gap={6}>{current.map(keyRow)}</Stack>}
        {previous.length > 0 && (old
          ? <Stack gap={6}>{previous.map(keyRow)}</Stack>
          : <Anchor size="xs" onClick={() => setOld(true)}>Vis tidligere nøgler ({previous.length})</Anchor>)}
      </Stack>
      <ReauthModal opened={!!ask} onClose={() => setAsk(null)} onDone={(t) => { const k = ask; setAsk(null); if (k) void reveal(k, t); }} />
      <Modal opened={!!shown} onClose={() => setShown(null)} title={shown ? `Gendannelsesnøgle – ${m.name} ${shown.k.volume}` : ''} size="lg">
        {shown && <Stack>
          <Code block fz="lg" ta="center" style={{ letterSpacing: 1, userSelect: 'all' }}>{shown.key}</Code>
          {shown.k.kind === 'bitlocker' && <Text size="xs" c="dimmed">Pc&apos;en viser nøgle-ID&apos;et, når den beder om nøglen. Det skal begynde med {shown.k.protectorId.slice(0, 9)}.</Text>}
          {shown.k.superseded && <Alert color="yellow" p="xs">Det er en tidligere nøgle. Den virker kun på en disk, der ikke er krypteret om siden.</Alert>}
          <Group justify="flex-end">
            <CopyButton value={shown.key}>{({ copied, copy }) => <Button variant="default" onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
            <Button onClick={() => setShown(null)}>Luk</Button>
          </Group>
        </Stack>}
      </Modal>
    </Panel>
  );
}

/** Skriv adgangskoden (eller en kode fra appen) igen. Giver et bevis, der gælder i 5 minutter. */
function ReauthModal({ opened, onClose, onDone }: { opened: boolean; onClose: () => void; onDone: (token: string) => void }) {
  const [kind, setKind] = useState<'password' | 'code'>('password');
  const [v, setV] = useState('');
  const [busy, setBusy] = useState(false);
  const close = () => { setV(''); onClose(); };
  const submit = async (ev: FormEvent) => {
    ev.preventDefault(); setBusy(true);
    try {
      const r = await post<{ token: string; expires: string }>('/api/account/reauth', kind === 'code' ? { code: v } : { password: v });
      reauth = { token: r.token, until: new Date(r.expires).getTime() };
      setV('');
      onDone(r.token);
    } catch (x) { notifyErr(x); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={close} title="Bekræft at det er dig">
      <Stack component="form" onSubmit={submit}>
        <Text size="sm" c="dimmed">Gælder i 5 minutter.</Text>
        <SegmentedControl value={kind} onChange={(k) => { setKind(k as 'password' | 'code'); setV(''); }}
          data={[{ value: 'password', label: 'Adgangskode' }, { value: 'code', label: 'Kode fra appen' }]} />
        {kind === 'code'
          ? <TextInput label="Kode fra appen" value={v} onChange={(x) => setV(x.currentTarget.value)} autoComplete="one-time-code" inputMode="numeric" required autoFocus />
          : <PasswordInput label="Din adgangskode" value={v} onChange={(x) => setV(x.currentTarget.value)} autoComplete="current-password" required autoFocus />}
        <Group justify="flex-end"><Button variant="default" onClick={close}>Annullér</Button><Button type="submit" color="red" loading={busy}>Vis nøgle</Button></Group>
      </Stack>
    </Modal>
  );
}
