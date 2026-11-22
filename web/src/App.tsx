import { NavLink as RouterLink, Route, Routes, useLocation, useNavigate } from 'react-router';
import {
  ActionIcon, AppShell, Avatar, Badge, Box, type BoxProps, Button, Group, Kbd, Menu, Modal, PasswordInput, ScrollArea, Stack, Text, Tooltip, UnstyledButton, useComputedColorScheme, useMantineColorScheme,
} from '@mantine/core';
import { useDisclosure, useHotkeys, useMediaQuery } from '@mantine/hooks';
import { Spotlight, spotlight, type SpotlightActionData } from '@mantine/spotlight';
import { modals } from '@mantine/modals';
import { useEffect, useRef, useState, type ComponentType, Suspense, lazy } from 'react';
import {
  IconActivityHeartbeat, IconAdjustments, IconApps, IconBox, IconCheck, IconClipboardList, IconCode, IconDeviceDesktop, IconDownload, IconLayoutDashboard,
  IconChevronDown, IconChevronsLeft, IconChevronsRight, IconDots, IconHeartRateMonitor, IconLayoutNavbar, IconLayoutSidebar, IconListCheck, IconLogout, IconMessages, IconMoon, IconNetwork, IconPalette, IconUsb, IconPlus, IconSearch,
  IconServer2, IconSettings, IconShieldLock, IconSun, IconSunMoon, IconKey, IconReportAnalytics,
  IconTerminal2, IconAlertOctagon, IconPrinter, IconPackage, IconRouter, IconRocket,
} from '@tabler/icons-react';
import { notifyErr, notifyOk, post, useAction, useApi, useLiveUpdates } from './api';
import { useMe } from './auth';
import { BackupAlertBanner } from './pages/Backup';
import type { AlertRow, DeviceType, IpadList, MachineRow } from './types';
import { ALWAYS_SHOWN, DEVICE_ORDER, DEVICES } from './design/devices';
import { Logo, LogoMark } from './design/Logo';
import { useSkin } from './design/skin';
import { TwoFactorSettings } from './design/twofa';
import { ActivityIndicator, PowerBanner } from './design/activity';
import { DashboardPage } from './pages/Dashboard';
import { EnrollModal, MachinesPage, MachinePage } from './pages/Machines';
import { HealthPage } from './pages/Health';
import { EventsPage } from './pages/Events';
import { PackagesPage } from './pages/Packages';
import { ProfilesPage, ProfilePage } from './pages/Profiles';
import { JobsPage, ScriptsPage } from './pages/Jobs';
import { SettingsPage, WinSetupPage } from './pages/Admin';
import { AuditPage } from './pages/AuditLog';
import { ReportsPage } from './pages/Reports';
import { DeployPage } from './pages/Deploy';
import { EspPage } from './pages/Esp';
import { UpdatesPage } from './pages/Updates';
import { DebloatPage } from './design/debloat';
import { ServersPage } from './pages/Servers';
import { MonitoringPage } from './pages/Monitoring';
import { PrintersPage } from './pages/Printers';
import { AppsPage } from './pages/Apps';
import { IpadPage, IpadsPage } from './pages/Ipads';
import { askUpdateAll, type UpdateMode } from './design/updateAll';
import { useAccessRequests } from './design/accessrequests';
import { waitingCount } from './design/accessRequestsText';
import { NAV_GROUPS, TABBAR, activeGroup, alertOf, badgeText, countOf, flatNav, groupCount, isActive, navFor, type Counts, type NavGroup, type NavPage } from './nav';
import { HelpdeskPage, useHelpdeskUnread } from './design/helpdesk';
import {
  NAV_TRANSITION_MS, browserStorage, canCollapse, navWidth, railBadge, railLabel, readCollapsed, shortcutLabel, shouldToggle, toggleLabel, writeCollapsed,
} from './design/navCollapse';

// Terminalen (xterm) hentes først, når siden åbnes
const TerminalPage = lazy(() => import('./pages/Terminal').then((m) => ({ default: m.TerminalPage })));

type Icon = ComponentType<{ size?: number; stroke?: number; className?: string }>;
type Nav = NavPage<Icon>;

/** Ikonerne til menuens sider (navnene står i nav.ts). */
const ICONS: Record<string, Icon> = {
  dashboard: IconLayoutDashboard, health: IconActivityHeartbeat, monitor: IconHeartRateMonitor, machines: IconDeviceDesktop, messages: IconMessages,
  terminal: IconTerminal2, events: IconAlertOctagon, updates: IconDownload, packages: IconApps, apps: IconPackage, debloat: IconShieldLock, printers: IconPrinter,
  servers: IconServer2, esp: IconUsb, deploy: IconNetwork, winsetup: IconBox, profiles: IconAdjustments, scripts: IconCode, jobs: IconListCheck,
  settings: IconSettings, reports: IconReportAnalytics, audit: IconClipboardList, network: IconRouter, rocket: IconRocket,
};
const withIcons = (groups: NavGroup[]) =>
  groups.map((g) => ({ ...g, Icon: ICONS[g.icon] ?? IconLayoutDashboard, items: g.items.map((e) => (e === 'devices' ? e : { ...e, icon: ICONS[e.icon] ?? IconLayoutDashboard })) }));
const GROUPS = withIcons(NAV_GROUPS);

/** Menuen for brugeren: enhedstyperne (iPads og Apple TV altid, iPhone/Android først når der er en enhed af typen) og kun administrator-sider for administratorer. */
function useNav(isAdmin: boolean, types: DeviceType[] | undefined) {
  const devices: Nav[] = DEVICE_ORDER.filter((t) => ALWAYS_SHOWN.includes(t) || types?.includes(t))
    .map((t): Nav => ({ to: DEVICES[t].path, label: DEVICES[t].plural, icon: DEVICES[t].icon, keywords: ['enhed', 'skærmtid'] }));
  return navFor(GROUPS, { isAdmin, devices });
}

/** ?embed=1 (Kontrolrum): uden egen top og sidemenu. Huskes i fanen, ?embed=0 slår det fra. */
function useEmbed() {
  const q = new URLSearchParams(window.location.search).get('embed');
  try {
    if (q === '1') sessionStorage.setItem('ws_embed', '1');
    if (q === '0') sessionStorage.removeItem('ws_embed');
    return sessionStorage.getItem('ws_embed') === '1';
  } catch { return q === '1'; }
}

function NavItem({ n, c, onClick, rail }: { n: Nav; c: Counts; onClick?: () => void; rail?: boolean }) {
  const loc = useLocation();
  const active = isActive(n.to, loc.pathname) || undefined;
  if (rail) {
    // smal skinne: kun ikonet, navnet i tooltip og aria-label, tal som en lille prik eller et lille tal på ikonet
    const b = railBadge(n, c);
    const label = railLabel(n.label, b);
    return (
      <Tooltip label={label} position="right" withArrow>
        <RouterLink to={n.to} className="fd-nav" data-rail data-active={active} onClick={onClick} aria-label={label}>
          <span className="fd-rail-icon">
            <n.icon size={20} stroke={1.7} />
            {b?.kind === 'number' && <span className="fd-rail-num" aria-hidden>{b.text}</span>}
            {b?.kind === 'dot' && <span className="fd-rail-dot" aria-hidden />}
          </span>
        </RouterLink>
      </Tooltip>
    );
  }
  const count = countOf(n, c);
  const alert = alertOf(n, c);
  return (
    <RouterLink to={n.to} className="fd-nav" data-active={active} onClick={onClick}>
      <n.icon size={18} stroke={1.7} />{n.label}
      {alert !== undefined && <span className="fd-count fd-count-alert" data-red title={`${alert} venter på godkendelse`}>{alert}</span>}
      {count !== undefined && <span className="fd-count" data-red={n.red || undefined}>{count}</span>}
    </RouterLink>
  );
}

/** Sidemenuen og telefonens Mere-ark: grupperne med en lille overskrift (den første gruppe, Oversigt, uden). Som skinne bliver overskriften en tynd streg. */
function SideNav({ groups, c, onClick, rail }: { groups: NavGroup<Nav>[]; c: Counts; onClick?: () => void; rail?: boolean }) {
  return (
    <Stack gap={2} component="nav" aria-label="Menu">
      {groups.map((g, i) => (
        <div key={g.key} role="group" aria-label={g.label} style={{ display: 'contents' }}>
          {i > 0 && (rail ? <div className="fd-group-line" aria-hidden /> : <div className="fd-group">{g.label}</div>)}
          {g.items.map((n) => <NavItem key={n.to} n={n} c={c} onClick={onClick} rail={rail} />)}
        </div>
      ))}
    </Stack>
  );
}

/** Knappen nederst i sidemenuen, der klapper den sammen til en ikon-skinne og folder den ud igen (også Ctrl/⌘ B). */
function CollapseToggle({ collapsed, onToggle }: { collapsed: boolean; onToggle: () => void }) {
  const label = toggleLabel(collapsed);
  const keys = shortcutLabel(typeof navigator === 'undefined' ? undefined : navigator.platform);
  const Chevron = collapsed ? IconChevronsRight : IconChevronsLeft;
  return (
    <Tooltip label={collapsed ? `${label} (${keys})` : `Genvej: ${keys}`} position="right" withArrow>
      <UnstyledButton className="fd-nav fd-nav-toggle" data-rail={collapsed || undefined} onClick={onToggle}
        aria-label={label} aria-expanded={!collapsed} aria-controls="fd-sidenav" aria-keyshortcuts={keys === '⌘B' ? 'Meta+B' : 'Control+B'}>
        <Chevron size={18} stroke={1.7} />
        {!collapsed && <span>{label}</span>}
      </UnstyledButton>
    </Tooltip>
  );
}

const CountBadge = ({ n, red }: { n: number; red?: boolean }) => (
  <Badge size="xs" variant={red ? 'filled' : 'light'} color={red ? 'red' : undefined} circle={n < 10} style={{ overflow: 'visible' }}>{badgeText(n)}</Badge>
);

/** Menuen i toppen: én rullemenu pr. gruppe (en gruppe med kun én side bliver et almindeligt link). */
function TopNav({ groups, c }: { groups: (NavGroup<Nav> & { Icon: Icon })[]; c: Counts }) {
  const loc = useLocation();
  const current = activeGroup(groups, loc.pathname);
  return (
    <Group gap={2} visibleFrom="sm" wrap="nowrap" component="nav" aria-label="Menu">
      {groups.map((g) => {
        const gc = groupCount(g, c);
        const badge = gc ? <CountBadge n={gc.n} red={gc.red} /> : undefined;
        // smalle skærme: kun ikonet (navnet står i title/aria-label og øverst i rullemenuen)
        const label = <><g.Icon size={18} stroke={1.7} className="fd-top-icon" /><span className="fd-top-label">{g.short}</span></>;
        if (g.items.length === 1) {
          const n = g.items[0];
          return <Button key={g.key} component={RouterLink} to={n.to} variant="subtle" color="gray" size="compact-md" className="fd-top"
            data-active={isActive(n.to, loc.pathname) || undefined} rightSection={badge} aria-label={g.label} title={g.label}>{label}</Button>;
        }
        return (
          <Menu key={g.key} position="bottom-start" withinPortal width={250}>
            <Menu.Target>
              <Button variant="subtle" color="gray" size="compact-md" className="fd-top" data-active={current?.key === g.key || undefined}
                aria-label={g.label} title={g.label} rightSection={<Group gap={4} wrap="nowrap">{badge}<IconChevronDown size={14} className="fd-top-chev" /></Group>}>{label}</Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label className={g.short === g.label ? 'fd-top-same' : undefined}>{g.label}</Menu.Label>
              {g.items.map((n) => {
                const cnt = n.count === 'machines' ? alertOf(n, c) : countOf(n, c);
                const active = isActive(n.to, loc.pathname);
                return (
                  <Menu.Item key={n.to} component={RouterLink} to={n.to} leftSection={<n.icon size={16} />} data-active={active || undefined}
                    fw={active ? 600 : undefined}
                    rightSection={cnt !== undefined ? <CountBadge n={cnt} red={n.red || n.count === 'machines'} /> : undefined}>{n.label}</Menu.Item>
                );
              })}
            </Menu.Dropdown>
          </Menu>
        );
      })}
    </Group>
  );
}

/** Søgefeltet (Ctrl K). compact = smallere udgave til menu i toppen; phone = fylder rækken på telefonen. */
function SearchField({ variant = 'wide', ...box }: { variant?: 'wide' | 'compact' | 'phone' } & BoxProps) {
  return (
    <UnstyledButton {...box} onClick={() => spotlight.open()} className="fd-search" data-variant={variant} aria-label="Søg (Ctrl K)">
      <IconSearch size={15} style={{ flexShrink: 0 }} />
      <span className="fd-search-text">{variant === 'wide' ? 'Søg efter maskiner, programmer, sider…' : variant === 'compact' ? 'Søg…' : 'Søg'}</span>
      {variant !== 'phone' && <Kbd size="xs" ml="auto">Ctrl K</Kbd>}
    </UnstyledButton>
  );
}

/** Stil (Command/Aurora) og tema (lyst/mørkt/system) – vælges fra topbjælken og huskes i browseren. */
function LookMenu() {
  const { skin, setSkin, nav, setNav } = useSkin();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const computed = useComputedColorScheme('light');
  const check = (on: boolean) => (on ? <IconCheck size={14} /> : <span style={{ width: 14 }} />);
  return (
    <Menu position="bottom-end" width={230} withinPortal>
      <Menu.Target>
        <ActionIcon variant="default" size="lg" aria-label="Udseende">{computed === 'dark' ? <IconMoon size={17} /> : <IconSun size={17} />}</ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Stil</Menu.Label>
        <Menu.Item leftSection={check(skin === 'command')} onClick={() => setSkin('command')}>Command <Text span size="xs" c="dimmed">– skarp og rolig</Text></Menu.Item>
        <Menu.Item leftSection={check(skin === 'aurora')} onClick={() => setSkin('aurora')}>Aurora <Text span size="xs" c="dimmed">– farver og glas</Text></Menu.Item>
        <Menu.Divider />
        <Menu.Label>Tema</Menu.Label>
        <Menu.Item leftSection={<IconSun size={14} />} rightSection={check(colorScheme === 'light')} onClick={() => setColorScheme('light')}>Lyst</Menu.Item>
        <Menu.Item leftSection={<IconMoon size={14} />} rightSection={check(colorScheme === 'dark')} onClick={() => setColorScheme('dark')}>Mørkt</Menu.Item>
        <Menu.Item leftSection={<IconSunMoon size={14} />} rightSection={check(colorScheme === 'auto')} onClick={() => setColorScheme('auto')}>Som systemet</Menu.Item>
        <Menu.Divider />
        <Menu.Label>Menu</Menu.Label>
        <Menu.Item leftSection={<IconLayoutSidebar size={14} />} rightSection={check(nav === 'side')} onClick={() => setNav('side')}>I siden</Menu.Item>
        <Menu.Item leftSection={<IconLayoutNavbar size={14} />} rightSection={check(nav === 'top')} onClick={() => setNav('top')}>I toppen</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

export function App() {
  const { me } = useMe();
  return me.role === 'parent' ? <FamilyApp /> : <StaffApp />;
}

/** Forælder: kun iPad'erne (barnets styring), med en enkel top: navn, "Min konto" (adgangskode og totrinsbekræftelse) og "Log ud". Bygget til telefonen. */
function FamilyApp() {
  const { me, logout } = useMe();
  const nav = useNavigate();
  const [pw, setPw] = useState(false);
  useLiveUpdates(true);
  return (
    <AppShell header={{ height: 60 }} padding={{ base: 'sm', sm: 'lg' }} className="fd-shell fd-family">
      <AppShell.Header>
        <Group h="100%" px={{ base: 'sm', sm: 'lg' }} gap="xs" wrap="nowrap" justify="space-between">
          <UnstyledButton onClick={() => nav('/')} style={{ minWidth: 0 }} aria-label="Til forsiden">
            <Group gap={8} wrap="nowrap"><LogoMark size={28} /><Text fw={650} truncate>Hej {me.username}</Text></Group>
          </UnstyledButton>
          <Group gap={6} wrap="nowrap">
            <Button variant="default" size="sm" px="xs" leftSection={<IconKey size={16} />} onClick={() => setPw(true)} aria-label="Min konto">
              <Text span size="sm" visibleFrom="sm">Min konto</Text><Text span size="sm" hiddenFrom="sm">Konto</Text>
            </Button>
            <Button variant="light" color="red" size="sm" px="xs" leftSection={<IconLogout size={16} />} onClick={logout}>Log ud</Button>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Routes>
          {DEVICE_ORDER.map((t) => [
            <Route key={t} path={DEVICES[t].path} element={<IpadsPage type={t} />} />,
            <Route key={`${t}-id`} path={`${DEVICES[t].path}/:id/:tab?`} element={<IpadPage />} />,
          ])}
          <Route path="*" element={<IpadsPage />} />
        </Routes>
      </AppShell.Main>
      <AccountModal opened={pw} onClose={() => setPw(false)} />
    </AppShell>
  );
}

/** Forælderens "Min konto": adgangskode og totrinsbekræftelse. */
function AccountModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { me, refresh } = useMe();
  const [f, setF] = useState({ current: '', next: '' });
  return (
    <Modal opened={opened} onClose={onClose} title="Min konto" size="lg">
      <Stack>
        <Text fw={600}>Totrinsbekræftelse</Text>
        <TwoFactorSettings onChanged={refresh} />
        <Text fw={600} mt="md">Skift adgangskode</Text>
        <PasswordInput label="Nuværende" value={f.current} onChange={(e) => setF({ ...f, current: e.currentTarget.value })} autoComplete="current-password" />
        <PasswordInput label="Ny (min. 8 tegn)" value={f.next} onChange={(e) => setF({ ...f, next: e.currentTarget.value })} autoComplete="new-password" />
        <Group justify="flex-end"><Button onClick={async () => {
          try { await post(`/api/users/${me.id}/password`, { current: f.current, password: f.next }); notifyOk('Adgangskoden er skiftet'); setF({ current: '', next: '' }); onClose(); } catch (e) { notifyErr(e); }
        }}>Skift</Button></Group>
      </Stack>
    </Modal>
  );
}

function StaffApp() {
  const embed = useEmbed();
  const [opened, { toggle, close }] = useDisclosure();
  const [enroll, { open: openEnroll, close: closeEnroll }] = useDisclosure();
  const { me, logout, isAdmin } = useMe();
  // Opsætningsguidens "Tilføj din første maskine" lander her med ?tilfoej=1
  useEffect(() => {
    const q = new URLSearchParams(window.location.search);
    if (!q.has('tilfoej')) return;
    q.delete('tilfoej');
    window.history.replaceState(null, '', window.location.pathname + (q.size ? `?${q}` : ''));
    if (isAdmin) openEnroll();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const { nav: navPos, setNav } = useSkin();
  const top = navPos === 'top';
  // indlejret (Kontrolrum): "I siden" giver også en sidemenu – på smalle skærme bruges fanebjælken
  const wide = useMediaQuery('(min-width: 48em)') ?? true;
  const embedSide = embed && !top && wide;
  // sammenklappet sidemenu (smal ikon-skinne): kun menu i siden på bred skærm – mobilens skuffe og menuen i toppen er uændrede
  const railable = canCollapse({ navPos, wide, navbarShown: !embed || embedSide });
  const [collapsed, setCollapsed] = useState(() => readCollapsed(browserStorage()));
  const rail = railable && collapsed;
  const toggleRail = () => setCollapsed((v) => !v);
  useEffect(() => writeCollapsed(browserStorage(), collapsed), [collapsed]);
  useEffect(() => {
    if (!railable) return;
    const onKey = (e: KeyboardEvent) => {
      if (!shouldToggle(e, e.target instanceof HTMLElement ? e.target : null)) return;
      e.preventDefault();
      setCollapsed((v) => !v);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [railable]);
  const loc = useLocation();
  const nav = useNavigate();
  useLiveUpdates(true);
  useHotkeys([['mod+K', () => spotlight.open()]]);
  const { data: alerts = [] } = useApi<AlertRow[]>(['alerts', 'open'], '/api/alerts', { refetchInterval: 60_000 });
  const { data: machines = [] } = useApi<MachineRow[]>(['machines'], '/api/machines', { refetchInterval: 10_000 });
  const { data: checks = [] } = useApi<{ state: string }[]>(['checks'], '/api/checks', { refetchInterval: 30_000 });
  const { data: devices } = useApi<IpadList>(['ipads'], isAdmin ? '/api/ipads' : null);
  const helpdeskUnread = useHelpdeskUnread(isAdmin);
  const { data: accessRequests } = useAccessRequests(isAdmin);
  const groups = useNav(isAdmin, devices?.types);
  const pages = flatNav(groups);
  const tabs = TABBAR.map((to) => pages.find((p) => p.to === to)).filter((p): p is Nav => !!p);
  const counts: Counts = {
    machines: machines.length,
    upgrades: machines.reduce((s, m) => s + m.upgrades, 0),
    alerts: alerts.filter((a) => a.severity !== 'info' && !a.acknowledged).length,
    jobs: machines.reduce((s, m) => s + m.jobsActive, 0),
    servers: machines.filter((m) => /unraid/i.test(m.os ?? '')).length,
    checksDown: checks.filter((c) => c.state === 'down').length,
    messages: helpdeskUnread,
    access: waitingCount(accessRequests),
  };
  const upgradeAll = useAction((b: { ids: number[]; mode: UpdateMode }) => post<{ jobsCreated: number }>('/api/machines/actions', { machineIds: b.ids, action: 'update_all', params: { mode: b.mode } }),
    (r) => `Opdatér alt er sat i gang (${r.jobsCreated} job)`);
  const askUpgradeAll = () => {
    const ts = machines.filter((m) => m.online && m.upgrades > 0);
    if (!ts.length) { modals.openConfirmModal({ title: 'Opdatér alt', children: <Text size="sm">Ingen maskiner, der er online, har opdateringer lige nu.</Text>, labels: { confirm: 'OK', cancel: 'Luk' } }); return; }
    askUpdateAll(ts, (mode) => upgradeAll.mutate({ ids: ts.map((m) => m.id), mode }), `Opdatér alt på ${ts.length === 1 ? ts[0].name : `${ts.length} maskiner`}?`);
  };

  const actions: SpotlightActionData[] = [
    ...groups.flatMap((g) => g.items.map((n) => ({ id: `nav${n.to}`, label: n.label, description: `Gå til siden · ${g.label}`, keywords: [g.label, ...(n.keywords ?? [])],
      onClick: () => nav(n.to), leftSection: <n.icon size={18} /> }))),
    ...machines.map((m) => ({
      id: `m${m.id}`, label: m.name, description: [m.os, m.profile, m.online ? 'online' : 'offline'].filter(Boolean).join(' · '),
      keywords: [m.hostname, m.ip ?? '', m.user ?? '', ...m.tags], onClick: () => nav(`/maskiner/${m.id}`), leftSection: <IconDeviceDesktop size={18} />,
    })),
    ...(isAdmin ? [{ id: 'add', label: 'Tilføj maskine', description: 'Installér agenten på en ny computer', onClick: openEnroll, leftSection: <IconPlus size={18} /> }] : []),
  ];

  // indlejret fanebjælke (Kontrolrum): hold den aktive fane synlig på smalle skærme
  const navRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const center = () => {
      const vp = navRef.current; const btn = vp?.querySelector<HTMLElement>('[data-active]');
      if (!vp || !btn) return;
      const v = vp.getBoundingClientRect(), b = btn.getBoundingClientRect();
      vp.scrollLeft = Math.max(0, vp.scrollLeft + b.left - v.left - (v.width - b.width) / 2);
    };
    const raf = requestAnimationFrame(center); const t = setTimeout(center, 400);
    document.fonts?.ready.then(center).catch(() => {});
    return () => { cancelAnimationFrame(raf); clearTimeout(t); };
  }, [embed, loc.pathname]);

  return (
    <AppShell header={embed ? undefined : { height: 60 }}
      navbar={embed && !embedSide ? undefined : { width: navWidth({ rail, embed: embedSide }), breakpoint: 'sm', collapsed: { mobile: !embedSide && !opened, desktop: !embed && top } }}
      transitionDuration={NAV_TRANSITION_MS} padding={embed ? 'md' : 'lg'} className={embed ? undefined : 'fd-shell'}>
      {!embed && <AppShell.Header>
        <Group h="100%" px="lg" gap="sm" wrap="nowrap">
          <UnstyledButton hiddenFrom="sm" onClick={() => nav('/')}><LogoMark size={26} /></UnstyledButton>
          {top && <>
            <UnstyledButton visibleFrom="lg" onClick={() => nav('/')} mr="xs" aria-label="Til forsiden"><Logo size={26} /></UnstyledButton>
            <UnstyledButton visibleFrom="sm" hiddenFrom="lg" onClick={() => nav('/')} aria-label="Til forsiden"><LogoMark size={26} /></UnstyledButton>
            <TopNav groups={groups} c={counts} />
          </>}
          {!top && <SearchField visibleFrom="sm" style={{ flex: '0 1 380px' }} />}
          <SearchField variant="phone" hiddenFrom="sm" style={{ flex: 1 }} />
          <Box visibleFrom="sm" style={{ flex: 1 }} />
          {top && <SearchField variant="compact" visibleFrom="lg" style={{ width: 210, flexShrink: 0 }} />}
          {top && <ActionIcon visibleFrom="sm" hiddenFrom="lg" variant="default" size="lg" onClick={() => spotlight.open()} aria-label="Søg (Ctrl K)" title="Søg (Ctrl K)"><IconSearch size={17} /></ActionIcon>}
          <ActivityIndicator machines={machines} />
          {isAdmin && !top && <Button variant="default" leftSection={<IconDownload size={16} />} onClick={askUpgradeAll} visibleFrom="md">Opdatér alt</Button>}
          {isAdmin && <Button className="fd-primary" leftSection={<IconPlus size={16} />} onClick={openEnroll} visibleFrom={top ? 'xl' : 'sm'} style={{ flexShrink: 0 }}>Tilføj maskine</Button>}
          {isAdmin && top && <ActionIcon className="fd-primary" size="lg" onClick={openEnroll} visibleFrom="sm" hiddenFrom="xl" aria-label="Tilføj maskine" title="Tilføj maskine"><IconPlus size={17} /></ActionIcon>}
          <LookMenu />
          <Menu position="bottom-end" withinPortal>
            <Menu.Target><Avatar radius="xl" size={34} color="initials" name={me.username} style={{ cursor: 'pointer' }} /></Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>{me.username} · {me.role === 'admin' ? 'administrator' : me.role === 'parent' ? 'forælder' : 'læser'}</Menu.Label>
              <Menu.Item leftSection={<IconKey size={14} />} onClick={() => nav('/indstillinger?fane=konto')}>Min konto</Menu.Item>
              <Menu.Item leftSection={<IconPalette size={14} />} onClick={() => nav('/indstillinger')}>Indstillinger</Menu.Item>
              <Menu.Item leftSection={<IconLogout size={14} />} color="red" onClick={logout}>Log ud</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </Group>
      </AppShell.Header>}
      {(!embed || embedSide) && <AppShell.Navbar p={rail ? 10 : 'sm'} id="fd-sidenav" className="fd-navbar" data-rail={rail || undefined}>
        {!embed && <AppShell.Section px={rail ? 0 : 8} pt={4} pb={14} visibleFrom="sm" className="fd-nav-logo">
          <UnstyledButton onClick={() => nav('/')} aria-label="Til forsiden">{rail ? <LogoMark size={30} /> : <Logo size={30} />}</UnstyledButton>
        </AppShell.Section>}
        {embedSide && <AppShell.Section pb={8} className="fd-nav-logo">
          {rail
            ? <Tooltip label="Søg (Ctrl K)" position="right" withArrow><ActionIcon variant="default" size="lg" onClick={() => spotlight.open()} aria-label="Søg (Ctrl K)"><IconSearch size={16} /></ActionIcon></Tooltip>
            : <SearchField variant="compact" style={{ width: '100%' }} />}
        </AppShell.Section>}
        <AppShell.Section grow component={ScrollArea} type={rail ? 'scroll' : undefined} scrollbarSize={rail ? 4 : undefined}>
          <SideNav groups={groups} c={counts} onClick={close} rail={rail} />
        </AppShell.Section>
        <AppShell.Section className="fd-nav-foot">
          {embedSide && (rail
            ? <Tooltip label="Menu i toppen" position="right" withArrow><UnstyledButton className="fd-nav" data-rail onClick={() => setNav('top')} aria-label="Menu i toppen"><IconLayoutNavbar size={18} stroke={1.7} /></UnstyledButton></Tooltip>
            : <Button variant="subtle" color="gray" size="compact-xs" ml={4} leftSection={<IconLayoutNavbar size={14} />} onClick={() => setNav('top')}>Menu i toppen</Button>)}
          {!rail && <Text size="xs" c="dimmed" p="xs">Fleetdeck {me.version}</Text>}
          {railable && <CollapseToggle collapsed={rail} onToggle={toggleRail} />}
        </AppShell.Section>
      </AppShell.Navbar>}
      <AppShell.Main>
        <PowerBanner machines={machines} isAdmin={isAdmin} />
        {embed && !embedSide && (
          <ScrollArea type="never" mb="sm" viewportRef={navRef} className="ws-embed-nav">
            <Group gap={4} wrap="nowrap" w="max-content">
              <Button size="compact-sm" variant="default" style={{ flexShrink: 0 }} leftSection={<IconSearch size={15} stroke={1.6} />} onClick={() => spotlight.open()}
                aria-label="Søg (Ctrl K)" title="Søg (Ctrl K)" mr={4}>Søg</Button>
              {groups.map((g, i) => [
                i > 0 && <span key={`${g.key}-sep`} className="fd-embed-sep" aria-hidden />,
                ...g.items.map((n) => {
                  const active = isActive(n.to, loc.pathname);
                  const cnt = n.count === 'machines' ? undefined : countOf(n, counts);
                  return (
                    <Button key={n.to} component={RouterLink} to={n.to} size="compact-sm" variant={active ? 'light' : 'subtle'} color={active ? undefined : 'gray'}
                      data-active={active || undefined} style={{ flexShrink: 0 }} leftSection={<n.icon size={15} stroke={1.6} />} title={g.label}
                      rightSection={cnt !== undefined ? <CountBadge n={cnt} red={n.red} /> : undefined}>{n.label}</Button>
                  );
                }),
              ])}
              {wide && <ActionIcon variant="subtle" color="gray" ml={6} aria-label="Menu i siden" title="Menu i siden" onClick={() => setNav('side')}><IconLayoutSidebar size={16} stroke={1.6} /></ActionIcon>}
            </Group>
          </ScrollArea>
        )}
        {isAdmin && <BackupAlertBanner />}
        <Routes>
          <Route path="/" element={<DashboardPage onAdd={openEnroll} />} />
          <Route path="/maskiner" element={<MachinesPage />} />
          <Route path="/maskiner/:id/:tab?" element={<MachinePage />} />
          <Route path="/opdateringer" element={<UpdatesPage />} />
          <Route path="/helbred" element={<HealthPage />} />
          <Route path="/haendelser" element={<EventsPage />} />
          <Route path="/pakker" element={<PackagesPage />} />
          <Route path="/profiler" element={<ProfilesPage />} />
          <Route path="/profiler/:id" element={<ProfilePage />} />
          <Route path="/printere" element={<PrintersPage />} />
          <Route path="/apps" element={<AppsPage />} />
          <Route path="/servere" element={<ServersPage />} />
          <Route path="/udrulning" element={<DeployPage />} />
          <Route path="/esp" element={<EspPage />} />
          <Route path="/overvaagning" element={<MonitoringPage />} />
          {DEVICE_ORDER.map((t) => [
            <Route key={t} path={DEVICES[t].path} element={<IpadsPage type={t} />} />,
            <Route key={`${t}-id`} path={`${DEVICES[t].path}/:id/:tab?`} element={<IpadPage />} />,
          ])}
          <Route path="/terminal" element={<Suspense fallback={null}><TerminalPage /></Suspense>} />
          <Route path="/beskeder/:id?" element={<HelpdeskPage />} />
          <Route path="/jobs" element={<JobsPage />} />
          <Route path="/scripts" element={<ScriptsPage />} />
          <Route path="/winsetup" element={<WinSetupPage />} />
          <Route path="/debloat" element={<DebloatPage />} />
          <Route path="/log" element={<AuditPage />} />
          <Route path="/rapporter" element={<ReportsPage />} />
          <Route path="/indstillinger" element={<SettingsPage />} />
          <Route path="*" element={<DashboardPage onAdd={openEnroll} />} />
        </Routes>
      </AppShell.Main>
      {!embed && (
        <nav className="fd-tabbar">
          {tabs.map((n) => {
            // Maskiner: kun det røde tal for anmodninger om adgang (ikke antallet af maskiner)
            const cnt = n.to === '/maskiner' ? alertOf(n, counts) : countOf(n, counts);
            return (
              <RouterLink key={n.to} to={n.to} data-active={isActive(n.to, loc.pathname) || undefined} onClick={close}>
                <span style={{ position: 'relative' }}><n.icon size={22} stroke={1.7} />{cnt ? <b data-red={n.red || n.to === '/maskiner' || undefined}>{badgeText(cnt)}</b> : null}</span>
                {n.label}
              </RouterLink>
            );
          })}
          <button type="button" onClick={toggle} data-active={opened || undefined}><IconDots size={22} stroke={1.7} />Mere</button>
        </nav>
      )}
      <EnrollModal opened={enroll} onClose={closeEnroll} />
      <Spotlight actions={actions} nothingFound="Intet fundet" highlightQuery limit={12}
        searchProps={{ leftSection: <IconSearch size={18} />, placeholder: 'Søg efter maskiner og sider…' }} shortcut={null} />
    </AppShell>
  );
}
