import { useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Code, Group, Modal, Paper, PasswordInput, SegmentedControl, Select, Stack, Text, Textarea, TextInput, Title, Tooltip } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { IconCheck, IconCopy, IconKey, IconPassword, IconPlus, IconTrash, IconUpload } from '@tabler/icons-react';
import { CopyButton } from '../design/copy';
import { term, type TermCredential, type TermHost } from './client';

const fail = (e: unknown) => notifications.show({ color: 'red', message: e instanceof Error ? e.message : 'Noget gik galt' });

function AddCredential({ opened, onClose, onSaved }: { opened: boolean; onClose: () => void; onSaved: () => void }) {
  const [kind, setKind] = useState('generate'); const [name, setName] = useState(''); const [password, setPassword] = useState('');
  const [privateKey, setPrivateKey] = useState(''); const [passphrase, setPassphrase] = useState(''); const [busy, setBusy] = useState(false);
  const reset = () => { setName(''); setPassword(''); setPrivateKey(''); setPassphrase(''); };
  const save = async () => {
    setBusy(true);
    try { await term('POST', 'credentials', { name, kind, password, privateKey, passphrase }); reset(); onSaved(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={() => { reset(); onClose(); }} title="Nyt login">
      <Stack gap="sm">
        <SegmentedControl fullWidth value={kind} onChange={setKind} data={[{ value: 'generate', label: 'Lav nøgle' }, { value: 'import', label: 'Indsæt nøgle' }, { value: 'password', label: 'Adgangskode' }]} />
        <TextInput label="Navn" value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
        {kind === 'generate' && <Text size="sm" c="dimmed">Fleetdeck laver en ed25519-nøgle. Den private del forlader aldrig serveren.</Text>}
        {kind === 'import' && <>
          <Textarea label="Privat nøgle" autosize minRows={4} maxRows={8} styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12 } }} value={privateKey} onChange={(e) => setPrivateKey(e.currentTarget.value)} />
          <PasswordInput label="Løsen (hvis nøglen har et)" value={passphrase} onChange={(e) => setPassphrase(e.currentTarget.value)} />
        </>}
        {kind === 'password' && <>
          <PasswordInput label="Adgangskode" value={password} onChange={(e) => setPassword(e.currentTarget.value)} />
          <Alert color="yellow" variant="light">Brug hellere en nøgle, hvis enheden understøtter det.</Alert>
        </>}
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button loading={busy} onClick={() => { void save(); }}>Gem</Button></Group>
      </Stack>
    </Modal>
  );
}

function InstallKey({ cred, hosts, onClose, onDone }: { cred: TermCredential | null; hosts: TermHost[]; onClose: () => void; onDone: () => void }) {
  const [host, setHost] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  const usable = hosts.filter((h) => h.credentialId && h.credentialId !== cred?.id);
  const go = async () => {
    setBusy(true);
    try { await term('POST', `hosts/${host}/install-key`, { credentialId: cred!.id }); notifications.show({ color: 'green', message: 'Nøglen er lagt på værten, og værten bruger den nu.' }); onDone(); onClose(); }
    catch (e) { fail(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={!!cred} onClose={onClose} title={`Installér ${cred?.name ?? ''} på en vært`}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Fleetdeck logger ind med værtens nuværende login, lægger den offentlige nøgle i authorized_keys og skifter derefter værten til nøglen.</Text>
        <Select label="Vært" placeholder={usable.length ? 'Vælg vært' : 'Ingen værter med et andet login'} data={usable.map((h) => ({ value: String(h.id), label: h.name }))} value={host} onChange={setHost} />
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button disabled={!host} loading={busy} onClick={() => { void go(); }}>Installér</Button></Group>
      </Stack>
    </Modal>
  );
}

/** Nøgleboksen. Listen viser kun navn, type og offentlig nøgle: hemmeligheder sendes aldrig til browseren. */
export function Vault({ creds, hosts, reload }: { creds: TermCredential[]; hosts: TermHost[]; reload: () => void }) {
  const [adding, setAdding] = useState(false); const [install, setInstall] = useState<TermCredential | null>(null);
  const remove = (c: TermCredential) => modals.openConfirmModal({
    title: `Slet ${c.name}?`, labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    children: <Text size="sm">{c.hosts ? `${c.hosts} vært(er) mister deres login.` : 'Login bruges ikke af nogen vært.'} Det kan ikke fortrydes.</Text>,
    onConfirm: () => { void term('DELETE', `credentials/${c.id}`).then(reload).catch(fail); },
  });
  return (
    <Stack p="md" gap="sm" style={{ overflow: 'auto', height: '100%' }}>
      <Group justify="space-between" wrap="nowrap">
        <div><Title order={3}>Nøgleboks</Title><Text size="sm" c="dimmed">Krypteret på serveren. Indholdet sendes aldrig til browseren.</Text></div>
        <Button leftSection={<IconPlus size={16} />} onClick={() => setAdding(true)}>Nyt login</Button>
      </Group>
      {creds.length === 0 && <Text c="dimmed" size="sm">Der er ingen login endnu.</Text>}
      {creds.map((c) => (
        <Paper key={c.id} p="sm">
          <Group wrap="nowrap" align="flex-start">
            {c.kind === 'key' ? <IconKey size={20} /> : <IconPassword size={20} />}
            <Stack gap={4} flex={1} miw={0}>
              <Group gap={8}><Text fw={600}>{c.name}</Text><Badge size="xs" variant="light" color={c.kind === 'key' ? 'teal' : 'yellow'}>{c.kind === 'key' ? 'SSH-nøgle' : 'Adgangskode'}</Badge><Text size="xs" c="dimmed">{c.hosts} vært(er)</Text></Group>
              {c.fingerprint && <Code style={{ overflowWrap: 'anywhere' }}>{c.fingerprint}</Code>}
            </Stack>
            {c.publicKey && <CopyButton value={c.publicKey}>{({ copied, copy }) => <Tooltip label={copied ? 'Kopieret' : 'Kopiér offentlig nøgle'}><ActionIcon variant="default" size="lg" aria-label="Kopiér offentlig nøgle" onClick={copy}>{copied ? <IconCheck size={16} /> : <IconCopy size={16} />}</ActionIcon></Tooltip>}</CopyButton>}
            {c.publicKey && <Tooltip label="Installér på vært"><ActionIcon variant="default" size="lg" aria-label="Installér på vært" onClick={() => setInstall(c)}><IconUpload size={16} /></ActionIcon></Tooltip>}
            <Tooltip label="Slet"><ActionIcon variant="default" color="red" size="lg" aria-label="Slet" onClick={() => remove(c)}><IconTrash size={16} /></ActionIcon></Tooltip>
          </Group>
        </Paper>
      ))}
      <AddCredential opened={adding} onClose={() => setAdding(false)} onSaved={reload} />
      <InstallKey cred={install} hosts={hosts} onClose={() => setInstall(null)} onDone={reload} />
    </Stack>
  );
}
