import { useEffect, useState } from 'react';
import { ActionIcon, Badge, Box, Button, Group, Loader, Menu, Modal, Paper, Select, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import { IconDots, IconPencil, IconPlus, IconPrinter, IconRadar, IconRefresh, IconSettings, IconTrash } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { del, post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { ago } from '../format';
import {
  DriverBadge, InkBars, LEVEL_COLOR, PrinterModal, SOURCE, SetupModal, SourceBadge, StatusDot, draftFrom, draftOf, emptyDraft, foundBy, when,
  type FoundPrinter, type PrinterDraft, type PrinterRow, type PrinterSearch, type PrintersResponse,
} from '../design/printers';

const rowBorder = (i: number) => (i ? { borderTop: '1px solid var(--mantine-color-default-border)' } : undefined);

/** "Hjemmekontor · 5 maskiner" ud fra tildelingerne. */
function placedText(p: PrinterRow) {
  if (!p.assignments.length) return 'Ikke lagt på';
  const names = p.assignments.map((a) => a.label.replace(/^Profilen /, ''));
  const first = names.slice(0, 2).join(', ') + (names.length > 2 ? ` +${names.length - 2}` : '');
  return `${first} · ${p.machines} ${p.machines === 1 ? 'maskine' : 'maskiner'}`;
}

/** "sidst i dag 08.12 · Bonjour, serverens net 192.0.2.0/24 og 3 pc'er" – hvad den seneste søgning gjorde. */
function searchText(s: PrinterSearch) {
  if (s.running) return 'Søger efter printere på netværket…';
  if (!s.lastRun) return s.auto ? 'Fleetdeck søger om lidt.' : 'Automatisk søgning er slået fra. Tryk "Søg nu".';
  const how = [
    ...(s.networkOff ? [] : ['Bonjour']),
    ...(s.subnets.length ? [`${s.subnets.length === 1 ? 'nettet' : 'nettene'} ${s.subnets.join(', ')}`] : []),
    ...(s.agents ? [`${s.agents} ${s.agents === 1 ? 'pc' : "pc'er"}`] : []),
  ];
  return `${s.auto ? `hver ${s.hours === 1 ? 'time' : `${s.hours}. time`} · ` : ''}sidst ${when(s.lastRun)}${how.length ? ` · ${how.join(', ').replace(/, ([^,]*)$/, ' og $1')}` : ''}`;
}

/** Statuslinjen for søgningen med "Søg nu" og indstillingerne. */
function SearchBar({ data, isAdmin, onSettings }: { data: PrintersResponse; isAdmin: boolean; onSettings: () => void }) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const s = data.search;
  const discover = useAction(() => post<{ started: boolean }>('/api/printers/discover', {}), (r) => r.started ? 'Søger efter printere – pc\'erne melder også, hvad de ser' : 'En søgning kører allerede');
  if (!s) return null;
  return (
    <Paper withBorder radius="md" px="md" py={8} mb="md" bg="var(--mantine-color-default-hover)">
      <Group gap="sm" wrap="nowrap">
        {s.running ? <Loader size={16} /> : <IconRadar size={18} style={{ flex: 'none', opacity: 0.8 }} />}
        <Box style={{ flex: 1, minWidth: 0 }}>
          <Text size="sm">{s.auto && !s.running && s.lastRun && <b>Fleetdeck søger selv </b>}{searchText(s)}</Text>
          {s.error && <Text size="xs" c="red">Seneste søgning mislykkedes: {s.error}</Text>}
        </Box>
        {isAdmin && (mobile
          ? <Tooltip label="Søg nu"><ActionIcon variant="default" size={30} loading={s.running || discover.isPending} onClick={() => discover.mutate(undefined)} aria-label="Søg nu"><IconRefresh size={16} /></ActionIcon></Tooltip>
          : <Button variant="default" size="compact-sm" leftSection={<IconRefresh size={14} />} loading={s.running || discover.isPending} onClick={() => discover.mutate(undefined)}>Søg nu</Button>)}
        {isAdmin && <Tooltip label="Indstillinger for søgningen"><ActionIcon variant="subtle" color="gray" onClick={onSettings} aria-label="Indstillinger for søgningen"><IconSettings size={16} /></ActionIcon></Tooltip>}
      </Group>
    </Paper>
  );
}

/** Indstillinger: automatisk søgning til/fra, hvor ofte, og et ekstra område (fx et andet net end serverens eget). */
function SearchSettings({ data, opened, onClose }: { data: PrintersResponse; opened: boolean; onClose: () => void }) {
  const s = data.search;
  const [auto, setAuto] = useState(true);
  const [hours, setHours] = useState('6');
  const [subnet, setSubnet] = useState('');
  useEffect(() => { if (opened && s) { setAuto(s.auto); setHours(String(s.hours)); setSubnet(data.subnet ?? ''); } }, [opened]);
  const save = useAction(() => put('/api/printers/search', { auto, hours: Number(hours), subnet: subnet.trim() }), 'Indstillingerne er gemt');
  return (
    <Modal opened={opened} onClose={onClose} title="Søgning efter printere" size="md">
      <Stack gap="sm">
        <Text size="sm">Fleetdeck spørger netværket med Bonjour og scanner serverens eget net{s?.ownSubnets.length ? ` (${s.ownSubnets.join(', ')})` : ''} for IPP (port 631) – roligt og kun private adresser. Pc'erne med agenten melder også de printere, de selv ser (Bonjour, WSD og dem, de har installeret).</Text>
        <Switch label="Søg automatisk" checked={auto} onChange={(e) => setAuto(e.currentTarget.checked)} />
        <Select label="Hvor ofte" data={[['1', 'Hver time'], ['3', 'Hver 3. time'], ['6', 'Hver 6. time'], ['12', 'Hver 12. time'], ['24', 'En gang i døgnet']].map(([value, label]) => ({ value, label }))}
          value={hours} onChange={(v) => v && setHours(v)} disabled={!auto} allowDeselect={false} />
        <TextInput label="Scan også et andet område (valgfrit)" placeholder="192.168.1.0/24" value={subnet} onChange={(e) => setSubnet(e.currentTarget.value)}
          description="Kun private adresser, højst /22. Scannes for port 631 og 9100." />
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>Annullér</Button>
          <Button loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Gem</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Siden Printere: printere, Fleetdeck selv har fundet på netværket ("Opsæt på…"), og de printere, der er i Fleetdeck. */
export function PrintersPage() {
  const { isAdmin } = useMe();
  const mobile = useMediaQuery('(max-width: 48em)');
  const qc = useQueryClient();
  const [poll, setPoll] = useState(false);
  const { data, isLoading } = useApi<PrintersResponse>(['printers'], '/api/printers', { refetchInterval: poll ? 4000 : undefined });
  const [draft, setDraft] = useState<PrinterDraft | null>(null);
  const [editing, setEditing] = useState<PrinterRow | null>(null);
  const [setup, setSetup] = useState<FoundPrinter | null>(null);
  const [settings, setSettings] = useState(false);
  // mens en søgning kører (og et øjeblik efter, mens pc'erne melder deres fund), hentes listen igen af sig selv
  const lastRun = data?.search?.lastRun;
  const fresh = lastRun != null && Date.now() - new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(lastRun) ? lastRun : lastRun + 'Z').getTime() < 90000;
  useEffect(() => { setPoll(!!data && (data.scanning || data.search?.running === true || fresh || (data.search?.auto === true && !lastRun && !data.search?.error))); }, [data, fresh, lastRun]);
  const refresh = useAction(() => post('/api/printers/refresh'), 'Status er opdateret');
  const remove = useAction((id: number) => del(`/api/printers/${id}`), 'Printeren er slettet – agenterne fjerner den igen');
  if (isLoading || !data) return <Loading />;
  const { printers, found } = data;
  const s = data.search;
  const askDelete = (p: PrinterRow) => modals.openConfirmModal({
    title: `Slet ${p.name}?`, labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    children: <Text size="sm">Printeren fjernes igen fra de {p.machines} maskiner, Fleetdeck har lagt den på. Printere, brugerne selv har lagt på, røres ikke.</Text>,
    onConfirm: () => remove.mutate(p.id, { onSuccess: () => qc.invalidateQueries() }),
  });
  const open = (d: PrinterDraft, p: PrinterRow | null = null) => { setEditing(p); setDraft(d); };
  const emptyFound = s?.running || (!s?.lastRun && s?.auto)
    ? 'Fleetdeck søger efter printere på netværket… Pc\'erne med agenten melder også, hvad de ser.'
    : !s?.lastRun ? 'Fleetdeck har ikke søgt endnu. Tryk "Søg nu".'
    : 'Ingen nye printere fundet. Tjek, at printeren er tændt og på samme net – eller tilføj den med dens adresse.';

  return (
    <>
      <PageHeader title="Printere" sub={mobile ? undefined : "Fleetdeck finder printerne på netværket. Vælg, hvilke pc'er de skal på – agenterne installerer dem og holder dem på."}>
        {isAdmin && printers.length > 0 && <Tooltip label="Spørg printerne om status nu"><ActionIcon variant="default" size={30} loading={refresh.isPending} onClick={() => refresh.mutate(undefined)} aria-label="Opdatér status"><IconRefresh size={16} /></ActionIcon></Tooltip>}
        {isAdmin && <Button variant="default" size="xs" leftSection={<IconPlus size={14} />} onClick={() => open(emptyDraft())}>Tilføj med adresse</Button>}
      </PageHeader>

      <SearchBar data={data} isAdmin={isAdmin} onSettings={() => setSettings(true)} />

      <Group gap="xs" mb="xs"><Text fw={700} size="sm">Fundne printere</Text>{found.length > 0 && <Badge size="sm" variant="light">{found.length} {found.length === 1 ? 'ny' : 'nye'}</Badge>}</Group>
      <Panel p={0}>
        {found.length === 0 && <Empty>{emptyFound}</Empty>}
        {found.map((f: FoundPrinter, i) => (
          <Group key={f.host} gap="sm" wrap="nowrap" px="md" py={10} style={rowBorder(i)}>
            <IconPrinter size={18} style={{ flex: 'none', opacity: 0.8 }} />
            <Box style={{ flex: 1, minWidth: 0 }}>
              <Text size="sm" fw={600} lineClamp={1}>{f.name}</Text>
              <Text size="xs" c="dimmed" lineClamp={1}><Text span ff="monospace" size="xs">{f.host}</Text>{f.makeModel && f.makeModel !== f.name && !mobile ? ` · ${f.makeModel}` : ''} · {mobile ? SOURCE[(f.sources ?? [])[0]]?.short ?? '' : foundBy(f)}</Text>
            </Box>
            {!mobile && <Box w={130}><SourceBadge f={f} /></Box>}
            {!mobile && <Box w={110}><DriverBadge driverless={f.driverless} /></Box>}
            {isAdmin && <Button size="compact-sm" variant="light" leftSection={<IconPlus size={12} />} onClick={() => setSetup(f)}>{mobile ? 'Opsæt' : 'Opsæt på…'}</Button>}
            {isAdmin && !mobile && <Menu position="bottom-end" withinPortal>
              <Menu.Target><ActionIcon variant="subtle" color="gray" aria-label="Mere"><IconDots size={16} /></ActionIcon></Menu.Target>
              <Menu.Dropdown><Menu.Item leftSection={<IconPencil size={14} />} onClick={() => open(draftFrom(f))}>Tilføj med flere indstillinger</Menu.Item></Menu.Dropdown>
            </Menu>}
          </Group>
        ))}
      </Panel>

      <Text fw={700} size="sm" mb="xs">I Fleetdeck</Text>
      <Panel p={0}>
        {printers.length === 0 && <Empty>Ingen printere i Fleetdeck endnu. Vælg "Opsæt på…" ved en fundet printer, eller tilføj en med dens adresse.</Empty>}
        {printers.map((p, i) => (
          <Group key={p.id} gap="sm" wrap="nowrap" px="md" py={10} style={rowBorder(i)}>
            <IconPrinter size={18} style={{ flex: 'none', opacity: 0.8 }} />
            <Box style={{ flex: mobile ? 1 : '1 1 30%', minWidth: 0 }}>
              <Text size="sm" fw={600} lineClamp={1}>{p.name}<StatusDot level={p.status.level} /></Text>
              <Text size="xs" c="dimmed" lineClamp={1}>{[p.makeModel, p.host].filter(Boolean).join(' · ')}</Text>
              {mobile && p.status.level !== 'ok' && <Text size="xs" c={LEVEL_COLOR[p.status.level] === 'gray' ? 'dimmed' : LEVEL_COLOR[p.status.level]}>{p.status.text}</Text>}
            </Box>
            {!mobile && <Box w={110}><DriverBadge driverless={p.driverless} /></Box>}
            {!mobile && <Box w={80}><InkBars markers={p.status.markers} /></Box>}
            {!mobile && <Tooltip label={p.status.error ?? (p.status.at ? `Tjekket ${ago(p.status.at)}` : 'Ikke tjekket endnu')}>
              <Text size="xs" w={110} c={p.status.level === 'ok' ? 'dimmed' : LEVEL_COLOR[p.status.level]} lineClamp={1}>{p.status.text}</Text>
            </Tooltip>}
            {!mobile && <Text size="xs" c="dimmed" w={170} lineClamp={2}>{placedText(p)}</Text>}
            {isAdmin && (
              <Menu position="bottom-end" withinPortal>
                <Menu.Target><ActionIcon variant="subtle" color="gray" aria-label="Mere"><IconDots size={16} /></ActionIcon></Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => open(draftOf(p), p)}>Ret og læg på</Menu.Item>
                  <Menu.Item leftSection={<IconTrash size={14} />} color="red" onClick={() => askDelete(p)}>Slet</Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
          </Group>
        ))}
      </Panel>

      <PrinterModal draft={draft} assignments={editing?.assignments} onClose={() => { setDraft(null); setEditing(null); }} />
      <SetupModal found={setup} data={data} onClose={() => setSetup(null)} />
      <SearchSettings data={data} opened={settings} onClose={() => setSettings(false)} />
    </>
  );
}
