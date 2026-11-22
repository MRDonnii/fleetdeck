import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import {
  ActionIcon, Anchor, Badge, Box, Button, Chip, CopyButton, Group, Loader, Menu, Modal, MultiSelect, Paper, ScrollArea, SegmentedControl, Select, Stack, Switch, Text, Textarea, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAlertTriangle, IconArchive, IconArrowBackUp, IconArrowLeft, IconCheck, IconChecks, IconClock, IconCopy, IconDeviceDesktop, IconDownload, IconFileZip, IconInfoCircle, IconPhoto, IconPlus, IconSend, IconSettings, IconTemplate, IconTrash } from '@tabler/icons-react';
import { notifyOk, post, put, useAction, useApi } from '../api';
import { Empty, Loading, OnlineDot, PageHeader } from '../components';
import { ago, fmt } from '../format';
import type { MachineRow, Profile } from '../types';

// ------------------------------------------------------------------ typer (svarer til /api/helpdesk*)
export type HdAction = '' | 'reboot' | 'shutdown';
export interface HdButton { label: string; action: HdAction }
export interface HdMessage {
  id: number; fromMachine: boolean; author: string; text: string; buttons: HdButton[]; answer: number | null; answerAt: string | null;
  replyToId: number | null; created: string; deliveredAt: string | null; readAt: string | null; info: string; hasScreenshot: boolean;
}
/** Logfiler fra maskinen: sendt af brugeren (messageId) eller hentet af admin (jobId). */
export interface HdAttachment {
  id: number; messageId: number | null; jobId: number | null; kind: 'logs'; name: string; size: number;
  entries: { name: string; size: number }[]; by: string; created: string;
}
export interface HdLogsJob { id: number; status: string; created: string; finished: string | null; output: string; createdBy: string }
export interface HdThread {
  machineId: number; machine: string; status: 'open' | 'closed' | null; sender: string; platform: string; canCollectLogs: boolean;
  messages: HdMessage[]; attachments: HdAttachment[]; logsJob: HdLogsJob | null;
}
export interface HdConversation {
  machineId: number; machine: string; platform: string; online: boolean; user: string | null; status: 'open' | 'closed';
  lastActivity: string; preview: string; lastFromMachine: boolean; unread: number; waiting: number;
}
export interface HdTemplate { name: string; text: string; buttons: HdButton[] }
export interface HdSettings { sender: string; notify: boolean; templates: HdTemplate[] }

/** Svarknapperne, man kan vælge imellem (egne knapper kommer fra skabelonerne). */
export const PRESET_BUTTONS: HdButton[] = [
  { label: 'OK', action: '' }, { label: 'Ja', action: '' }, { label: 'Nej', action: '' }, { label: 'Senere', action: '' },
  { label: 'Ja, genstart nu', action: 'reboot' }, { label: 'Ja, sluk nu', action: 'shutdown' },
];
const MAX_BUTTONS = 4;

/** Antal ulæste svar til menuen (kun administratorer). */
export function useHelpdeskUnread(enabled: boolean) {
  const { data } = useApi<{ unread: number }>(['helpdesk', 'unread'], enabled ? '/api/helpdesk/unread' : null, { refetchInterval: 30_000 });
  return data?.unread ?? 0;
}

const time = (ts: string) => new Date(ts).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' });
const when = (ts: string) => (Date.now() - new Date(ts).getTime() < 20 * 3600_000 ? time(ts) : fmt(ts));

/** Status for en besked til maskinen: venter, leveret, læst – og svaret, hvis der var knapper. */
function Delivery({ m, online }: { m: HdMessage; online: boolean }) {
  const answered = m.answer != null && m.buttons[m.answer];
  return (
    <Group gap={6} justify="flex-end" mt={3} wrap="wrap">
      {!m.deliveredAt && <Text size="xs" c="dimmed"><IconClock size={12} style={{ verticalAlign: -2 }} /> {online ? 'Sendes…' : 'Venter – maskinen er offline. Den får beskeden, når den kommer online.'}</Text>}
      {m.deliveredAt && !m.readAt && <Text size="xs" c="dimmed"><IconCheck size={12} style={{ verticalAlign: -2 }} /> Leveret {time(m.deliveredAt)}</Text>}
      {m.readAt && <Text size="xs" c="dimmed"><IconChecks size={12} style={{ verticalAlign: -2 }} /> Læst {time(m.readAt)}</Text>}
      {answered && <Badge size="sm" tt="none" variant="light" color={answered.action ? 'orange' : 'green'} leftSection={<IconCheck size={11} />}>Svar: {answered.label}</Badge>}
    </Group>
  );
}

const kb = (n: number) => (n < 1024 * 1024 ? `${Math.max(1, Math.round(n / 1024))} kB` : `${(n / 1024 / 1024).toFixed(1).replace('.', ',')} MB`);
const ACTIVE = ['pending', 'running', 'waiting_user', 'restarting'];

/** Logfiler som en lille fil med "Hent" (zip). Indholdet står i værktøjstippet. */
function LogsFile({ a, align }: { a: HdAttachment; align: 'left' | 'right' }) {
  const what = a.entries.filter((e) => e.name !== 'LÆS-MIG.txt').map((e) => e.name.replace(/\.txt$/, '')).join(', ');
  return (
    <Group gap={8} wrap="nowrap" mt={6} justify={align === 'right' ? 'flex-end' : 'flex-start'} style={{ maxWidth: '100%', minWidth: 0 }}>
      <Paper withBorder radius="md" px="sm" py={6} style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0, maxWidth: '100%' }}>
        <IconFileZip size={22} style={{ flexShrink: 0 }} />
        <Box style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" fw={600} truncate>Logfiler · {kb(a.size)}</Text>
          <Tooltip label={a.name} withinPortal><Text size="xs" c="dimmed" truncate>{what || a.name}</Text></Tooltip>
        </Box>
        <Button component="a" href={`api/helpdesk/attachments/${a.id}`} download={a.name} size="compact-sm" variant="light" leftSection={<IconDownload size={14} />} style={{ flexShrink: 0 }}>Hent</Button>
      </Paper>
    </Group>
  );
}

/** Logfiler, admin hentede uden at brugeren gjorde noget: en linje midt i samtalen. */
function FetchedLogs({ a }: { a: HdAttachment }) {
  return (
    <Box my={8}>
      <Text size="xs" c="dimmed" ta="center">Logfiler hentet fra maskinen{a.by ? ` (${a.by})` : ''} · {when(a.created)}</Text>
      <Group justify="center" px={4} style={{ minWidth: 0 }}><Box style={{ maxWidth: '100%', minWidth: 0 }}><LogsFile a={a} align="left" /></Box></Group>
    </Box>
  );
}

/** Kopiér beskedens tekst (fx til en sag et andet sted). */
function Copy({ text }: { text: string }) {
  return (
    <CopyButton value={text} timeout={1500}>
      {({ copied, copy }) => (
        <Tooltip label={copied ? 'Kopieret' : 'Kopiér teksten'} withinPortal>
          <ActionIcon size="sm" variant="subtle" color={copied ? 'green' : 'gray'} onClick={copy} aria-label="Kopiér teksten">
            {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
          </ActionIcon>
        </Tooltip>
      )}
    </CopyButton>
  );
}

/** Én besked som boble. Al tekst vises som ren tekst (React escaper den; ingen links eller HTML). */
function Bubble({ m, sender, online, files }: { m: HdMessage; sender: string; online: boolean; files: HdAttachment[] }) {
  const mine = !m.fromMachine;
  return (
    <Box style={{ display: 'flex', justifyContent: mine ? 'flex-end' : 'flex-start' }} my={6}>
      <Box style={{ maxWidth: '80%', minWidth: 0 }}>
        <Group gap={4} justify={mine ? 'flex-end' : 'flex-start'} mx={4} mb={2} wrap="nowrap">
          <Text size="xs" c="dimmed">{mine ? `${sender} (${m.author})` : m.author} · {when(m.created)}</Text>
          {(m.text || m.info) && <Copy text={[m.text, m.info].filter(Boolean).join('\n\n')} />}
        </Group>
        {m.info && (
          <Paper withBorder radius="md" p="xs" mb={6}>
            <Group gap={6} mb={2}><IconInfoCircle size={15} /><Text size="xs" fw={600}>Sendt med sagen</Text></Group>
            <Text size="xs" c="dimmed" style={{ whiteSpace: 'pre-wrap' }}>{m.info}</Text>
          </Paper>
        )}
        {m.text && (
          <Paper radius="lg" px="sm" py={8} withBorder={!mine} bg={mine ? 'var(--mantine-primary-color-filled)' : 'var(--mantine-color-default-hover)'}
            style={{ borderBottomRightRadius: mine ? 4 : undefined, borderBottomLeftRadius: mine ? undefined : 4 }}>
            <Text size="sm" c={mine ? 'white' : undefined} style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{m.text}</Text>
          </Paper>
        )}
        {m.hasScreenshot && (
          <Anchor href={`api/helpdesk/screenshots/${m.id}`} target="_blank" rel="noopener noreferrer" display="block" mt={6}>
            <img src={`api/helpdesk/screenshots/${m.id}`} alt="Skærmbillede fra maskinen" style={{ maxWidth: 260, maxHeight: 170, borderRadius: 10, border: '1px solid var(--mantine-color-default-border)', display: 'block' }} />
          </Anchor>
        )}
        {files.map((a) => <LogsFile key={a.id} a={a} align={mine ? 'right' : 'left'} />)}
        {mine && m.buttons.length > 0 && (
          <Group gap={4} justify="flex-end" mt={4}>{m.buttons.map((b, i) => <Badge key={i} size="sm" tt="none" variant="outline" color={b.action ? 'orange' : 'gray'}>{b.label}</Badge>)}</Group>
        )}
        {mine && <Delivery m={m} online={online} />}
      </Box>
    </Box>
  );
}

/** Valg af svarknapper: de faste plus dem fra en skabelon. Højst fire. */
function ButtonPicker({ value, onChange }: { value: HdButton[]; onChange: (b: HdButton[]) => void }) {
  const all = [...PRESET_BUTTONS, ...value.filter((v) => !PRESET_BUTTONS.some((p) => p.label === v.label))];
  const on = value.map((b) => b.label);
  return (
    <Chip.Group multiple value={on} onChange={(labels) => onChange(all.filter((b) => labels.includes(b.label)).slice(0, MAX_BUTTONS))}>
      <Group gap={6}>
        {all.map((b) => (
          <Chip key={b.label} value={b.label} size="xs" color={b.action ? 'orange' : undefined} disabled={!on.includes(b.label) && on.length >= MAX_BUTTONS}>{b.label}</Chip>
        ))}
      </Group>
    </Chip.Group>
  );
}

const actionNote = (b: HdButton[]) => b.some((x) => x.action)
  ? `"${b.filter((x) => x.action).map((x) => x.label).join('" og "')}" ${b.some((x) => x.action === 'shutdown') ? 'genstarter eller slukker' : 'genstarter'} kun maskinen, hvis brugeren trykker. Det skrives i revisionsloggen.`
  : 'Brugeren kan også svare med tekst, et skærmbillede og logfiler.';

/** Skriv en besked: tekst, skabelon og svarknapper. */
function Compose({ placeholder, onSend, busy, hint }: { placeholder: string; onSend: (text: string, buttons: HdButton[]) => Promise<unknown>; busy: boolean; hint?: string }) {
  const [text, setText] = useState('');
  const [buttons, setButtons] = useState<HdButton[]>([]);
  const { data: settings } = useApi<HdSettings>(['helpdesk', 'settings'], '/api/helpdesk/settings');
  const send = () => { if (text.trim()) onSend(text, buttons).then(() => { setText(''); setButtons([]); }).catch(() => {}); };
  return (
    <Stack gap={8}>
      <Group gap={6} wrap="wrap">
        <Menu position="top-start" withinPortal>
          <Menu.Target><Button size="compact-sm" variant="default" leftSection={<IconTemplate size={14} />}>Skabeloner</Button></Menu.Target>
          <Menu.Dropdown>
            {(settings?.templates ?? []).length === 0 && <Menu.Item disabled>Ingen skabeloner endnu</Menu.Item>}
            {(settings?.templates ?? []).map((t) => <Menu.Item key={t.name} onClick={() => { setText(t.text); setButtons(t.buttons); }}>{t.name}</Menu.Item>)}
          </Menu.Dropdown>
        </Menu>
        <ButtonPicker value={buttons} onChange={setButtons} />
      </Group>
      <Group align="flex-end" gap={8} wrap="nowrap">
        <Textarea style={{ flex: 1 }} autosize minRows={2} maxRows={8} maxLength={4000} placeholder={placeholder} value={text} onChange={(e) => setText(e.currentTarget.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) send(); }} aria-label="Besked" />
        <Button leftSection={<IconSend size={16} />} onClick={send} loading={busy} disabled={!text.trim()}>Send</Button>
      </Group>
      <Text size="xs" c="dimmed">{hint ? `${hint} ` : ''}{actionNote(buttons)} Vises som ren tekst på maskinen.</Text>
    </Stack>
  );
}

/** Samtalen med én maskine (fanen Beskeder og højre side af oversigten). */
export function HelpdeskThread({ machineId, online, compact }: { machineId: number; online: boolean; compact?: boolean }) {
  const [fetching, setFetching] = useState(false);
  const { data } = useApi<HdThread>(['helpdesk', 'machine', machineId], `/api/machines/${machineId}/helpdesk`, { refetchInterval: fetching ? 3_000 : 15_000 });
  const send = useAction((b: { text: string; buttons: HdButton[] }) => post(`/api/helpdesk/send`, { machineIds: [machineId], ...b }));
  const status = useAction((close: boolean) => post(`/api/machines/${machineId}/helpdesk/${close ? 'close' : 'reopen'}`));
  const fetchLogs = useAction(() => post(`/api/machines/${machineId}/helpdesk/logs`), 'Logfilerne hentes – det tager typisk under et minut');
  const job = data?.logsJob ?? null;
  const jobActive = !!job && ACTIVE.includes(job.status);
  useEffect(() => setFetching(jobActive), [jobActive]);
  const unread = data?.messages.some((m) => m.fromMachine && !m.readAt);
  useEffect(() => { if (unread) post(`/api/machines/${machineId}/helpdesk/read`).catch(() => {}); }, [unread, machineId]);
  // nyeste besked nederst og i syne
  const viewport = useRef<HTMLDivElement>(null);
  const count = (data?.messages.length ?? 0) + (data?.attachments?.length ?? 0);
  useEffect(() => { const v = viewport.current; if (v) v.scrollTo({ top: v.scrollHeight }); }, [count, machineId]);
  if (!data) return <Loading />;
  const msgs = data.messages;
  const files = data.attachments ?? [];
  // samtalen i tidsorden: beskeder og de logfiler, admin hentede (uden en besked)
  const rows = [
    ...msgs.map((m) => ({ at: m.created, key: `m${m.id}`, el: <Bubble key={`m${m.id}`} m={m} sender={data.sender} online={online} files={files.filter((a) => a.messageId === m.id)} /> })),
    ...files.filter((a) => a.messageId == null).map((a) => ({ at: a.created, key: `a${a.id}`, el: <FetchedLogs key={`a${a.id}`} a={a} /> })),
  ].sort((a, b) => a.at.localeCompare(b.at));
  const failed = job && !jobActive && job.status !== 'success' && job.finished && Date.now() - new Date(job.finished).getTime() < 3600_000;
  return (
    <Stack gap={0} style={{ minHeight: 0, flex: 1 }}>
      <Group justify="space-between" px="md" py={8} gap={8} style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
        {data.status
          ? <Badge variant="light" tt="none" color={data.status === 'open' ? 'green' : 'gray'}>{data.status === 'open' ? 'Åben' : 'Lukket'}</Badge>
          : <Text size="xs" c="dimmed">Ingen sag endnu</Text>}
        <Group gap={6}>
          {data.canCollectLogs && (
            <Tooltip withinPortal multiline w={260} label={online ? "Agentens log, seneste fejl og advarsler og systeminfo som zip. Brugeren skal ikke gøre noget." : 'Maskinen er offline – logfilerne hentes, når den kommer online.'}>
              <Button size="compact-sm" variant="default" leftSection={jobActive ? <Loader size={12} /> : <IconFileZip size={14} />} disabled={jobActive}
                loading={fetchLogs.isPending} onClick={() => fetchLogs.mutate(undefined)}>
                {jobActive ? 'Henter logfiler…' : 'Hent logfiler'}
              </Button>
            </Tooltip>
          )}
          {data.status === 'open' && <Button size="compact-sm" variant="default" leftSection={<IconArchive size={14} />} onClick={() => status.mutate(true)}>Luk sagen</Button>}
          {data.status === 'closed' && <Button size="compact-sm" variant="default" leftSection={<IconArrowBackUp size={14} />} onClick={() => status.mutate(false)}>Åbn igen</Button>}
        </Group>
      </Group>
      <ScrollArea.Autosize mah={compact ? 520 : 'calc(100vh - 380px)'} px="md" py={4} type="auto" offsetScrollbars viewportRef={viewport} style={{ flex: 1 }}
        styles={{ content: { minWidth: 0, display: 'block' } }}>
        {rows.length === 0
          ? <Empty>Ingen beskeder endnu. Skriv til maskinen herunder – brugeren ser beskeden i et lille vindue og kan svare.</Empty>
          : rows.map((r) => r.el)}
        {jobActive && (
          <Group justify="center" gap={6} my={8}><Loader size={12} /><Text size="xs" c="dimmed">{online ? 'Henter logfiler fra maskinen…' : 'Venter – maskinen er offline. Logfilerne hentes, når den kommer online.'}</Text></Group>
        )}
        {failed && (
          <Group justify="center" gap={6} my={8} wrap="nowrap"><IconAlertTriangle size={14} color="var(--mantine-color-orange-6)" /><Text size="xs" c="dimmed">Logfilerne kunne ikke hentes: {job!.output || job!.status}</Text></Group>
        )}
      </ScrollArea.Autosize>
      <Box px="md" py="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <Compose placeholder={`Skriv en besked til ${data.machine}…`} busy={send.isPending}
          hint={online ? 'Maskinen er online – beskeden vises med det samme.' : 'Maskinen er offline – beskeden venter, til den kommer online.'}
          onSend={(text, buttons) => send.mutateAsync({ text, buttons })} />
      </Box>
    </Stack>
  );
}

type Filter = 'open' | 'unread' | 'closed' | 'all';

function ConversationRow({ c, active, onClick }: { c: HdConversation; active: boolean; onClick: () => void }) {
  return (
    <UnstyledButton onClick={onClick} px="md" py={10} w="100%" data-active={active || undefined}
      style={{ borderTop: '1px solid var(--mantine-color-default-border)', background: active ? 'var(--mantine-primary-color-light)' : undefined, display: 'block' }}>
      <Group gap={8} wrap="nowrap" align="flex-start">
        <IconDeviceDesktop size={18} style={{ marginTop: 2, flexShrink: 0 }} />
        <Box style={{ minWidth: 0, flex: 1 }}>
          <Group gap={6} wrap="nowrap">
            <Text fw={600} size="sm" truncate>{c.machine}</Text><OnlineDot online={c.online} />
            {c.user && <Text size="xs" c="dimmed" truncate>{c.user}</Text>}
            <Text size="xs" c="dimmed" ml="auto" style={{ flexShrink: 0 }}>{ago(c.lastActivity)}</Text>
          </Group>
          <Group gap={6} wrap="nowrap">
            <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{c.lastFromMachine ? '' : 'Du: '}{c.preview}</Text>
            {c.waiting > 0 && <Tooltip label="Venter på levering"><IconClock size={13} /></Tooltip>}
            {c.unread > 0 && <Badge size="sm" circle>{c.unread}</Badge>}
            {c.status === 'closed' && <Badge size="xs" variant="light" color="gray">Lukket</Badge>}
          </Group>
        </Box>
      </Group>
    </UnstyledButton>
  );
}

/** Siden Beskeder: alle samtaler til venstre, den valgte til højre (på mobilen: listen, og samtalen for sig). */
export function HelpdeskPage() {
  const { id } = useParams();
  const nav = useNavigate();
  const wide = useMediaQuery('(min-width: 62em)') ?? true;
  const [filter, setFilter] = useState<Filter>('open');
  const [composeOpen, setComposeOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const status = filter === 'open' || filter === 'closed' ? filter : 'all';
  const { data } = useApi<{ unread: number; conversations: HdConversation[] }>(['helpdesk', 'list', status], `/api/helpdesk?status=${status}`, { refetchInterval: 30_000 });
  const rows = (data?.conversations ?? []).filter((c) => filter !== 'unread' || c.unread > 0);
  const sel = id ? Number(id) : wide ? rows[0]?.machineId : undefined;
  const cur = (data?.conversations ?? []).find((c) => c.machineId === sel);
  const { data: machines = [] } = useApi<MachineRow[]>(['machines'], '/api/machines');
  const selOnline = cur?.online ?? machines.find((m) => m.id === sel)?.online ?? false;

  const list = (
    <Paper withBorder radius="md" style={{ overflow: 'hidden', width: wide ? 380 : '100%', flexShrink: 0 }}>
      <Box p="sm">
        <SegmentedControl size="xs" fullWidth value={filter} onChange={(v) => setFilter(v as Filter)}
          data={[{ value: 'open', label: 'Åbne' }, { value: 'unread', label: `Ulæste${data?.unread ? ` (${data.unread})` : ''}` }, { value: 'closed', label: 'Lukkede' }, { value: 'all', label: 'Alle' }]} />
      </Box>
      {!data ? <Loading /> : rows.length === 0
        ? <Empty>{filter === 'unread' ? 'Ingen ulæste svar.' : 'Ingen samtaler endnu. Tryk "Ny besked" for at skrive til en maskine.'}</Empty>
        : <ScrollArea.Autosize mah={wide ? 'calc(100vh - 250px)' : undefined}>{rows.map((c) => <ConversationRow key={c.machineId} c={c} active={c.machineId === sel} onClick={() => nav(`/beskeder/${c.machineId}`)} />)}</ScrollArea.Autosize>}
    </Paper>
  );
  const thread = sel ? (
    <Paper withBorder radius="md" style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <Group px="md" py={10} gap="sm" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }} wrap="nowrap">
        {!wide && <ActionIcon variant="subtle" color="gray" onClick={() => nav('/beskeder')} aria-label="Tilbage"><IconArrowLeft size={18} /></ActionIcon>}
        <IconDeviceDesktop size={20} />
        <Box style={{ minWidth: 0, flex: 1 }}>
          <Group gap={6}><Text fw={700} truncate>{cur?.machine ?? machines.find((m) => m.id === sel)?.name ?? `Maskine ${sel}`}</Text><OnlineDot online={selOnline} /></Group>
          {cur?.user && <Text size="xs" c="dimmed">{cur.user}</Text>}
        </Box>
        <Button size="compact-sm" variant="subtle" onClick={() => nav(`/maskiner/${sel}`)}>Maskinen</Button>
      </Group>
      <HelpdeskThread machineId={sel} online={selOnline} />
    </Paper>
  ) : wide ? <Paper withBorder radius="md" style={{ flex: 1 }}><Empty>Vælg en samtale.</Empty></Paper> : null;

  return (
    <>
      {(wide || !id) && (
        <PageHeader title="Beskeder" sub="En samtale pr. maskine. Brugeren svarer i et lille vindue på pc'en og kan selv skrive til support fra ikonet.">
          <Button variant="default" leftSection={<IconSettings size={16} />} onClick={() => setSettingsOpen(true)}>Skabeloner og afsender</Button>
          <Button leftSection={<IconPlus size={16} />} onClick={() => setComposeOpen(true)}>Ny besked</Button>
        </PageHeader>
      )}
      {wide ? <Group align="stretch" gap="md" wrap="nowrap">{list}{thread}</Group> : (id ? thread : list)}
      <NewMessageModal opened={composeOpen} onClose={() => setComposeOpen(false)} machines={machines} onSent={(ids) => { if (ids.length === 1) nav(`/beskeder/${ids[0]}`); }} />
      <HelpdeskSettingsModal opened={settingsOpen} onClose={() => setSettingsOpen(false)} />
    </>
  );
}

/** Ny besked til flere maskiner: udvalgte maskiner, alle med et mærke eller alle på en profil. */
export function NewMessageModal({ opened, onClose, machines, onSent, preset }: {
  opened: boolean; onClose: () => void; machines: MachineRow[]; onSent?: (ids: number[]) => void; preset?: number[];
}) {
  const [kind, setKind] = useState<'machines' | 'tag' | 'profile'>('machines');
  const [ids, setIds] = useState<string[]>([]);
  const [tag, setTag] = useState<string | null>(null);
  const [profile, setProfile] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [buttons, setButtons] = useState<HdButton[]>([]);
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], opened ? '/api/profiles' : null);
  const { data: settings } = useApi<HdSettings>(['helpdesk', 'settings'], opened ? '/api/helpdesk/settings' : null);
  useEffect(() => { if (opened) { setIds((preset ?? []).map(String)); setKind('machines'); } }, [opened, preset]);
  const tags = useMemo(() => [...new Set(machines.flatMap((m) => m.tags))].sort((a, b) => a.localeCompare(b, 'da')), [machines]);
  const targets = kind === 'machines' ? machines.filter((m) => ids.includes(String(m.id)))
    : kind === 'tag' ? machines.filter((m) => tag && m.tags.some((t) => t.toLowerCase() === tag.toLowerCase()))
    : machines.filter((m) => profile && m.profileId === Number(profile));
  const send = useAction((b: object) => post<{ sent: number; machineIds: number[] }>('/api/helpdesk/send', b));
  const go = () => send.mutate({ machineIds: kind === 'machines' ? ids.map(Number) : null, tag: kind === 'tag' ? tag : null, profileId: kind === 'profile' && profile ? Number(profile) : null, text, buttons }, {
    onSuccess: (r) => { notifyOk(`Sendt til ${r.sent === 1 ? '1 maskine' : `${r.sent} maskiner`}`); setText(''); setButtons([]); onClose(); onSent?.(r.machineIds); },
  });
  const online = targets.filter((m) => m.online).length;
  return (
    <Modal opened={opened} onClose={onClose} title="Ny besked" size="lg">
      <Stack gap="sm">
        <SegmentedControl value={kind} onChange={(v) => setKind(v as typeof kind)} data={[{ value: 'machines', label: 'Maskiner' }, { value: 'tag', label: 'Mærke' }, { value: 'profile', label: 'Profil' }]} />
        {kind === 'machines' && <MultiSelect label="Til" searchable placeholder="Vælg maskiner" value={ids} onChange={setIds} data={machines.map((m) => ({ value: String(m.id), label: m.name }))} />}
        {kind === 'tag' && <Select label="Til alle med mærket" searchable placeholder={tags.length ? 'Vælg et mærke' : 'Ingen maskiner har mærker endnu'} value={tag} onChange={setTag} data={tags} />}
        {kind === 'profile' && <Select label="Til alle på profilen" placeholder="Vælg en profil" value={profile} onChange={setProfile} data={profiles.map((p) => ({ value: String(p.id), label: p.name }))} />}
        <Text size="xs" c="dimmed">{targets.length === 0 ? 'Ingen maskiner valgt.' : `${targets.length === 1 ? '1 maskine' : `${targets.length} maskiner`}, ${online} online. De andre får beskeden, når de kommer online.`}</Text>
        <Select label="Skabelon" placeholder="Vælg en fast besked (valgfrit)" clearable data={(settings?.templates ?? []).map((t) => t.name)}
          onChange={(n) => { const t = settings?.templates.find((x) => x.name === n); if (t) { setText(t.text); setButtons(t.buttons); } }} />
        <Textarea label="Besked" autosize minRows={3} maxRows={10} maxLength={4000} value={text} onChange={(e) => setText(e.currentTarget.value)}
          description="Vises som ren tekst på maskinen. Links og HTML virker ikke." />
        <Box>
          <Text size="sm" fw={500} mb={4}>Svarknapper</Text>
          <ButtonPicker value={buttons} onChange={setButtons} />
          <Text size="xs" c="dimmed" mt={4}>{actionNote(buttons)}</Text>
        </Box>
        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button leftSection={<IconSend size={16} />} disabled={!text.trim() || targets.length === 0} loading={send.isPending} onClick={go}>
            {targets.length > 1 ? `Send til ${targets.length} maskiner` : 'Send'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Afsendernavn, notifikation ved nye svar og skabeloner. */
export function HelpdeskSettingsModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { data } = useApi<HdSettings>(['helpdesk', 'settings'], opened ? '/api/helpdesk/settings' : null);
  const [v, setV] = useState<HdSettings | null>(null);
  useEffect(() => { if (opened && data) setV(structuredClone(data)); }, [opened, data]);
  const save = useAction((s: HdSettings) => put('/api/helpdesk/settings', s), 'Gemt');
  const setT = (i: number, t: Partial<HdTemplate>) => setV((x) => x && { ...x, templates: x.templates.map((y, j) => (j === i ? { ...y, ...t } : y)) });
  return (
    <Modal opened={opened} onClose={onClose} title="Beskeder: afsender og skabeloner" size="lg">
      {!v ? <Loading /> : (
        <Stack gap="sm">
          <TextInput label="Afsendernavn" description="Det navn, brugeren ser som afsender på maskinen (fx IT-support)." maxLength={40} value={v.sender} onChange={(e) => setV({ ...v, sender: e.currentTarget.value })} />
          <Switch label="Notifikation ved nye svar" description="Som andre alarmer: Home Assistant og webhook, hvis de er sat op under Indstillinger." checked={v.notify} onChange={(e) => setV({ ...v, notify: e.currentTarget.checked })} />
          <Text fw={600} size="sm" mt="xs">Skabeloner</Text>
          {v.templates.length === 0 && <Text size="sm" c="dimmed">Ingen skabeloner.</Text>}
          {v.templates.map((t, i) => (
            <Paper key={i} withBorder radius="md" p="sm">
              <Stack gap={6}>
                <Group gap="xs" wrap="nowrap">
                  <TextInput style={{ flex: 1 }} size="xs" placeholder="Navn" maxLength={60} value={t.name} onChange={(e) => setT(i, { name: e.currentTarget.value })} aria-label="Skabelonens navn" />
                  <ActionIcon variant="subtle" color="red" aria-label="Slet skabelonen" onClick={() => setV({ ...v, templates: v.templates.filter((_, j) => j !== i) })}><IconTrash size={16} /></ActionIcon>
                </Group>
                <Textarea size="xs" autosize minRows={2} maxLength={4000} value={t.text} onChange={(e) => setT(i, { text: e.currentTarget.value })} aria-label="Skabelonens tekst" />
                <ButtonPicker value={t.buttons} onChange={(b) => setT(i, { buttons: b })} />
              </Stack>
            </Paper>
          ))}
          <Group justify="space-between" mt="xs">
            <Button variant="default" leftSection={<IconPlus size={16} />} onClick={() => setV({ ...v, templates: [...v.templates, { name: '', text: '', buttons: [] }] })}>Ny skabelon</Button>
            <Group gap="xs">
              <Button variant="default" onClick={onClose}>Annullér</Button>
              <Button loading={save.isPending} onClick={() => save.mutate(v, { onSuccess: onClose })}>Gem</Button>
            </Group>
          </Group>
          <Text size="xs" c="dimmed"><IconPhoto size={12} style={{ verticalAlign: -2 }} /> Samtaler og skærmbilleder hører til installationens data og kommer ikke med i opsætningsfilen. Afsender og skabeloner gør.</Text>
        </Stack>
      )}
    </Modal>
  );
}
