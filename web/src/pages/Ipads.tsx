import { useEffect, useMemo, useState } from 'react';
import { Alert, Anchor, Autocomplete, Badge, Button, Group, Menu, Modal, NumberInput, Paper, Progress, SegmentedControl, Select, SimpleGrid, Stack, Switch, Tabs, TagsInput, Text, TextInput, Title, UnstyledButton } from '@mantine/core';
import { BarChart } from '@mantine/charts';
import { modals } from '@mantine/modals';
import { useQueryClient } from '@tanstack/react-query';
import { Navigate, useLocation, useNavigate, useParams } from 'react-router';
import { IconBrandYoutube, IconChevronDown, IconMoon, IconPlayerPause, IconPlus, IconQrcode, IconSchool, IconWorld } from '@tabler/icons-react';
import { del, notifyErr, notifyOk, post, put, useAction, useApi } from '../api';
import { ago } from '../format';
import { CopyButton } from '../design/copy';
import { defaultBedtime, Qr, SLOTS, WeekGrid } from '../design/family';
import { Empty, Loading, PageHeader } from '../components';
import { AppsTab, LockedSection } from './IpadStatic';
import { UsbBanner, UsbSection } from './IpadUsb';
import { NoWipeSection } from './IpadNoWipe';
import { MdmIpadTab } from './IpadMdm';
import { InstalledAppsTab } from './IpadApps';
import { ReleaseSection } from './IpadRelease';
import { ControlLine, SetupChecklist, WatchAlert, WatchPanel } from './IpadGuide';
import { APPLE_TV_MODEL_CHOICES, DEVICE_ORDER, DEVICES, deviceSubtitle, devMeta, kindLabel, modelName, pickServices, QUICK_BY_TYPE, QUICK_GROUPS_BY_TYPE, type DeviceMeta } from '../design/devices';
import type { AgService, DeviceType, IpadActivity, IpadDetail, IpadList, IpadRow, KidsMode, YtMode } from '../types';

const clock = (ts: string | null | undefined) => (ts ? new Date(ts).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }) : '');
const dayClock = (ts: string | null | undefined) => {
  if (!ts) return '';
  const d = new Date(ts); const today = new Date();
  const tomorrow = new Date(); tomorrow.setDate(today.getDate() + 1);
  const pre = d.toDateString() === today.toDateString() ? '' : d.toDateString() === tomorrow.toDateString() ? 'i morgen ' : d.toLocaleDateString('da-DK', { weekday: 'short' }) + ' ';
  return pre + clock(ts);
};
const mins = (m: number) => (m >= 60 ? `${Math.floor(m / 60)} t ${m % 60 ? `${m % 60} min.` : ''}`.trim() : `${m} min.`);

const MODE: Record<string, { label: string; cls: string; icon: typeof IconWorld }> = {
  free: { label: 'Internet tilladt', cls: 'ok', icon: IconWorld },
  school: { label: 'Skoletid', cls: 'school', icon: IconSchool },
  bed: { label: 'Sengetid', cls: 'bed', icon: IconMoon },
  pause: { label: 'Internettet er sat på pause', cls: 'pause', icon: IconPlayerPause },
};
const YT: Record<string, string> = {
  open: 'Åben', moderate: 'Begrænset', strict: 'Streng', kids: 'Spærret', blocked: 'Spærret', window: 'Uden for YouTube-tid', quota: 'Dagens tid er brugt', school: 'Spærret i skoletid',
};
const KIDS: Record<string, string> = {
  allowed: 'Tilladt', blocked: 'Spærret', window: 'Uden for tiden', quota: 'Dagens tid er brugt', school: 'Spærret i skoletid',
};

function stateText(r: IpadRow) {
  const m = MODE[r.mode];
  let title = m.label; let sub = '';
  if (r.mode === 'pause') sub = r.until ? `Til kl. ${dayClock(r.until)}` : '';
  else if (r.mode === 'bed') {
    title = `Sengetid${r.nextChange ? ` til ${dayClock(r.nextChange)}` : ''}`;
    sub = devMeta(r.deviceType).ios ? 'Kun Apples tjenester (beskeder, FaceTime) og tilladte sider' : 'Alt er spærret undtagen tilladte sider';
  }
  else if (r.mode === 'school') { title = `Skoletid${r.nextChange ? ` til ${dayClock(r.nextChange)}` : ''}`; sub = 'Spil, sociale medier og video er spærret'; }
  else if (r.extra) { title = 'Ekstra tid'; sub = `Til kl. ${dayClock(r.until)}`; }
  else if (r.nextChange && r.nextMode !== 'free') sub = `${r.nextMode === 'bed' ? 'Sengetid' : 'Skoletid'} starter ${dayClock(r.nextChange)}`;
  return { title, sub, cls: r.extra ? 'ok' : m.cls, Icon: m.icon };
}

/** Fanerne pr. enhedstype. Forældre ser kun det, der styrer barnet: Regler, YouTube, Apps (kun visning) og Aktivitet. */
function tabsFor(t: DeviceMeta, admin: boolean): string[] {
  if (!t.ios) return admin ? ['regler', 'youtube', 'aktivitet', 'opsaetning'] : ['regler', 'youtube', 'aktivitet'];
  return admin ? ['regler', 'youtube', 'installeret', 'apps', 'aktivitet', 'apple', 'opsaetning'] : ['regler', 'youtube', 'installeret', 'aktivitet', 'tjekliste'];
}

/** Knapperne Pause / Ekstra tid / YouTube-bonus – bruges både på kortet og på detaljesiden. */
function QuickActions({ r, can, compact, big }: { r: IpadRow; can: string[]; compact?: boolean; big?: boolean }) {
  const act = useAction((a: { url: string; minutes: number }) => post<{ msg: string }>(`/api/ipads/${r.id}/${a.url}`, { minutes: a.minutes }), (x) => x.msg);
  if (!can.includes('pause')) return null;
  const go = (url: string, minutes: number) => act.mutate({ url, minutes });
  // forældre (ofte på telefonen): store knapper, der er nemme at ramme
  const size = big ? 'md' : compact ? 'xs' : 'sm';
  return (
    <Group gap={big ? 8 : 6} wrap="wrap" className={big ? 'fd-big-actions' : undefined}>
      {r.mode === 'pause'
        ? <Button size={size} color="green" onClick={() => go('pause', 0)}>Slå internettet til igen</Button>
        : <Menu withinPortal position="bottom-start">
            <Menu.Target><Button size={size} color="red" rightSection={<IconChevronDown size={14} />}>Pause internet</Button></Menu.Target>
            <Menu.Dropdown>
              {[15, 30, 60, 120].map((m) => <Menu.Item key={m} onClick={() => go('pause', m)}>{mins(m)}</Menu.Item>)}
              <Menu.Item onClick={() => go('pause', -1)}>Til i morgen kl. 07</Menu.Item>
            </Menu.Dropdown>
          </Menu>}
      {(r.mode === 'bed' || r.mode === 'school' || r.extra) && (
        <Menu withinPortal position="bottom-start">
          <Menu.Target><Button size={size} variant="default" rightSection={<IconChevronDown size={14} />}>Ekstra tid</Button></Menu.Target>
          <Menu.Dropdown>
            {[15, 30, 60].map((m) => <Menu.Item key={m} onClick={() => go('extra', m)}>+{mins(m)}</Menu.Item>)}
            {r.extra && <Menu.Item color="red" onClick={() => go('extra', 0)}>Stop ekstra tid</Menu.Item>}
          </Menu.Dropdown>
        </Menu>)}
      {r.ytLimit > 0 && r.mode === 'free' && (
        <Button size={size} variant="default" leftSection={<IconBrandYoutube size={15} />} onClick={() => go('youtube-bonus', 15)}>+15 min. YouTube</Button>)}
    </Group>
  );
}


/** Slå apps og hele grupper til/fra med ét klik (rød = spærret). Virker med det samme – også uden for hjemmet. */
function QuickBlocks({ r, can, big }: { r: IpadRow; can: string[]; big?: boolean }) {
  const qc = useQueryClient();
  const { data: services = [] } = useApi<AgService[]>(['ag-services'], '/api/ipads/services');
  if (!can.includes('rules') || services.length === 0) return null;
  const blocked = new Set(r.blockedServices ?? []);
  const name = (id: string) => services.find((s) => s.id === id)?.name ?? id;
  const save = async (next: Set<string>, msg: string) => {
    try { await put(`/api/ipads/${r.id}`, { blockedServices: [...next] }); notifyOk(msg); qc.invalidateQueries(); } catch (e) { notifyErr(e); }
  };
  const toggle = (id: string) => { const n = new Set(blocked); if (n.has(id)) n.delete(id); else n.add(id); save(n, `${name(id)} er ${n.has(id) ? 'spærret' : 'tilladt igen'} på ${r.name}`); };
  const groupIds = (g: string) => services.filter((s) => s.group === g).map((s) => s.id);
  const groupOn = (g: string) => { const ids = groupIds(g); return ids.length > 0 && ids.every((i) => blocked.has(i)); };
  const toggleGroup = (g: string, label: string) => {
    const n = new Set(blocked); const on = groupOn(g);
    for (const i of groupIds(g)) { if (on) n.delete(i); else n.add(i); }
    save(n, `${label} er ${on ? 'tilladt igen' : 'spærret'} på ${r.name}`);
  };
  const serviceOff = blocked.has('youtube');
  const ytOn = !serviceOff && r.ytMode !== 'blocked' && r.ytMode !== 'kids';
  const kidsOn = !serviceOff && r.kidsMode === 'allowed';
  const setApp = async (app: 'youtube' | 'kids', allowed: boolean) => {
    try { const x = await post<{ msg: string }>(`/api/ipads/${r.id}/youtube-app`, { app, allowed }); notifyOk(x.msg); qc.invalidateQueries(); } catch (e) { notifyErr(e); }
  };
  const sz = big ? 'sm' : 'compact-xs';
  return (
    <Group gap={big ? 8 : 6} mb="sm" wrap="wrap">
      <Button size={sz} radius="xl" variant={ytOn ? 'default' : 'filled'} color={ytOn ? undefined : 'red'} onClick={() => setApp('youtube', !ytOn)}
        title={ytOn ? 'Tilladt – klik for at spærre YouTube. YouTube Kids røres ikke' : 'Spærret – klik for at tillade YouTube. YouTube Kids røres ikke'}>YouTube</Button>
      <Button size={sz} radius="xl" variant={kidsOn ? 'default' : 'filled'} color={kidsOn ? undefined : 'red'} onClick={() => setApp('kids', !kidsOn)}
        title={kidsOn ? 'Tilladt – klik for at spærre YouTube Kids. YouTube røres ikke' : 'Spærret – klik for at tillade YouTube Kids. YouTube røres ikke'}>YouTube Kids</Button>
      {pickServices(services, QUICK_BY_TYPE[r.deviceType] ?? QUICK_BY_TYPE.ipad).filter((x) => x.id !== 'youtube').map(({ id: q }) => (
        <Button key={q} size={sz} radius="xl" variant={blocked.has(q) ? 'filled' : 'default'} color={blocked.has(q) ? 'red' : undefined}
          onClick={() => toggle(q)} title={blocked.has(q) ? 'Spærret – klik for at tillade' : 'Tilladt – klik for at spærre'}>{name(q)}</Button>))}
      {(QUICK_GROUPS_BY_TYPE[r.deviceType] ?? QUICK_GROUPS_BY_TYPE.ipad).map(([g, label]) => (
        <Button key={g} size={sz} radius="xl" variant={groupOn(g) ? 'filled' : 'light'} color={groupOn(g) ? 'red' : 'gray'} onClick={() => toggleGroup(g, label)}>
          {groupOn(g) ? `${label}: fra` : `${label}: til`}</Button>))}
    </Group>
  );
}

function Fact({ label, value, color }: { label: string; value: React.ReactNode; color?: string }) {
  return <div className="fd-fact"><span>{label}</span><b style={color ? { color } : undefined}>{value}</b></div>;
}

function IpadCard({ r, can, admin }: { r: IpadRow; can: string[]; admin: boolean }) {
  const nav = useNavigate();
  const s = stateText(r); const t = devMeta(r.deviceType);
  const ytColor = r.yt === 'quota' || r.yt === 'blocked' ? 'var(--mantine-color-red-5)' : r.yt === 'open' ? undefined : 'var(--mantine-color-green-5)';
  return (
    <Paper className="fd-ipad" data-type={r.deviceType} withBorder radius="lg" p="md">
      <UnstyledButton onClick={() => nav(`${t.path}/${r.id}`)} style={{ width: '100%' }}>
        <Group gap="sm" wrap="nowrap">
          <div className="fd-ipad-dev" data-type={r.deviceType} title={t.one}><t.icon size={22} stroke={1.5} /></div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Text fw={650} size="md" truncate>{r.name}</Text>
            <Text size="xs" c="dimmed" truncate>{deviceSubtitle(r)}</Text>
          </div>
          {admin && (r.applyError ? <Badge color="red" variant="light">Fejl</Badge>
            : r.control.state !== 'missing' ? null
              : t.ios ? <Badge color="yellow" variant="light">Mangler profil</Badge>
                : <Badge color="gray" variant="light" title={`AdGuard har ikke set ${t.the} på ${r.lanIp ?? "IP-adressen"} endnu`}>Ikke set</Badge>)}
        </Group>
      </UnstyledButton>
      <ControlLine r={r} />
      <WatchAlert r={r} can={can} />
      <div className={`fd-ipad-state ${s.cls}`}>
        <s.Icon size={22} stroke={1.6} />
        <div style={{ flex: 1, minWidth: 0 }}><Text fw={700}>{s.title}</Text>{s.sub && <Text size="xs" c="dimmed">{s.sub}</Text>}</div>
      </div>
      <div className="fd-facts">
        <Fact label="Online i dag" value={r.minutesToday ? `ca. ${mins(r.minutesToday)}` : '–'} />
        <Fact label="YouTube" value={r.ytLimit > 0 ? `${r.ytUsed} / ${r.ytLimit} min.` : YT[r.yt]} color={ytColor} />
        <Fact label="YouTube Kids" value={r.kids === 'allowed' && r.kidsLimit > 0 ? `${r.kidsUsed} / ${r.kidsLimit} min.` : KIDS[r.kids]} color={r.kids === 'allowed' ? 'var(--mantine-color-green-5)' : 'var(--mantine-color-red-5)'} />
      </div>
      {admin && t.ios && !!r.outdatedApps && <UnstyledButton onClick={() => nav(`${t.path}/${r.id}/installeret`)} mb={6} title="Apps med en nyere version i App Store – se fanen Apps">
        <Badge color="orange" variant="light" style={{ cursor: 'pointer' }}>{r.outdatedApps} {r.outdatedApps === 1 ? 'forældet app' : 'forældede apps'}</Badge>
      </UnstyledButton>}
      <QuickBlocks r={r} can={can} big={!admin} />
      <QuickActions r={r} can={can} compact={admin} big={!admin} />
    </Paper>
  );
}

/** Listen for én enhedstype. Uden type (forælderens forside): den første type, hun har enheder af. */
export function IpadsPage({ type }: { type?: DeviceType }) {
  const { data } = useApi<IpadList>(['ipads'], '/api/ipads', { refetchInterval: 30_000 });
  const [add, setAdd] = useState(false);
  const nav = useNavigate();
  if (!data) return <Loading />;
  const present = DEVICE_ORDER.filter((x) => data.ipads.some((i) => (i.deviceType ?? 'ipad') === x));
  const cur = type ?? present[0] ?? 'ipad';
  const t = DEVICES[cur];
  const rows = data.ipads.filter((i) => (i.deviceType ?? 'ipad') === cur);
  const sub = data.admin
    ? t.ios ? `Internet, sengetid og YouTube – følger ${t.the}, også uden for hjemmet.` : `Internet, sengetid og YouTube – styres via ${t.the}s faste IP-adresse derhjemme.`
    : 'Tryk på en app for at spærre eller tillade den. Rød = spærret.';
  return (
    <>
      {/* forælder: faner øverst, kun for de typer hun har enheder af */}
      {!data.admin && present.length > 1 && (
        <Tabs value={cur} onChange={(v) => v && nav(DEVICES[v as DeviceType].path)} mb="md" className="fd-device-tabs">
          <Tabs.List className="fd-parent-tabs">
            {present.map((x) => { const m = DEVICES[x]; return <Tabs.Tab key={x} value={x} leftSection={<m.icon size={18} stroke={1.6} />}>{m.plural}</Tabs.Tab>; })}
          </Tabs.List>
        </Tabs>)}
      <PageHeader title={t.plural} sub={sub}>
        {data.admin && <Button leftSection={<IconPlus size={16} />} onClick={() => setAdd(true)}>Tilføj enhed</Button>}
      </PageHeader>
      {data.admin && data.error && <Alert color="red" mb="md" title="Forbindelsen til AdGuard fejler">{data.error} – se Indstillinger → iPads.</Alert>}
      {data.admin && data.watchHeld && <Alert color="yellow" mb="md" title="Ingen enheder bruger styringen">Alle overvågede enheder har været stille i hele perioden. Det ligner en fejl på den offentlige DNS-adresse, så der er ikke sendt advarsler for de enkelte enheder. Tjek forbindelsen under Indstillinger → iPads.</Alert>}
      {data.admin && t.ios && <UsbBanner />}
      {rows.length === 0
        ? <Empty>{data.admin ? `Ingen ${t.plural} endnu. Tryk "Tilføj enhed" for at komme i gang.` : `Du har ikke adgang til nogen ${t.plural} endnu.`}</Empty>
        : <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">{rows.map((r) => <IpadCard key={r.id} r={r} can={data.can} admin={data.admin} />)}</SimpleGrid>}
      <AddDevice opened={add} onClose={() => setAdd(false)} initialType={cur} />
    </>
  );
}

const IP_RE = /^(\d{1,3}\.){3}\d{1,3}$|^[0-9a-f:]+:[0-9a-f:]*$/i;

/** "Tilføj enhed": vælg typen først. Apple TV og Android kræver en fast IP-adresse derhjemme. */
function AddDevice({ opened, onClose, initialType }: { opened: boolean; onClose: () => void; initialType: DeviceType }) {
  const qc = useQueryClient(); const nav = useNavigate();
  const blank = { deviceType: initialType, name: '', kind: 'child', owner: '', model: '', lanIp: '' };
  const [f, setF] = useState(blank);
  useEffect(() => { if (opened) setF({ ...blank, deviceType: initialType }); }, [opened, initialType]);
  const t = DEVICES[f.deviceType];
  const ipOk = IP_RE.test(f.lanIp.trim());
  const ok = !!f.name.trim() && (t.ios ? !f.lanIp.trim() || ipOk : ipOk);
  const save = async () => {
    try {
      const r = await post<{ id: number }>('/api/ipads', { ...f, lanIp: f.lanIp.trim() || null });
      qc.invalidateQueries(); onClose(); nav(`${t.path}/${r.id}/opsaetning`);
    } catch (e) { notifyErr(e); }
  };
  const tv = f.deviceType === 'appletv';
  return (
    <Modal opened={opened} onClose={onClose} title="Tilføj enhed">
      <Stack>
        <div>
          <Text size="sm" fw={500} mb={6}>Hvad vil du tilføje?</Text>
          <SimpleGrid cols={{ base: 2, xs: 4 }} spacing={6}>
            {DEVICE_ORDER.map((x) => { const m = DEVICES[x]; return (
              <UnstyledButton key={x} className="fd-devpick" data-on={f.deviceType === x || undefined} onClick={() => setF({ ...f, deviceType: x })}>
                <m.icon size={26} stroke={1.5} /><Text size="sm" fw={600}>{m.one}</Text>
              </UnstyledButton>); })}
          </SimpleGrid>
        </div>
        <TextInput label="Navn" placeholder={tv ? 'fx Stue-TV' : f.deviceType === 'ipad' ? 'fx Emmas iPad' : `fx Emmas ${t.one}`} value={f.name} onChange={(e) => setF({ ...f, name: e.currentTarget.value })} data-autofocus />
        <SegmentedControl value={f.kind} onChange={(v) => setF({ ...f, kind: v })} data={[{ value: 'child', label: t.kinds[0] }, { value: 'family', label: t.kinds[1] }]} />
        <Text size="xs" c="dimmed">{f.kind === 'child' ? 'Starter med sengetid (20–07 / weekend 21–08), filter for voksenindhold, SafeSearch og YouTube i begrænset tilstand.' : 'Starter uden begrænsninger – kun beskyttelse mod reklamer og farlige sider.'}</Text>
        <SimpleGrid cols={2}>
          <TextInput label={tv ? 'Rum (valgfri)' : 'Bruges af (valgfri)'} placeholder={tv ? 'fx stue' : undefined} value={f.owner} onChange={(e) => setF({ ...f, owner: e.currentTarget.value })} />
          {tv
            ? <Autocomplete label="Model (valgfri)" placeholder="Apple TV HD" data={APPLE_TV_MODEL_CHOICES} value={f.model} onChange={(v) => setF({ ...f, model: v })} />
            : <TextInput label="Model (valgfri)" placeholder={f.deviceType === 'ipad' ? 'iPad 10. gen' : undefined} value={f.model} onChange={(e) => setF({ ...f, model: e.currentTarget.value })} />}
        </SimpleGrid>
        <TextInput label={t.ios ? 'Fast IP-adresse derhjemme (valgfri)' : 'Fast IP-adresse derhjemme'} required={!t.ios} placeholder="fx 192.168.1.50" value={f.lanIp}
          onChange={(e) => setF({ ...f, lanIp: e.currentTarget.value })} error={f.lanIp.trim() && !ipOk ? 'Skriv en IP-adresse, fx 192.168.1.50' : undefined}
          description={t.ios ? 'Så virker reglerne derhjemme med det samme – også før profilen er installeret.'
            : `Lås IP'en fast i routeren (DHCP-reservation), så den ikke skifter – ${t.the} styres via adressen, og routeren skal bruge AdGuard som DNS.`} />
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button onClick={save} disabled={!ok}>Tilføj {t.one}</Button></Group>
      </Stack>
    </Modal>
  );
}

// ------------------------------------------------------------------------------------------ detaljer
const POPULAR = ['youtube', 'tiktok', 'snapchat', 'instagram', 'roblox', 'fortnite', 'minecraft', 'discord', 'netflix', 'twitch', 'facebook', 'whatsapp', 'steam', 'spotify', 'disneyplus', 'reddit'];
const GROUP_LABEL: Record<string, string> = {
  gaming: 'Spil', social_network: 'Sociale medier', streaming: 'Video og streaming', messenger: 'Beskeder', music: 'Musik', shopping: 'Shopping',
  ai: 'AI', dating: 'Dating', gambling: 'Spil om penge', privacy: 'VPN og privatliv', software: 'Software', hosting: 'Hosting', cdn: 'CDN',
};

type Draft = Omit<IpadDetail, 'row' | 'can' | 'locked' | 'setup'>;
const TAB_LABEL: Record<string, string> = {
  regler: 'Regler', youtube: 'YouTube', installeret: 'Apps', apps: 'Apps og begrænsninger', aktivitet: 'Aktivitet', apple: 'Apple-styring', opsaetning: 'Opsætning', tjekliste: 'Tjekliste',
};
/** Apple TV: streamingtjenesterne først på listen over spærrede tjenester. */
const POPULAR_TV = ['netflix', 'youtube', 'disneyplus', 'hbomax', 'twitch', 'viaplay', 'tv2play', 'drtv', 'primevideo', 'skyshowtime', 'spotify', 'roblox', 'fortnite', 'minecraft', 'steam', 'tiktok'];

export function IpadPage() {
  const { id, tab: tabParam = 'regler' } = useParams();
  const nav = useNavigate(); const loc = useLocation();
  const { data: list } = useApi<IpadList>(['ipads'], '/api/ipads', { refetchInterval: 30_000 });
  const { data: d } = useApi<IpadDetail>(['ipad', id], `/api/ipads/${id}`, { refetchInterval: 30_000 });
  const [draft, setDraft] = useState<Draft | null>(null);
  const qc = useQueryClient();
  useEffect(() => { setDraft(null); }, [id]);
  if (!d || !list) return <Loading />;
  const r = d.row; const can = d.can; const s = stateText(r);
  const t = devMeta(r.deviceType);
  // gammelt link (/ipads/5 til et Apple TV): videre til enhedstypens egen sti, så menuen viser den rigtige fane
  if (!loc.pathname.startsWith(`${t.path}/`)) return <Navigate replace to={`${t.path}/${r.id}${tabParam !== 'regler' ? `/${tabParam}` : ''}`} />;
  const admin = list.admin;
  const tabs = tabsFor(t, admin);
  const tab = tabs.includes(tabParam) ? tabParam : 'regler';
  const siblings = list.ipads.filter((x) => (x.deviceType ?? 'ipad') === t.type);
  const cur: Draft = draft ?? d;
  const edit = (p: Partial<Draft>) => setDraft({ ...cur, ...p });
  const dirty = draft !== null;
  const ro = !can.includes('rules');
  const save = async () => {
    try { await put(`/api/ipads/${id}`, { ...cur, ytWindows: cur.ytWindows ?? '', kidsWindows: cur.kidsWindows ?? '', ageRatingApps: cur.ageRatingApps ?? 0 }); notifyOk(`Reglerne er gemt og sendt til ${t.the}`); setDraft(null); qc.invalidateQueries(); }
    catch (e) { notifyErr(e); }
  };
  return (
    <>
      <PageHeader title={r.name} sub={<Anchor size="sm" onClick={() => nav(t.path)}>← Alle {t.plural}</Anchor>} />
      <div className="fd-ipad-split">
        <Paper withBorder radius="lg" p={0} className="fd-ipad-list" visibleFrom="md">
          {siblings.map((x) => {
            const st = stateText(x);
            return (
              <UnstyledButton key={x.id} className="fd-ipad-li" data-active={x.id === r.id || undefined} onClick={() => nav(`${t.path}/${x.id}/${tab}`)}>
                <t.icon size={20} stroke={1.5} />
                <div style={{ flex: 1, minWidth: 0 }}><Text fw={600} size="sm" truncate>{x.name}</Text><Text size="xs" c="dimmed" truncate>{st.title}</Text></div>
                <span className={`fd-ipad-dot ${st.cls}`} />
              </UnstyledButton>
            );
          })}
        </Paper>
        <Paper withBorder radius="lg" p="lg">
          <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
            <div className={`fd-ipad-state ${s.cls}`} style={{ margin: 0, flex: '1 1 260px' }}>
              <s.Icon size={22} stroke={1.6} />
              <div><Text fw={700}>{s.title}</Text>{s.sub && <Text size="xs" c="dimmed">{s.sub}</Text>}</div>
            </div>
            <QuickActions r={r} can={can} big={!admin} />
          </Group>
          <div style={{ marginTop: 12 }}><QuickBlocks r={r} can={can} big={!admin} /></div>
          <Tabs value={tab} onChange={(v) => nav(`${t.path}/${id}/${v}`)} mt="md" keepMounted={false}>
            <Tabs.List className={admin ? undefined : 'fd-parent-tabs'}>
              {tabs.map((v) => (
                <Tabs.Tab key={v} value={v} rightSection={v === 'installeret' && admin && r.outdatedApps ? <Badge size="xs" color="orange" variant="light">{r.outdatedApps}</Badge> : undefined}>
                  {TAB_LABEL[v]}</Tabs.Tab>))}
            </Tabs.List>
            <Tabs.Panel value="regler" pt="md"><RulesTab cur={cur} edit={edit} ro={ro} t={t} /></Tabs.Panel>
            <Tabs.Panel value="youtube" pt="md"><YoutubeTab cur={cur} edit={edit} ro={ro} r={r} t={t} /></Tabs.Panel>
            {tabs.includes('installeret') && <Tabs.Panel value="installeret" pt="md"><InstalledAppsTab r={r} d={d} simple={!admin} /></Tabs.Panel>}
            {tabs.includes('apps') && <Tabs.Panel value="apps" pt="md"><AppsTab cur={cur} edit={edit} ro={ro} kind={r.kind} locked={d.locked} /></Tabs.Panel>}
            <Tabs.Panel value="aktivitet" pt="md"><ActivityTab id={r.id} t={t} /></Tabs.Panel>
            {tabs.includes('apple') && <Tabs.Panel value="apple" pt="md"><MdmIpadTab id={r.id} name={r.name} kind={r.kind} /></Tabs.Panel>}
            {tabs.includes('tjekliste') && <Tabs.Panel value="tjekliste" pt="md"><Stack gap="lg"><WatchAlert r={r} can={can} /><WatchPanel r={r} can={can} /><SetupChecklist r={r} d={d} can={can} /></Stack></Tabs.Panel>}
            {tabs.includes('opsaetning') && <Tabs.Panel value="opsaetning" pt="md">
              {t.ios ? <SetupTab r={r} d={d} can={can} admin={admin} /> : <LanSetupTab r={r} t={t} can={can} />}</Tabs.Panel>}
          </Tabs>
          {dirty && !ro && (
            <div className="fd-savebar">
              <Text size="sm">Du har ændringer, der ikke er gemt.</Text>
              <Group gap="xs"><Button variant="default" onClick={() => setDraft(null)}>Fortryd</Button><Button onClick={save}>Gem ændringer</Button></Group>
            </div>
          )}
        </Paper>
      </div>
    </>
  );
}

function RulesTab({ cur, edit, ro, t }: { cur: Draft; edit: (p: Partial<Draft>) => void; ro: boolean; t: DeviceMeta }) {
  const { data: services = [] } = useApi<AgService[]>(['ag-services'], '/api/ipads/services');
  const [all, setAll] = useState(false);
  const shown = useMemo(() => {
    const pop = (t.type === 'appletv' ? POPULAR_TV : POPULAR).map((p) => services.find((s) => s.id === p)).filter((s): s is AgService => !!s);
    return all ? services : pop.length ? pop : services.slice(0, 16);
  }, [services, all, t.type]);
  const groups = useMemo(() => {
    const m = new Map<string, AgService[]>();
    for (const s of shown) { const g = all ? s.group : ''; m.set(g, [...(m.get(g) ?? []), s]); }
    return [...m.entries()];
  }, [shown, all]);
  const toggle = (sid: string) => edit({ blockedServices: cur.blockedServices.includes(sid) ? cur.blockedServices.filter((x) => x !== sid) : [...cur.blockedServices, sid] });
  return (
    <Stack gap="lg">
      <div>
        <Group justify="space-between" mb={6}>
          <div><Title order={5}>Sengetid og skoletid</Title><Text size="xs" c="dimmed">{t.ios ? 'Sengetid: kun Apples tjenester og tilladte sider.' : 'Sengetid: alt er spærret undtagen tilladte sider.'} Skoletid: spil, sociale medier og video er spærret.</Text></div>
          {!ro && <Group gap={6}>
            <Button size="compact-xs" variant="default" onClick={() => edit({ schedule: defaultBedtime() })}>Standard sengetid</Button>
            <Button size="compact-xs" variant="default" onClick={() => edit({ schedule: '0'.repeat(SLOTS) })}>Ryd</Button>
          </Group>}
        </Group>
        <WeekGrid value={cur.schedule} onChange={(v) => edit({ schedule: v })} readOnly={ro}
          paints={[{ ch: '0', label: 'Fri', color: 'var(--fd-week-free)' }, { ch: 's', label: 'Skoletid', color: '#0e7490' }, { ch: 'b', label: 'Sengetid', color: '#4f46e5' }]} />
      </div>
      <div>
        <Group justify="space-between" mb={6}>
          <div><Title order={5}>Spærrede tjenester</Title><Text size="xs" c="dimmed">Altid spærret på {t.the}.</Text></div>
          <Button size="compact-xs" variant="subtle" onClick={() => setAll(!all)}>{all ? 'Vis kun de populære' : `Vis alle ${services.length}`}</Button>
        </Group>
        {services.length === 0 && <Text size="sm" c="dimmed">Listen hentes fra AdGuard, når forbindelsen er sat op.</Text>}
        {groups.map(([g, list]) => (
          <div key={g}>
            {g && <Text size="xs" fw={700} c="dimmed" tt="uppercase" mt="sm" mb={4}>{GROUP_LABEL[g] ?? g}</Text>}
            <SimpleGrid cols={{ base: 2, sm: 3, lg: 4 }} spacing={6}>
              {list.map((sv) => (
                <UnstyledButton key={sv.id} className="fd-svc" data-on={cur.blockedServices.includes(sv.id) || undefined} disabled={ro} onClick={() => toggle(sv.id)}>
                  {sv.icon ? <img alt="" src={`data:image/svg+xml;base64,${sv.icon}`} /> : <span className="fd-svc-i" />}
                  <Text size="sm" truncate style={{ flex: 1 }}>{sv.name}</Text>
                  <Switch size="xs" checked={cur.blockedServices.includes(sv.id)} readOnly tabIndex={-1} />
                </UnstyledButton>))}
            </SimpleGrid>
          </div>))}
      </div>
      <div>
        <Title order={5} mb={6}>Filtre</Title>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
          <Switch label="Spær voksenindhold" description="Porno, vold og lignende (AdGuards forældrefilter)" checked={cur.filterAdult} disabled={ro} onChange={(e) => edit({ filterAdult: e.currentTarget.checked })} />
          <Switch label="Tvungen SafeSearch" description="Google, Bing, DuckDuckGo m.fl. viser kun sikre resultater" checked={cur.safeSearch} disabled={ro} onChange={(e) => edit({ safeSearch: e.currentTarget.checked })} />
        </SimpleGrid>
      </div>
      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <TagsInput label="Altid tilladt" description="Virker også ved sengetid, fx skolens sider (aula.dk)" placeholder="Skriv et domæne og tryk Enter" value={cur.allowedDomains} disabled={ro} onChange={(v) => edit({ allowedDomains: v })} />
        <TagsInput label="Altid spærret" description="Ekstra sider, der skal spærres" placeholder="Skriv et domæne og tryk Enter" value={cur.blockedDomains} disabled={ro} onChange={(v) => edit({ blockedDomains: v })} />
      </SimpleGrid>
    </Stack>
  );
}

/** Tider og minutter for én app. */
function AppLimits({ app, daily, windows, used, limit, ro, off, onDaily, onWindows }: {
  app: string; daily: number; windows: string | null; used: number; limit: number; ro: boolean; off: boolean; onDaily: (v: number) => void; onWindows: (v: string | null) => void;
}) {
  const grid = windows && windows.length === SLOTS ? windows : null;
  const pct = limit > 0 ? Math.min(100, (100 * used) / limit) : 0;
  return (
    <>
      <Group align="flex-end" gap="md">
        <NumberInput w={180} label="Minutter pr. dag" description="0 = ingen grænse" min={0} max={1440} step={15} disabled={ro || off} value={daily} onChange={(v) => onDaily(Number(v) || 0)} />
        {limit > 0 && <div style={{ flex: 1, minWidth: 200 }}>
          <Text size="sm" mb={4}>I dag: ca. {used} af {limit} min.</Text>
          <Progress value={pct} color={pct >= 100 ? 'red' : pct > 75 ? 'yellow' : 'green'} />
        </div>}
      </Group>
      <Group justify="space-between" mt="md" mb={6}>
        <Text size="sm" fw={600}>Tider for {app}</Text>
        <Switch label="Kun på bestemte tider" disabled={ro || off} checked={!!grid} onChange={(e) => onWindows(e.currentTarget.checked ? afternoons() : null)} />
      </Group>
      {grid && <WeekGrid value={grid} onChange={onWindows} readOnly={ro}
        paints={[{ ch: '0', label: 'Ikke tilladt', color: 'var(--fd-week-free)' }, { ch: '1', label: `${app} tilladt`, color: '#dc2626' }]} />}
    </>
  );
}

function YoutubeTab({ cur, edit, ro, r, t }: { cur: Draft; edit: (p: Partial<Draft>) => void; ro: boolean; r: IpadRow; t: DeviceMeta }) {
  const ytMode = cur.ytMode === 'kids' ? 'blocked' : cur.ytMode;
  const ytOff = ytMode === 'blocked'; const kidsOff = cur.kidsMode === 'blocked';
  const serviceOff = cur.blockedServices.includes('youtube');
  // Teksterne følger de regler, IpadPolicy.Build faktisk skriver til AdGuard for hver kombination
  const combo = ytOff && kidsOff ? `Begge apps er spærret på ${t.the}.`
    : ytOff ? 'youtube.com, youtu.be og indlejrede videoer er spærret, og YouTube Kids virker. YouTube-appen bruger de samme adresser til data og video som YouTube Kids, så DNS kan ikke spærre den helt: '
      + 'den kan stadig åbne og vise indhold i streng Begrænset tilstand.' + (t.ios ? ' Skjul appen (com.google.ios.youtube) med Skærmtid eller under »Apps og begrænsninger« for at være sikker.' : '')
      : kidsOff ? 'youtubekids.com er spærret, og YouTube virker. YouTube Kids deler adresser med YouTube, så dele af appen kan stadig indlæses.'
        : ytMode === 'open' ? 'Begge apps virker uden begrænsning af indholdet.'
          : `YouTube Kids virker. YouTubes "Begrænset tilstand" er tvunget til i både appen og browseren på ${t.the}${ytMode === 'strict' ? ' og skjuler endnu mere' : ''}.`;
  const unlink = () => edit({ kidsLinked: false, kidsDailyMin: cur.ytDailyMin, kidsWindows: cur.ytWindows });
  return (
    <Stack gap="lg">
      {serviceOff && <Alert color="yellow">YouTube er også spærret som tjeneste under Regler. Det rammer begge apps, uanset valgene her.</Alert>}
      <div>
        <Title order={5} mb={6}>YouTube</Title>
        <SegmentedControl fullWidth disabled={ro} value={ytMode} onChange={(v) => edit({ ytMode: v as YtMode })}
          data={[{ value: 'open', label: 'Åben' }, { value: 'moderate', label: 'Begrænset' }, { value: 'strict', label: 'Streng' }, { value: 'blocked', label: 'Spærret' }]} />
      </div>
      <div>
        <Title order={5} mb={6}>YouTube Kids</Title>
        <SegmentedControl fullWidth disabled={ro} value={cur.kidsMode} onChange={(v) => edit({ kidsMode: v as KidsMode })}
          data={[{ value: 'allowed', label: 'Tilladt' }, { value: 'blocked', label: 'Spærret' }]} />
      </div>
      <Alert color="blue" variant="light" title="Sådan virker det">{combo}</Alert>
      <div>
        <Title order={5} mb={6}>Tider og minutter for YouTube</Title>
        <AppLimits app="YouTube" daily={cur.ytDailyMin} windows={cur.ytWindows} used={r.ytUsed} limit={r.ytLimit} ro={ro} off={ytOff}
          onDaily={(v) => edit({ ytDailyMin: v })} onWindows={(v) => edit({ ytWindows: v })} />
      </div>
      <div>
        <Title order={5} mb={6}>Tider og minutter for YouTube Kids</Title>
        <Switch mb="sm" disabled={ro || kidsOff} checked={cur.kidsLinked} onChange={(e) => (e.currentTarget.checked ? edit({ kidsLinked: true }) : unlink())}
          label="Samme tider og minutter som YouTube" description="De to apps deler så én fælles dagsgrænse." />
        {!cur.kidsLinked && <AppLimits app="YouTube Kids" daily={cur.kidsDailyMin} windows={cur.kidsWindows} used={r.kidsUsed} limit={r.kidsLimit} ro={ro} off={kidsOff}
          onDaily={(v) => edit({ kidsDailyMin: v })} onWindows={(v) => edit({ kidsWindows: v })} />}
      </div>
      <Text size="xs" c="dimmed">Minutterne er et skøn i perioder af 5 minutter. Med egne minutter tæller kun adresser, der hører til én af apps'ene. Fælles adresser til video og data kan ikke henføres til en app og tæller ikke med, så tallet kan være for lavt. Når tiden er brugt, spærres kun den app til i morgen. Sengetid og skoletid gælder altid begge apps.</Text>
    </Stack>
  );
}

/** Forslag: YouTube 15–19 på hverdage og 09–19 i weekenden. */
function afternoons() {
  const c = Array<string>(SLOTS).fill('0');
  for (let d = 0; d < 7; d++) { const from = d >= 5 ? 18 : 30; for (let s = from; s < 38; s++) c[d * 48 + s] = '1'; }
  return c.join('');
}

function ActivityTab({ id, t }: { id: number; t: DeviceMeta }) {
  const { data } = useApi<IpadActivity>(['ipad-act', id], `/api/ipads/${id}/activity?days=7`, { refetchInterval: 60_000 });
  const { data: services = [] } = useApi<AgService[]>(['ag-services'], '/api/ipads/services');
  if (!data) return <Loading />;
  const name = (sid: string) => services.find((s) => s.id === sid)?.name ?? sid;
  const today = data.days[data.days.length - 1];
  const chart = data.days.map((d) => ({ dag: new Date(d.day).toLocaleDateString('da-DK', { weekday: 'short', day: 'numeric' }), Online: Math.round(d.total / 6) / 10, YouTube: Math.round((d.services.find((s) => s.id === 'youtube')?.minutes ?? 0) / 6) / 10 }));
  return (
    <Stack gap="lg">
      {data.days.length === 0 ? <Empty>{t.ios ? `Ingen aktivitet endnu. Den vises, når ${t.the} har fået profilen og er brugt.` : `Ingen aktivitet endnu. Den vises, når ${t.the} er brugt på hjemmets netværk.`}</Empty> : <>
        <div>
          <Title order={5} mb={6}>De sidste 7 dage (timer)</Title>
          <BarChart h={200} data={chart} dataKey="dag" series={[{ name: 'Online', color: 'indigo.5' }, { name: 'YouTube', color: 'red.6' }]} tickLine="none" gridAxis="y" withLegend />
        </div>
        {today && <div>
          <Title order={5} mb={6}>Mest brugt i dag</Title>
          {today.services.length === 0 ? <Text size="sm" c="dimmed">Ingen kendte tjenester i dag.</Text> :
            <Stack gap={6}>{today.services.slice(0, 10).map((s) => (
              <Group key={s.id} gap="sm" wrap="nowrap">
                <Text size="sm" w={140} truncate>{name(s.id)}</Text>
                <Progress style={{ flex: 1 }} value={(100 * s.minutes) / Math.max(...today.services.map((x) => x.minutes))} color={s.id === 'youtube' ? 'red' : 'indigo'} />
                <Text size="sm" w={70} ta="right">{mins(s.minutes)}</Text>
              </Group>))}</Stack>}
        </div>}
        <Text size="xs" c="dimmed">Tider er omtrentlige: de bygger på, hvornår {t.the} slår adresser op (i perioder af 5 minutter). Baggrundstrafik kan tælle med i "Online".</Text>
      </>}
    </Stack>
  );
}

function SetupTab({ r, d, can, admin }: { r: IpadRow; d: IpadDetail; can: string[]; admin: boolean }) {
  const qc = useQueryClient(); const nav = useNavigate();
  const [link, setLink] = useState<{ url: string; shortUrl?: string; urlB64?: string; shortUrlB64?: string; expires: string } | null>(null);
  const [f, setF] = useState({ name: r.name, owner: r.owner ?? '', model: r.model ?? '', kind: r.kind, lanIp: r.lanIp ?? '', deviceType: r.deviceType ?? 'ipad' });
  const t = devMeta(r.deviceType);
  const mk = useAction(() => post<{ url: string; shortUrl?: string; urlB64?: string; shortUrlB64?: string; expires: string }>(`/api/ipads/${r.id}/profile-link`), () => 'Linket er klar');
  return (
    <Stack gap="lg">
      <div>
        <Title order={5} mb={4}>Installér profilen på iPad'en</Title>
        <Text size="sm" c="dimmed" mb="sm">Profilen sender iPad'ens DNS krypteret hjem til AdGuard, så reglerne virker overalt – også på 4G og andres wifi.</Text>
        {r.control.state === 'active' ? <Alert color="green" mb="sm">iPad'en bruger profilen – sidst set {ago(r.control.seen)}.</Alert>
          : r.control.state === 'stale' ? <Alert color="yellow" mb="sm">iPad'en har ikke brugt profilen i et stykke tid – sidst set {ago(r.control.seen)}. Den kan være slukket, eller profilen kan være fjernet.</Alert>
            : <Alert color="yellow" mb="sm">iPad'en har ikke brugt profilen endnu.{r.control.lanSeen ? ` Den er set på hjemmets netværk ${ago(r.control.lanSeen)}, men ikke via profilen.` : ''}</Alert>}
        {d.locked.applied !== null && <Alert color="blue" variant="light" mb="sm">Den låste profil er installeret og indeholder DNS-låsen – brug ikke QR-koden på denne iPad (se nedenfor).</Alert>}
        {can.includes('rules') && d.locked.applied === null && (link
          ? <Group align="flex-start" gap="lg">
              <Qr text={link.url} />
              <Stack gap={6} style={{ flex: 1, minWidth: 240 }}>
                <Text size="sm" fw={600}>Sådan gør du:</Text>
                <Text size="sm" component="div">1. Scan QR-koden med iPad'ens <b>Kamera</b>-app (ikke Google-appen), og tryk på den gule "Safari"-boble – eller skriv adressen herunder i Safari.</Text>
                <Text size="sm">2. Tryk "Tillad" – profilen hentes.</Text>
                <Text size="sm">3. Åbn Indstillinger → "Profil hentet" → Installér, og skriv iPad'ens kode.</Text>
                <Text size="sm">4. Kortet her skifter til "aktiv" inden for et minut.</Text>
                <Text size="sm" mt={4}>Eller skriv adressen i <b>Safari</b> på iPad'en (med http:// foran):</Text>
                <Text ff="monospace" fw={700} size="lg" style={{ wordBreak: 'break-all' }}>{link.shortUrl ?? link.url}</Text>
                <Group gap={6} mt={4}><TextInput size="xs" readOnly value={link.shortUrl ?? link.url} style={{ flex: 1 }} />
                  <CopyButton value={link.shortUrl ?? link.url}>{({ copied, copy }) => <Button size="xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton></Group>
                <Text size="xs" c="dimmed">Linket virker til {dayClock(link.expires)}.</Text>
              </Stack>
            </Group>
          : <Button leftSection={<IconQrcode size={16} />} loading={mk.isPending} onClick={() => mk.mutate(undefined, { onSuccess: (x) => setLink({ ...x, url: x.urlB64 ? atob(x.urlB64) : x.url, shortUrl: x.shortUrlB64 ? atob(x.shortUrlB64) : x.shortUrl }) })}>Lav QR-kode til profilen</Button>)}
        <Text size="xs" c="dimmed" mt="sm">Skærmtid kan ikke forhindre, at en almindelig profil slettes. Kun den låste profil nedenfor låser den helt.</Text>
      </div>
      <WatchAlert r={r} can={can} />
      <WatchPanel r={r} can={can} />
      <SetupChecklist r={r} d={d} can={can} />
      <LockedSection r={r} d={d} can={can} admin={admin} />
      {(admin || can.includes('update')) && <UsbSection r={r} d={d} can={can} admin={admin} />}
      {admin && can.includes('erase') && <NoWipeSection r={r} />}
      {admin && <ReleaseSection r={r} />}
      {r.applyError && <Alert color="red" title="AdGuard afviste ændringen">{r.applyError}</Alert>}
      {admin && <div>
        <Title order={5} mb="xs">iPad'en</Title>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Navn" value={f.name} onChange={(e) => setF({ ...f, name: e.currentTarget.value })} />
          <Select label="Type" value={f.kind} onChange={(v) => setF({ ...f, kind: (v as 'child' | 'family') ?? 'child' })} allowDeselect={false}
            data={[{ value: 'child', label: t.kinds[0] }, { value: 'family', label: t.kinds[1] }]} />
          <Select label="Enhed" value={f.deviceType} onChange={(v) => setF({ ...f, deviceType: (v as DeviceType) ?? 'ipad' })} allowDeselect={false}
            data={DEVICE_ORDER.map((x) => ({ value: x, label: DEVICES[x].one }))} description="Apple TV og Android styres via den faste IP-adresse" />
          <TextInput label="Bruges af" value={f.owner} onChange={(e) => setF({ ...f, owner: e.currentTarget.value })} />
          <TextInput label="Model" value={f.model} onChange={(e) => setF({ ...f, model: e.currentTarget.value })} />
          <TextInput label="IP-adresse på hjemmets wifi (valgfri)" placeholder="fx 192.168.1.87" value={f.lanIp} onChange={(e) => setF({ ...f, lanIp: e.currentTarget.value })}
            description="Så virker reglerne derhjemme med det samme – også uden profil. Se adressen på iPad'en: Indstillinger → Wi-Fi → (i). Slå &quot;Privat Wi-Fi-adresse&quot; fra (eller sæt den til Fast), så adressen ikke skifter." />
        </SimpleGrid>
        <Group mt="sm" justify="space-between">
          <Button variant="default" onClick={async () => {
            try { await put(`/api/ipads/${r.id}`, f); notifyOk('Gemt'); qc.invalidateQueries(); if (f.deviceType !== r.deviceType) nav(`${DEVICES[f.deviceType].path}/${r.id}/opsaetning`); }
            catch (e) { notifyErr(e); }
          }}>Gem</Button>
          <Button color="red" variant="subtle" onClick={() => modals.openConfirmModal({
            title: `Fjern ${r.name}?`, children: <Text size="sm">Reglerne fjernes fra AdGuard. Husk også at slette profilen på iPad'en (Indstillinger → Generelt → VPN og enhedsadministration).</Text>,
            labels: { confirm: 'Fjern', cancel: 'Annullér' }, confirmProps: { color: 'red' },
            onConfirm: async () => { try { await del(`/api/ipads/${r.id}`); qc.invalidateQueries(); nav(t.path); } catch (e) { notifyErr(e); } },
          })}>Fjern iPad</Button>
        </Group>
      </div>}
    </Stack>
  );
}

/** Opsætning for enheder, der styres via deres faste IP-adresse (Apple TV, Android): IP, model, vejledning – ingen QR/USB/overvågning endnu. */
function LanSetupTab({ r, t, can }: { r: IpadRow; t: DeviceMeta; can: string[] }) {
  const qc = useQueryClient(); const nav = useNavigate();
  const [f, setF] = useState({ name: r.name, owner: r.owner ?? '', model: r.model ?? '', kind: r.kind, lanIp: r.lanIp ?? '', deviceType: r.deviceType });
  const tv = r.deviceType === 'appletv';
  const ipOk = IP_RE.test(f.lanIp.trim());
  const save = async () => {
    try {
      await put(`/api/ipads/${r.id}`, f); notifyOk('Gemt'); qc.invalidateQueries();
      if (f.deviceType !== r.deviceType) nav(`${DEVICES[f.deviceType].path}/${r.id}/opsaetning`);
    } catch (e) { notifyErr(e); }
  };
  return (
    <Stack gap="lg">
      <div>
        <Title order={5} mb={4}>Styring via IP-adressen</Title>
        <Text size="sm" c="dimmed" mb="sm">
          {t.the[0].toUpperCase() + t.the.slice(1)} styres af AdGuard ud fra sin faste IP-adresse på hjemmets netværk. Reglerne virker derfor kun derhjemme,
          og routeren skal dele AdGuard ud som DNS.
        </Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }} spacing="xs" mb="sm">
          <Paper withBorder radius="md" p="sm"><Text size="xs" c="dimmed">IP-adresse</Text><Text fw={650} ff="monospace">{r.lanIp ?? '–'}</Text></Paper>
          <Paper withBorder radius="md" p="sm"><Text size="xs" c="dimmed">Model</Text><Text fw={650}>{modelName(r.model) ?? '–'}</Text></Paper>
          <Paper withBorder radius="md" p="sm"><Text size="xs" c="dimmed">{tv ? 'Rum' : 'Bruges af'}</Text><Text fw={650}>{r.owner ?? '–'} · {kindLabel(r)}</Text></Paper>
        </SimpleGrid>
        {r.lastSeen
          ? <Alert color="green">AdGuard ser {t.the} på {r.lanIp} – sidst set {ago(r.lastSeen)}.</Alert>
          : <Alert color="yellow">AdGuard har ikke set {t.the} på {r.lanIp ?? 'IP-adressen'} endnu. Tjek at adressen er låst fast i routeren, og at {t.the} bruger routerens DNS (AdGuard).</Alert>}
      </div>
      <WatchPanel r={r} can={can} />
      {tv && <div>
        <Group gap={8} mb={4}><Title order={5}>DNS-profil til Apple TV</Title><Badge variant="light" color="gray">Kommer</Badge></Group>
        <Text size="sm" c="dimmed">
          tvOS kan få en låst DNS-indstilling (com.apple.dnsSettings.managed), så reglerne følger Apple TV'et uanset netværk – men kun på et overvåget Apple TV
          via Apple Configurator eller MDM. QR-koden, som bruges til iPads, virker ikke på Apple TV. Indtil da styres Apple TV'et via IP-adressen ovenfor.
        </Text>
      </div>}
      {tv && <div>
        <Title order={5} mb={4}>Overvåg Apple TV'et med Apple Configurator (over netværket)</Title>
        <Text size="sm" c="dimmed" mb={6}>Forberedelse til den låste DNS-profil. Apple TV HD og 4K har ingen USB-port, så parringen sker over wifi/netværk.</Text>
        <Stack gap={4}>
          <Text size="sm">1. Sæt Mac'en og Apple TV'et på samme netværk.</Text>
          <Text size="sm">2. På Apple TV'et: Indstillinger → Fjernbetjeninger og enheder → Fjernbetjeningsapp og enheder (Settings → Remotes and Devices → Remote App and Devices).</Text>
          <Text size="sm">3. Åbn Apple Configurator på Mac'en. Apple TV'et dukker op – vælg det og skriv den kode, der vises på fjernsynet, for at parre.</Text>
          <Text size="sm">4. Vælg "Forbered" (Prepare) og slå "Overvåg enheder" (Supervise) til. Bemærk: at gøre Apple TV'et overvåget sletter det, så det skal sættes op igen.</Text>
        </Stack>
      </div>}
      {r.applyError && <Alert color="red" title="AdGuard afviste ændringen">{r.applyError}</Alert>}
      <div>
        <Title order={5} mb="xs">{t.one}</Title>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Navn" value={f.name} onChange={(e) => setF({ ...f, name: e.currentTarget.value })} />
          <Select label="Type" value={f.kind} onChange={(v) => setF({ ...f, kind: (v as 'child' | 'family') ?? 'child' })} allowDeselect={false}
            data={[{ value: 'child', label: t.kinds[0] }, { value: 'family', label: t.kinds[1] }]} />
          <TextInput label={tv ? 'Rum' : 'Bruges af'} placeholder={tv ? 'fx stue' : undefined} value={f.owner} onChange={(e) => setF({ ...f, owner: e.currentTarget.value })} />
          {tv
            ? <Autocomplete label="Model" data={APPLE_TV_MODEL_CHOICES} value={f.model} onChange={(v) => setF({ ...f, model: v })} />
            : <TextInput label="Model" value={f.model} onChange={(e) => setF({ ...f, model: e.currentTarget.value })} />}
          <TextInput label="Fast IP-adresse derhjemme" required placeholder="fx 192.168.1.50" value={f.lanIp} onChange={(e) => setF({ ...f, lanIp: e.currentTarget.value })}
            error={f.lanIp.trim() && !ipOk ? 'Skriv en IP-adresse, fx 192.168.1.50' : undefined}
            description="Lås IP'en fast i routeren (DHCP-reservation), så den ikke skifter." />
          <Select label="Enhed" value={f.deviceType} onChange={(v) => setF({ ...f, deviceType: (v as DeviceType) ?? r.deviceType })} allowDeselect={false}
            data={DEVICE_ORDER.map((x) => ({ value: x, label: DEVICES[x].one }))} />
        </SimpleGrid>
        <Group mt="sm" justify="space-between">
          <Button variant="default" onClick={save} disabled={!f.name.trim() || !ipOk}>Gem</Button>
          <Button color="red" variant="subtle" onClick={() => modals.openConfirmModal({
            title: `Fjern ${r.name}?`, children: <Text size="sm">Reglerne for {r.lanIp ?? 'IP-adressen'} fjernes fra AdGuard.</Text>,
            labels: { confirm: 'Fjern', cancel: 'Annullér' }, confirmProps: { color: 'red' },
            onConfirm: async () => { try { await del(`/api/ipads/${r.id}`); qc.invalidateQueries(); nav(t.path); } catch (e) { notifyErr(e); } },
          })}>Fjern {t.one}</Button>
        </Group>
      </div>
    </Stack>
  );
}
