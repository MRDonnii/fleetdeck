import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { Anchor, Badge, Button, Group, Paper, RingProgress, SegmentedControl, SimpleGrid, Stack, Text, ThemeIcon, Title } from '@mantine/core';
import { IconActivityHeartbeat, IconAlertTriangle, IconCheck, IconClock, IconDeviceMobile, IconDownload, IconLoader2, IconNetwork, IconPlus, IconServer2, IconTemperature, IconUser, IconWifi, IconX } from '@tabler/icons-react';
import { post, useAction, useApi } from '../api';
import { ConnectButton } from '../design/remote';
import { PowerCountdown, RunningLine, useNow } from '../design/activity';
import { useMe } from '../auth';
import { ago, clockAt, presence } from '../format';
import { JobLive, Loading } from '../components';
import { KIND, KindIcon, kindOf, type Kind } from '../design/platform';
import type { AlertRow, Dashboard, MachineRow } from '../types';
import { askUpdateAll, type UpdateMode } from '../design/updateAll';
import { AppUpdatesLine } from '../design/apps';
import { briefText } from '../design/networkText';
import { fixButton, shortWhy } from '../design/alertHelpText';
import { useRunFix } from '../helper';

type Week = { day: string; ok: number; failed: number }[];

export function DashboardPage({ onAdd }: { onAdd?: () => void }) {
  const { data: d } = useApi<Dashboard & { week?: Week }>(['dashboard'], '/api/dashboard', { refetchInterval: 15_000 });
  const { isAdmin } = useMe();
  if (!d) return <Loading />;
  const t = d.totals;
  const scores = d.machines.filter((m) => m.healthScore != null).map((m) => m.healthScore!);
  const health = scores.length ? Math.round(scores.reduce((a, b) => a + b, 0) / scores.length) : null;
  const attention = d.alerts.filter((a) => a.severity !== 'info' && !a.acknowledged);
  const withUpdates = d.machines.filter((m) => m.upgrades > 0).length;
  const servers = d.machines.filter((m) => kindOf(m) === 'unraid');
  const serverUpdates = servers.reduce((n, m) => n + m.upgrades, 0);

  return (
    <Stack gap="lg">
      <div>
        <Title order={2} fw={700} style={{ letterSpacing: '-0.02em' }}>Oversigt</Title>
        <Text c="dimmed" size="sm" mt={2}>{t.machines} maskiner · {t.online} online{d.running.length ? ` · ${d.running.length} job kører` : ''}</Text>
      </div>

      <AppUpdatesLine />

      <SimpleGrid cols={{ base: 2, sm: 3, xl: 6 }} spacing="md">
        <Paper p="md" className="fd-kpi-hero">
          <Text size="xs" fw={600}>● Online</Text>
          <Text className="fd-kpi-value" fz={28} fw={700} lh={1.2} mt={4}>{t.online}<Text span fz={14} fw={500}> / {t.machines}</Text></Text>
          <div className="fd-spark" style={{ marginTop: 8 }}>{d.machines.map((m) => <b key={m.id} style={{ height: m.online ? '100%' : '30%' }} />)}</div>
        </Paper>
        <Paper p="md">
          <Group gap="sm" wrap="nowrap">
            <RingProgress size={52} thickness={6} roundCaps sections={health != null ? [{ value: health, color: health >= 85 ? 'teal' : health >= 65 ? 'yellow' : 'red' }] : []} />
            <div><Text size="xs" c="dimmed" fw={600}>Sundhed</Text><Text fz={26} fw={700} lh={1.2}>{health ?? '–'}<Text span fz={13} c="dimmed" fw={500}> / 100</Text></Text></div>
          </Group>
        </Paper>
        <Kpi to="/opdateringer" icon={<IconDownload size={14} />} label="Opdateringer" value={t.upgrades} color={t.upgrades ? 'orange' : 'teal'}
          sub={t.upgrades ? `på ${withUpdates} maskine${withUpdates === 1 ? '' : 'r'}` : 'alt er opdateret'} />
        <Kpi to="/jobs" icon={<IconLoader2 size={14} />} label="Jobs i gang" value={t.jobsActive} color="blue" sub={d.queued.length ? `${d.queued.length} i kø` : `${t.jobsOk24h} ok det seneste døgn`} />
        <Kpi to="/helbred" icon={<IconAlertTriangle size={14} />} label="Kræver opmærksomhed" value={attention.length} color={attention.length ? 'red' : 'teal'}
          sub={attention[0] ? `${attention[0].machine}: ${attention[0].title}` : 'intet lige nu'} />
        <Kpi to="/servere" icon={<IconServer2 size={14} />} label="Servere" value={servers.length} color="grape"
          sub={servers.length ? `${servers.filter((m) => m.online).length} online${serverUpdates ? ` · ${serverUpdates} klar` : ''}` : 'ingen endnu'} />
      </SimpleGrid>

      <Fleet machines={d.machines} onAdd={isAdmin ? onAdd : undefined} />

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Attention alerts={attention} />
        <Activity d={d} />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
        <WeekChart week={d.week ?? []} />
        <Servers machines={d.machines} />
        <Platforms machines={d.machines} />
      </SimpleGrid>
    </Stack>
  );
}

function Kpi({ to, icon, label, value, color, sub }: { to: string; icon: React.ReactNode; label: string; value: number; color: string; sub: string }) {
  const nav = useNavigate();
  return (
    <Paper p="md" component="button" type="button" onClick={() => nav(to)} style={{ cursor: 'pointer', textAlign: 'left', width: '100%' }}>
      <Text c="dimmed" size="xs" fw={600} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{icon}{label}</Text>
      <Text fz={28} fw={700} lh={1.2} mt={4} c={value ? color : undefined}>{value}</Text>
      <Text size="xs" c="dimmed" lineClamp={1}>{sub}</Text>
    </Paper>
  );
}

function Fleet({ machines, onAdd }: { machines: MachineRow[]; onAdd?: () => void }) {
  const [filter, setFilter] = useState<'all' | Kind>('all');
  const kinds = useMemo(() => [...new Set(machines.map(kindOf))], [machines]);
  const list = machines.filter((m) => filter === 'all' || kindOf(m) === filter)
    .sort((a, b) => Number(b.online) - Number(a.online) || b.alerts - a.alerts || a.name.localeCompare(b.name));
  return (
    <Paper p="md">
      <Group justify="space-between" mb="sm">
        <Text fw={650}>Flåden</Text>
        <Group gap="xs">
          {kinds.length > 1 && <SegmentedControl size="xs" value={filter} onChange={(v) => setFilter(v as 'all' | Kind)}
            data={[{ value: 'all', label: 'Alle' }, ...kinds.map((k) => ({ value: k, label: KIND[k].label }))]} />}
          {onAdd && <Button size="compact-sm" variant="light" leftSection={<IconPlus size={14} />} onClick={onAdd}>Tilføj</Button>}
        </Group>
      </Group>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
        {list.map((m) => <MachineCard key={m.id} m={m} isAdmin={!!onAdd} />)}
      </SimpleGrid>
    </Paper>
  );
}

const uptime = (h?: number | null) => {
  if (h == null) return null;
  const d = Math.floor(h / 24), t = Math.floor(h % 24), min = Math.round((h % 1) * 60);
  return d > 0 ? `${d} d ${t} t` : t > 0 ? `${t} t ${min} m` : `${min} m`;
};

function Bar({ label, value, color, online }: { label: string; value?: number | null; color: string; online: boolean }) {
  return (
    <>
      <span>{label}</span>
      <div className="fd-minibar"><i style={{ width: `${online ? value ?? 0 : 0}%`, background: color }} /></div>
      <span>{online && value != null ? `${Math.round(value)}\u00A0%` : '–'}</span>
    </>
  );
}

function MachineCard({ m, isAdmin }: { m: MachineRow; isAdmin: boolean }) {
  const now = useNow(!!m.power);
  const kind = kindOf(m);
  const state = !m.online ? 'off' : m.healthState === 'critical' ? 'bad' : m.healthState === 'warning' || m.alerts ? 'warn' : 'ok';
  const dot = { off: 'var(--fd-off)', bad: 'var(--fd-bad)', warn: 'var(--fd-warn)', ok: 'var(--fd-ok)' }[state];
  const upgrade = useAction((mode: UpdateMode) => post('/api/machines/actions', { machineIds: [m.id], action: 'update_all', params: { mode } }), `Opdatering af ${m.name} er sat i gang`);
  const askUpgrade = (e: React.MouseEvent) => {
    e.preventDefault(); e.stopPropagation();
    askUpdateAll([m], (mode) => upgrade.mutate(mode));
  };
  const up = uptime(m.uptimeH);
  return (
    <Link to={`/maskiner/${m.id}${kind === 'unraid' ? '/unraid' : ''}`} className="fd-machine" data-offline={!m.online || undefined}>
      <Group gap={10} wrap="nowrap">
        <KindIcon kind={kind} distro={m.distro} />
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text fw={650} size="sm" lineClamp={1}>{m.name}</Text>
          <Text size="xs" c="dimmed" lineClamp={1}>{m.online ? [m.os, m.profile].filter(Boolean).join(' · ') : presence(m)}</Text>
        </div>
        <span className="fd-dot" style={{ color: dot }} />
      </Group>
      <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr auto', gap: '5px 8px', alignItems: 'center', fontSize: 11, color: 'var(--mantine-color-dimmed)' }}>
        <Bar label="CPU" value={m.cpu} color="var(--mantine-primary-color-filled)" online={m.online} />
        <Bar label="RAM" value={m.ram} color={(m.ram ?? 0) > 90 ? 'var(--fd-bad)' : '#8b5cf6'} online={m.online} />
        <Bar label="Disk" value={m.disk} color={(m.disk ?? 0) > 85 ? 'var(--fd-bad)' : '#14b8a6'} online={m.online} />
      </div>
      {m.online && (
        <div className="fd-chips">
          {m.temp != null && <span title="CPU-temperatur"><IconTemperature size={12} />{m.temp}{'\u00A0'}°C</span>}
          {m.onlineSince && <span title={presence(m)}><IconWifi size={12} />{clockAt(m.onlineSince).replace('kl. ', '')}</span>}
          {up && <span title="Oppetid siden sidste genstart"><IconClock size={12} />{up}</span>}
          {m.ip && <span title={['IP-adresse', briefText(m.net)].filter(Boolean).join(' · ')} style={m.net?.warn ? { color: 'var(--fd-warn)' } : undefined}>
            {m.net && (m.net.kind === 'wifi' ? <IconWifi size={12} /> : m.net.kind === 'mobile' ? <IconDeviceMobile size={12} /> : <IconNetwork size={12} />)}{m.ip}</span>}
          {m.user && <span title="Logget ind"><IconUser size={12} />{m.user}</span>}
        </div>
      )}
      <RunningLine m={m} />
      <Group gap={5}>
        {m.power && <PowerCountdown m={m} now={now} />}
        {m.jobsActive > 0 && !m.running && <Badge size="xs" variant="light">{m.jobsActive} job i kø</Badge>}
        {m.upgrades > 0 && <Badge size="xs" variant="light" color="orange">{m.upgrades} opdatering{m.upgrades > 1 ? 'er' : ''}</Badge>}
        {m.alerts > 0 && <Badge size="xs" variant="light" color="red">{m.alerts} alarm{m.alerts > 1 ? 'er' : ''}</Badge>}
        {!!m.headless && <Badge size="xs" variant="light" color="grape" title="Browsere uden vindue (fx fra automatisering) – luk dem fra maskinens side under Strøm">{m.headless} hovedløs{m.headless > 1 ? 'e' : ''} browser{m.headless > 1 ? 'e' : ''}</Badge>}
        {m.online && !m.alerts && m.healthState === 'ok' && <Badge size="xs" variant="light" color="teal">Sund</Badge>}
        {!m.online && <Badge size="xs" variant="light" color="gray">Offline</Badge>}
      </Group>
      {isAdmin && (
        <div className="fd-card-actions" onClick={(e) => e.stopPropagation()}>
          <Button size="xs" variant="light" color="orange" leftSection={<IconDownload size={14} />} disabled={!m.online || !m.upgrades}
            loading={upgrade.isPending} onClick={askUpgrade}>Opdatér{m.upgrades ? ` (${m.upgrades})` : ''}</Button>
          <ConnectButton id={m.id} name={m.name} remote={m.online ? m.remote : null} web={m.webUrl} size="xs" disabledLook compact />
        </div>
      )}
    </Link>
  );
}

function Attention({ alerts }: { alerts: AlertRow[] }) {
  const { isAdmin } = useMe();
  const run = useRunFix();
  return (
    <Paper p="md">
      <Group justify="space-between" mb="sm"><Text fw={650}>Kræver opmærksomhed</Text><Anchor component={Link} to="/helbred" size="xs" fw={600}>Helbred</Anchor></Group>
      {alerts.length === 0 ? (
        <Group gap="sm"><ThemeIcon variant="light" color="teal" radius="xl"><IconCheck size={16} /></ThemeIcon><Text size="sm" c="dimmed">Alt ser fint ud.</Text></Group>
      ) : (
        <Stack gap="xs">
          {alerts.slice(0, 4).map((a) => {
            const c = a.severity === 'critical' ? 'bad' : 'warn';
            return (
              <Link key={a.id} to={`/maskiner/${a.machineId}/helbred`} style={{ textDecoration: 'none', color: 'inherit' }}>
                <Paper p="sm" style={{ background: `color-mix(in srgb, var(--fd-${c}) 9%, transparent)`, borderColor: `color-mix(in srgb, var(--fd-${c}) 28%, transparent)` }}>
                  <Group justify="space-between" wrap="nowrap" gap="xs">
                    <div style={{ minWidth: 0 }}><Text size="sm" fw={600} lineClamp={1}>{a.machine}: {a.title}</Text><Text size="xs" c="dimmed" lineClamp={1}>{shortWhy(a)}</Text></div>
                    {a.fix && isAdmin && !a.resolved
                      ? <Button size="compact-xs" variant="light" style={{ flex: 'none' }} loading={a.fixJob?.status === 'pending' || a.fixJob?.status === 'running'}
                          onClick={(e) => { e.preventDefault(); e.stopPropagation(); run.fixAlert(a); }}>{fixButton(a.fix)!.icon} {fixButton(a.fix)!.label}</Button>
                      : <Text size="xs" fw={600} c={c === 'bad' ? 'red' : 'orange'} style={{ whiteSpace: 'nowrap' }}>Se hvordan →</Text>}
                  </Group>
                </Paper>
              </Link>
            );
          })}
        </Stack>
      )}
    </Paper>
  );
}

function Activity({ d }: { d: Dashboard }) {
  const done = d.recentJobs.filter((j) => j.status !== 'running' && j.status !== 'pending').slice(0, 5);
  return (
    <Paper p="md">
      <Group justify="space-between" mb="sm"><Text fw={650}>Aktivitet</Text><Anchor component={Link} to="/jobs" size="xs" fw={600}>Alle jobs</Anchor></Group>
      <Stack gap="sm">
        {d.running.map((j) => (
          <Group key={j.id} gap="sm" wrap="nowrap" align="flex-start">
            <ThemeIcon variant="light" radius="md"><IconLoader2 size={16} /></ThemeIcon>
            <div style={{ flex: 1, minWidth: 0 }}><Text size="sm" fw={600} lineClamp={1}>{j.title}</Text><Text size="xs" c="dimmed">{j.machine}</Text><JobLive job={j} /></div>
          </Group>
        ))}
        {done.map((j) => (
          <Group key={j.id} gap="sm" wrap="nowrap">
            <ThemeIcon variant="light" radius="md" color={j.status === 'success' ? 'teal' : j.status === 'failed' ? 'red' : 'gray'}>
              {j.status === 'success' ? <IconCheck size={16} /> : j.status === 'failed' ? <IconX size={16} /> : <IconActivityHeartbeat size={16} />}
            </ThemeIcon>
            <div style={{ minWidth: 0 }}><Text size="sm" fw={600} lineClamp={1}>{j.title}</Text><Text size="xs" c="dimmed">{j.machine} · {ago(j.finished ?? j.created)}</Text></div>
          </Group>
        ))}
        {!d.running.length && !done.length && <Text size="sm" c="dimmed">Ingen aktivitet endnu.</Text>}
      </Stack>
    </Paper>
  );
}

function WeekChart({ week }: { week: Week }) {
  const max = Math.max(1, ...week.map((w) => w.ok + w.failed));
  const total = week.reduce((s, w) => s + w.ok, 0), failed = week.reduce((s, w) => s + w.failed, 0);
  const days = ['Søn', 'Man', 'Tir', 'Ons', 'Tor', 'Fre', 'Lør'];
  return (
    <Paper p="md">
      <Group justify="space-between" mb="sm"><Text fw={650}>Installationer denne uge</Text><Text size="xs" c="dimmed">{total} gennemført{failed ? ` · ${failed} fejlet` : ''}</Text></Group>
      <Group align="flex-end" gap={12} h={120} wrap="nowrap">
        {week.map((w, i) => (
          <Stack key={w.day} gap={6} align="center" justify="flex-end" style={{ flex: 1, height: '100%' }}>
            <div style={{ width: '100%', height: Math.max(4, (w.ok / max) * 92), borderRadius: '6px 6px 3px 3px',
              background: 'linear-gradient(180deg, var(--fd-logo-a), var(--fd-logo-b))', opacity: i === week.length - 1 ? 1 : 0.55 }} title={`${w.ok} ok, ${w.failed} fejlet`} />
            <Text size="xs" c="dimmed">{days[new Date(w.day).getDay()]}</Text>
          </Stack>
        ))}
      </Group>
    </Paper>
  );
}

function Servers({ machines }: { machines: MachineRow[] }) {
  const servers = machines.filter((m) => kindOf(m) === 'unraid');
  return (
    <Paper p="md">
      <Group justify="space-between" mb="sm"><Text fw={650}>Servere &amp; Docker</Text><Anchor component={Link} to="/servere" size="xs" fw={600}>Åbn</Anchor></Group>
      {servers.length === 0 ? <Text size="sm" c="dimmed">Ingen servere tilmeldt endnu.</Text> : (
        <Stack gap="sm">
          {servers.map((s) => (
            <Link key={s.id} to={`/maskiner/${s.id}/unraid`} style={{ textDecoration: 'none', color: 'inherit' }}>
              <Group gap="sm" wrap="nowrap">
                <ThemeIcon variant="light" color="orange" radius="md"><IconServer2 size={16} /></ThemeIcon>
                <div style={{ flex: 1 }}><Text size="sm" fw={600}>{s.name}</Text><Text size="xs" c="dimmed">{s.os} · {s.online ? 'online' : 'offline'}</Text></div>
                {s.upgrades > 0 && <Badge size="xs" color="orange" variant="light">{s.upgrades} klar</Badge>}
              </Group>
            </Link>
          ))}
        </Stack>
      )}
    </Paper>
  );
}

function Platforms({ machines }: { machines: MachineRow[] }) {
  const counts = (Object.keys(KIND) as Kind[]).map((k) => ({ k, n: machines.filter((m) => kindOf(m) === k).length })).filter((x) => x.n);
  const total = machines.length || 1;
  return (
    <Paper p="md">
      <Text fw={650} mb="sm">Platforme</Text>
      <Group gap="lg" wrap="nowrap">
        <RingProgress size={96} thickness={12} sections={counts.map((c) => ({ value: (c.n / total) * 100, color: KIND[c.k].color, tooltip: `${KIND[c.k].label}: ${c.n}` }))}
          label={<Text ta="center" fw={700} fz={20}>{machines.length}</Text>} />
        <Stack gap={4}>{counts.map((c) => <Group key={c.k} gap={8}><span className="fd-dot" style={{ color: KIND[c.k].color }} /><Text size="sm">{KIND[c.k].label} <Text span c="dimmed">{c.n}</Text></Text></Group>)}</Stack>
      </Group>
    </Paper>
  );
}
