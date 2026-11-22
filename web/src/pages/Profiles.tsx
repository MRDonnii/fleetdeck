import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { Alert, Anchor, Badge, Button, Checkbox, Group, Modal, NumberInput, ScrollArea, Select, SimpleGrid, Stack, Switch, Table, TagsInput, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { del, notifyErr, notifyOk, post, put, useApi } from '../api';
import { useMe } from '../auth';
import { DAYS, INTENT, options } from '../format';
import type { HelperFix, Intent, Package, Profile, ProfileDetail } from '../types';
import { RISK } from '../helper';
import { Empty, Loading, PageHeader, Panel, StatusBadge } from '../components';
import { DrivesPanel } from '../design/drives';
import { ProfilePrintersPanel } from '../design/printers';
import { PKGTYPE } from '../format';
import { autoUpdateOff, rebootRuleText } from '../design/updateRunsText';

const sched = (mode: string, time: string, day: number) => mode === 'off' ? <Text c="dimmed" size="sm">Fra</Text> : <Text size="sm">{mode === 'daily' ? 'Dagligt' : DAYS[day]} {time}</Text>;

export function ProfilesPage() {
  const nav = useNavigate();
  const { isAdmin } = useMe();
  const { data: ps } = useApi<Profile[]>(['profiles'], '/api/profiles');
  if (!ps) return <Loading />;
  const create = async () => {
    try {
      const r = await post<{ id: number }>('/api/profiles', { ...defaults, name: 'Ny profil' });
      nav(`/profiler/${r.id}`);
    } catch (e) { notifyErr(e); }
  };
  return (
    <>
      <PageHeader title="Profiler" sub="Hvad der skal være installeret, og hvordan maskinerne holdes opdaterede">{isAdmin && <Button onClick={create}>+ Ny profil</Button>}</PageHeader>
      <Panel p={0}>
        <ScrollArea type="auto"><Table highlightOnHover miw={760}><Table.Thead><Table.Tr><Table.Th>Profil</Table.Th><Table.Th ta="right">Pakker</Table.Th><Table.Th ta="right">Maskiner</Table.Th>
          <Table.Th>Vedligeholdelse</Table.Th><Table.Th>Programmer</Table.Th><Table.Th>Windows Update</Table.Th><Table.Th>Backup</Table.Th><Table.Th>Gennemtving</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>{ps.map((p) => (
            <Table.Tr key={p.id} style={{ cursor: 'pointer' }} onClick={() => nav(`/profiler/${p.id}`)}>
              <Table.Td><Group gap={6}><Text fw={600} size="sm">{p.name}</Text>{p.isDefault && <Badge size="sm" variant="light">Standard</Badge>}</Group><Text size="xs" c="dimmed">{p.description}</Text></Table.Td>
              <Table.Td ta="right">{p.packageCount}</Table.Td><Table.Td ta="right">{p.machines}</Table.Td>
              <Table.Td>{sched(p.maintMode, p.maintTime, p.maintDay)}</Table.Td>
              <Table.Td>{sched(p.upgradeMode, p.upgradeTime, p.upgradeDay)}</Table.Td><Table.Td>{sched(p.windowsUpdateMode, p.windowsUpdateTime, p.windowsUpdateDay)}</Table.Td>
              <Table.Td>{sched(p.backupMode, p.backupTime, p.backupDay)}</Table.Td>
              <Table.Td><Badge variant="light" color={p.enforce ? 'green' : 'gray'}>{p.enforce ? 'Til' : 'Fra'}</Badge></Table.Td>
            </Table.Tr>))}</Table.Tbody></Table></ScrollArea>
        {!ps.length && <Empty>Ingen profiler endnu</Empty>}
      </Panel>
    </>
  );
}

const defaults = {
  description: '', enforce: true, isDefault: false, upgradeMode: 'off', upgradeTime: '03:00', upgradeDay: 6, upgradeExclude: [] as string[], rebootPolicy: 'never',
  windowsUpdateMode: 'off', windowsUpdateTime: '03:30', windowsUpdateDay: 6, backupMode: 'off', backupTime: '21:00', backupDay: 6,
  provDrivers: true, provRestore: true, provAppearance: true, provWindowsUpdate: true, packages: [] as { packageId: number; intent: Intent }[],
  autoFix: ['defender_signatures', 'crash_analysis', 'retry_failed'] as string[],
  maintMode: 'off', maintTime: '02:30', maintDay: 6, maintWake: true, maintTasks: ['windows_update', 'upgrade'] as string[],
  keepRunning: [] as string[], kitPaths: [] as string[], kitMode: 'off', kitTime: '01:30', kitDay: 6,
  maintAfter: 'shutdown_if_woken', maintReport: true,
  updDrivers: true, updFirmware: false, updFeatureUpdates: false, updMaxRounds: 4, updCatchUpDays: 3,
  updNeverAuto: false, updRebootNoWarning: false, updQuietFrom: '', updQuietTo: '',
};
const MAINT_TASKS = [
  { value: 'windows_update', label: 'Windows Update (Linux: systemopdateringer)' },
  { value: 'upgrade', label: 'Opdatér alle programmer' },
  { value: 'disk_cleanup', label: 'Ryd op på disken (kun Windows)' },
  { value: 'defender_signatures', label: 'Nye virusdefinitioner (kun Windows med Defender)' },
];
const MAINT_AFTER = [
  { value: 'none', label: 'Ingenting – lad den køre' },
  { value: 'reboot', label: 'Genstart, hvis opdateringerne kræver det' },
  { value: 'shutdown_if_woken', label: 'Genstart hvis nødvendigt, og sluk igen hvis den var slukket' },
  { value: 'shutdown', label: 'Genstart hvis nødvendigt, og sluk altid bagefter' },
];
type Form = typeof defaults & { name: string };
type Item = { packageId: number; intent: Intent; name: string; type: string; category: string };

function Schedule({ f, k, set, ro }: { f: Form; k: 'upgrade' | 'windowsUpdate' | 'backup' | 'maint' | 'kit'; set: (k: keyof Form, v: unknown) => void; ro: boolean }) {
  const mode = f[`${k}Mode`] as string;
  return (
    <SimpleGrid cols={{ base: 1, xs: 3 }}>
      <Select label="Hvornår" disabled={ro} data={[{ value: 'off', label: 'Fra' }, { value: 'daily', label: 'Dagligt' }, { value: 'weekly', label: 'Ugentligt' }]}
        value={mode} onChange={(v) => set(`${k}Mode`, v ?? 'off')} allowDeselect={false} />
      <TextInput label="Klokken" type="time" disabled={ro || mode === 'off'} value={f[`${k}Time`] as string} onChange={(e) => set(`${k}Time`, e.currentTarget.value)} />
      <Select label="Ugedag" disabled={ro || mode !== 'weekly'} data={DAYS.map((d, i) => ({ value: String(i), label: d }))} value={String(f[`${k}Day`])}
        onChange={(v) => set(`${k}Day`, Number(v ?? 6))} allowDeselect={false} />
    </SimpleGrid>
  );
}

export function ProfilePage() {
  const { id = '' } = useParams();
  const nav = useNavigate();
  const qc = useQueryClient();
  const { isAdmin } = useMe();
  const ro = !isAdmin;
  const { data } = useApi<ProfileDetail>(['profile', id], `/api/profiles/${id}`);
  const { data: pkgs = [] } = useApi<Package[]>(['packages'], '/api/packages');
  const { data: catalog = [] } = useApi<HelperFix[]>(['helper-catalog'], '/api/helper/catalog');
  const [f, setF] = useState<Form | null>(null);
  const [items, setItems] = useState<Item[]>([]);
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!data) return;
    const p = data.profile;
    setF({ ...defaults, ...p, packages: [] });
    setItems(data.packages.map((x) => ({ packageId: x.packageId, intent: x.intent, name: x.name, type: x.type, category: x.category })));
  }, [data]);
  if (!data || !f) return <Loading />;
  const set = (k: keyof Form, v: unknown) => setF((x) => (x ? { ...x, [k]: v } : x));
  const save = async () => {
    setBusy(true);
    try {
      const r = await put<{ jobsCreated: number }>(`/api/profiles/${id}`, { ...f, packages: items.map((x) => ({ packageId: x.packageId, intent: x.intent })) });
      notifyOk(`Profilen er gemt${r.jobsCreated ? ` – ${r.jobsCreated} job(s) oprettet` : ''}`); qc.invalidateQueries();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const remove = () => modals.openConfirmModal({
    title: `Slet ${f.name}?`, children: <Text size="sm">Maskinerne beholder deres programmer, men har ingen profil bagefter.</Text>,
    labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: async () => { try { await del(`/api/profiles/${id}`); qc.invalidateQueries(); nav('/profiler'); } catch (e) { notifyErr(e); } },
  });
  const p = data.profile;
  return (
    <>
      <PageHeader title={f.name} sub={`${p.machines} maskine(r) · ${items.length} pakker`}>
        {isAdmin && <><Button variant="subtle" color="red" onClick={remove}>Slet</Button><Button loading={busy} onClick={save}>Gem profil</Button></>}
      </PageHeader>
      <SimpleGrid cols={{ base: 1, lg: 2 }}>
        <Stack gap={0}>
          <Panel title="Generelt">
            <Stack>
              <TextInput label="Navn" disabled={ro} value={f.name} onChange={(e) => set('name', e.currentTarget.value)} />
              <TextInput label="Beskrivelse" disabled={ro} value={f.description} onChange={(e) => set('description', e.currentTarget.value)} />
              <Switch disabled={ro} label="Standardprofil for nye maskiner" checked={f.isDefault} onChange={(e) => set('isDefault', e.currentTarget.checked)} />
              <Switch disabled={ro} label="Gennemtving: installér og afinstallér automatisk, så maskinerne passer til profilen" checked={f.enforce} onChange={(e) => set('enforce', e.currentTarget.checked)} />
              <Text size="sm" c="dimmed">Maskiner: {data.machineList.length ? data.machineList.map((m, i) => <span key={m.id}>{i > 0 && ', '}<Anchor component={Link} to={`/maskiner/${m.id}`}>{m.name}</Anchor></span>) : 'ingen'}</Text>
            </Stack>
          </Panel>
          <Panel title="Natlig vedligeholdelse">
            <Stack>
              {f.updNeverAuto ? (
                <Alert color="gray" title="Aldrig automatisk">Maskinerne i denne profil opdateres og genstartes aldrig automatisk – kun når du trykker Opdatér ud for en linje.</Alert>
              ) : autoUpdateOff(f) && (
                <Alert color="orange" title="Automatisk opdatering er slået fra">
                  <Stack gap={6}>
                    <Text size="sm">Maskinerne i denne profil bliver kun opdateret, når nogen trykker på knapperne. Ugentlig vedligeholdelse om natten anbefales.</Text>
                    {!ro && <Group><Button size="xs" variant="light" color="orange" onClick={() => set('maintMode', 'weekly')}>Slå ugentlig vedligeholdelse til</Button></Group>}
                  </Stack>
                </Alert>
              )}
              <Text size="sm" c="dimmed">Tænder pc'erne, opdaterer i fast rækkefølge (Windows-opdateringer → drivere → programmer → firmware sidst), genstarter og fortsætter i runder, til intet er tilbage – og sender én morgenrapport med det, der mangler. {rebootRuleText(f.updRebootNoWarning)}</Text>
              <Schedule f={f} k="maint" set={set} ro={ro} />
              <Stack gap={6}>
                <Text size="sm" fw={500}>Opgaver</Text>
                {MAINT_TASKS.map((t) => (
                  <Checkbox key={t.value} disabled={ro || f.maintMode === 'off'} label={t.label} checked={f.maintTasks.includes(t.value)}
                    onChange={(e) => set('maintTasks', e.currentTarget.checked ? [...f.maintTasks, t.value] : f.maintTasks.filter((x) => x !== t.value))} />
                ))}
              </Stack>
              <Select label="Bagefter" disabled={ro || f.maintMode === 'off'} data={MAINT_AFTER} value={f.maintAfter} onChange={(v) => set('maintAfter', v ?? 'none')} allowDeselect={false} />
              <Switch disabled={ro || f.maintMode === 'off'} label="Tænd slukkede pc'er med Wake-on-LAN – kun når der er noget at installere" checked={f.maintWake} onChange={(e) => set('maintWake', e.currentTarget.checked)} />
              <Switch disabled={ro || f.maintMode === 'off'} label="Send morgenrapport (via beskeder under Indstillinger)" checked={f.maintReport} onChange={(e) => set('maintReport', e.currentTarget.checked)} />
              <Text size="xs" c="dimmed">Næste kørsel: {p.nextMaint ?? '–'}</Text>
            </Stack>
          </Panel>
          <Panel title="Opdateringer: rækkefølge og runder">
            <Stack>
              <Text size="sm" c="dimmed">Gælder alle automatiske opdateringer – vedligeholdelse, planlagte opdateringer og "Opdatér alt (automatisk)".</Text>
              <Switch disabled={ro} label="Drivere med (frarådede drivere – gamle eller nedgraderinger – springes stadig over)" checked={f.updDrivers} onChange={(e) => set('updDrivers', e.currentTarget.checked)} />
              <Switch disabled={ro} label="Firmware med (fra som standard) – altid sidst, kun på netstrøm og aldrig BIOS (heller ikke BIOS fra Windows Update)" checked={f.updFirmware} onChange={(e) => set('updFirmware', e.currentTarget.checked)} />
              <Switch disabled={ro} label="Windows-funktionsopdateringer (ny Windows-version)" checked={f.updFeatureUpdates} onChange={(e) => set('updFeatureUpdates', e.currentTarget.checked)} />
              <SimpleGrid cols={{ base: 1, xs: 2 }}>
                <NumberInput label="Højst runder (og genstarter)" min={1} max={8} disabled={ro} value={f.updMaxRounds} onChange={(v) => set('updMaxRounds', Number(v) || 4)} />
                <NumberInput label="Indhent missede vinduer i (døgn)" min={0} max={14} disabled={ro} value={f.updCatchUpDays} onChange={(v) => set('updCatchUpDays', Number(v) || 0)} />
              </SimpleGrid>
              <SimpleGrid cols={{ base: 1, xs: 2 }}>
                <TextInput label="Stilletid fra (ingen genstart)" type="time" disabled={ro} value={f.updQuietFrom} onChange={(e) => set('updQuietFrom', e.currentTarget.value)} />
                <TextInput label="Stilletid til" type="time" disabled={ro} value={f.updQuietTo} onChange={(e) => set('updQuietTo', e.currentTarget.value)} />
              </SimpleGrid>
              <Switch disabled={ro} label="Genstart uden varsel (kun til dine egne pc'er – ingen nedtælling, intet krav om at pc'en er ledig)" checked={f.updRebootNoWarning} onChange={(e) => set('updRebootNoWarning', e.currentTarget.checked)} />
              <Switch disabled={ro} color="red" label="Aldrig automatisk opdatering eller genstart (fx en styring, der skal køre uafbrudt)" checked={f.updNeverAuto} onChange={(e) => set('updNeverAuto', e.currentTarget.checked)} />
            </Stack>
          </Panel>
          <Panel title="Programopdateringer (winget)">
            <Stack><Schedule f={f} k="upgrade" set={set} ro={ro} />
              <Select label="Genstart" disabled={ro} data={[{ value: 'never', label: 'Aldrig automatisk' }, { value: 'after_upgrade', label: 'Efter opdatering, hvis Windows kræver det' }]}
                value={f.rebootPolicy} onChange={(v) => set('rebootPolicy', v ?? 'never')} allowDeselect={false} />
              <TagsInput label="Opdatér aldrig disse (winget-id)" disabled={ro} value={f.upgradeExclude} onChange={(v) => set('upgradeExclude', v)} placeholder="fx Valve.Steam" />
              <Text size="xs" c="dimmed">Næste kørsel: {p.nextUpgrade ?? '–'}</Text></Stack>
          </Panel>
          <Panel title="Linux: hold kørende og gendannelse">
            <Stack>
              <TagsInput label="Hold kørende (tjenester)" disabled={ro} value={f.keepRunning} onChange={(v) => set('keepRunning', v)} placeholder="fx min-gateway"
                description="Agenten tjekker hvert halve minut og starter en stoppet tjeneste igen med det samme. Kørende tjenester røres aldrig." />
              <TagsInput label="Gendannelsespunkt: stier" disabled={ro} value={f.kitPaths} onChange={(v) => set('kitPaths', v)} placeholder="fx /opt/min-app-*"
                description="Pakkes (kun læsning, laveste prioritet) og gemmes i Fleetdeck. Egne tjenester, scripts de bruger, udev-regler, boot-opsætning og pakkelisten kommer altid med." />
              <Schedule f={f} k="kit" set={set} ro={ro} />
              <Text size="xs" c="dimmed">Næste gendannelsespunkt: {p.nextKit ?? '–'} · Gendan overskriver aldrig eksisterende filer og starter kun tjenester, der ikke kører.</Text>
            </Stack>
          </Panel>
          <Panel title="Windows Update"><Stack><Schedule f={f} k="windowsUpdate" set={set} ro={ro} />
            {f.windowsUpdateMode !== 'off' && f.rebootPolicy === 'never' && (
              <Alert color="yellow" p="xs">Genstart er sat til "Aldrig automatisk" (under Programopdateringer). Så kan Windows Update kun køre én runde: kræver en opdatering genstart, venter resten til næste gang. Vælg "Efter opdatering, hvis Windows kræver det" – eller brug vedligeholdelsen – for at få det hele med.</Alert>
            )}
            <Text size="xs" c="dimmed">Næste kørsel: {p.nextWindowsUpdate ?? '–'}</Text></Stack></Panel>
          <Panel title="Automatisk backup"><Stack><Schedule f={f} k="backup" set={set} ro={ro} />
            <Text size="xs" c="dimmed">Kører WinSetup-backup til Unraid, når nogen er logget ind. Næste: {p.nextBackup ?? '–'}</Text></Stack></Panel>
          <Panel title="Hjælper: automatisk rettelse">
            <Text size="sm" c="dimmed" mb="xs">Vælg hvad hjælperen må rette uden at spørge, når en alarm opstår. Den venter 6 timer mellem forsøg og prøver højst 3 gange. Alt kan ses under Jobs.</Text>
            <Stack gap="xs">
              {catalog.filter((c) => c.autoAllowed).map((c) => (
                <Checkbox key={c.id} disabled={ro} checked={f.autoFix.includes(c.id)}
                  onChange={(e) => set('autoFix', e.currentTarget.checked ? [...f.autoFix, c.id] : f.autoFix.filter((x) => x !== c.id))}
                  label={<Group gap={6}>{c.title}<Badge size="xs" variant="light" color={RISK[c.risk].color}>{RISK[c.risk].label}</Badge></Group>}
                  description={c.description} />
              ))}
            </Stack>
            <Text size="xs" c="dimmed" mt="xs">Fx Defender, firewall, Windows Update, genstart og geninstallation kører aldrig automatisk. De kræver altid dit klik.</Text>
          </Panel>
          <DrivesPanel profileId={Number(id)} isAdmin={isAdmin} />
          <ProfilePrintersPanel profileId={Number(id)} isAdmin={isAdmin} />
          <Panel title="Nyinstallation">
            <Text size="sm" c="dimmed" mb="xs">Når en pc installeres fra WinSetup-USB'en og tilmelder sig:</Text>
            <Stack gap="xs">
              <Checkbox disabled={ro} label="Installér gemte drivere" checked={f.provDrivers} onChange={(e) => set('provDrivers', e.currentTarget.checked)} />
              <Checkbox disabled checked label="Installér alle påkrævede programmer og spil" />
              <Checkbox disabled={ro} label="Gendan filer og programindstillinger fra backup" checked={f.provRestore} onChange={(e) => set('provRestore', e.currentTarget.checked)} />
              <Checkbox disabled={ro} label="Læg baggrund og Windows-opsætning på" checked={f.provAppearance} onChange={(e) => set('provAppearance', e.currentTarget.checked)} />
              <Checkbox disabled={ro} label="Installér Windows-opdateringer" checked={f.provWindowsUpdate} onChange={(e) => set('provWindowsUpdate', e.currentTarget.checked)} />
              <Checkbox disabled checked label="Genstart til sidst" />
            </Stack>
          </Panel>
        </Stack>
        <Panel title="Pakker og spil" right={isAdmin && <Button size="xs" onClick={() => setAdding(true)}>+ Tilføj</Button>} p={0}>
          {items.length ? (
            <ScrollArea type="auto"><Table miw={420}><Table.Tbody>{items.map((x, i) => (
              <Table.Tr key={x.packageId}>
                <Table.Td><Group gap={6} wrap="nowrap"><StatusBadge map={PKGTYPE} value={x.type} /><Text size="sm">{x.name}</Text></Group><Text size="xs" c="dimmed">{x.category}</Text></Table.Td>
                <Table.Td w={150}><Select size="xs" disabled={ro} data={options(INTENT)} value={x.intent} allowDeselect={false}
                  onChange={(v) => setItems((a) => a.map((y, j) => (j === i ? { ...y, intent: (v ?? 'required') as Intent } : y)))} /></Table.Td>
                <Table.Td w={70}>{isAdmin && <Button size="compact-xs" variant="subtle" color="red" onClick={() => setItems((a) => a.filter((_, j) => j !== i))}>Fjern</Button>}</Table.Td>
              </Table.Tr>))}</Table.Tbody></Table></ScrollArea>
          ) : <Empty>Ingen pakker i profilen</Empty>}
        </Panel>
      </SimpleGrid>
      <AddPackages opened={adding} onClose={() => setAdding(false)} all={pkgs} have={new Set(items.map((x) => x.packageId))}
        onAdd={(ids, intent) => setItems((a) => [...a, ...pkgs.filter((x) => ids.includes(x.id)).map((x) => ({ packageId: x.id, intent, name: x.name, type: x.type, category: x.category }))])} />
    </>
  );
}

function AddPackages({ opened, onClose, all, have, onAdd }: { opened: boolean; onClose: () => void; all: Package[]; have: Set<number>; onAdd: (ids: number[], intent: Intent) => void }) {
  const [sel, setSel] = useState<string[]>([]);
  const [intent, setIntent] = useState<Intent>('required');
  const [q, setQ] = useState('');
  const avail = all.filter((x) => !have.has(x.id) && (!q || x.name.toLowerCase().includes(q.toLowerCase())));
  return (
    <Modal opened={opened} onClose={onClose} title="Tilføj pakker til profilen">
      <Stack>
        <Select label="Krav" data={options(INTENT)} value={intent} onChange={(v) => setIntent((v ?? 'required') as Intent)} allowDeselect={false} />
        <TextInput placeholder="Filtrér…" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        <ScrollArea h={360}><Checkbox.Group value={sel} onChange={setSel}><Stack gap={6}>
          {avail.map((x) => <Checkbox key={x.id} value={String(x.id)} label={<Group gap={6}>{x.name}<Text size="xs" c="dimmed">{x.category}</Text></Group>} />)}
        </Stack></Checkbox.Group>{!avail.length && <Empty>Alle pakker er allerede med</Empty>}</ScrollArea>
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button disabled={!sel.length} onClick={() => { onAdd(sel.map(Number), intent); setSel([]); onClose(); }}>Tilføj {sel.length || ''}</Button></Group>
      </Stack>
    </Modal>
  );
}
