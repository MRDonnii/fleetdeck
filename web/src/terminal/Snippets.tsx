import { useEffect, useMemo, useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Checkbox, Code, Group, Modal, Select, Stack, Switch, Text, Textarea, TextInput, Title, Tooltip, UnstyledButton } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconPencil, IconPlayerPlay, IconPlus, IconSearch, IconShieldLock } from '@tabler/icons-react';
import { TermError, term, type TermSnippet } from './client';

export interface RunTarget { id: string; label: string; protected: boolean; platform?: string | null }
interface Need { sessionId: string; label: string; protected: boolean }

const fail = (e: unknown) => notifications.show({ color: 'red', message: e instanceof Error ? e.message : 'Noget gik galt' });
const PLATFORMS = [{ value: '', label: 'Alle' }, { value: 'linux', label: 'Linux' }, { value: 'windows', label: 'Windows' }, { value: 'mac', label: 'Mac' }];

function Editor({ snippet, opened, onClose, onSaved }: { snippet: TermSnippet | null; opened: boolean; onClose: () => void; onSaved: () => void }) {
  const empty = { name: '', command: '', description: '', platform: '', risky: false };
  const [f, setF] = useState(empty); const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (opened) setF(snippet ? { name: snippet.name, command: snippet.command, description: snippet.description, platform: snippet.platform, risky: snippet.risky } : empty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, snippet]);
  const save = async () => {
    setBusy(true);
    try { await (snippet ? term('PUT', `snippets/${snippet.id}`, f) : term('POST', 'snippets', f)); onSaved(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); }
  };
  const remove = async () => { setBusy(true); try { await term('DELETE', `snippets/${snippet!.id}`); onSaved(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); } };
  return (
    <Modal opened={opened} onClose={onClose} title={snippet ? 'Redigér kommando' : 'Ny kommando'}>
      <Stack gap="sm">
        <TextInput label="Navn" value={f.name} onChange={(e) => setF({ ...f, name: e.currentTarget.value })} data-autofocus />
        <Textarea label="Kommando" description="Skriv {{navn}} for en værdi, der udfyldes, når kommandoen køres." autosize minRows={2} maxRows={8}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12.5 } }} value={f.command} onChange={(e) => setF({ ...f, command: e.currentTarget.value })} />
        <TextInput label="Beskrivelse" value={f.description} onChange={(e) => setF({ ...f, description: e.currentTarget.value })} />
        <Select label="Platform" data={PLATFORMS} value={f.platform} onChange={(v) => setF({ ...f, platform: v ?? '' })} allowDeselect={false} />
        <Switch label="Risikabel: kræv bekræftelse før kørsel" checked={f.risky} onChange={(e) => setF({ ...f, risky: e.currentTarget.checked })} />
        <Group justify="flex-end">{snippet && <Button mr="auto" color="red" variant="subtle" loading={busy} onClick={() => { void remove(); }}>Slet</Button>}<Button variant="default" onClick={onClose}>Annullér</Button><Button loading={busy} onClick={() => { void save(); }}>Gem</Button></Group>
      </Stack>
    </Modal>
  );
}

function RunDialog({ snippet, targets, activeId, onClose }: { snippet: TermSnippet | null; targets: RunTarget[]; activeId: string | null; onClose: () => void }) {
  const [values, setValues] = useState<Record<string, string>>({}); const [picked, setPicked] = useState<string[]>([]);
  const [needs, setNeeds] = useState<Need[] | null>(null); const [typed, setTyped] = useState<Record<string, string>>({}); const [busy, setBusy] = useState(false);
  const usable = useMemo(() => targets.filter((t) => !snippet?.platform || !t.platform || t.platform === snippet.platform), [targets, snippet]);
  useEffect(() => { if (snippet) { setValues({}); setNeeds(null); setTyped({}); setPicked(activeId && usable.some((t) => t.id === activeId) ? [activeId] : []); } }, [snippet, activeId, usable]);
  const run = async (confirm: boolean) => {
    setBusy(true);
    try {
      await term('POST', `snippets/${snippet!.id}/run`, { sessionIds: picked, values, confirm, confirmNames: typed });
      notifications.show({ color: 'green', message: `${snippet!.name} er sendt til ${picked.length} session${picked.length === 1 ? '' : 'er'}.` });
      onClose();
    } catch (e) {
      const d = e instanceof TermError ? (e.data as { code?: string; needs?: Need[] } | null) : null;
      if (d?.code === 'confirm' && d.needs) setNeeds(d.needs); else fail(e);
    } finally { setBusy(false); }
  };
  const ready = needs?.every((n) => !n.protected || (typed[n.sessionId] ?? '').trim().toLowerCase() === n.label.toLowerCase()) ?? false;
  return (
    <Modal opened={!!snippet} onClose={onClose} title={needs ? `Kør ${snippet?.name} på ${needs.map((n) => n.label).join(', ')}?` : snippet?.name}>
      {snippet && !needs && (
        <Stack gap="sm">
          <Code block style={{ whiteSpace: 'pre-wrap' }}>{snippet.command}</Code>
          {snippet.variables.map((v) => <TextInput key={v} label={v} value={values[v] ?? ''} onChange={(e) => { const x = e.currentTarget.value; setValues((s) => ({ ...s, [v]: x })); }} />)}
          <Checkbox.Group label="Kør i" value={picked} onChange={setPicked}>
            <Stack gap={6} mt={6}>{usable.map((t) => <Checkbox key={t.id} value={t.id} label={<Group gap={5}>{t.label}{t.protected && <IconShieldLock size={14} color="var(--fd-warn)" />}</Group>} />)}</Stack>
          </Checkbox.Group>
          {usable.length === 0 && <Text size="sm" c="dimmed">Åbn en terminal, der passer til kommandoen.</Text>}
          <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button leftSection={<IconPlayerPlay size={15} />} disabled={picked.length === 0} loading={busy} onClick={() => { void run(false); }}>Kør</Button></Group>
        </Stack>
      )}
      {snippet && needs && (
        <Stack gap="sm">
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />}>Kommandoen kan afbryde eller slette noget.</Alert>
          {needs.filter((n) => n.protected).map((n) => (
            <Stack key={n.sessionId} gap={6}>
              <Alert color="yellow" variant="light" icon={<IconShieldLock size={18} />}><b>{n.label}</b> er en beskyttet maskine og må ikke afbrydes ved et uheld.</Alert>
              <TextInput label={`Skriv ${n.label} for at bekræfte`} value={typed[n.sessionId] ?? ''} onChange={(e) => { const x = e.currentTarget.value; setTyped((s) => ({ ...s, [n.sessionId]: x })); }} autoComplete="off" />
            </Stack>
          ))}
          <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button color="red" disabled={!ready} loading={busy} onClick={() => { void run(true); }}>Kør på {needs.map((n) => n.label).join(', ')}</Button></Group>
        </Stack>
      )}
    </Modal>
  );
}

/** Gemte kommandoer: søg, udfyld variabler og kør i en eller flere åbne sessioner. */
export function SnippetPanel({ snippets, targets, activeId, reload }: { snippets: TermSnippet[]; targets: RunTarget[]; activeId: string | null; reload: () => void }) {
  const [q, setQ] = useState(''); const [run, setRun] = useState<TermSnippet | null>(null); const [edit, setEdit] = useState<TermSnippet | null>(null); const [editing, setEditing] = useState(false);
  const active = targets.find((t) => t.id === activeId);
  const list = snippets.filter((s) => (!active?.platform || !s.platform || s.platform === active.platform) && `${s.name} ${s.command}`.toLowerCase().includes(q.trim().toLowerCase()));
  return (
    <Stack gap={8} p={10} h="100%" style={{ overflow: 'auto' }}>
      <Group justify="space-between" wrap="nowrap"><Title order={5}>Kommandoer</Title><Tooltip label="Ny kommando"><ActionIcon variant="default" aria-label="Ny kommando" onClick={() => { setEdit(null); setEditing(true); }}><IconPlus size={15} /></ActionIcon></Tooltip></Group>
      <TextInput size="xs" leftSection={<IconSearch size={14} />} placeholder="Søg i kommandoer" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
      {list.map((s) => (
        <UnstyledButton key={s.id} className="fd-term-snip" onClick={() => setRun(s)}>
          <Group gap={6} wrap="nowrap"><Text size="sm" fw={600} flex={1} truncate>{s.name}</Text>{s.risk && <Badge size="xs" color="red" variant="light">Risikabel</Badge>}
            <ActionIcon component="span" size="xs" variant="subtle" color="gray" aria-label="Redigér" onClick={(e: React.MouseEvent) => { e.stopPropagation(); setEdit(s); setEditing(true); }}><IconPencil size={12} /></ActionIcon></Group>
          <Text size="xs" c="dimmed" ff="monospace" truncate>{s.command}</Text>
        </UnstyledButton>
      ))}
      {list.length === 0 && <Text size="sm" c="dimmed">Ingen kommandoer passer.</Text>}
      <RunDialog snippet={run} targets={targets} activeId={activeId} onClose={() => setRun(null)} />
      <Editor snippet={edit} opened={editing} onClose={() => setEditing(false)} onSaved={reload} />
    </Stack>
  );
}
