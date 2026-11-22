import { useState } from 'react';
import { ActionIcon, Anchor, Badge, Box, Button, Group, Menu, Paper, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import {
  IconBrandGithub, IconDots, IconExternalLink, IconLoader2, IconLock, IconPackage, IconPencil, IconPlayerPlay, IconPlus, IconRefresh, IconSettings, IconTrash,
} from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { del, post, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading, PageHeader, Panel } from '../components';
import {
  AppModal, AppSettingsModal, AppStatusBadge, ChannelBadge, NeverBadge, Notes, PLACE, READ, UpdateConfirm, appState, draftOfApp, emptyApp, when,
  type AppDraft, type AppRow, type AppsResponse,
} from '../design/apps';

const rowBorder = (i: number) => (i ? { borderTop: '1px solid var(--mantine-color-default-border)' } : undefined);
const mono = { fontFamily: 'var(--mantine-font-family-monospace)' } as const;

/** "minpi · container minapp · HTTP" */
const where = (a: AppRow) => [a.placeName ? `${PLACE[a.placeKind]} ${a.placeName}` : null, READ[a.readKind]].filter(Boolean).join(' · ');

/** Siden Apps (forslaget Command): liste over egne apps med status, og noterne for den valgte app i siden. */
export function AppsPage() {
  const { isAdmin } = useMe();
  const mobile = useMediaQuery('(max-width: 62em)');
  const qc = useQueryClient();
  const { data, isLoading } = useApi<AppsResponse>(['apps'], '/api/apps', { refetchInterval: 20_000 });
  const [draft, setDraft] = useState<AppDraft | null>(null);
  const [confirm, setConfirm] = useState<AppRow | null>(null);
  const [settings, setSettings] = useState(false);
  const [picked, setPicked] = useState<number | null>(null);
  const check = useAction((id?: number) => post<{ count: number; errors: string[] }>(`/api/apps/check${id ? `?id=${id}` : ''}`),
    (r) => (r.errors.length ? r.errors.join(' · ') : r.count === 1 ? 'Appen er tjekket' : `${r.count} apps er tjekket`));
  const remove = useAction((id: number) => del(`/api/apps/${id}`), 'Appen er fjernet fra Fleetdeck');
  if (isLoading || !data) return <Loading />;
  const { apps } = data;
  const refresh = () => qc.invalidateQueries({ queryKey: ['apps'] });
  const count = (f: (a: AppRow) => boolean) => apps.filter(f).length;
  const sel = apps.find((a) => a.id === picked) ?? apps.find((a) => a.status === 'new') ?? apps[0];
  const askDelete = (a: AppRow) => modals.openConfirmModal({
    title: `Fjern ${a.name}?`, labels: { confirm: 'Fjern', cancel: 'Annullér' }, confirmProps: { color: 'red' },
    children: <Text size="sm">Fleetdeck holder op med at holde øje med appen. Selve appen på maskinen røres ikke.</Text>,
    onConfirm: () => remove.mutate(a.id, { onSuccess: refresh }),
  });
  const rate = data.settings.rate;
  const checkedAt = apps.map((a) => a.checked).filter(Boolean).sort().at(-1);
  const updateButton = (a: AppRow, size: 'compact-xs' | 'compact-sm' = 'compact-xs') => isAdmin && a.status === 'new' && a.hasUpdateCommand && (
    a.updateJob && ['pending', 'running'].includes(a.updateJob.status)
      ? <Badge size="sm" variant="light" color="blue" leftSection={<IconLoader2 size={12} />}>Opdaterer…</Badge>
      : <Button size={size} variant={a.neverAuto ? 'default' : 'filled'} leftSection={<IconPlayerPlay size={12} />}
          onClick={(e) => { e.stopPropagation(); setConfirm(a); }}>{a.neverAuto ? 'Opdatér…' : 'Opdatér'}</Button>);
  const menu = (a: AppRow) => isAdmin && (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target><ActionIcon variant="subtle" color="gray" aria-label="Mere" onClick={(e) => e.stopPropagation()}><IconDots size={16} /></ActionIcon></Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconRefresh size={14} />} onClick={() => check.mutate(a.id, { onSuccess: refresh })}>Tjek nu</Menu.Item>
        <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setDraft(draftOfApp(a))}>Ret</Menu.Item>
        <Menu.Item leftSection={<IconBrandGithub size={14} />} component="a" href={`https://github.com/${a.repo}/releases`} target="_blank" rel="noreferrer">Udgivelser på GitHub</Menu.Item>
        <Menu.Item leftSection={<IconTrash size={14} />} color="red" onClick={() => askDelete(a)}>Fjern</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
  const detail = (a: AppRow) => (
    <ReleasePanel a={a} action={updateButton(a, 'compact-sm')} />
  );

  return (
    <>
      <PageHeader title="Apps" sub={mobile ? undefined : 'Dine egne apps: Fleetdeck spørger GitHub om nye udgivelser og sammenligner med den version, der kører.'}>
        {isAdmin && <Tooltip label="Indstillinger og GitHub-nøgle"><ActionIcon variant="default" size={30} onClick={() => setSettings(true)} aria-label="Indstillinger"><IconSettings size={16} /></ActionIcon></Tooltip>}
        {isAdmin && apps.length > 0 && <Button variant="default" size="xs" leftSection={<IconRefresh size={14} />} loading={check.isPending}
          onClick={() => check.mutate(undefined, { onSuccess: refresh })}>Tjek nu</Button>}
        {isAdmin && <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => setDraft(emptyApp())}>Tilføj app</Button>}
      </PageHeader>

      {apps.length > 0 && (
        <Group gap="xs" mb="md" wrap="wrap">
          {([['nye versioner', (a: AppRow) => a.status === 'new', 'orange'], ['opdateret', (a: AppRow) => a.status === 'ok', 'teal'],
            ['nyere end udgivelsen', (a: AppRow) => a.status === 'ahead', 'blue'], ['kan ikke læses', (a: AppRow) => a.status === 'unknown' && !!a.readError, 'red']] as const)
            .map(([label, f, color]) => count(f) > 0 && (
              <Paper key={label} px="sm" py={6}><Group gap={6} wrap="nowrap"><Text fw={700} c={color} fz={mobile ? 15 : 18}>{count(f)}</Text><Text size="sm" c="dimmed">{label}</Text></Group></Paper>
            ))}
          <Box style={{ flex: 1 }} />
          {!mobile && <Text size="xs" c="dimmed">
            {checkedAt ? `GitHub tjekket ${when(checkedAt)}` : 'GitHub er ikke tjekket endnu'}
            {rate?.remaining != null ? ` · ${rate.remaining.toLocaleString('da-DK')} forespørgsler tilbage` : ''}
            {rate?.blockedUntil && new Date(rate.blockedUntil) > new Date() ? ` · venter til ${when(rate.blockedUntil)}` : ''}
          </Text>}
        </Group>
      )}

      {apps.length === 0 && <Panel><Empty>Ingen apps endnu. Tilføj en af dine egne apps med dens GitHub-projekt (ejer/navn).</Empty></Panel>}

      {apps.length > 0 && mobile && (
        <Stack gap="xs">
          {apps.map((a) => (
            <Paper key={a.id} p="sm">
              <Group gap="sm" wrap="nowrap" align="flex-start">
                <IconPackage size={18} style={{ flex: 'none', opacity: 0.8, marginTop: 2 }} />
                <Box style={{ flex: 1, minWidth: 0 }}>
                  <Text size="sm" fw={600} lineClamp={1}>{a.name}</Text>
                  <Text size="xs" c="dimmed" lineClamp={1}>{[a.machine, READ[a.readKind]].filter(Boolean).join(' · ')}</Text>
                </Box>
                {menu(a)}
              </Group>
              <Group gap="xs" mt={6} wrap="nowrap">
                <AppStatusBadge a={a} />
                <Text size="sm" style={{ ...mono, flex: 1, minWidth: 0 }} lineClamp={1}>
                  {a.readKind === 'none' ? a.latest?.version ?? '–' : a.running ?? '–'}{a.status === 'new' && a.latest ? <> → <b>{a.latest.version}</b></> : null}
                </Text>
                {a.neverAuto && <IconLock size={14} color="var(--mantine-color-orange-6)" />}
                {updateButton(a)}
              </Group>
              {a.readError && <Text size="xs" c="red" mt={4}>{a.readError}</Text>}
              {a.checkError && <Text size="xs" c="red" mt={4}>{a.checkError}</Text>}
              {a.status === 'new' && a.latest && (
                <UnstyledButton mt={6} onClick={() => setPicked(picked === a.id ? null : a.id)}><Text size="xs" c="blue">{picked === a.id ? 'Skjul noter' : 'Vis noter'}</Text></UnstyledButton>
              )}
              {picked === a.id && a.latest && <Box mt={6}><Notes text={a.latest.notes} max={20} /></Box>}
            </Paper>
          ))}
        </Stack>
      )}

      {apps.length > 0 && !mobile && (
        <Group align="flex-start" gap="md" wrap="nowrap">
          <Box style={{ flex: 1, minWidth: 0 }}>
            <Panel p={0}>
              <Group gap="sm" wrap="nowrap" px="md" py={8}>
                {[['App', '1 1 26%'], ['Kører på', '1 1 24%'], ['Nu', '0 0 96px'], ['Seneste', '0 0 120px'], ['Status', '0 0 170px'], ['', '0 0 120px']].map(([l, f]) => (
                  <Text key={l || 'x'} size="xs" c="dimmed" fw={600} style={{ flex: f }}>{l}</Text>
                ))}
              </Group>
              {apps.map((a, i) => (
                <Group key={a.id} gap="sm" wrap="nowrap" px="md" py={10} onClick={() => setPicked(a.id)}
                  style={{ ...rowBorder(i), cursor: 'pointer', background: sel?.id === a.id ? 'var(--mantine-primary-color-light)' : undefined }}>
                  <Group gap={10} wrap="nowrap" style={{ flex: '1 1 26%', minWidth: 0 }}>
                    <IconPackage size={18} style={{ flex: 'none', opacity: 0.8 }} />
                    <Box style={{ minWidth: 0 }}>
                      <Text size="sm" fw={600} lineClamp={1}>{a.name}</Text>
                      <Text size="xs" c="dimmed" lineClamp={1} style={mono}>{a.repo}</Text>
                    </Box>
                  </Group>
                  <Box style={{ flex: '1 1 24%', minWidth: 0 }}>
                    <Group gap={6} wrap="nowrap">
                      {a.machine && <Box w={8} h={8} style={{ flex: 'none', borderRadius: 99, background: `var(--mantine-color-${a.online ? 'teal' : 'gray'}-6)` }} />}
                      <Text size="sm" lineClamp={1}>{a.machine ?? '–'}</Text>
                    </Group>
                    <Text size="xs" c="dimmed" lineClamp={1}>{where(a)}</Text>
                  </Box>
                  <Text size="sm" style={{ ...mono, flex: '0 0 96px' }} lineClamp={1} c={a.running ? undefined : 'dimmed'}>
                    {a.reading && !a.running ? 'læser…' : a.running ?? '–'}
                  </Text>
                  <Group gap={5} wrap="nowrap" style={{ flex: '0 0 120px', minWidth: 0 }}>
                    <Text size="sm" fw={700} style={mono} lineClamp={1}>{a.latest?.version ?? '–'}</Text>
                    {a.channel === 'beta' && <ChannelBadge c="beta" />}
                  </Group>
                  <Stack gap={3} style={{ flex: '0 0 170px', alignItems: 'flex-start' }}>
                    <Tooltip label={a.readError ?? a.checkError ?? (a.runningAt ? `Version læst ${when(a.runningAt)}` : 'Ikke læst endnu')} multiline maw={320}>
                      <span><AppStatusBadge a={a} /></span>
                    </Tooltip>
                    {a.neverAuto ? <NeverBadge /> : a.autoUpdate ? <Text size="xs" c="dimmed">Automatisk om natten</Text> : null}
                    {(a.readError || a.checkError) && <Text size="xs" c="red" lineClamp={1}>{a.readError ?? a.checkError}</Text>}
                  </Stack>
                  <Group gap={4} justify="flex-end" wrap="nowrap" style={{ flex: '0 0 120px' }}>{updateButton(a)}{menu(a)}</Group>
                </Group>
              ))}
            </Panel>
          </Box>
          {sel && <Box w={380} style={{ flex: 'none' }}>{detail(sel)}</Box>}
        </Group>
      )}

      <AppModal draft={draft} machines={data.machines} onClose={() => setDraft(null)} />
      <UpdateConfirm app={confirm} onClose={() => setConfirm(null)} />
      <AppSettingsModal opened={settings} settings={data.settings} onClose={() => setSettings(false)} />
    </>
  );
}

/** Noterne for den valgte app: den nyeste udgivelse, de mellemliggende versioner og kommandoen. */
function ReleasePanel({ a, action }: { a: AppRow; action: React.ReactNode }) {
  const s = appState(a);
  const rel = a.latest;
  return (
    <Panel>
      <Stack gap="sm">
        <Group gap="xs" wrap="nowrap">
          <IconPackage size={20} style={{ flex: 'none' }} />
          <Text fw={700} style={{ flex: 1 }} lineClamp={1}>{a.name} {rel?.version ?? ''}</Text>
          <ChannelBadge c={a.channel} />
        </Group>
        <Text size="xs" c="dimmed">
          {rel?.published ? `Udgivet ${when(rel.published, false)} · ` : ''}<span style={mono}>{a.repo}</span>
          {a.running ? ` · kører ${a.running}${a.machine ? ` på ${a.machine}` : ''}` : ''}
        </Text>
        {a.status === 'ahead' && <Text size="sm" c="blue">Du kører {a.running}, som er nyere end seneste udgivelse på kanalen ({rel?.version}). Der er intet at opdatere.</Text>}
        {a.status === 'ok' && <Text size="sm" c="teal">{a.name} kører den nyeste version.</Text>}
        {a.status === 'norelease' && <Text size="sm" c="dimmed">Der er ingen udgivelse på kanalen {a.channel === 'beta' ? 'beta' : 'stabil'} endnu.</Text>}
        {a.status === 'unknown' && a.readError && <Text size="sm" c="red">{s.label}: {a.readError}</Text>}
        {rel && (
          <Box p="sm" style={{ background: 'var(--mantine-color-default-hover)', borderRadius: 8, maxHeight: 340, overflow: 'auto' }}>
            <Notes text={rel.notes} />
          </Box>
        )}
        {a.newer.length > 1 && <Text size="xs" c="dimmed">Også nyere end din version: {a.newer.slice(1).map((r) => r.version).join(', ')}</Text>}
        {a.neverAuto && (
          <Box p="xs" style={{ border: '1px solid var(--mantine-color-orange-4)', borderRadius: 8, background: 'var(--mantine-color-orange-light)' }}>
            <Group gap={8} wrap="nowrap" align="flex-start"><IconLock size={16} style={{ flex: 'none', marginTop: 2 }} color="var(--mantine-color-orange-6)" />
              <Text size="sm"><b>Aldrig automatisk.</b> Fleetdeck opdaterer kun appen, når du selv trykker og bekræfter.</Text></Group>
          </Box>
        )}
        {a.updateCommand && (
          <Box>
            <Text size="xs" fw={600} mb={4}>Opdatér-kommando{a.machine ? ` (kører på ${a.machine})` : ''}</Text>
            <Text size="xs" p={8} style={{ ...mono, whiteSpace: 'pre-wrap', border: '1px solid var(--mantine-color-default-border)', borderRadius: 6 }}>{a.updateCommand}</Text>
          </Box>
        )}
        {a.updateJob && <Text size="xs" c="dimmed">Opdatering til {a.updateJob.to}: {a.updateJob.step ?? a.updateJob.status}</Text>}
        <Group justify="flex-end" gap="xs">
          {rel?.url && <Anchor size="sm" href={rel.url} target="_blank" rel="noreferrer"><Group gap={4}><IconExternalLink size={14} />Se på GitHub</Group></Anchor>}
          {action}
        </Group>
      </Stack>
    </Panel>
  );
}
