import { useState } from 'react';
import { Button, Group, Modal, ScrollArea, SegmentedControl, Select, Stack, Table, Text, Textarea, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { del, notifyErr, notifyOk, post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { options, RUNAS } from '../format';
import type { JobRow, RunAs, Script } from '../types';
import { Empty, JobTable, Loading, MachinePicker, PageHeader, Panel } from '../components';

export function JobsPage() {
  const { isAdmin } = useMe();
  const [status, setStatus] = useState('active');
  const { data: jobs } = useApi<JobRow[]>(['jobs', status], `/api/jobs?status=${status}`, { refetchInterval: 10_000 });
  const cleanup = useAction(() => post<{ deleted: number }>('/api/jobs/cleanup'), (r) => `${r.deleted} gamle jobs slettet`);
  return (
    <>
      <PageHeader title="Jobs" sub="Alt der er sat i kø eller kørt på maskinerne">{isAdmin && <Button variant="default" onClick={() => cleanup.mutate(undefined)}>Ryd op (ældre end 30 dage)</Button>}</PageHeader>
      <SegmentedControl mb="md" value={status} onChange={setStatus} data={[{ value: 'active', label: 'Aktive' }, { value: '', label: 'Alle' }, { value: 'failed', label: 'Fejlede' },
        { value: 'success', label: 'OK' }, { value: 'waiting_user', label: 'Venter på brugeren' }, { value: 'restarting', label: 'Genstarter…' }, { value: 'hung', label: 'Hænger' }]} />
      <Panel p={0}>{jobs ? <JobTable jobs={jobs} /> : <Loading />}</Panel>
    </>
  );
}

const EXAMPLES: [string, RunAs, string][] = [
  ['Ryd midlertidige filer', 'system', "Get-ChildItem $env:windir\\Temp, 'C:\\Users\\*\\AppData\\Local\\Temp' -Recurse -Force -ErrorAction SilentlyContinue |\n  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue\n'Temp ryddet'"],
  ['Hurtig Defender-scanning', 'system', 'Update-MpSignature\nStart-MpScan -ScanType QuickScan\nGet-MpComputerStatus | Select-Object AntivirusSignatureLastUpdated, QuickScanEndTime'],
  ['Tøm DNS-cache og forny IP', 'system', 'ipconfig /flushdns\nipconfig /renew\nGet-NetIPConfiguration | Format-List InterfaceAlias, IPv4Address, IPv4DefaultGateway'],
  ['Diskoprydning (komponentlager)', 'system', 'Dism.exe /Online /Cleanup-Image /StartComponentCleanup'],
  ['Vis store mapper på C:', 'system', "Get-ChildItem C:\\ -Directory -ErrorAction SilentlyContinue | ForEach-Object {\n  [pscustomobject]@{ Mappe = $_.FullName; GB = [math]::Round((Get-ChildItem $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB, 1) }\n} | Sort-Object GB -Descending | Format-Table -AutoSize"],
  ['Batteri- og strømrapport', 'system', 'powercfg /batteryreport /output $env:TEMP\\battery.html | Out-Null\npowercfg /getactivescheme'],
];

export function ScriptsPage() {
  const { isAdmin } = useMe();
  const qc = useQueryClient();
  const { data: ss } = useApi<Script[]>(['scripts'], '/api/scripts');
  const [edit, setEdit] = useState<Partial<Script> | null>(null);
  const [runFor, setRunFor] = useState<Script | null>(null);
  if (!ss) return <Loading />;
  const addExamples = async () => {
    try { for (const [name, runAs, body] of EXAMPLES) await post('/api/scripts', { name, runAs, body, description: '' }); qc.invalidateQueries(); } catch (e) { notifyErr(e); }
  };
  return (
    <>
      <PageHeader title="Scripts" sub="Gemte PowerShell-scripts der kan køres på maskinerne">
        {isAdmin && <>{!ss.length && <Button variant="default" onClick={addExamples}>Tilføj eksempler</Button>}
          <Button onClick={() => setEdit({ name: '', runAs: 'system', description: '', body: '' })}>+ Nyt script</Button></>}
      </PageHeader>
      <Panel p={0}>
        {ss.length ? (
          <ScrollArea type="auto"><Table highlightOnHover miw={500}><Table.Thead><Table.Tr><Table.Th>Navn</Table.Th><Table.Th>Kører som</Table.Th><Table.Th /></Table.Tr></Table.Thead>
            <Table.Tbody>{ss.map((s) => (
              <Table.Tr key={s.id}><Table.Td><Text fw={600} size="sm">{s.name}</Text><Text size="xs" c="dimmed">{s.description}</Text></Table.Td><Table.Td>{RUNAS[s.runAs]}</Table.Td>
                <Table.Td>{isAdmin && <Group gap={4} justify="flex-end" wrap="nowrap"><Button size="compact-xs" variant="default" onClick={() => setEdit(s)}>Redigér</Button>
                  <Button size="compact-xs" onClick={() => setRunFor(s)}>Kør på…</Button></Group>}</Table.Td></Table.Tr>))}</Table.Tbody></Table></ScrollArea>
        ) : <Empty>Ingen scripts endnu</Empty>}
      </Panel>
      {edit && <ScriptModal s={edit} onClose={() => setEdit(null)} />}
      <MachinePicker opened={runFor !== null} onClose={() => setRunFor(null)} title={`Kør "${runFor?.name ?? ''}"`}
        onRun={async (ids) => { const r = await post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: ids, action: 'script', params: { scriptId: runFor!.id } }); notifyOk(`${r.jobsCreated} job(s) sat i kø`); }} />
    </>
  );
}

function ScriptModal({ s, onClose }: { s: Partial<Script>; onClose: () => void }) {
  const qc = useQueryClient();
  const [d, setD] = useState(s);
  const save = async () => {
    try { if (d.id) await put(`/api/scripts/${d.id}`, d); else await post('/api/scripts', d); notifyOk('Gemt'); qc.invalidateQueries(); onClose(); } catch (e) { notifyErr(e); }
  };
  return (
    <Modal opened onClose={onClose} title={d.id ? 'Redigér script' : 'Nyt script'} size="xl">
      <Stack>
        <Group grow><TextInput label="Navn" value={d.name ?? ''} onChange={(e) => setD({ ...d, name: e.currentTarget.value })} />
          <Select label="Kører som" data={options(RUNAS)} value={d.runAs ?? 'system'} onChange={(v) => setD({ ...d, runAs: (v ?? 'system') as RunAs })} allowDeselect={false} /></Group>
        <TextInput label="Beskrivelse" value={d.description ?? ''} onChange={(e) => setD({ ...d, description: e.currentTarget.value })} />
        <Textarea label="PowerShell (exit-kode 0 = OK)" autosize minRows={10} maxRows={28} ff="monospace" value={d.body ?? ''} onChange={(e) => setD({ ...d, body: e.currentTarget.value })} />
        <Group justify="space-between">
          {d.id ? <Button variant="subtle" color="red" onClick={() => modals.openConfirmModal({
            title: `Slet scriptet ${d.name}?`, labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
            onConfirm: async () => { try { await del(`/api/scripts/${d.id}`); qc.invalidateQueries(); onClose(); } catch (e) { notifyErr(e); } },
          })}>Slet</Button> : <span />}
          <Group><Button variant="default" onClick={onClose}>Annullér</Button><Button onClick={save}>Gem</Button></Group>
        </Group>
      </Stack>
    </Modal>
  );
}
