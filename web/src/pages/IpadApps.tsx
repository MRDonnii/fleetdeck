import { useMemo, useState } from 'react';
import { Alert, Anchor, Avatar, Badge, Button, Group, Paper, SegmentedControl, Select, Stack, Text, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { IconExternalLink, IconRefresh, IconSearch } from '@tabler/icons-react';
import { notifyErr, notifyOk, post, put, useApi } from '../api';
import { ago, JOB } from '../format';
import { Empty, JobLive, Loading, StatusBadge } from '../components';
import type { IpadApp, IpadAppsInfo, IpadDetail, IpadRow, JobStatus } from '../types';

const VIA: Record<string, string> = { mac: 'via Mac\'en', mdm: 'via Apple-styring' };

/** Apps på iPad'en: installeret version mod App Store, forældede apps og hurtige handlinger. */
/** simple: forælderens visning – kun listen (ingen scanning, skjul/spær eller tekniske oplysninger). */
export function InstalledAppsTab({ r, d, simple }: { r: IpadRow; d: IpadDetail; simple?: boolean }) {
  const qc = useQueryClient();
  const { data, isLoading } = useApi<IpadAppsInfo>(['ipad-apps', r.id], `/api/ipads/${r.id}/apps`, { refetchInterval: 20_000 });
  const [filter, setFilter] = useState<'all' | 'outdated'>('all');
  const [q, setQ] = useState('');
  const [via, setVia] = useState<string | null>(null);
  const [macId, setMacId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const shown = useMemo(() => {
    const t = q.trim().toLowerCase();
    return (data?.apps ?? []).filter((a) => (filter === 'all' || a.outdated) && (!t || a.name.toLowerCase().includes(t) || a.bundleId.toLowerCase().includes(t)));
  }, [data, filter, q]);
  if (isLoading || !data) return <Loading />;

  const canScan = !simple && data.can.includes('update');
  const canRules = !simple && data.can.includes('rules');
  const hasMac = !!data.ecid && data.macs.length > 0;
  const both = data.mdm && hasMac;
  const chosen = via ?? (data.mdm ? 'mdm' : 'mac');
  const jobActive = !!data.job && ['pending', 'running'].includes(data.job.status);
  const scanning = jobActive || (data.mdmPending && chosen === 'mdm');

  const scan = async () => {
    setBusy(true);
    try {
      const res = await post<{ msg: string }>(`/api/ipads/${r.id}/apps/scan`, { via: chosen, macId: chosen === 'mac' && macId ? Number(macId) : null });
      notifyOk(res.msg); qc.invalidateQueries();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const save = async (body: object, msg: string) => {
    try { await put(`/api/ipads/${r.id}`, body); notifyOk(msg); qc.invalidateQueries(); } catch (e) { notifyErr(e); }
  };
  const toggleHidden = (a: IpadApp) => a.hidden
    ? save({ hiddenApps: d.hiddenApps.filter((x) => x !== a.bundleId) }, `${a.name} skjules ikke længere – installér den nye låste profil`)
    : save({ hiddenApps: [...d.hiddenApps, a.bundleId] }, `${a.name} skjules, når den nye låste profil er installeret på ${r.name}`);
  const toggleService = (a: IpadApp) => {
    if (!a.service) return;
    const on = d.blockedServices.includes(a.service);
    save({ blockedServices: on ? d.blockedServices.filter((x) => x !== a.service) : [...d.blockedServices, a.service] },
      `${a.serviceName ?? a.service} er ${on ? 'tilladt igen' : 'spærret'} på ${r.name}`);
  };

  if (simple) return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed">De apps, der er på {r.name}{data.scannedAt ? ` (sidst tjekket ${ago(data.scannedAt)})` : ''}. Vil du spærre en app, så brug knapperne øverst eller fanen Regler.</Text>
      {data.apps.length === 0 ? <Empty>Listen over apps er ikke hentet endnu.</Empty> : <>
        <TextInput leftSection={<IconSearch size={14} />} placeholder="Søg efter app" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        <Stack gap={6}>
          {shown.map((a) => <AppRow key={a.bundleId} a={a} canRules={false} simple onHide={() => undefined} onService={() => undefined} />)}
          {shown.length === 0 && <Text size="sm" c="dimmed">Ingen apps matcher.</Text>}
        </Stack>
      </>}
    </Stack>
  );

  return (
    <Stack gap="md">
      <Alert color="blue" variant="light">
        <Text size="sm">Her ses de apps, der er installeret på iPad'en, og om App Store har en nyere version. Fleetdeck opdaterer ikke selv apps:
          opdateringer sker via <b>automatiske App Store-opdateringer</b> på iPad'en (Indstillinger → App Store → App-opdateringer). Senere kan det ske
          via Apple-styring og Apple Business.</Text>
      </Alert>

      <Paper withBorder radius="md" p="md">
        <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
          <div>
            <Text fw={650}>{data.scannedAt ? `Sidst scannet ${ago(data.scannedAt)} ${VIA[data.source ?? ''] ?? ''}` : 'Ikke scannet endnu'}</Text>
            <Text size="xs" c="dimmed">
              {data.apps.length} apps · {data.outdated} forældede
              {data.pendingLookups > 0 ? ` · henter App Store-oplysninger for ${data.pendingLookups} apps…` : ''}
            </Text>
          </div>
          {canScan && <Group gap="xs" align="flex-end" wrap="wrap">
            {both && <SegmentedControl size="xs" value={chosen} onChange={setVia}
              data={[{ value: 'mdm', label: 'Apple-styring' }, { value: 'mac', label: 'Mac (kabel)' }]} />}
            {chosen === 'mac' && data.macs.length > 1 && <Select size="xs" w={180} allowDeselect={false} value={macId ?? String(data.macs[0].id)} onChange={setMacId}
              data={data.macs.map((m) => ({ value: String(m.id), label: m.online ? m.name : `${m.name} (offline)` }))} />}
            <Button leftSection={<IconRefresh size={16} />} loading={busy} disabled={scanning || (!data.mdm && !data.ecid)} onClick={scan}>
              {scanning ? 'Scanner…' : 'Scan apps nu'}</Button>
          </Group>}
        </Group>
        {!data.mdm && !data.ecid && <Text size="xs" c="dimmed" mt="xs">
          For at scanne skal iPad'en enten være tilmeldt Apple-styring, eller linket til en Mac med Apple Configurator (Opsætning → Via Mac'en).</Text>}
        {chosen === 'mac' && data.ecid && <Text size="xs" c="dimmed" mt="xs">Via Mac'en: iPad'en skal være tilsluttet med kabel og låst op.</Text>}
        {chosen === 'mdm' && data.mdmPending && <Text size="xs" c="dimmed" mt="xs">Venter på svar fra iPad'en (den svarer, når den er tændt og på internettet).</Text>}
        {data.error && <Text size="xs" c="red" mt="xs">Seneste scanning mislykkedes: {data.error}</Text>}
        {data.job && <Paper withBorder radius="sm" p="xs" mt="sm">
          <Group gap="xs" justify="space-between" wrap="nowrap">
            <Text size="sm" fw={600} truncate>{data.job.title}</Text>
            <Group gap={6} wrap="nowrap"><Text size="xs" c="dimmed">{ago(data.job.finished ?? data.job.created)}</Text><StatusBadge map={JOB} value={data.job.status} /></Group>
          </Group>
          {jobActive && <JobLive job={{ id: data.job.id, status: data.job.status as JobStatus, progress: data.job.progress, step: data.job.step, stepSince: data.job.stepSince }} />}
        </Paper>}
      </Paper>

      {data.apps.length === 0 ? <Empty>Ingen apps endnu. Tryk "Scan apps nu" for at hente listen fra iPad'en.</Empty> : <>
        <Group gap="sm" wrap="wrap">
          <SegmentedControl size="xs" value={filter} onChange={(v) => setFilter(v as 'all' | 'outdated')}
            data={[{ value: 'all', label: `Alle (${data.apps.length})` }, { value: 'outdated', label: `Forældede (${data.outdated})` }]} />
          <TextInput size="xs" w={220} leftSection={<IconSearch size={14} />} placeholder="Søg efter app" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
        </Group>
        <Stack gap={6}>
          {shown.map((a) => <AppRow key={a.bundleId} a={a} canRules={canRules} onHide={() => toggleHidden(a)} onService={() => toggleService(a)} />)}
          {shown.length === 0 && <Text size="sm" c="dimmed">Ingen apps matcher.</Text>}
        </Stack>
        {canRules && <Text size="xs" c="dimmed">"Skjul app" lægger appen i den låste profil (Apps og begrænsninger) – den virker først, når den nye version af profilen er
          installeret på en overvåget iPad. "Spær" spærrer tjenesten i AdGuard med det samme.</Text>}
      </>}
    </Stack>
  );
}

function AppRow({ a, canRules, simple, onHide, onService }: { a: IpadApp; canRules: boolean; simple?: boolean; onHide: () => void; onService: () => void }) {
  const storeText = a.store === 'found' ? `App Store ${a.storeVersion ?? '?'}` : a.store === 'pending' ? 'App Store: henter…'
    : a.store === 'missing' ? 'Ikke i den danske App Store' : 'App Store: kunne ikke hentes';
  return (
    <Paper withBorder radius="md" p="xs">
      <Group gap="sm" wrap="nowrap" align="center">
        <Avatar src={a.iconUrl} radius="md" size={40} alt="">{a.name.slice(0, 1)}</Avatar>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Group gap={6} wrap="nowrap">
            <Text fw={600} size="sm" truncate>{a.name}</Text>
            {!simple && a.outdated && <Badge size="xs" color="orange" variant="light">forældet</Badge>}
            {a.hidden && <Badge size="xs" color="gray" variant="light">skjult</Badge>}
            {a.serviceBlocked && <Badge size="xs" color="red" variant="light">spærret</Badge>}
          </Group>
          {simple
            ? (a.serviceBlocked || a.hidden) && <Text size="xs" c="dimmed">{a.serviceBlocked ? 'Spærret' : 'Skjult på iPad\'en'}</Text>
            : <Text size="xs" c="dimmed" truncate title={a.bundleId}>
                Installeret {a.version ?? '?'} · <span style={a.outdated ? { color: 'var(--mantine-color-orange-6)' } : undefined}>{storeText}</span> · {a.bundleId}
              </Text>}
        </div>
        <Group gap={6} wrap="nowrap">
          {a.storeUrl && <Anchor href={a.storeUrl} target="_blank" rel="noreferrer" size="xs" title="Åbn i App Store"><IconExternalLink size={16} /></Anchor>}
          {canRules && a.service && <Button size="compact-xs" variant={a.serviceBlocked ? 'filled' : 'default'} color={a.serviceBlocked ? 'red' : undefined} onClick={onService}
            title={a.serviceBlocked ? 'Spærret i AdGuard – klik for at tillade' : `Spær tjenesten ${a.serviceName ?? a.service} i AdGuard`}>
            {a.serviceBlocked ? `Tillad ${a.serviceName ?? ''}`.trim() : 'Spær tjeneste'}</Button>}
          {canRules && <Button size="compact-xs" variant="default" onClick={onHide}>{a.hidden ? 'Vis igen' : 'Skjul app'}</Button>}
        </Group>
      </Group>
    </Paper>
  );
}
