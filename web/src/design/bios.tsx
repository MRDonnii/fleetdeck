// BIOS og firmware (design: docs/design/bios-mockups, skitserne bios-1 … bios-7).
// A: sektionen på maskinens Opdateringer-fane med "Opdatér BIOS …"-guiden (backup → kontrol → bekræft → opdatering → efter genstart),
//    sammenlign og gendan, og "Lav USB-stik" (reserven). B: fanen BIOS under Opdateringer (flådeoversigt med sidepanel).
// Læsere ser status, men ingen knapper. BIOS kommer aldrig med i "Opdatér alt", og opdateringen tager altid en backup først.
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  ActionIcon, Alert, Anchor, Badge, Box, Button, Checkbox, Code, Grid, Group, Loader, Modal, Paper, PasswordInput, Progress, Radio, ScrollArea, SegmentedControl,
  Select, Stack, Stepper, Table, Text, TextInput, ThemeIcon, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import {
  IconAlertTriangle, IconArrowUp, IconArrowsDiff, IconBolt, IconCheck, IconChevronRight, IconCircleCheck, IconCircleDashed, IconCircleX, IconCpu2,
  IconDatabaseExport, IconDownload, IconExternalLink, IconHistory, IconInfoCircle, IconListCheck, IconLock, IconMinus, IconPoint, IconRefresh,
  IconRestore, IconSearch, IconShieldLock, IconSparkles, IconTrash, IconUsb,
} from '@tabler/icons-react';
import { ApiError, CSRF_HEADER, del, get, notifyErr, notifyOk, post, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading } from '../components';
import { ago, fmt } from '../format';
import type {
  BiosCheck, BiosDiff, BiosFleetRow, BiosPackageRow, BiosSettingItem, BiosSettingsInfo, BiosSnapshotMeta, BiosSnapshotReason, BiosStatus,
  BiosUpdateCheck, BiosUpdateInfo, BiosVendorStatus, JobDetail, MachineDetail,
} from '../types';
import { KindIcon, kindOf } from './platform';
import { BIOS_UPDATE_BUTTON, biosUpdateState, biosUpdateWarnings, rowUsbOnly } from './biosUpdateText';
import {
  BIOS_COLOR, ERASE_WARNING, SNAPSHOT_REASON, UPDATE_ALL_NEVER_BIOS, UPDATE_ALL_SKIPS_BIOS, UPDATE_STEPS, USB_ONLY_LOCAL, USB_STEPS, backupFresh,
  biosBadgeTip, biosBadges, biosStateLine, bytes, confirmTickText, diffKind, diffSummary, fleetCounts, fleetFilter, fleetQuiet, importantChange, localLink,
  backupLabel, mergeChecks, onLocalhost, restoreButton, routeLabel, routeLong, shortVersion, updateTimeline, usbChoosable, usbDiskName, vendorName,
  wizardStepFor, type FleetFilter, type TlItem, type Tone,
} from './biosText';
import { USB_EXTERNAL_DRIVE, usbWriterHere } from './biosText';

type SnapRow = { id: number; takenAt: string; reason: BiosSnapshotReason; driver: string; biosVersion?: string | null; by: string };
type MachineBios = { status: BiosStatus | null; snapshots: SnapRow[]; settings: BiosSettingsInfo | null; catalog: BiosVendorStatus | null; localPort?: number };

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
const toneColor = (t: Tone | 'violet') => (t === 'yellow' ? 'orange' : t);
const ToneBadge = ({ t, icon }: { t: { text: string; tone: Tone | 'violet' }; icon?: ReactNode }) => (
  <Badge variant="light" color={toneColor(t.tone)} leftSection={icon} style={{ textTransform: 'none' }}>{t.text}</Badge>
);
/**
 * Mærkerne i flådetabellen: altid i fuld længde (Mantines Badge afkorter ellers med "…", når sidepanelet gør tabellen smal –
 * labelen har overflow: hidden, så cellen kan presses under tekstens bredde), og med den fulde forklaring som tooltip (skitse bios-7).
 */
export const FleetBadges = ({ s }: { s: BiosStatus | null | undefined }) => (
  <Group gap={5} wrap="wrap" className="fd-bios-badges">{biosBadges(s).map((x) => (
    <Tooltip key={x.text} label={biosBadgeTip(s, x)} multiline maw={320} withArrow openDelay={250}>
      <Badge variant="light" color={toneColor(x.tone)} leftSection={x.important ? <IconShieldLock size={11} /> : undefined}
        style={{ textTransform: 'none', flexShrink: 0, maxWidth: 'none', overflow: 'visible' }}
        styles={{ label: { overflow: 'visible', textOverflow: 'clip' } }}>{x.text}</Badge>
    </Tooltip>))}
  </Group>
);
export const BiosBadge = () => <Badge size="xs" variant="light" color={BIOS_COLOR} leftSection={<IconCpu2 size={11} />}>BIOS</Badge>;
const Info = ({ children }: { children: ReactNode }) => (
  <Group gap={6} wrap="nowrap" align="flex-start"><IconInfoCircle size={14} style={{ flexShrink: 0, marginTop: 2, opacity: 0.6 }} /><Text size="xs" c="dimmed">{children}</Text></Group>
);
const CheckIcon = ({ state }: { state: BiosUpdateCheck['state'] }) => (
  state === 'ok' ? <IconCircleCheck size={16} color="var(--fd-ok)" /> : state === 'warn' ? <IconAlertTriangle size={16} color="var(--fd-warn)" /> : <IconCircleX size={16} color="var(--fd-bad)" />
);

/** Et vindue åbnes andre steder fra (fx BIOS-rækken i opdateringslisten): send en hændelse, sektionen lytter. */
const OPEN_EVENT = 'fd-bios-open';
export const openBios = (machineId: number, what: 'update' | 'usb' | 'compare') =>
  window.dispatchEvent(new CustomEvent(OPEN_EVENT, { detail: { machineId, what } }));

/** Relativt til <base href>, så downloads virker bag en understi. */
const download = (url: string) => { window.location.href = url.replace(/^\//, ''); };

/** "Hent pakke": hent til serverens cache (kun på et tryk) og download den, når den er klar. Fejl giver en tekst, aldrig en evig spinner. */
/** "Hent pakke" (USB-pakken). <paramref name="size"/>: størrelsen, når den er kendt (vises i knappen som på skitse bios-1). */
function usePackageDownload(machineId: number, size?: number | null) {
  const [busy, setBusy] = useState<BiosPackageRow | null>(null);
  const timer = useRef<number | null>(null);
  useEffect(() => () => { if (timer.current) window.clearTimeout(timer.current); }, []);
  const poll = (id: number) => {
    timer.current = window.setTimeout(async () => {
      try {
        const p = await get<BiosPackageRow>(`/api/bios/packages/${id}`);
        if (p.state === 'ready') { setBusy(null); download(`/api/bios/packages/${p.id}/download?machineId=${machineId}`); }
        else if (p.state === 'failed' || p.stale) { setBusy(null); notifyErr(new Error(p.error ?? 'Pakken kunne ikke hentes fra leverandøren.')); }
        else { setBusy(p); poll(id); }
      } catch (e) { setBusy(null); notifyErr(e); }
    }, 1500);
  };
  const start = async () => {
    try {
      const p = await post<BiosPackageRow>('/api/bios/packages', { machineId, kind: 'usb-zip' });
      if (p.state === 'ready') download(`/api/bios/packages/${p.id}/download?machineId=${machineId}`);
      else { setBusy(p); notifyOk('Pakken hentes fra leverandøren. Den downloades, når den er klar.'); poll(p.id); }
    } catch (e) { notifyErr(e); }
  };
  return { start, busy, label: busy ? `Henter … ${busy.total ? Math.round((busy.bytes / busy.total) * 100) : 0} %` : size ? `Hent pakke (${bytes(size)})` : 'Hent pakke' };
}

/** Må guiden "Lav USB-stik" åbnes for maskinen? */
const allowUsb = (isAdmin: boolean, b: MachineBios, platform: string) =>
  isAdmin && !!b.status?.latest && b.status.vendor === 'lenovo' && platform !== 'mac';
/** Må guiden "Opdatér BIOS" åbnes (en vej findes, og der er en nyere BIOS eller en kørsel at vise)? */
const allowUpdate = (isAdmin: boolean, b: MachineBios, u: BiosUpdateInfo | undefined) =>
  isAdmin && !!u?.route && (b.status?.state === 'newer' || !!u.run && ['checking', 'staging', 'staged', 'rebooting', 'awaiting_report'].includes(u.run.effective));

async function takeBackup(machineId: number, reason: 'manual' | 'pre-update' = 'manual') {
  return post<{ jobId: number }>(`/api/bios/machines/${machineId}/settings/read`, { reason });
}

// ====================================================================== A: maskinsiden

/** Sektionen "BIOS og firmware" øverst på maskinens Opdateringer-fane (skitse bios-1). */
export function BiosMachineSection({ m }: { m: MachineDetail }) {
  const { isAdmin } = useMe();
  const mobile = !!useMediaQuery('(max-width: 48em)');
  const { data: b, refetch } = useApi<MachineBios>(['bios', m.id], `/api/bios/machines/${m.id}`, { refetchInterval: 20_000 });
  const { data: u } = useApi<BiosUpdateInfo>(['bios-update', m.id], `/api/bios/machines/${m.id}/update`, { refetchInterval: 15_000 });
  const [open, setOpen] = useState<null | 'update' | 'usb' | { from?: number; to?: number }>(null);
  const [showAll, setShowAll] = useState(false);
  const [backupJob, setBackupJob] = useState<number | null>(null);
  const pkg = usePackageDownload(m.id, u?.route === 'capsule' && u.package?.size ? u.package.size : null);   // kapsel-vejen bruger samme USB-pakke
  const { data: bj } = useApi<JobDetail>(['job', backupJob], backupJob ? `/api/jobs/${backupJob}` : null, { refetchInterval: 2500 });
  const [params, setParams] = useSearchParams();
  // dybe links (?bios=…) og "åbn"-hændelser: guiderne åbnes kun, hvis brugeren må og maskinen kan (ellers ignoreres de)
  const [want, setWant] = useState<null | 'update' | 'usb' | 'compare'>(null);
  useEffect(() => {
    const w = params.get('bios');
    if (!w) return;
    setWant(w === 'usb' ? 'usb' : w === 'compare' ? 'compare' : 'update');
    const p = new URLSearchParams(params);
    p.delete('bios');
    setParams(p, { replace: true });
  }, [params, setParams]);
  useEffect(() => {
    if (!want || !b || (want === 'update' && !u)) return;
    if (want === 'compare') setOpen({});
    else if (want === 'usb' ? allowUsb(isAdmin, b, m.platform) : allowUpdate(isAdmin, b, u)) setOpen(want);
    setWant(null);
  }, [want, b, u, isAdmin, m.platform]);
  useEffect(() => {
    const f = (e: Event) => {
      const d = (e as CustomEvent<{ machineId: number; what: 'update' | 'usb' | 'compare' }>).detail;
      if (d.machineId === m.id) setWant(d.what);
    };
    window.addEventListener(OPEN_EVENT, f);
    return () => window.removeEventListener(OPEN_EVENT, f);
  }, [m.id]);
  useEffect(() => {
    if (bj && !['pending', 'running', 'waiting_user'].includes(bj.status)) {
      if (bj.status === 'success') notifyOk('Backup af BIOS-indstillingerne er gemt.');
      else notifyErr(new Error(bj.output || 'Backup af BIOS-indstillingerne mislykkedes.'));
      setBackupJob(null);
      refetch();
    }
  }, [bj, refetch]);
  if (!b) return null;
  const s = b.status;
  const quiet = !s || ['mac', 'pi', 'virtual', 'unsupported', 'unchecked', 'uncertain'].includes(s.state);
  const line = biosStateLine(s, b.catalog?.error ? `Leverandørens katalog kunne ikke hentes siden ${fmt(b.catalog.errorSince)}. Sidste kendte svar vises.` : null);
  const run = u?.run ?? null;
  const running = !!run && ['checking', 'staging', 'staged', 'rebooting', 'awaiting_report'].includes(run.effective);
  const imp = importantChange(s);
  const changes = s?.changes ?? [];
  const canUpdate = isAdmin && !!u?.route && s?.state === 'newer';
  const canUsb = allowUsb(isAdmin, b, m.platform);
  const head = (
    <Group gap="sm" wrap="nowrap">
      <IconCpu2 size={20} /><Title order={4} style={{ flex: 1 }}>BIOS og firmware</Title>
      {running ? <Badge variant="light" color="blue" leftSection={<Loader size={10} />}>Opdatering i gang</Badge>
        : s?.state === 'newer' ? <Badge variant="light" color="orange" leftSection={<IconArrowUp size={12} />}>Nyere findes</Badge>
          : s?.state === 'newest' ? <Badge variant="light" color="green">Nyeste</Badge> : null}
    </Group>
  );

  const main = (
    <Paper withBorder radius="md" p="md" mb="md" style={{ minWidth: 0 }}>
      {head}
      <Box mt="sm">
        {mobile ? <Kv rows={[
          ['Nuværende', <Text span ff="monospace" size="sm">{s?.current ?? m.facts.biosVersion ?? '–'}<Text span c="dimmed" inherit>{s?.currentDate ? ` · ${s.currentDate}` : ''}</Text></Text>],
          ['Nyeste', s?.latest ? <Text span size="sm"><Text span ff="monospace" fw={700} inherit>{s.latest}</Text><Text span c="dimmed" inherit>{s.latestDate ? ` · ${s.latestDate}` : ''}</Text></Text>
            : <Text span size="sm" c={toneColor(line.tone)}>{line.text}</Text>],
          ...(s?.state === 'newer' ? [['Vej', routeLabel(u?.route, s?.vendor, canUsb)] as [string, ReactNode]] : []),
          ['Indstillinger', b.settings ? `${b.settings.count} · ${b.settings.adminPassword ? 'BIOS-password er sat' : 'intet BIOS-password'}` : 'Kan ikke læses'],
        ]} /> : <Kv rows={[
          ['Model', <>{[vendorName(s?.vendor === 'none' ? null : s?.vendor), m.facts.productVersion || m.facts.model].filter((x) => x && x !== '–').join(' ') || '–'}
            {s?.catalogKey && <Text span c="dimmed"> · BIOS-familie {s.catalogKey.split('/')[0]}</Text>}</>],
          ['Nuværende', <Text span ff="monospace" size="sm">{s?.current ?? m.facts.biosVersion ?? '–'}<Text span c="dimmed" inherit>{s?.currentDate ? ` · ${s.currentDate}` : ''}{s?.currentNumeric ? ` · ${s.currentNumeric}` : ''}</Text></Text>],
          ['Nyeste', s?.latest
            ? <Text span size="sm"><Text span ff="monospace" fw={700} inherit>{s.latest}</Text><Text span c="dimmed" ff="monospace" inherit>{s.latestDate ? ` · ${s.latestDate}` : ''}{s.latestNumeric ? ` · ${s.latestNumeric}` : ''}</Text>
              {' '}<Text span fw={600} c={toneColor(line.tone)} inherit>{line.text}</Text></Text>
            : <Text span size="sm" c={toneColor(line.tone)}>{line.text}</Text>],
          ...(!quiet ? [['Kilde', `${s?.source || `${vendorName(s?.vendor)}-katalog`} · tjekket ${ago(s?.checkedAt)}${s?.matchQuality === 'exact' ? ' · modellen passer sikkert' : ''}`] as [string, ReactNode]] : []),
          ...(s?.state === 'newer' ? [['Opdateringsvej', <>{routeLong(u?.route, u?.routeWhy)} {u?.route && canUsb && <Text span c="dimmed" inherit>Reserve: USB-stik.</Text>}</>] as [string, ReactNode]] : []),
          ['Indstillinger', b.settings ? `${b.settings.count} kan læses${b.settings.writable ? ' og skrives' : ''} (${b.settings.driver})${b.settings.adminPassword ? ' · BIOS-password er sat' : ' · intet BIOS-password'}`
            : 'Agenten kan ikke læse BIOS-indstillingerne på denne maskine.'],
        ]} />}
      </Box>
      {imp && isSecureBootLine(imp.text) && (
        <Alert mt="sm" color="orange" variant="light" icon={<IconShieldLock size={16} />}>
          Secure Boot-nøglerne fra 2011 udløber i 2026. {s?.latest} har de nye nøgler fra 2023.
        </Alert>
      )}
      {changes.length > 0 && (
        <Box mt="md">
          <Group justify="space-between" mb={4}>
            <Text size="sm" fw={650}>Det er nyt siden din version</Text>
            {changes.length > 4 && <Anchor size="xs" c="dimmed" onClick={() => setShowAll((x) => !x)}>{showAll ? 'Vis færre' : 'Vis hele listen'}</Anchor>}
          </Group>
          {(showAll ? changes : changes.slice(0, 4)).map((c) => (
            <Group key={c.version} gap="sm" wrap="nowrap" align="flex-start" py={3}>
              <Text size="sm" ff="monospace" fw={700} c="dimmed" w={42} style={{ flexShrink: 0 }}>{shortVersion(c.version)}</Text>
              <Stack gap={2} style={{ minWidth: 0 }}>
                {(c.lines.length ? c.lines : [{ text: '–', important: false }]).slice(0, showAll ? 20 : 2).map((l, i) => (
                  <Text key={i} size="sm">{l.text}{l.important && <Badge ml={6} size="xs" variant="light" color="red" leftSection={<IconShieldLock size={10} />} style={{ textTransform: 'none' }}>
                    Vigtig{isSecureBootLine(l.text) ? ' · Secure Boot' : ''}</Badge>}</Text>
                ))}
              </Stack>
            </Group>
          ))}
        </Box>
      )}
      {isAdmin && !quiet && mobile && (
        <Stack mt="md" gap={8}>
          {canUpdate && <Button fullWidth leftSection={<IconCpu2 size={16} />} onClick={() => setOpen('update')} disabled={!running && !u?.canCheck}>{running ? 'Vis opdateringen' : BIOS_UPDATE_BUTTON}</Button>}
          <Group gap={8} grow>
            {canUsb && <Button variant="default" px={6} leftSection={<IconUsb size={16} />} onClick={() => setOpen('usb')}>USB-stik</Button>}
            {s?.latest && <Button variant="default" px={6} leftSection={<IconDownload size={16} />} disabled={!!pkg.busy} onClick={pkg.start}>{pkg.busy ? pkg.label : 'Hent'}</Button>}
            {b.settings && <Button variant="default" px={6} leftSection={<IconDatabaseExport size={16} />} loading={!!backupJob}
              onClick={() => takeBackup(m.id).then((r) => { setBackupJob(r.jobId); notifyOk('Backup af BIOS-indstillingerne er bestilt.'); }, notifyErr)}>Backup</Button>}
          </Group>
        </Stack>
      )}
      {isAdmin && !quiet && !mobile && (
        <Group mt="md" gap="xs">
          {canUpdate && <Button leftSection={<IconCpu2 size={16} />} onClick={() => setOpen('update')} disabled={!running && !u?.canCheck}>{running ? 'Vis opdateringen' : BIOS_UPDATE_BUTTON}</Button>}
          {canUsb && <Button variant="default" leftSection={<IconUsb size={16} />} onClick={() => setOpen('usb')}>Lav USB-stik</Button>}
          {s?.latest && <Button variant="default" leftSection={<IconDownload size={16} />} loading={false} disabled={!!pkg.busy} onClick={pkg.start}>{pkg.label}</Button>}
          {b.settings && <Button variant="default" leftSection={<IconDatabaseExport size={16} />} loading={!!backupJob}
            onClick={() => takeBackup(m.id).then((r) => { setBackupJob(r.jobId); notifyOk('Backup af BIOS-indstillingerne er bestilt.'); }, notifyErr)}>Tag backup af indstillinger</Button>}
        </Group>
      )}
      {!quiet && <Box mt="sm"><Info>{BIOS_UPDATE_BUTTON.replace('…', '')} tager altid en backup af indstillingerne først. BIOS kommer aldrig med i "Opdatér alt".</Info></Box>}
      {running && run && <Alert mt="sm" variant="light" color={toneColor(biosUpdateState(run).tone)} icon={<IconCpu2 size={16} />}>{biosUpdateState(run).text}</Alert>}
    </Paper>
  );

  return (
    <>
      {quiet ? main : (
        <Grid gap="md" mb={0}>
          <Grid.Col span={{ base: 12, lg: 7 }}>{main}</Grid.Col>
          <Grid.Col span={{ base: 12, lg: 5 }}>
            {u && s?.state === 'newer' && <ReadyPanel u={u} secureBoot={m.facts.secureBoot} />}
            <SnapshotsPanel machineId={m.id} snaps={b.snapshots} settings={b.settings} isAdmin={isAdmin} onCompare={(from, to) => setOpen({ from, to })} />
          </Grid.Col>
        </Grid>
      )}
      {open === 'update' && u && <UpdateWizard m={m} b={b} u={u} onClose={() => setOpen(null)} onCompare={() => setOpen({})} />}
      {open === 'usb' && s && <UsbWizard m={m} b={b} status={s} onClose={() => setOpen(null)} />}
      {open && typeof open === 'object' && <CompareModal machineId={m.id} name={m.name} snaps={b.snapshots} from={open.from} to={open.to} isAdmin={isAdmin} onClose={() => setOpen(null)} />}
    </>
  );
}

function Kv({ rows }: { rows: [string, ReactNode][] }) {
  return (
    <Table withRowBorders={false} verticalSpacing={4} horizontalSpacing={0}>
      <Table.Tbody>{rows.map(([k, v]) => (
        <Table.Tr key={k}><Table.Td w={130} c="dimmed" style={{ verticalAlign: 'top' }}><Text size="sm" c="dimmed">{k}</Text></Table.Td>
          <Table.Td style={{ overflowWrap: 'anywhere' }}><Text size="sm" component="div">{v}</Text></Table.Td></Table.Tr>
      ))}</Table.Tbody>
    </Table>
  );
}

const isSecureBootLine = (t: string) => /secure ?boot|\bKEK\b|UEFI CA/i.test(t);

/** "Klar til opdatering": serverens og agentens kontroller. */
function ReadyPanel({ u, secureBoot }: { u: BiosUpdateInfo; secureBoot?: boolean }) {
  const checks = mergeChecks(u.serverChecks, u.run && ['checked', 'blocked'].includes(u.run.state) ? u.run.checks : null);
  const ready = u.canStart && !checks.some((c) => c.state === 'fail');
  return (
    <Paper withBorder radius="md" p="md" mb="md">
      <Group gap="sm" mb="xs"><IconListCheck size={18} /><Text fw={650} style={{ flex: 1 }}>Klar til opdatering</Text>
        <Badge variant="light" color={ready ? 'green' : 'orange'}>{ready ? 'Ja' : 'Ikke endnu'}</Badge></Group>
      <Stack gap={6}>
        {secureBoot !== undefined && <Group gap={8} wrap="nowrap"><IconCircleCheck size={16} color="var(--fd-ok)" /><Text size="sm" style={{ flex: 1 }}>Secure Boot</Text><Text size="sm" c="dimmed">{secureBoot ? 'Til' : 'Fra'}</Text></Group>}
        {checks.map((c) => <Group key={c.id} gap={8} wrap="nowrap" align="flex-start"><CheckIcon state={c.state} /><Text size="sm">{c.text}</Text></Group>)}
      </Stack>
    </Paper>
  );
}

/** "Backup af BIOS-indstillinger": kopierne med "Sammenlign med nu" og download som fil. */
function SnapshotsPanel({ machineId, snaps, settings, isAdmin, onCompare }: {
  machineId: number; snaps: SnapRow[]; settings: BiosSettingsInfo | null; isAdmin: boolean; onCompare: (from?: number, to?: number) => void;
}) {
  const mobile = useMediaQuery('(max-width: 48em)');
  const { data: chk } = useApi<BiosCheck>(['bios-check', machineId], snaps.some((s) => s.reason === 'pre-restore') ? `/api/bios/machines/${machineId}/settings/check` : null);
  const save = async (id: number) => {
    try {
      const d = await get<{ snapshot: BiosSnapshotMeta; items: BiosSettingItem[] }>(`/api/bios/snapshots/${id}`);
      const blob = new Blob([JSON.stringify(d, null, 2)], { type: 'application/json' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `bios-indstillinger-${d.snapshot.biosVersion ?? 'ukendt'}-${d.snapshot.takenAt.slice(0, 10)}.json`;
      a.click();
      URL.revokeObjectURL(a.href);
    } catch (e) { notifyErr(e); }
  };
  if (!settings && !snaps.length) return null;
  return (
    <Paper withBorder radius="md" p={0} mb="md">
      <Group gap="sm" p="md" pb="xs"><IconHistory size={18} /><Text fw={650} style={{ flex: 1 }}>Backup af BIOS-indstillinger</Text>
        {snaps.length > 1 && !mobile && <Button size="compact-sm" variant="subtle" leftSection={<IconArrowsDiff size={14} />} onClick={() => onCompare()}>Sammenlign to…</Button>}</Group>
      {!snaps.length ? <Text size="sm" c="dimmed" px="md" pb="md">Ingen backup endnu.{isAdmin ? ' Tryk "Tag backup af indstillinger".' : ''}</Text>
        : snaps.slice(0, 6).map((s, i) => (
          <Group key={s.id} px="md" py={9} gap="xs" wrap="nowrap" style={{ borderTop: '1px solid var(--fd-border)' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Text size="sm" fw={600}>{fmt(s.takenAt)}</Text>
              <Text size="xs" c="dimmed" ff="monospace" truncate>{s.biosVersion ?? '–'} · {SNAPSHOT_REASON[s.reason] ?? s.reason}{s.by && s.by !== 'system' ? ` · ${s.by}` : ''}</Text>
            </div>
            {i > 0 && <Button size="compact-sm" variant="default" leftSection={mobile ? undefined : <IconArrowsDiff size={14} />} onClick={() => onCompare(s.id, snaps[0].id)}>{mobile ? <IconArrowsDiff size={14} /> : 'Sammenlign med nu'}</Button>}
            <Button size="compact-sm" variant="subtle" color="gray" aria-label="Hent som fil" onClick={() => save(s.id)}><IconDownload size={14} /></Button>
          </Group>
        ))}
      {chk?.rows && (
        <Box px="md" py="sm" style={{ borderTop: '1px solid var(--fd-border)' }}>
          <Text size="sm" fw={600} mb={4}>Kontrol efter gendannelse</Text>
          {chk.rows.map((r) => (
            <Group key={r.name} gap={6} wrap="nowrap"><Badge size="xs" color={r.color === 'yellow' ? 'orange' : r.color} variant="light">{r.color === 'green' ? 'OK' : r.color === 'yellow' ? 'Normaliseret' : 'Afvist'}</Badge>
              <Text size="xs" truncate>{r.name}: {r.actual ?? '–'}{r.why ? ` · ${r.why}` : ''}</Text></Group>
          ))}
        </Box>
      )}
    </Paper>
  );
}

/** BIOS-rækken i maskinens opdateringsliste: violet mærke, knappen åbner guiden – aldrig "Opdatér". */
export function BiosUpgradeRow({ m }: { m: MachineDetail }) {
  const { isAdmin } = useMe();
  const { data: b } = useApi<MachineBios>(['bios', m.id], `/api/bios/machines/${m.id}`, { refetchInterval: 20_000 });
  const s = b?.status;
  const { data: u } = useApi<BiosUpdateInfo>(['bios-update', m.id], s?.state === 'newer' ? `/api/bios/machines/${m.id}/update` : null);
  if (!s || s.state !== 'newer') return null;
  // ingen vej inde fra styresystemet (fx Lenovos rå ROM på Linux): knappen åbner USB-guiden i stedet for en guide, der ikke kan bruges.
  // Først når vejen er hentet: ellers blinker "Lav USB-stik…" (eller bliver stående, hvis kaldet fejler) på en maskine med en vej fra Windows.
  const usbOnly = rowUsbOnly(u, allowUsb(isAdmin, b, m.platform));
  return (
    <Paper withBorder radius="md" p={0} mb="md">
      <Group px="md" py="sm" gap="sm" wrap="nowrap">
        <Group gap={7} style={{ flex: 1, minWidth: 0 }} wrap="nowrap"><Text size="sm" truncate>BIOS {s.current}</Text><BiosBadge /></Group>
        <Text size="sm" ff="monospace" c="dimmed" visibleFrom="xs">{s.current}</Text>
        <Text size="sm" ff="monospace" fw={700}>{s.latest}</Text>
        {isAdmin && (u?.route ? <Button size="compact-xs" variant="default" onClick={() => openBios(m.id, 'update')}>{BIOS_UPDATE_BUTTON}</Button>
          : usbOnly && <Button size="compact-xs" variant="default" leftSection={<IconUsb size={14} />} onClick={() => openBios(m.id, 'usb')}>Lav USB-stik…</Button>)}
      </Group>
      <Box px="md" py={8} style={{ borderTop: '1px solid var(--fd-border)' }}><Info>{UPDATE_ALL_SKIPS_BIOS}</Info></Box>
    </Paper>
  );
}

// ====================================================================== "Opdatér BIOS …"-guiden (skitse bios-2 … bios-5)

function Timeline({ items }: { items: TlItem[] }) {
  return (
    <Stack gap={0}>
      {items.map((t, i) => (
        <Group key={i} gap="sm" wrap="nowrap" align="flex-start" py={5}>
          <ThemeIcon size={24} radius="xl" variant={t.state === 'wait' ? 'default' : 'light'}
            color={t.state === 'done' ? 'green' : t.state === 'run' ? 'blue' : t.state === 'bad' ? 'red' : 'gray'}>
            {t.state === 'done' ? <IconCheck size={14} /> : t.state === 'run' ? <Loader size={12} /> : t.state === 'bad' ? <IconAlertTriangle size={14} /> : <IconPoint size={14} />}
          </ThemeIcon>
          <div style={{ minWidth: 0, paddingTop: 2 }}>
            <Text size="sm" fw={t.state === 'wait' ? 500 : 600} c={t.state === 'wait' ? 'dimmed' : undefined}>{t.text}</Text>
            {t.sub && <Text size="xs" c="dimmed">{t.sub}</Text>}
          </div>
        </Group>
      ))}
    </Stack>
  );
}

function WizardFooter({ mobile, children }: { mobile: boolean; children: ReactNode }) {
  return mobile
    ? <Stack gap="xs" mt="md" style={{ flexDirection: 'column-reverse' }}>{children}</Stack>
    : <Group justify="flex-end" mt="md" gap="xs">{children}</Group>;
}

function UpdateWizard({ m, b, u: initial, onClose, onCompare }: { m: MachineDetail; b: MachineBios; u: BiosUpdateInfo; onClose: () => void; onCompare: () => void }) {
  const mobile = !!useMediaQuery('(max-width: 48em)');
  const { data: u0, refetch } = useApi<BiosUpdateInfo>(['bios-update', m.id], `/api/bios/machines/${m.id}/update`, { refetchInterval: 2500 });
  const u = u0 ?? initial;
  const { data: bios } = useApi<MachineBios>(['bios', m.id], `/api/bios/machines/${m.id}`, { refetchInterval: 3000 });
  const resume = wizardStepFor(initial.run);
  const [step, setStep] = useState(resume ?? 0);
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(t); }, []);
  const target = u.target ?? initial.target ?? '';
  const run = u.run;

  // trin 1: backup (kan ikke springes over)
  const [since] = useState(() => Date.now() - 2000);
  const [backupJob, setBackupJob] = useState<number | null>(null);
  const [backupErr, setBackupErr] = useState<string | null>(null);
  const started = useRef(false);
  const { data: bj } = useApi<JobDetail>(['job', backupJob], backupJob ? `/api/jobs/${backupJob}` : null, { refetchInterval: 1500 });
  const snap = (bios ?? b).snapshots.find((x) => new Date(x.takenAt).getTime() >= since);
  const { data: snapItems } = useApi<{ snapshot: BiosSnapshotMeta; items: BiosSettingItem[] }>(['bios-snap', snap?.id], snap ? `/api/bios/snapshots/${snap.id}` : null);
  useEffect(() => {
    if (step !== 0 || started.current || resume !== null) return;
    started.current = true;
    if (!b.settings) { setBackupErr('Agenten kan ikke læse BIOS-indstillingerne på denne maskine, så der kan ikke tages en backup. Opdateringen kræver en backup. Brug USB-stikket.'); return; }
    takeBackup(m.id, 'pre-update').then((r) => setBackupJob(r.jobId), (e) => setBackupErr(errText(e)));
  }, [step, resume, b.settings, m.id]);
  useEffect(() => { if (bj && ['failed', 'cancelled', 'timeout'].includes(bj.status)) setBackupErr(bj.output || 'Backuppen kunne ikke tages.'); }, [bj]);

  // trin 2: kontrol (kun læsning på maskinen)
  const [checkRun, setCheckRun] = useState<number | null>(null);
  const [checkErr, setCheckErr] = useState<string | null>(null);
  useEffect(() => {
    if (step !== 1 || checkRun !== null) return;
    setCheckRun(-1);
    post<{ runId: number }>(`/api/bios/machines/${m.id}/update/check`).then((r) => { setCheckRun(r.runId); refetch(); }, (e) => setCheckErr(errText(e)));
  }, [step, checkRun, m.id, refetch]);
  const checkDone = run && run.id === checkRun && ['checked', 'blocked', 'failed'].includes(run.state);
  const checks = mergeChecks(u.serverChecks, run && run.id === checkRun ? run.checks : null);

  // trin 3: bekræft
  const [when, setWhen] = useState<'now' | 'later'>('now');
  const [tick, setTick] = useState(false);
  const [typed, setTyped] = useState('');
  const [waitPkg, setWaitPkg] = useState(false);
  const [busy, setBusy] = useState(false);
  const [startErr, setStartErr] = useState<string | null>(null);
  const wantReboot = useRef(false);
  // den version, brugeren skrev og bekræftede: også de automatiske forsøg efter pakkehentningen sender netop den
  const confirmed = useRef<string | null>(null);
  const start = async (auto = false) => {
    const t = auto ? confirmed.current : typed.trim();
    if (!t) return;
    confirmed.current = t;
    setBusy(true); setStartErr(null);
    try {
      await post(`/api/bios/machines/${m.id}/update/start`, { confirm: true, target: t });
      wantReboot.current = when === 'now';
      setWaitPkg(false);
      setStep(3);
      refetch();
    } catch (e) {
      const msg = errText(e);
      if (e instanceof ApiError && e.status === 409 && /hentes nu/.test(msg)) setWaitPkg(true);
      else { setWaitPkg(false); setStartErr(msg); }
    } finally { setBusy(false); }
  };
  useEffect(() => {
    if (!waitPkg) return;
    if (confirmed.current && u.target && u.target.toUpperCase() !== confirmed.current.toUpperCase()) {
      // målet er skiftet, mens pakken blev hentet: intet startes uden en ny bekræftelse
      setWaitPkg(false); setTick(false); setTyped('');
      setStartErr(`Fleetdeck kender nu en anden version (${u.target}). Bekræft den, hvis du vil fortsætte.`);
    } else if (u.package?.state === 'ready' && !busy) start(true);
    else if (u.package?.state === 'failed') { setWaitPkg(false); setStartErr(u.package.error ?? 'Pakken kunne ikke hentes.'); }
  });

  // trin 4: genstart nu, når kapslen ligger klar (brugerens valg)
  const rebooted = useRef(false);
  useEffect(() => {
    if (step === 3 && run?.effective === 'staged' && run.canReboot && wantReboot.current && !rebooted.current) {
      rebooted.current = true;
      post(`/api/bios/machines/${m.id}/update/reboot`).then(() => refetch(), notifyErr);
    }
    if (step === 3 && run && wizardStepFor(run) === 4) setStep(4);
  }, [step, run, m.id, refetch]);

  const title = step >= 3 ? `BIOS-opdatering på ${m.name}` : `Opdatér BIOS på ${m.name}`;
  const done = (bj?.status === 'success' && !!snap) || !!snapItems;
  const items = snapItems?.items ?? [];
  const state = run ? biosUpdateState(run) : null;

  return (
    <Modal opened onClose={onClose} zIndex={300} size={980} fullScreen={mobile} title={<Group gap={8}><IconCpu2 size={20} /><Text fw={700}>{title}</Text></Group>}>
      <Stepper active={step} size="sm" allowNextStepsSelect={false} mb="md" iconSize={mobile ? 26 : 30}>
        {UPDATE_STEPS.map((l, i) => <Stepper.Step key={l} label={mobile && i !== step ? undefined : l} />)}
      </Stepper>

      {step === 0 && (
        <Stack gap="sm">
          <Text size="sm"><Text span ff="monospace" inherit>{u.current}</Text> → <Text span ff="monospace" fw={700} inherit>{target}</Text>. Først gemmes alle BIOS-indstillinger,
            så de kan sammenlignes og sættes tilbage efter opdateringen. <b>Trinnet kan ikke springes over.</b></Text>
          <Paper p="md" radius="md" style={{ background: 'var(--fd-chip)' }}>
            <Group gap="sm" mb={8}><IconDatabaseExport size={18} /><Text fw={600} style={{ flex: 1 }}>{done ? 'Backuppen er gemt' : backupErr ? 'Backuppen kunne ikke tages' : "Læser indstillinger fra BIOS'en …"}</Text>
              <Text size="sm" c="dimmed" ff="monospace">{done ? `${snapItems?.snapshot.count ?? items.length} af ${b.settings?.count ?? '?'}` : bj?.progress != null ? `${bj.progress} %` : ''}</Text></Group>
            <Progress value={done ? 100 : bj?.progress ?? 35} animated={!done && !backupErr} color={backupErr ? 'red' : done ? 'green' : 'blue'} />
            {items.length > 0 && (
              <Stack gap={2} mt="sm">
                {items.slice(0, mobile ? 5 : 9).map((it) => (
                  <Group key={it.name} gap={8} wrap="nowrap"><IconCircleCheck size={14} color="var(--fd-ok)" /><Text size="sm" style={{ flex: 1 }} truncate>{it.display || it.name}</Text>
                    <Text size="sm" c="dimmed" ff="monospace" truncate maw={mobile ? 130 : 260}>{it.value}</Text></Group>
                ))}
                {items.length > (mobile ? 5 : 9) && <Group gap={8}><IconCircleDashed size={14} style={{ opacity: 0.5 }} /><Text size="xs" c="dimmed">+ {items.length - (mobile ? 5 : 9)} mere</Text></Group>}
              </Stack>
            )}
          </Paper>
          <Alert variant="light" color="gray" icon={<IconLock size={16} />}>Passwords gemmes aldrig. Backuppen gemmes på serveren og kan hentes som fil bagefter.</Alert>
          {backupErr ? <Alert variant="light" color="red" icon={<IconAlertTriangle size={16} />}>{backupErr} Der er ikke ændret noget på maskinen.</Alert>
            : <Alert variant="light" color="orange" icon={<IconAlertTriangle size={16} />}>Kan backuppen ikke tages, stopper opdateringen her. Der bliver ikke ændret noget på maskinen.</Alert>}
          <WizardFooter mobile={mobile}>
            <Button variant="default" onClick={onClose}>Annullér</Button>
            <Button rightSection={<IconChevronRight size={16} />} disabled={!done} onClick={() => setStep(1)}>Næste: Kontrol</Button>
          </WizardFooter>
          {!done && <Text size="xs" c="dimmed" ta={mobile ? 'center' : 'right'}>Næste bliver mulig, når backuppen er gemt.</Text>}
        </Stack>
      )}

      {step === 1 && (
        <Stack gap="sm">
          <Text size="sm" c="dimmed" fw={600}>KONTROL</Text>
          {checkErr ? <Alert color="red" variant="light">{checkErr}</Alert> : !checkDone && <Group gap="sm"><Loader size="sm" /><Text size="sm">Agenten kontrollerer maskinen … (intet ændres)</Text></Group>}
          <Stack gap={6}>{checks.map((c) => <Group key={c.id} gap={8} wrap="nowrap" align="flex-start"><CheckIcon state={c.state} /><Text size="sm">{c.text}</Text></Group>)}</Stack>
          {checkDone && run?.state !== 'checked' && <Alert color="orange" variant="light" icon={<IconAlertTriangle size={16} />}>{run?.message ?? 'Maskinen kan ikke opdateres lige nu.'} Brug evt. USB-stikket.</Alert>}
          <WizardFooter mobile={mobile}>
            <Button variant="default" onClick={onClose}>Annullér</Button>
            <Button rightSection={<IconChevronRight size={16} />} disabled={!checkDone || run?.state !== 'checked'} onClick={() => setStep(2)}>Næste: Bekræft</Button>
          </WizardFooter>
        </Stack>
      )}

      {step === 2 && (
        <Stack gap="sm">
          <Grid gap="lg">
            <Grid.Col span={{ base: 12, md: 6 }}>
              <Text size="sm" c="dimmed" fw={600} mb={6}>KONTROL</Text>
              <Stack gap={6}>{(mobile ? checks.filter((c) => c.state !== 'ok').concat(checks.filter((c) => c.state === 'ok').slice(0, 4)) : checks)
                .map((c) => <Group key={c.id} gap={8} wrap="nowrap" align="flex-start"><CheckIcon state={c.state} /><Text size="sm">{c.text}</Text></Group>)}</Stack>
            </Grid.Col>
            <Grid.Col span={{ base: 12, md: 6 }}>
              <Stack gap="sm">
                <Alert variant="light" color="blue" icon={<IconInfoCircle size={16} />}><b>Sådan foregår det.</b> {u.route === 'capsule'
                  ? "Fleetdeck lægger leverandørens kapsel på EFI-partitionen. Ved genstarten installerer BIOS'en den selv, før styresystemet starter. Det tager typisk 2-5 minutter, og maskinen kan genstarte 2 gange."
                  : "Fleetdeck kører leverandørens eget program og lægger opdateringen klar. Den installeres ved genstarten og tager typisk 2-5 minutter."} Bagefter kontrollerer Fleetdeck versionen og tager en ny backup.</Alert>
                <Alert variant="light" color="red" icon={<IconBolt size={16} />}><b>Strømmen må ikke gå.</b> Går den midt i opdateringen, kan maskinen ende med ikke at kunne starte. Sluk ikke maskinen, selv om skærmen er sort i flere minutter.</Alert>
                {biosUpdateWarnings(u.route ?? 'capsule').slice(2).map((w) => <Info key={w}>{w}</Info>)}
                <Radio.Group label="Hvornår" value={when} onChange={(v) => setWhen(v as 'now' | 'later')}>
                  <Group mt={6}><Radio value="now" label="Genstart nu" /><Radio value="later" label="Ved næste genstart" /></Group>
                </Radio.Group>
                {u.route === 'lenovo-exe' && when === 'later' && (
                  <Alert variant="light" color="orange" icon={<IconShieldLock size={16} />} p="xs">
                    Har pc'en BitLocker, står den på pause, fra opdateringen er lagt klar, til pc'en er genstartet to gange. Imens kan disken læses uden TPM'en. Genstart snarest.
                  </Alert>
                )}
                <Checkbox checked={tick} onChange={(e) => setTick(e.currentTarget.checked)} label={confirmTickText(m.name)} />
                <TextInput size="sm" label={`Skriv ${target} for at bekræfte`} value={typed} onChange={(e) => setTyped(e.currentTarget.value)} ff="monospace" autoComplete="off" />
                {!mobile && <Info>Afviser BIOS'en opdateringen, beholder den den gamle version, og Fleetdeck viser fejlen. Reserven er et USB-stik.</Info>}
              </Stack>
            </Grid.Col>
          </Grid>
          {waitPkg && <Alert variant="light" color="blue" icon={<Loader size={14} />}>Pakken hentes fra leverandøren og kontrolleres. Opdateringen starter, når den er klar.</Alert>}
          {startErr && <Alert variant="light" color="red">{startErr}</Alert>}
          <WizardFooter mobile={mobile}>
            {!mobile && <Button variant="subtle" color="gray" mr="auto" onClick={() => setStep(1)}>Tilbage</Button>}
            <Button variant="default" onClick={onClose}>Annullér</Button>
            <Button color="red" leftSection={<IconCpu2 size={16} />} loading={busy || waitPkg}
              disabled={!tick || typed.trim().toUpperCase() !== target.toUpperCase()} onClick={() => start()}>{when === 'now' ? 'Opdatér BIOS og genstart' : 'Læg opdateringen klar'}</Button>
          </WizardFooter>
        </Stack>
      )}

      {step >= 3 && run && (
        <Stack gap="sm">
          {step === 4 && state && (
            <Alert variant="light" color={toneColor(state.tone)} icon={state.tone === 'green' ? <IconCircleCheck size={18} /> : <IconAlertTriangle size={18} />}>
              <b>{state.text}</b>{run.effective === 'updated' ? ' Se, hvad der er ændret, og vælg hvad der skal sættes tilbage.' : ''}
            </Alert>
          )}
          <Group gap="xs"><Text size="sm" ff="monospace">{run.fromVersion} → <b>{run.targetVersion}</b></Text><Box style={{ flex: 1 }} />
            <Text size="xs" c="dimmed">Startet {fmt(run.createdAt)} af {run.createdBy}</Text></Group>
          <Timeline items={updateTimeline(run, now, u.route)} />
          {step === 3 && run.effective === 'staged' && !wantReboot.current && (
            <Alert variant="light" color="blue">Opdateringen ligger klar og installeres ved næste genstart. {run.canReboot && <Anchor size="sm" onClick={() => { wantReboot.current = true; setNow(Date.now()); }}>Genstart nu</Anchor>}</Alert>
          )}
          {step === 3 && <Alert variant="light" color="red" icon={<IconBolt size={16} />}>Sluk ikke {m.name}, og tag ikke strømmen, selv om skærmen er sort.</Alert>}
          {step === 3 && <Info>Du kan godt lukke vinduet. Opdateringen fortsætter, og status står på maskinsiden. Er maskinen ikke online efter 15 min, viser Fleetdeck, hvad du kan gøre.</Info>}
          <WizardFooter mobile={mobile}>
            <Button variant="default" onClick={onClose}>Luk</Button>
            {step === 4 && run.canVerify && <Button variant="default" leftSection={<IconRefresh size={16} />} onClick={() => post(`/api/bios/machines/${m.id}/update/verify`).then(() => refetch(), notifyErr)}>Kontrollér igen</Button>}
            {step === 4 && run.canCompare && <Button leftSection={<IconArrowsDiff size={16} />} onClick={onCompare}>Sammenlign og gendan…</Button>}
          </WizardFooter>
        </Stack>
      )}
      {step >= 3 && !run && <Loading />}
    </Modal>
  );
}

// ====================================================================== sammenlign og gendan (skitse bios-6)

function CompareModal({ machineId, name, snaps, from: f0, to: t0, isAdmin, onClose }: {
  machineId: number; name: string; snaps: SnapRow[]; from?: number; to?: number; isAdmin: boolean; onClose: () => void;
}) {
  const mobile = !!useMediaQuery('(max-width: 48em)');
  const [from, setFrom] = useState<number | undefined>(f0);
  const [to, setTo] = useState<number | undefined>(t0);
  const q = [from ? `from=${from}` : '', to ? `to=${to}` : ''].filter(Boolean).join('&');
  const { data: d, error } = useApi<BiosDiff>(['bios-diff', machineId, from, to], `/api/bios/machines/${machineId}/settings/diff${q ? `?${q}` : ''}`);
  const [only, setOnly] = useState<'diff' | 'all'>('diff');
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [pw, setPw] = useState('');
  const [busy, setBusy] = useState(false);
  const [errs, setErrs] = useState<string[]>([]);
  useEffect(() => {
    if (!d) return;
    setFrom(d.from.id); setTo(d.to.id);
    setSel(new Set(d.rows.filter((r) => r.preselected).map((r) => r.name)));
  }, [d?.from.id, d?.to.id]);   // eslint-disable-line react-hooks/exhaustive-deps
  // kopierne fra listen plus de to, sammenligningen faktisk bruger (listen viser kun de nyeste 20)
  const all: { id: number; takenAt: string; reason: BiosSnapshotReason; biosVersion?: string | null }[] = [...snaps];
  for (const x of d ? [d.from, d.to] : []) if (!all.some((y) => y.id === x.id)) all.push(x);
  const opts = all.sort((a, b) => b.takenAt.localeCompare(a.takenAt))
    .map((s) => ({ value: String(s.id), label: `${fmt(s.takenAt)} · ${s.biosVersion ?? '–'} · ${(SNAPSHOT_REASON[s.reason] ?? s.reason).toLowerCase()}` }));
  const rows = useMemo(() => (d?.rows ?? []).filter((r) => only === 'all' || r.state !== 'same'), [d, only]);
  const toggle = (n: string) => setSel((s) => { const x = new Set(s); if (x.has(n)) x.delete(n); else x.add(n); return x; });
  const restore = () => {
    if (!d) return;
    const chosen = d.rows.filter((r) => sel.has(r.name));
    modals.openConfirmModal({
      title: `Gendan ${chosen.length} BIOS-indstilling${chosen.length === 1 ? '' : 'er'} på ${name}?`, centered: true,
      children: (
        <Stack gap="xs">
          {chosen.map((r) => <Text key={r.name} size="sm"><b>{r.display || r.name}</b>: <Code>{r.now ?? '–'}</Code> → <Code>{r.before ?? '–'}</Code></Text>)}
          <Text size="sm" c="dimmed">Der tages en backup lige før. Ændringerne gemmes på én gang og virker efter næste genstart.</Text>
        </Stack>
      ),
      labels: { confirm: 'Gendan', cancel: 'Annullér' },
      onConfirm: async () => {
        setBusy(true); setErrs([]);
        try {
          await post(`/api/bios/machines/${machineId}/settings/restore`, {
            from: d.from.id, to: d.to.id, names: chosen.map((r) => r.name),
            acknowledged: chosen.filter((r) => r.rule === 'ask').map((r) => r.name), password: d.adminPassword ? pw : null,
          });
          notifyOk('Gendannelsen er sat i gang. Genstart maskinen, så kontrollerer Fleetdeck værdierne bagefter.');
          onClose();
        } catch (e) {
          setErrs(e instanceof ApiError ? [e.message] : [errText(e)]);
        } finally { setBusy(false); }
      },
    });
  };
  const sum = d ? diffSummary(d.rows, sel) : [];
  const Kind = ({ r }: { r: BiosDiff['rows'][number] }) => {
    const k = diffKind(r);
    const icon = k.icon === 'check' ? <IconCheck size={11} /> : k.icon === 'alert' ? <IconAlertTriangle size={11} /> : k.icon === 'lock' ? <IconLock size={11} /> : k.icon === 'sparkles' ? <IconSparkles size={11} /> : <IconMinus size={11} />;
    return <ToneBadge t={{ text: k.label, tone: k.tone }} icon={icon} />;
  };
  return (
    <Modal opened onClose={onClose} zIndex={300} size={1000} fullScreen={mobile} title={<Group gap={8} wrap="nowrap"><IconArrowsDiff size={20} style={{ flexShrink: 0 }} /><Text fw={700} lineClamp={1}>Sammenlign indstillinger · {name}</Text></Group>}>
      <Stack gap="sm">
        <Group grow={!mobile} gap="sm" style={mobile ? { flexDirection: 'column', alignItems: 'stretch' } : undefined}>
          <Select label="Før" data={opts} value={from ? String(from) : null} onChange={(v) => v && setFrom(Number(v))} allowDeselect={false} />
          <Select label="Nu" data={opts} value={to ? String(to) : null} onChange={(v) => v && setTo(Number(v))} allowDeselect={false} />
        </Group>
        {error ? <Alert color="orange" variant="light">{errText(error)}</Alert> : !d ? <Loading /> : (
          <>
            <Group justify="space-between">
              <SegmentedControl size="xs" value={only} onChange={(v) => setOnly(v as 'diff' | 'all')}
                data={[{ value: 'diff', label: `Kun forskelle (${d.rows.filter((r) => r.state !== 'same').length})` }, { value: 'all', label: `Alle ${d.counts.all}` }]} />
              {!mobile && <Text size="xs" c="dimmed">Regler: almindelige forvælges, resten kræver dit valg</Text>}
            </Group>
            <ScrollArea.Autosize mah={mobile ? 'calc(100dvh - 470px)' : 420}>
              {rows.length === 0 ? <Empty>Ingen forskelle.</Empty> : mobile ? (
                <Stack gap={8}>{rows.map((r) => (
                  <Paper key={r.name} withBorder p="sm" radius="md" style={sel.has(r.name) ? { borderColor: 'var(--mantine-primary-color-filled)' } : undefined}>
                    <Group gap={8} wrap="nowrap"><Checkbox size="xs" disabled={!isAdmin || !r.selectable} checked={sel.has(r.name)} onChange={() => toggle(r.name)} aria-label={r.name} />
                      <Text size="sm" fw={600} style={{ flex: 1 }} truncate>{r.display || r.name}</Text><Kind r={r} /></Group>
                    <Text size="xs" ff="monospace" ml={26} mt={4}><Text span c="dimmed" inherit>Før </Text>{r.before ?? '(fandtes ikke)'}<br /><Text span c="dimmed" inherit>Nu  </Text>{r.now ?? '(findes ikke)'}</Text>
                    {(r.warn || r.why) && <Text size="xs" c="orange" ml={26} mt={4}>{r.warn ?? r.why}</Text>}
                  </Paper>))}</Stack>
              ) : (
                <Table verticalSpacing={7} highlightOnHover>
                  <Table.Thead><Table.Tr><Table.Th w={32} /><Table.Th>Indstilling</Table.Th><Table.Th>Før ({d.from.biosVersion ?? '–'})</Table.Th><Table.Th>Nu ({d.to.biosVersion ?? '–'})</Table.Th><Table.Th w={170}>Type</Table.Th></Table.Tr></Table.Thead>
                  <Table.Tbody>{rows.map((r) => [
                    <Table.Tr key={r.name} bg={sel.has(r.name) ? 'var(--mantine-primary-color-light)' : undefined}>
                      <Table.Td><Checkbox size="xs" disabled={!isAdmin || !r.selectable} checked={sel.has(r.name)} onChange={() => toggle(r.name)} aria-label={r.name} /></Table.Td>
                      <Table.Td><Text size="sm" fw={550}>{r.display || r.name}</Text></Table.Td>
                      <Table.Td><Text size="sm" ff="monospace">{r.before ?? '(fandtes ikke)'}</Text></Table.Td>
                      <Table.Td><Text size="sm" ff="monospace" c={r.state === 'changed' && r.selectable ? 'orange' : undefined}>{r.now ?? '(findes ikke)'}</Text></Table.Td>
                      <Table.Td><Kind r={r} /></Table.Td>
                    </Table.Tr>,
                    (r.warn || r.why) && r.state !== 'same' ? <Table.Tr key={`${r.name}-w`}><Table.Td /><Table.Td colSpan={4} pt={0}><Group gap={6} wrap="nowrap"><IconAlertTriangle size={14} color="var(--fd-warn)" /><Text size="xs" c="orange">{r.warn ?? r.why}</Text></Group></Table.Td></Table.Tr> : null,
                  ])}</Table.Tbody>
                </Table>
              )}
            </ScrollArea.Autosize>
            <Group gap="md">{sum.map((x, i) => <Text key={x.text} size="sm" fw={i === 0 ? 700 : 400} c={x.tone === 'yellow' ? 'orange' : x.tone === 'gray' ? 'dimmed' : undefined}>{x.text}</Text>)}</Group>
            {d.adminPassword && isAdmin && <PasswordInput label="BIOS-password" description="Bruges kun til denne gendannelse og gemmes aldrig." value={pw} onChange={(e) => setPw(e.currentTarget.value)} autoComplete="off" />}
            {!d.canRestore && d.cannotRestore && <Alert variant="light" color="orange">{d.cannotRestore}</Alert>}
            {errs.map((e) => <Alert key={e} variant="light" color="red">{e}</Alert>)}
            {!mobile && <Info>Der tages en backup lige før. Ændringerne gemmes på én gang og virker efter næste genstart. Bagefter kontrollerer Fleetdeck, at de blev sat.</Info>}
            <WizardFooter mobile={mobile}>
              <Button variant="default" onClick={onClose}>Annullér</Button>
              {isAdmin && <Button leftSection={<IconRestore size={16} />} disabled={!d.canRestore || sel.size === 0 || (d.adminPassword && !pw)} loading={busy} onClick={restore}>{restoreButton(sel.size)}</Button>}
            </WizardFooter>
          </>
        )}
      </Stack>
    </Modal>
  );
}

// ====================================================================== "Lav USB-stik" (design afsnit 7)

type UsbDisk = { id: string; path: string; size: number; model: string; vendor: string; serial: string; partitions: number; labels: string[]; selectable: boolean; why?: string | null; large: boolean };
type JobBriefLive = { id: number; status: string; progress?: number | null; step?: string | null; created: string; finished?: string | null };
type UsbSession = {
  sessionId: string; targetId: number; target: string; vendor: string; layout: string; version: string; nonceValid: boolean;
  thisMachine: { id: number; name: string; platform: string } | null; package: BiosPackageRow | null;
  listJob: JobBriefLive | null; baseline: boolean; disks: UsbDisk[]; disksAt: string | null; listMessage: string | null;
  chosen: UsbDisk | null; writeJob: JobBriefLive | null; result: { outcome: string; message: string; files: number; verified: boolean; ejected: boolean } | null;
  maxBytes: number; steps: string[];
};
const activeJob = (j: JobBriefLive | null | undefined) => !!j && ['pending', 'running', 'waiting_user'].includes(j.status);

/** Pc'er, hvor agenten kan lave stikket (en Mac kan ikke endnu). */

/** Browserens tidsgrænse for confirm: længere end agentens egne 30 s, så agentens danske fejltekst når frem først. */
const CONFIRM_MS = 40_000;

// confirm læser diskene igen (på Windows via PowerShell, som kan tage nogle sekunder); agenten giver selv op efter 30 s
/** Kald agenten på denne pc (kun på http://localhost; LocalWeb svarer selv og sender intet videre). */
async function local<T>(path: string, q: Record<string, string>, ms = 10_000): Promise<T> {
  const ctl = new AbortController();
  const t = window.setTimeout(() => ctl.abort(), ms);
  try {
    const r = await fetch(`/_fd/${path}?${new URLSearchParams(q)}`, { method: 'POST', headers: { ...CSRF_HEADER }, signal: ctl.signal, credentials: 'omit' });
    // en ældre agent kender ikke /_fd/ og sender kaldet videre til serveren, som svarer med forsiden (HTML og 200)
    const json = (r.headers.get('content-type') ?? '').includes('application/json');
    const data = json ? await r.json().catch(() => ({})) : {};
    if (!r.ok) throw new Error((data as { error?: string }).error ?? `Fejl ${r.status}`);
    if (!json) throw new Error(OLD_AGENT);
    return data as T;
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw new Error('Denne pc kunne ikke genkendes. Kører Fleetdeck-agenten?');
    throw e;
  } finally { window.clearTimeout(t); }
}

const OLD_AGENT = 'Fleetdeck-agenten på denne pc er for gammel til at lave USB-stik. Opdatér agenten, og prøv igen.';

function UsbWizard({ m, b, status, onClose }: { m: MachineDetail; b: MachineBios; status: BiosStatus; onClose: () => void }) {
  const mobile = !!useMediaQuery('(max-width: 48em)');
  const isLocal = onLocalhost(window.location.hostname);
  const pkg = usePackageDownload(m.id);
  const [sid, setSid] = useState<string | null>(null);
  const [startErr, setStartErr] = useState<string | null>(null);
  const nonce = useRef<string | null>(null);
  const { data: s, refetch } = useApi<UsbSession>(['bios-usb', sid], sid ? `/api/bios/usb/${sid}` : null, { refetchInterval: 2000 });
  const { data: bios } = useApi<MachineBios>(['bios', m.id], `/api/bios/machines/${m.id}`, { refetchInterval: 4000 });
  const [step, setStep] = useState(0);
  const [noBackup, setNoBackup] = useState(false);
  const [backupJob, setBackupJob] = useState<number | null>(null);
  const { data: bj } = useApi<JobDetail>(['job', backupJob], backupJob ? `/api/jobs/${backupJob}` : null, { refetchInterval: 2000 });
  useEffect(() => {
    if (bj && ['failed', 'cancelled', 'timeout'].includes(bj.status)) { notifyErr(new Error(bj.output || 'Backuppen kunne ikke tages.')); setBackupJob(null); }
  }, [bj]);
  const [claimErr, setClaimErr] = useState<string | null>(null);
  const [pick, setPick] = useState<string | null>(null);
  const [erase, setErase] = useState(false);
  const [large, setLarge] = useState(false);
  const [busy, setBusy] = useState(false);
  const [writeErr, setWriteErr] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (!isLocal || started.current) return;
    started.current = true;
    post<{ sessionId: string; nonce: string }>(`/api/bios/machines/${m.id}/usb`).then((r) => { setSid(r.sessionId); nonce.current = r.nonce; }, (e) => setStartErr(errText(e)));
  }, [isLocal, m.id]);
  // ryd op, når vinduet lukkes (intet er skrevet, før det sidste trin)
  useEffect(() => () => { if (sid) del(`/api/bios/usb/${sid}`).catch(() => undefined); }, [sid]);

  const snaps = (bios ?? b).snapshots;
  const fresh = snaps.find((x) => backupFresh(x.takenAt, Date.now()));
  // uden en frisk backup kan man altid springe den over med et flueben (stikket er reserven, også når maskinen er offline)
  const backupOk = !!fresh || noBackup;
  const pkgReady = s?.package?.state === 'ready';

  const claim = async () => {
    if (!sid) return;
    setClaimErr(null); setBusy(true);
    try {
      if (!s?.nonceValid || !nonce.current) nonce.current = (await post<{ nonce: string }>(`/api/bios/usb/${sid}/renew`)).nonce;
      const r = await local<{ machine?: string }>('claim', { nonce: nonce.current! });
      if (!r || typeof r.machine !== 'string') throw new Error(OLD_AGENT);
      const after = await refetch();
      if (!after.data?.thisMachine) throw new Error('Fleetdeck har ikke hørt fra agenten på denne pc endnu. Prøv igen om lidt.');
      setStep(2);
    } catch (e) { setClaimErr(errText(e)); } finally { setBusy(false); }
  };
  const scan = () => sid && post(`/api/bios/usb/${sid}/disks`).then(() => refetch(), notifyErr);
  // øjebliksbilledet tages straks; derefter ventes der én gang automatisk på stikket (siden med "Søg igen")
  const scans = useRef(0);
  useEffect(() => {
    if (step !== 2 || !s || activeJob(s.listJob)) return;
    if (!s.baseline && scans.current === 0) { scans.current = 1; scan(); }
    else if (s.baseline && scans.current === 1) { scans.current = 2; scan(); }
  });
  const waiting = activeJob(s?.listJob);
  const disks = s?.disks ?? [];
  const chosen = disks.find((d) => d.id === pick) ?? null;
  const write = async () => {
    if (!sid || !chosen) return;
    setBusy(true); setWriteErr(null);
    try {
      const r = await local<{ hash: string }>('confirm', { session: sid, disk: chosen.id, serial: chosen.serial, size: String(chosen.size), model: chosen.model, max: String(s?.maxBytes ?? 0), large: large ? '1' : '0' }, CONFIRM_MS);
      await post(`/api/bios/usb/${sid}/write`, { diskId: chosen.id, serial: chosen.serial, size: chosen.size, confirmHash: r.hash, allowLarge: large });
      setStep(4);
      refetch();
    } catch (e) { setWriteErr(errText(e)); } finally { setBusy(false); }
  };
  useEffect(() => { if (step === 4 && s?.result) setStep(5); }, [step, s?.result]);

  const footer = (back: (() => void) | null, next: ReactNode) => (
    <WizardFooter mobile={mobile}>
      {back && !mobile && <Button variant="subtle" color="gray" mr="auto" onClick={back}>Tilbage</Button>}
      <Button variant="default" onClick={onClose}>Annullér</Button>
      {next}
    </WizardFooter>
  );
  const title = <Group gap={8}><IconUsb size={20} /><Text fw={700}>USB-stik med BIOS {status.latest} til {m.name}</Text></Group>;

  if (!isLocal) {
    return (
      <Modal opened onClose={onClose} zIndex={300} size="lg" fullScreen={mobile} title={title}>
        <Stack gap="sm">
          <Alert variant="light" color="blue" icon={<IconInfoCircle size={16} />}>{USB_ONLY_LOCAL}</Alert>
          <Text size="sm">Stikket laves på den pc, du sidder ved, gennem Fleetdeck-agenten på pc'en. Det virker fra en Linux-pc, en Windows-pc og en Mac med agenten.</Text>
          <Group gap="xs">
            <Button component="a" {...localLink(`/maskiner/${m.id}/opdateringer?bios=usb`, b.localPort)} variant="default" leftSection={<IconExternalLink size={16} />}>Åbn via denne pc</Button>
            <Button variant="default" leftSection={<IconDownload size={16} />} disabled={!!pkg.busy} onClick={pkg.start}>{pkg.label}</Button>
          </Group>
          <Info>Virker linket ikke, så åbn Fleetdeck fra genvejen på pc'en (agenten kan bruge en anden port).</Info>
          <Info>Pakken er en zip med den rigtige mappestruktur og LÆS-MIG.txt: formatér et USB-stik som FAT32 og pak indholdet ud i roden.</Info>
        </Stack>
      </Modal>
    );
  }

  return (
    <Modal opened onClose={onClose} zIndex={300} size={860} fullScreen={mobile} title={title}>
      {mobile ? (
        <Box mb="md"><Text size="sm" fw={600} mb={6}>Trin {step + 1} af {USB_STEPS.length} · {USB_STEPS[step]}</Text><Progress size="sm" value={((step + 1) / USB_STEPS.length) * 100} /></Box>
      ) : (
        <Stepper active={step} size="sm" allowNextStepsSelect={false} mb="md" iconSize={30}>
          {USB_STEPS.map((l) => <Stepper.Step key={l} label={l} />)}
        </Stepper>
      )}
      {startErr && <Alert color="red" variant="light" mb="sm">{startErr}</Alert>}
      {!s && !startErr && <Loading />}

      {s && step === 0 && (
        <Stack gap="sm">
          <Paper withBorder p="sm" radius="md">
            <Group gap="sm" wrap="nowrap"><IconDownload size={18} /><Text size="sm" fw={600} style={{ flex: 1 }}>Pakke {s.package?.fileName ?? ''}</Text>
              {pkgReady ? <Badge color="green" variant="light">Hentet</Badge> : s.package?.state === 'failed' || s.package?.stale ? <Badge color="red" variant="light">Fejl</Badge> : <Loader size="xs" />}</Group>
            {s.package && !pkgReady && s.package.state === 'downloading' && !s.package.stale && <Progress mt={8} value={s.package.total ? (s.package.bytes / s.package.total) * 100 : 10} animated />}
            {(s.package?.state === 'failed' || s.package?.stale) && <Text size="sm" c="red" mt={6}>{s.package.error ?? 'Pakken kunne ikke hentes fra leverandøren.'}</Text>}
            {pkgReady && <Text size="xs" c="dimmed" mt={6} ff="monospace" style={{ overflowWrap: 'anywhere' }}>SHA-256 {s.package!.sha256} {s.package!.shaSource === 'tofu' ? '(låst ved første hentning – leverandøren oplyser ingen kontrolsum for USB-pakken)' : '(fra leverandøren)'}</Text>}
          </Paper>
          <Paper withBorder p="sm" radius="md">
            <Group gap="sm" wrap="nowrap"><IconDatabaseExport size={18} /><Text size="sm" fw={600} style={{ flex: 1 }}>Backup af BIOS-indstillingerne på {m.name}</Text>
              {fresh ? <Badge color="green" variant="light">{fmt(fresh.takenAt)}</Badge> : backupJob && bj?.status !== 'success' ? <Loader size="xs" /> : null}</Group>
            {!fresh && b.settings && (
              <Group mt={8} gap="xs"><Text size="sm" c="dimmed" style={{ flex: 1 }}>Der er ingen backup fra de sidste 24 timer. Tag den først, hvis maskinen er tændt.</Text>
                <Button size="xs" loading={!!backupJob} onClick={() => takeBackup(m.id, 'pre-update').then((r) => setBackupJob(r.jobId), notifyErr)}>Tag backup nu</Button></Group>
            )}
            {!fresh && b.settings && !m.online && <Text size="xs" c="orange" mt={6}>{m.name} er offline. Backuppen tages, når maskinen melder sig igen.</Text>}
            {!fresh && <Checkbox mt={8} checked={noBackup} onChange={(e) => setNoBackup(e.currentTarget.checked)}
              label={b.settings ? 'Jeg har ingen backup og noterer selv vigtige indstillinger.' : 'Maskinen kan ikke læse BIOS-indstillingerne. Jeg har ingen backup og noterer selv vigtige indstillinger.'} />}
          </Paper>
          {footer(null, <Button rightSection={<IconChevronRight size={16} />} disabled={!pkgReady || !backupOk} onClick={() => setStep(1)}>Næste</Button>)}
        </Stack>
      )}

      {s && step === 1 && (
        <Stack gap="sm">
          <Text size="sm">Fleetdeck skal vide, hvilken pc du sidder ved. Browseren sender en engangskode til Fleetdeck-agenten på pc'en, som melder sig til serveren. Agenten giver ikke browseren nogen nye rettigheder.</Text>
          {s.thisMachine && <Alert color="green" variant="light" icon={<IconCircleCheck size={16} />}>Denne pc: <b>{s.thisMachine.name}</b></Alert>}
          {s.thisMachine && !usbWriterHere(s.thisMachine.platform) && <Alert color="orange" variant="light">Stikket kan ikke laves fra denne maskine. Brug "Hent pakke", eller lav stikket fra en Linux-pc, en Windows-pc eller en Mac med Fleetdeck-agenten.</Alert>}
          {claimErr && <Alert color="red" variant="light">{claimErr} <Anchor size="sm" onClick={pkg.start}>Hent pakken i stedet</Anchor></Alert>}
          {footer(() => setStep(0), s.thisMachine
            ? <Button rightSection={<IconChevronRight size={16} />} disabled={!usbWriterHere(s.thisMachine.platform)} onClick={() => setStep(2)}>Næste</Button>
            : <Button loading={busy} onClick={claim}>Find denne pc</Button>)}
        </Stack>
      )}

      {s && step === 2 && (
        <Stack gap="sm">
          {!s.baseline && !waiting && s.listMessage ? (
            <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />}>
              {s.listMessage}
              <Group mt={8}><Button size="xs" variant="default" onClick={() => { scans.current = 0; scan(); }}>Prøv igen</Button></Group>
            </Alert>
          ) : !s.baseline ? <Group gap="sm"><Loader size="sm" /><Text size="sm">Tager et øjebliksbillede af diskene på {s.thisMachine?.name} …</Text></Group> : (
            <>
              <Alert color="blue" variant="light" icon={<IconUsb size={16} />}><b>Sæt USB-stikket i nu.</b> Kun stik, der bliver sat i nu, kan vælges. Sidder stikket allerede i, så tag det ud og sæt det i igen.</Alert>
              {waiting && <Group gap="sm"><Loader size="sm" /><Text size="sm">Venter på stikket … (op til 90 sekunder)</Text></Group>}
              {!waiting && s.listMessage && <Text size="sm" c="dimmed">{s.listMessage}</Text>}
              <Radio.Group value={pick} onChange={setPick}>
                <Stack gap={6}>{disks.map((d) => (
                  <Paper key={d.id || d.path} withBorder p="sm" radius="md" style={{ opacity: usbChoosable(d, s.maxBytes) ? 1 : 0.55 }}>
                    <Radio value={d.id} disabled={!usbChoosable(d, s.maxBytes) || !d.id} label={<Text size="sm" fw={600}>{usbDiskName(d)}</Text>}
                      description={<>{d.serial ? `Serie ${d.serial}` : 'Ingen serie'}{d.labels.length ? ` · nu: ${d.labels.join(', ')}` : ''}{d.why ? ` · ${d.why}` : ''}</>} />
                  </Paper>
                ))}</Stack>
              </Radio.Group>
              {!waiting && <Group><Button size="xs" variant="default" leftSection={<IconSearch size={14} />} onClick={scan}>Søg igen</Button></Group>}
            </>
          )}
          {footer(() => setStep(1), <Button rightSection={<IconChevronRight size={16} />} disabled={!chosen} onClick={() => { setErase(false); setLarge(false); setStep(3); }}>Næste</Button>)}
        </Stack>
      )}

      {s && step === 3 && chosen && (
        <Stack gap="sm">
          <Paper withBorder p="md" radius="md">
            <Kv rows={[['Stik', usbDiskName(chosen)], ['Serienummer', <Code>{chosen.serial || '–'}</Code>], ['På stikket nu', chosen.labels.length ? chosen.labels.join(', ') : `${chosen.partitions} partition(er)`],
              ['Pc', s.thisMachine?.name ?? '–'], ['Pakke', `${s.package?.fileName} (${s.version})`]]} />
          </Paper>
          <Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />}><b>{ERASE_WARNING}</b> Stikket formateres som FAT32, og pakken lægges på og kontrolleres.</Alert>
          <Checkbox checked={erase} onChange={(e) => setErase(e.currentTarget.checked)} label={`Slet alt på ${usbDiskName(chosen)} (serie ${chosen.serial || '–'})`} />
          {chosen.large && <Checkbox checked={large} onChange={(e) => setLarge(e.currentTarget.checked)} color="red" label={`${chosen.why === USB_EXTERNAL_DRIVE ? USB_EXTERNAL_DRIVE : `Stikket er større end ${bytes(s.maxBytes)}.`} Jeg er sikker på, at det er et USB-stik og ikke en backupdisk.`} />}
          {writeErr && <Alert color="red" variant="light">{writeErr}</Alert>}
          {footer(() => setStep(2), <Button color="red" leftSection={<IconUsb size={16} />} loading={busy} disabled={!erase || (chosen.large && !large)} onClick={write}>Slet og lav stikket</Button>)}
        </Stack>
      )}

      {s && step === 4 && (
        <Stack gap="sm">
          <Text size="sm" fw={600}>{s.writeJob && !activeJob(s.writeJob) ? 'Jobbet er slut. Henter resultatet …' : s.writeJob?.step || 'Venter på agenten …'}</Text>
          <Progress value={s.writeJob?.progress ?? 3} animated={activeJob(s.writeJob)} />
          <Info>Tag ikke stikket ud, før Fleetdeck siger, at det er klar.</Info>
        </Stack>
      )}

      {s && step === 5 && s.result && (
        <Stack gap="sm">
          <Alert color={s.result.outcome === 'ok' ? 'green' : 'red'} variant="light" icon={s.result.outcome === 'ok' ? <IconCircleCheck size={18} /> : <IconAlertTriangle size={18} />}>{s.result.message}</Alert>
          {s.result.outcome === 'ok' ? (
            <>
              <Text size="sm" fw={650}>Sådan opdaterer du {s.target}</Text>
              <Stack gap={4}>{s.steps.map((t, i) => <Group key={i} gap={8} wrap="nowrap" align="flex-start"><Badge size="sm" variant="light" circle>{i + 1}</Badge><Text size="sm">{t}</Text></Group>)}</Stack>
              <WizardFooter mobile={mobile}><Button onClick={onClose}>Luk</Button></WizardFooter>
            </>
          ) : footer(null, <Button onClick={() => { setPick(null); setStep(2); scans.current = 2; scan(); }}>Prøv igen</Button>)}
        </Stack>
      )}
    </Modal>
  );
}

// ====================================================================== B: fanen BIOS under Opdateringer (skitse bios-7)

export function useBiosFleet() {
  return useApi<BiosFleetRow[]>(['bios-fleet'], '/api/bios/fleet', { refetchInterval: 60_000 });
}

export function BiosFleetTab() {
  const { isAdmin } = useMe();
  const mobile = !!useMediaQuery('(max-width: 62em)');
  const { data } = useBiosFleet();
  const { data: cat } = useApi<BiosVendorStatus[]>(['bios-catalog'], '/api/bios/catalog');
  const [picked, setF] = useState<FleetFilter | null>(null);
  const f: FleetFilter = picked ?? (mobile ? 'behind' : 'all');   // telefonen viser dem, der er bagud, først
  const [q, setQ] = useState('');
  const [sel, setSel] = useState<number | null>(null);
  if (!data) return <Loading />;
  const c = fleetCounts(data);
  const rows = fleetFilter(data, f, q);
  const supported = (cat ?? []).filter((v) => v.supported);
  const checked = supported.map((v) => v.fetchedAt).filter(Boolean).sort().at(-1);
  const current = sel ? data.find((r) => r.machineId === sel) ?? null : (!mobile ? data.find((r) => r.status?.state === 'newer') ?? null : null);
  return (
    <Stack gap="sm">
      <Group gap="sm" wrap="wrap">
        <SegmentedControl size="xs" value={f} onChange={(v) => setF(v as FleetFilter)}
          data={[{ value: 'behind', label: `Bagud (${c.behind})` }, { value: 'all', label: `Alle (${c.all})` }, { value: 'unchecked', label: `Ikke tjekket (${c.unchecked})` }]} />
        {!mobile && <TextInput size="xs" w={260} placeholder="Søg maskine eller model" leftSection={<IconSearch size={14} />} value={q} onChange={(e) => setQ(e.currentTarget.value)} />}
        <Box style={{ flex: 1 }} />
        {!mobile && <Text size="xs" c="dimmed">Katalog: {supported.map((v) => v.name).join(', ') || '–'}{checked ? ` · tjekket ${ago(checked)}` : ''}</Text>}
      </Group>
      {mobile ? (
        <Stack gap={8}>
          {rows.map((r) => <FleetCard key={r.machineId} r={r} isAdmin={isAdmin} />)}
          {f === 'behind' && fleetQuiet(data) > 0 && (
            <Paper withBorder p="sm" radius="md"><Group gap="sm"><IconCircleCheck size={16} style={{ opacity: 0.6 }} />
              <Text size="sm" style={{ flex: 1 }}>{fleetQuiet(data)} andre maskiner er opdaterede eller understøttes ikke</Text>
              <Anchor size="sm" fw={600} onClick={() => setF('all')}>Vis</Anchor></Group></Paper>
          )}
          {rows.length === 0 && <Empty>Ingen maskiner her.</Empty>}
          <Info>Opdatér alt tager aldrig BIOS med.</Info>
        </Stack>
      ) : (
        <Group align="flex-start" gap="md" wrap="nowrap">
          <Stack gap="xs" style={{ flex: 1, minWidth: 0 }}>
            <Paper withBorder radius="md" p={0} style={{ overflow: 'hidden' }}>
              <ScrollArea type="auto"><Table highlightOnHover verticalSpacing={8} miw={820}>
                <Table.Thead><Table.Tr><Table.Th>Maskine</Table.Th><Table.Th>Model</Table.Th><Table.Th>Har</Table.Th><Table.Th>Nyeste</Table.Th><Table.Th>Status</Table.Th><Table.Th>Opdateringsvej</Table.Th><Table.Th>Backup</Table.Th><Table.Th w={20} /></Table.Tr></Table.Thead>
                <Table.Tbody>{rows.map((r) => {
                  const s = r.status;
                  return (
                    <Table.Tr key={r.machineId} onClick={() => setSel(r.machineId)} style={{ cursor: 'pointer' }} bg={current?.machineId === r.machineId ? 'var(--mantine-primary-color-light)' : undefined}>
                      <Table.Td><Group gap={7} wrap="nowrap"><KindIcon kind={kindOf({ platform: r.platform })} size={22} /><Text size="sm" fw={600} style={{ whiteSpace: 'nowrap' }}>{r.machine}</Text></Group></Table.Td>
                      <Table.Td><Text size="sm" truncate maw={220}>{[vendorName(s?.vendor === 'none' ? null : s?.vendor), r.model].filter((x) => x && x !== '–').join(' ') || '–'}</Text></Table.Td>
                      <Table.Td><Text size="sm" ff="monospace">{s?.current ?? '–'}</Text></Table.Td>
                      <Table.Td><Text size="sm" ff="monospace" fw={s?.state === 'newer' ? 700 : 400}>{s?.latest ?? (s?.state === 'unsupported' ? '?' : '–')}</Text></Table.Td>
                      <Table.Td><FleetBadges s={s} /></Table.Td>
                      <Table.Td><Text size="sm" c="dimmed">{fleetRoute(r)}</Text></Table.Td>
                      <Table.Td><Text size="sm" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{backupLabel(r.backupAt)}</Text></Table.Td>
                      <Table.Td><IconChevronRight size={15} style={{ opacity: 0.5 }} /></Table.Td>
                    </Table.Tr>
                  );
                })}</Table.Tbody>
              </Table></ScrollArea>
              {rows.length === 0 && <Empty>Ingen maskiner her.</Empty>}
            </Paper>
            <Info>{UPDATE_ALL_NEVER_BIOS}</Info>
          </Stack>
          {current && <FleetDrawer key={current.machineId} r={current} isAdmin={isAdmin} onClose={() => setSel(-1)} />}
        </Group>
      )}
    </Stack>
  );
}

/** Vejen inde fra styresystemet, som serveren tilbyder (Windows; Linux kun med en rigtig UEFI-kapsel i pakken), ellers null. */
const fleetRouteKey = (r: BiosFleetRow) => r.status?.state !== 'newer' || r.status.vendor !== 'lenovo' ? null : r.route ?? null;

/** Opdateringsvejen i tabellen: Lenovo kan opdateres inde fra styresystemet, de andre kun med pakken (endnu). */
const fleetRoute = (r: BiosFleetRow) => r.status?.state !== 'newer' ? '–'
  : r.status.vendor === 'lenovo' ? routeLabel(fleetRouteKey(r), r.status.vendor, r.platform !== 'mac') : 'Hent pakke';

/** Sidepanelet for den valgte maskine: changelog, klar-status og knapperne (handlingerne sker på maskinens side). */
function FleetDrawer({ r, isAdmin, onClose }: { r: BiosFleetRow; isAdmin: boolean; onClose: () => void }) {
  const s = r.status;
  const { data: u } = useApi<BiosUpdateInfo>(['bios-update', r.machineId], s?.state === 'newer' ? `/api/bios/machines/${r.machineId}/update` : null);
  const { data: b } = useApi<MachineBios>(['bios', r.machineId], `/api/bios/machines/${r.machineId}`);
  const pkg = usePackageDownload(r.machineId);
  const imp = importantChange(s);
  const ready = u ? u.canStart && !u.serverChecks.some((c) => c.state === 'fail') : null;
  const go = `/maskiner/${r.machineId}/opdateringer`;
  return (
    <Paper withBorder radius="md" p={0} w={360} style={{ flexShrink: 0 }}>
      <Group gap="sm" p="md" pb="xs"><KindIcon kind={kindOf({ platform: r.platform })} size={22} /><Title order={4} style={{ flex: 1 }}>{r.machine}</Title>
        <UnstyledButton aria-label="Luk" onClick={onClose}><IconCircleX size={18} style={{ opacity: 0.5 }} /></UnstyledButton></Group>
      <Stack gap="sm" px="md" pb="md">
        {s?.latest ? <Text ff="monospace" size="sm">{s.current} → <b>{s.latest}</b></Text> : <Text size="sm" c="dimmed">{biosStateLine(s).text}</Text>}
        <Text size="xs" c="dimmed" mt={-8}>{r.model ?? ''}{s?.latestDate ? ` · udgivet ${s.latestDate}` : ''}</Text>
        {imp && isSecureBootLine(imp.text) && <Alert variant="light" color="orange" icon={<IconShieldLock size={16} />} p="xs">{s?.latest} har de nye Secure Boot-nøgler fra 2023.</Alert>}
        {(s?.changes ?? []).length > 0 && <Stack gap={2}>{s!.changes.slice(0, 5).map((c) => (
          <Group key={c.version} gap="sm" wrap="nowrap" align="flex-start"><Text size="xs" ff="monospace" fw={700} c="dimmed" w={34}>{shortVersion(c.version)}</Text><Text size="xs">{c.lines[0]?.text ?? '–'}</Text></Group>
        ))}</Stack>}
        <Kv rows={[
          ['Vej', routeLabel(u?.route, s?.vendor, s?.vendor === 'lenovo' && r.platform !== 'mac')],
          ['Backup', b?.snapshots[0] ? `${fmt(b.snapshots[0].takenAt)}` : 'Ingen'],
          ...(ready !== null ? [['Klar', <Text span size="sm" fw={600} c={ready ? 'green' : 'orange'}>{ready ? 'Ja, alle kontroller er i orden' : 'Ikke endnu – se maskinen'}</Text>] as [string, ReactNode]] : []),
        ]} />
        {isAdmin && s?.state === 'newer' && (
          <>
            {u?.route && <Button fullWidth component={Link} to={`${go}?bios=update`} leftSection={<IconCpu2 size={16} />}>{BIOS_UPDATE_BUTTON}</Button>}
            <Group gap="xs" grow>
              {s.vendor === 'lenovo' && <Button variant="default" component={Link} to={`${go}?bios=usb`} leftSection={<IconUsb size={16} />}>USB-stik</Button>}
              <Button variant="default" leftSection={<IconDownload size={16} />} disabled={!!pkg.busy} onClick={pkg.start}>{pkg.label}</Button>
            </Group>
            {b?.settings && <Button fullWidth variant="default" leftSection={<IconDatabaseExport size={16} />} onClick={() => takeBackup(r.machineId).then(() => notifyOk('Backup af BIOS-indstillingerne er bestilt.'), notifyErr)}>Tag backup af indstillinger</Button>}
          </>
        )}
        <Anchor component={Link} to={go} size="sm" fw={600}>Åbn maskinen ›</Anchor>
      </Stack>
    </Paper>
  );
}

function FleetCard({ r, isAdmin }: { r: BiosFleetRow; isAdmin: boolean }) {
  const s = r.status;
  const pkg = usePackageDownload(r.machineId);
  const go = `/maskiner/${r.machineId}/opdateringer`;
  return (
    <Paper withBorder p="sm" radius="md">
      <Anchor component={Link} to={go} underline="never" c="inherit">
        <Group gap="sm" wrap="nowrap"><KindIcon kind={kindOf({ platform: r.platform })} size={22} /><Text fw={650} style={{ flex: 1 }}>{r.machine}</Text><IconChevronRight size={16} style={{ opacity: 0.5 }} /></Group>
      </Anchor>
      <Text size="xs" c="dimmed" mt={3}>{r.model ?? '–'}</Text>
      {s?.latest && <Text size="sm" ff="monospace" mt={6}>{s.current} → <b>{s.latest}</b></Text>}
      <div style={{ marginTop: 7 }}><FleetBadges s={s} /></div>
      {s?.state === 'newer' && <Text size="xs" c="dimmed" mt={8}>{fleetRoute(r)}</Text>}
      {isAdmin && s?.state === 'newer' && (
        <Group gap={8} mt={10} wrap="nowrap">
          {fleetRouteKey(r)
            ? <Button size="sm" style={{ flex: 1 }} component={Link} to={`${go}?bios=update`} leftSection={<IconCpu2 size={16} />}>{BIOS_UPDATE_BUTTON}</Button>
            : <Button size="sm" style={{ flex: 1 }} variant="default" leftSection={<IconDownload size={16} />} disabled={!!pkg.busy} onClick={pkg.start}>{pkg.label}</Button>}
          {s.vendor === 'lenovo' && <Button size="sm" variant="default" component={Link} to={`${go}?bios=usb`} aria-label="USB-stik"><IconUsb size={16} /></Button>}
        </Group>
      )}
    </Paper>
  );
}

// ====================================================================== Indstillinger → BIOS (design 4.3)

/** Leverandørernes kataloger og serverens pakkecache. Kun for administratorer; pakker hentes stadig kun på et tryk. */
export function BiosSettingsTab() {
  const { data: cat, refetch: refetchCat } = useApi<BiosVendorStatus[]>(['bios-catalog'], '/api/bios/catalog', { refetchInterval: 30_000 });
  const { data: pkgs, refetch } = useApi<BiosPackageRow[]>(['bios-packages'], '/api/bios/packages', { refetchInterval: 30_000 });
  const [busy, setBusy] = useState<string | null>(null);
  if (!cat || !pkgs) return <Loading />;
  const total = pkgs.filter((p) => p.state === 'ready').reduce((n, p) => n + p.size, 0);
  const check = async () => {
    setBusy('check');
    try { await post('/api/bios/check'); notifyOk('Katalogerne tjekkes nu.'); window.setTimeout(() => refetchCat(), 5000); } catch (e) { notifyErr(e); } finally { setBusy(null); }
  };
  const remove = (p: BiosPackageRow) => modals.openConfirmModal({
    title: 'Slet pakken fra cachen?',
    children: <Text size="sm">{p.fileName} ({p.version}) slettes fra serveren. Den hentes igen fra leverandøren, næste gang nogen beder om den.</Text>,
    labels: { confirm: 'Slet', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    onConfirm: async () => {
      setBusy(`p${p.id}`);
      try { await del(`/api/bios/packages/${p.id}`); notifyOk('Pakken er slettet.'); refetch(); } catch (e) { notifyErr(e); } finally { setBusy(null); }
    },
  });
  return (
    <Stack gap="md">
      <Paper withBorder radius="md" p="md">
        <Group mb="sm" gap="sm"><Title order={5} style={{ flex: 1 }}>Leverandørernes kataloger</Title>
          <Button size="xs" variant="default" leftSection={<IconRefresh size={14} />} loading={busy === 'check'} onClick={check}>Tjek nu</Button></Group>
        <ScrollArea type="auto"><Table verticalSpacing={6} miw={560}>
          <Table.Thead><Table.Tr><Table.Th>Leverandør</Table.Th><Table.Th>Maskiner</Table.Th><Table.Th>Modeller i kataloget</Table.Th><Table.Th>Hentet</Table.Th><Table.Th>Status</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>{cat.map((v) => (
            <Table.Tr key={v.vendor}>
              <Table.Td><Text size="sm" fw={600}>{v.name}</Text></Table.Td>
              <Table.Td><Text size="sm">{v.machines}</Text></Table.Td>
              <Table.Td><Text size="sm">{v.supported ? v.models : '–'}</Text></Table.Td>
              <Table.Td><Text size="sm" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{v.fetchedAt ? ago(v.fetchedAt) : '–'}</Text></Table.Td>
              <Table.Td><Text size="sm" c={v.error ? 'yellow' : 'dimmed'}>{v.text}</Text></Table.Td>
            </Table.Tr>
          ))}</Table.Tbody>
        </Table></ScrollArea>
        <Box mt="sm"><Info>Kataloget hentes én gang i døgnet og kun for leverandører, der findes i flåden. Dell og HP kommer i en senere version.</Info></Box>
      </Paper>
      <Paper withBorder radius="md" p="md">
        <Group mb="sm" gap="sm"><Title order={5} style={{ flex: 1 }}>Pakker på serveren</Title><Text size="sm" c="dimmed">{pkgs.length} pakker · {bytes(total)}</Text></Group>
        {pkgs.length === 0 ? <Empty>Ingen pakker hentet endnu.</Empty> : (
          <ScrollArea type="auto"><Table verticalSpacing={6} miw={640}>
            <Table.Thead><Table.Tr><Table.Th>Fil</Table.Th><Table.Th>Version</Table.Th><Table.Th>Størrelse</Table.Th><Table.Th>Kontrolsum</Table.Th><Table.Th>Sidst brugt</Table.Th><Table.Th w={40} /></Table.Tr></Table.Thead>
            <Table.Tbody>{pkgs.map((p) => (
              <Table.Tr key={p.id}>
                <Table.Td><Text size="sm" ff="monospace">{p.fileName}</Text>{p.error && <Text size="xs" c="red">{p.error}</Text>}</Table.Td>
                <Table.Td><Text size="sm" ff="monospace">{p.version}</Text></Table.Td>
                <Table.Td><Text size="sm" style={{ whiteSpace: 'nowrap' }}>{p.state === 'downloading' && !p.stale ? `Hentes … ${bytes(p.bytes)}` : bytes(p.size)}</Text></Table.Td>
                <Table.Td><Text size="sm" c="dimmed">{p.shaSource === 'catalog' ? 'Fra leverandøren' : 'Låst ved første hentning'}</Text></Table.Td>
                <Table.Td><Text size="sm" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{ago(p.lastUsedAt ?? p.downloadedAt ?? p.created)}</Text></Table.Td>
                <Table.Td>{!(p.state === 'downloading' && !p.stale) && (
                  <ActionIcon variant="subtle" color="gray" aria-label={`Slet ${p.fileName}`} loading={busy === `p${p.id}`} onClick={() => remove(p)}><IconTrash size={16} /></ActionIcon>)}</Table.Td>
              </Table.Tr>
            ))}</Table.Tbody>
          </Table></ScrollArea>
        )}
        <Box mt="sm"><Info>Pakker hentes kun, når nogen trykker Hent pakke, Lav USB-stik eller Opdatér. De 2 nyeste pr. model beholdes, og pakker der ikke er brugt i 90 dage slettes af sig selv.</Info></Box>
      </Paper>
    </Stack>
  );
}
