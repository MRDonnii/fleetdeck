import { useMemo, useState } from 'react';
import { Badge, Button, Code, FileInput, Group, Modal, ScrollArea, Select, SimpleGrid, Stack, Table, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconBrandSteam, IconSearch } from '@tabler/icons-react';
import { api, del, get, notifyErr, notifyOk, post, put, useApi } from '../api';
import { useMe } from '../auth';
import { options, PKGTYPE, RUNAS } from '../format';
import type { Package, PackageType, RunAs, WingetHit } from '../types';
import { Empty, Loading, MachinePicker, PageHeader, Panel, StatusBadge } from '../components';
import { useQueryClient } from '@tanstack/react-query';

type Draft = Omit<Package, 'id' | 'profiles' | 'fileOk'> & { id?: number };
const blank: Draft = { name: '', type: 'winget', identifier: '', args: '', runAs: 'system', detect: '', category: '', notes: '' };

export function PackagesPage() {
  const { isAdmin } = useMe();
  const { data: pkgs } = useApi<Package[]>(['packages'], '/api/packages');
  const qc = useQueryClient();
  const [q, setQ] = useState('');
  const [cat, setCat] = useState<string | null>(null);
  const [edit, setEdit] = useState<Draft | null>(null);
  const [search, setSearch] = useState(false);
  const [installFor, setInstallFor] = useState<Package | null>(null);
  const cats = useMemo(() => [...new Set((pkgs ?? []).map((p) => p.category || 'Andet'))].sort(), [pkgs]);
  if (!pkgs) return <Loading />;
  const rows = pkgs.filter((p) => (!q || `${p.name} ${p.identifier} ${p.notes}`.toLowerCase().includes(q.toLowerCase())) && (!cat || (p.category || 'Andet') === cat));
  return (
    <>
      <PageHeader title="Pakker og spil" sub="Kataloget over programmer og spil der kan installeres">
        {isAdmin && <>
          <Button variant="default" onClick={async () => {
            try { const r = await post<{ added: number }>('/api/packages/catalog'); notifyOk(r.added ? `${r.added} programmer tilføjet fra kataloget` : 'Kataloget er allerede tilføjet'); qc.invalidateQueries(); }
            catch (e) { notifyErr(e); }
          }}>Tilføj programkatalog</Button>
          <Button variant="default" leftSection={<IconBrandSteam size={16} />} onClick={() => setEdit({ ...blank, type: 'steam', runAs: 'user', category: 'Spil' })}>Tilføj Steam-spil</Button>
          <Button variant="default" leftSection={<IconSearch size={16} />} onClick={() => setSearch(true)}>Find i winget</Button>
          <Button onClick={() => setEdit({ ...blank })}>+ Ny pakke</Button>
        </>}
      </PageHeader>
      <Group mb="md"><TextInput leftSection={<IconSearch size={16} />} placeholder="Søg…" value={q} onChange={(e) => setQ(e.currentTarget.value)} w={{ base: '100%', sm: 280 }} />
        <Select placeholder="Alle kategorier" clearable data={cats} value={cat} onChange={setCat} w={{ base: '100%', sm: 200 }} /></Group>
      <Panel p={0}>
        <ScrollArea type="auto"><Table highlightOnHover miw={760}><Table.Thead><Table.Tr><Table.Th>Navn</Table.Th><Table.Th w={96}>Kilde</Table.Th><Table.Th>Id / fil</Table.Th>
          <Table.Th>Kører som</Table.Th><Table.Th>Kategori</Table.Th><Table.Th ta="right">Profiler</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>{rows.map((p) => (
            <Table.Tr key={p.id}>
              <Table.Td><Text fw={600} size="sm">{p.name}</Text>{p.notes && <Text size="xs" c="dimmed">{p.notes}</Text>}</Table.Td>
              <Table.Td><StatusBadge map={PKGTYPE} value={p.type} /></Table.Td>
              <Table.Td><Code>{p.identifier}</Code> {!p.fileOk && <Badge color="yellow" variant="light" size="sm">Mangler fil</Badge>}</Table.Td>
              <Table.Td>{RUNAS[p.runAs]}</Table.Td><Table.Td c="dimmed">{p.category}</Table.Td><Table.Td ta="right">{p.profiles}</Table.Td>
              <Table.Td style={{ whiteSpace: 'nowrap' }}>{isAdmin && <Group gap={4} wrap="nowrap">
                <Button size="compact-xs" variant="default" onClick={() => setEdit({ ...p })}>Redigér</Button>
                <Button size="compact-xs" variant="light" onClick={() => setInstallFor(p)}>Installér på…</Button></Group>}</Table.Td>
            </Table.Tr>))}</Table.Tbody></Table></ScrollArea>
        {!rows.length && <Empty>Ingen pakker</Empty>}
      </Panel>
      {edit && <PackageModal draft={edit} onClose={() => setEdit(null)} />}
      <WingetSearch opened={search} onClose={() => setSearch(false)} onPick={(h) => { setSearch(false); setEdit({ ...blank, name: h.name, identifier: h.id, runAs: h.perUser ? 'user' : 'system' }); }} />
      <MachinePicker opened={installFor !== null} onClose={() => setInstallFor(null)} title={`Installér ${installFor?.name ?? ''}`}
        onRun={async (ids) => { const r = await post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: ids, action: 'install', params: { packageId: installFor!.id } }); notifyOk(`${r.jobsCreated} job(s) sat i kø`); }} />
    </>
  );
}

function PackageModal({ draft, onClose }: { draft: Draft; onClose: () => void }) {
  const qc = useQueryClient();
  const [d, setD] = useState<Draft>(draft);
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const set = <K extends keyof Draft>(k: K, v: Draft[K]) => setD((x) => ({ ...x, [k]: v }));
  const userOnly = d.type === 'steam' || d.type === 'uri';
  const idLabel: Record<PackageType, string> = { winget: 'winget-id', msstore: 'Store-id (fx 9PKTQ5699M62)', file: 'Filnavn i Installers', steam: 'Steam app-id (tal, fx 730 = Counter-Strike 2)', uri: 'Launcher-link (fx com.epicgames.launcher://apps/…?action=install)', apt: 'Pakkenavn (fx vlc) – virker med apt, dnf, zypper og pacman; forskellige navne: "apt:x dnf:y"', pkg: 'Pakkenavn (som apt)', snap: 'snap-navn (fx code – evt. --classic under argumenter)', flatpak: 'Flathub-id (fx org.videolan.VLC)', brew: 'Homebrew-navn (fx firefox; cask:navn eller formula:navn)', mas: 'App Store-id (tal, find med mas search)' };
  const save = async () => {
    setBusy(true);
    try {
      const body = { ...d, runAs: userOnly ? 'user' : d.runAs };
      const r = d.id ? (await put(`/api/packages/${d.id}`, body), { id: d.id }) : await post<{ id: number }>('/api/packages', body);
      if (file) { const fd = new FormData(); fd.append('file', file); notifyOk(`Uploader ${file.name}…`); await api('POST', `/api/installers?packageId=${r.id}`, fd); }
      notifyOk('Pakken er gemt'); qc.invalidateQueries(); onClose();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const remove = () => modals.openConfirmModal({
    title: `Slet ${d.name}?`, children: <Text size="sm">Pakken fjernes også fra alle profiler.</Text>, labels: { confirm: 'Slet', cancel: 'Annullér' },
    confirmProps: { color: 'red' }, onConfirm: async () => { try { await del(`/api/packages/${d.id}`); qc.invalidateQueries(); onClose(); } catch (e) { notifyErr(e); } },
  });
  return (
    <Modal opened onClose={onClose} title={d.id ? `Redigér ${draft.name}` : 'Ny pakke'} size="xl">
      <Stack>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Navn" value={d.name} onChange={(e) => set('name', e.currentTarget.value)} required />
          <Select label="Kilde" data={[{ value: 'winget', label: 'winget' }, { value: 'msstore', label: 'Microsoft Store' }, { value: 'file', label: 'Egen installationsfil' },
            { value: 'steam', label: 'Steam-spil' }, { value: 'uri', label: 'Launcher-link (Epic, Battle.net …)' },
            { value: 'apt', label: 'Linux-pakke (apt/dnf/zypper/pacman)' }, { value: 'snap', label: 'snap (Linux)' }, { value: 'flatpak', label: 'Flatpak (Linux)' }, { value: 'script', label: 'Script (Linux/Mac – eget installationsscript)' },
            { value: 'brew', label: 'Homebrew (Mac)' }, { value: 'mas', label: 'App Store (Mac)' }]} value={d.type} onChange={(v) => set('type', (v ?? 'winget') as PackageType)} allowDeselect={false} />
          <TextInput label={idLabel[d.type]} value={d.identifier} onChange={(e) => set('identifier', e.currentTarget.value)} ff="monospace" />
          <Select label="Kører som" disabled={userOnly} data={options(RUNAS)} value={userOnly ? 'user' : d.runAs} onChange={(v) => set('runAs', (v ?? 'system') as RunAs)} allowDeselect={false} />
          {!userOnly && <TextInput label="Ekstra parametre" placeholder="fx --scope machine eller /S" value={d.args} onChange={(e) => set('args', e.currentTarget.value)} ff="monospace" />}
          <TextInput label="Kategori" value={d.category} onChange={(e) => set('category', e.currentTarget.value)} />
        </SimpleGrid>
        {d.type === 'file' && <FileInput label="Upload installationsfil til Installers på sharen" accept=".exe,.msi" value={file} onChange={setFile} clearable />}
        {d.type !== 'steam' && <TextInput label="Genkendelse (regulært udtryk mod programnavnet)" placeholder="Tom = navnet bruges, fx ^FileZilla" value={d.detect} onChange={(e) => set('detect', e.currentTarget.value)} ff="monospace" />}
        <TextInput label="Note" value={d.notes} onChange={(e) => set('notes', e.currentTarget.value)} />
        <Text size="sm" c="dimmed">
          {d.type === 'steam' ? 'Steam-spil installeres i den indloggede brugers Steam, som skal være logget ind. App-id findes i adressen på store.steampowered.com/app/<id>.'
            : <><b>System</b> installerer til alle brugere. <b>Bruger</b> installerer i den indloggede brugers session uden administrator. Det bruges af fx Spotify, Discord og VS Code.</>}
        </Text>
        <Group justify="space-between">
          {d.id ? <Button variant="subtle" color="red" onClick={remove}>Slet</Button> : <span />}
          <Group><Button variant="default" onClick={onClose}>Annullér</Button><Button loading={busy} onClick={save}>Gem</Button></Group>
        </Group>
      </Stack>
    </Modal>
  );
}

function WingetSearch({ opened, onClose, onPick }: { opened: boolean; onClose: () => void; onPick: (h: WingetHit) => void }) {
  const [q, setQ] = useState('');
  const [res, setRes] = useState<WingetHit[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const go = async () => {
    if (q.trim().length < 2) return;
    setBusy(true); setErr('');
    try { setRes(await get<WingetHit[]>(`/api/winget/search?q=${encodeURIComponent(q.trim())}`)); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={onClose} title="Find program i winget" size="xl">
      <Group wrap="nowrap"><TextInput style={{ flex: 1 }} placeholder="fx firefox, obs, powertoys…" value={q} onChange={(e) => setQ(e.currentTarget.value)} onKeyDown={(e) => e.key === 'Enter' && go()} autoFocus />
        <Button loading={busy} onClick={go}>Søg</Button></Group>
      <Text size="xs" c="dimmed" mt={4}>Søger i Microsofts offentlige winget-katalog. Første søgning henter kataloget og tager et øjeblik.</Text>
      {err && <Text c="red" mt="sm">{err}</Text>}
      {res && (res.length ? (
        <ScrollArea h={420} mt="md"><Table highlightOnHover><Table.Thead><Table.Tr><Table.Th>Navn</Table.Th><Table.Th>Id</Table.Th><Table.Th>Version</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>{res.map((h) => (
            <Table.Tr key={h.id}><Table.Td>{h.name}</Table.Td><Table.Td><Code>{h.id}</Code></Table.Td><Table.Td c="dimmed">{h.version}</Table.Td>
              <Table.Td>{h.inCatalog ? <Text size="xs" c="dimmed">I kataloget</Text> : <Button size="compact-xs" onClick={() => onPick(h)}>Tilføj</Button>}</Table.Td></Table.Tr>))}</Table.Tbody></Table></ScrollArea>
      ) : <Empty>Intet fundet</Empty>)}
    </Modal>
  );
}
