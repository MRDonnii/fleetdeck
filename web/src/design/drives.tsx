import { useState } from 'react';
import { Alert, Anchor, Badge, Button, Checkbox, Group, PasswordInput, SegmentedControl, Select, SimpleGrid, Stack, Text, TextInput, ThemeIcon, Tooltip } from '@mantine/core';
import { Link } from 'react-router';
import { IconCircleCheck, IconCircleX, IconClock, IconFolderShare, IconTrash } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { del, post, useAction, useApi } from '../api';
import { Panel } from '../components';
import { ago } from '../format';
import type { Settings } from '../types';
import { FILESERVER_LINK, fileServerLogin } from './fileserver-login';

interface ShareOption { server: string; host: string; share: string; comment?: string | null }
interface DriveRow { id: number; host: string; share: string; letter?: string | null; name: string; fromProfile?: boolean; adopted?: boolean; ownLogin?: boolean;
  state?: { ok: boolean; target: string; error?: string | null; lastOk?: string | null; since?: string | null } | null }

const LETTERS = 'ZYXWVUTSRQPONMLKJIHGFED'.split('');

/**
 * Netværksdrev for en profil (alle dens maskiner) eller én maskine. Mapperne vælges fra Unraid-serverens delte mapper,
 * og alle bruger brugeren på filserveren fra Indstillinger → Netværk og drev. På maskinens side vises status fra maskinen.
 */
/** "kl. 12:31" i dag, ellers "15. nov. kl. 12:31". */
const at = (iso: string) => {
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z');
  const t = d.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' });
  return d.toDateString() === new Date().toDateString() ? `kl. ${t}` : `${d.toLocaleDateString('da-DK', { day: 'numeric', month: 'short' })} kl. ${t}`;
};

/** Statuslinjen for et drev: forbundet nu / afbrudt siden … / ukendt, når maskinen er offline. */
function stateText(st: DriveRow['state'], online: boolean | undefined, lastSeen: string | null | undefined) {
  if (!st) return 'venter på maskinen';
  if (online === false) return `ukendt – maskinen er offline${lastSeen ? ` (sidst set ${ago(lastSeen)})` : ''}${st.lastOk ? ` · sidst forbundet ${ago(st.lastOk)}` : ''}`;
  if (st.ok) return `forbundet nu (${st.target})${st.since ? ` · siden ${at(st.since)}` : ''}`;
  return `${st.error ?? 'ikke forbundet'}${st.since ? ` · afbrudt siden ${at(st.since)}` : ''}${st.lastOk ? ` · sidst forbundet ${ago(st.lastOk)}` : ' · aldrig forbundet'}`;
}

export function DrivesPanel({ profileId, machineId, rows, isAdmin, windows = true, online, lastSeen }: {
  profileId?: number; machineId?: number; rows?: DriveRow[]; isAdmin: boolean; windows?: boolean; online?: boolean; lastSeen?: string | null;
}) {
  const qc = useQueryClient();
  const key = profileId ? ['drives', 'p', profileId] : ['drives', 'm', machineId];
  const { data: own = [] } = useApi<DriveRow[]>(key, profileId ? `/api/drives?profileId=${profileId}` : `/api/drives?machineId=${machineId}`);
  const { data: shares = [] } = useApi<ShareOption[]>(['drive-shares'], '/api/drives/shares');
  const { data: settings } = useApi<Settings>(['settings'], '/api/settings');
  const login = fileServerLogin(settings);
  const list = rows ?? own;
  const [pick, setPick] = useState<string | null>(null);
  const [letter, setLetter] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [mode, setMode] = useState<'unraid' | 'manual'>('unraid');
  const [path, setPath] = useState('');
  const [ownLogin, setOwnLogin] = useState(true);
  // \\server\mappe eller //server/mappe → server og mappe
  const parsed = (() => {
    const p = path.trim().replace(/^smb:\/\//i, '').replace(/^[\\/]+/, '').split(/[\\/]+/).filter(Boolean);
    return p.length === 2 ? { host: p[0], share: p[1] } : null;
  })();
  const used = new Set(list.map((d) => d.letter).filter(Boolean));
  const chosen = mode === 'manual' ? parsed : shares.find((s) => `${s.host}|${s.share}` === pick);
  const refresh = () => { qc.invalidateQueries({ queryKey: key }); qc.invalidateQueries({ queryKey: ['machine'] }); qc.invalidateQueries(); };
  const add = useAction(() => post('/api/drives', { profileId, machineId, host: chosen!.host, share: chosen!.share, letter, name: name || chosen!.share,
    ownLogin: mode === 'manual' && windows && !profileId && ownLogin }), 'Drevet er tilføjet');
  const remove = useAction((id: number) => del(`/api/drives/${id}`), 'Drevet er fjernet');
  const onPick = (v: string | null) => {
    setPick(v);
    const s = shares.find((x) => `${x.host}|${x.share}` === v);
    if (!s) return;
    setName(s.share);
    const first = s.share[0]?.toUpperCase();
    setLetter(first && LETTERS.includes(first) && !used.has(first) ? first : LETTERS.find((l) => !used.has(l)) ?? null);
  };
  return (
    <Panel title="Netværksdrev" right={settings && login.state === 'ok'
      ? <Text size="xs" c="dimmed">Logger ind som <b>{login.user}</b> · <Anchor component={Link} to={FILESERVER_LINK} size="xs">Ret login</Anchor></Text>
      : undefined}>
      <Stack gap="xs">
        {settings && login.state !== 'ok' && (
          <Alert color="orange" p="xs">
            <Text size="sm"><b>{login.state === 'none' ? 'Intet login sat.' : `Ingen adgangskode til ${login.user}.`}</b> Drevene kan ikke logge ind på filserveren
              {' '}(undtagen drev med eget login). <Anchor component={Link} to={FILESERVER_LINK} size="sm">Sæt login under Indstillinger → Netværk og drev</Anchor></Text>
          </Alert>
        )}
        {list.length === 0 && <Text size="sm" c="dimmed">Ingen netværksdrev{profileId ? ' i profilen' : ''} endnu.</Text>}
        {list.map((d) => {
          const own = !d.fromProfile;
          const st = d.state;
          return (
            <Group key={d.id} gap="sm" wrap="nowrap">
              <ThemeIcon variant="light" color={!st || online === false ? 'gray' : st.ok ? 'teal' : 'red'} radius="md">
                {!st ? <IconClock size={16} /> : st.ok ? <IconCircleCheck size={16} /> : <IconCircleX size={16} />}
              </ThemeIcon>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" fw={600} lineClamp={1}>
                  {d.letter ? `${d.letter}: ` : ''}{d.name}
                  {d.fromProfile && <Badge size="xs" variant="light" color="gray" ml={6}>fra profil</Badge>}
                  {d.adopted && <Badge size="xs" variant="light" color="blue" ml={6}>fundet på maskinen</Badge>}
                  {d.ownLogin && <Badge size="xs" variant="light" color="grape" ml={6}>eget login</Badge>}
                </Text>
                <Text size="xs" c={st && !st.ok && online !== false ? 'red' : 'dimmed'} lineClamp={2}>
                  {`\\\\${d.host}\\${d.share}`}{rows ? ` · ${stateText(st, online, lastSeen)}` : ''}
                </Text>
              </div>
              {isAdmin && own && (
                <Tooltip label={d.adopted ? 'Hold op med at holde øje (drevet bliver på maskinen)' : 'Fjern drevet'}><Button size="compact-xs" variant="subtle" color="red" onClick={() => remove.mutate(d.id, { onSuccess: refresh })}><IconTrash size={14} /></Button></Tooltip>
              )}
            </Group>
          );
        })}
        {isAdmin && (<>
          <SegmentedControl size="xs" mt={4} value={mode} onChange={(v) => { setMode(v as 'unraid' | 'manual'); setPick(null); setPath(''); setName(''); }}
            data={[{ value: 'unraid', label: 'Fra Unraid' }, { value: 'manual', label: 'Skriv selv' }]} style={{ alignSelf: 'flex-start' }} />
          <Group align="flex-end" gap="xs" wrap="wrap">
            {mode === 'manual' ? (
              <TextInput style={{ flex: '2 1 220px' }} label="Sti" placeholder={'\\\\server\\mappe  eller  //192.168.1.50/backup'} leftSection={<IconFolderShare size={16} />}
                value={path} error={path && !parsed ? 'Skriv \\\\server\\mappe' : undefined}
                onChange={(e) => { const v = e.currentTarget.value; setPath(v); const sh = v.trim().split(/[\\/]+/).filter(Boolean)[1]; if (sh && !name) setName(sh); }} />
            ) : <Select style={{ flex: '2 1 220px' }} label="Delt mappe" placeholder={shares.length ? 'Vælg en mappe' : 'Ingen Unraid-server fundet endnu'} searchable
              leftSection={<IconFolderShare size={16} />} value={pick} onChange={onPick}
              data={Object.entries(shares.reduce<Record<string, { value: string; label: string }[]>>((g, s) => {
                (g[s.server] ??= []).push({ value: `${s.host}|${s.share}`, label: s.comment ? `${s.share} – ${s.comment}` : s.share }); return g;
              }, {})).map(([group, items]) => ({ group, items }))} />}
            {windows && <Select style={{ flex: '0 0 90px' }} label="Bogstav" data={LETTERS.filter((l) => !used.has(l) || l === letter).map((l) => ({ value: l, label: `${l}:` }))}
              value={letter} onChange={setLetter} clearable placeholder="Auto" />}
            <TextInput style={{ flex: '1 1 140px' }} label="Navn" description={windows ? undefined : 'Mappen /mnt/<navn>'} value={name} onChange={(e) => setName(e.currentTarget.value)} />
            <Button disabled={!chosen} loading={add.isPending} onClick={() => add.mutate(undefined, { onSuccess: () => { setPick(null); setPath(''); setName(''); setLetter(null); refresh(); } })}>Tilføj</Button>
          </Group>
          {mode === 'manual' && windows && !profileId && <Checkbox size="xs" checked={ownLogin} onChange={(e) => setOwnLogin(e.currentTarget.checked)}
            label="Brug maskinens eget login (fx en anden server) – ellers brugeren på filserveren fra Indstillinger" />}
        </>)}
        <Text size="xs" c="dimmed">Drev, du selv kobler på maskinen, gemmes her automatisk og holdes forbundet: agenten tjekker hvert minut og kobler dem på igen, hvis de falder af.
          Windows: drevbogstav i Stifinder for den, der er logget ind. Linux: /mnt/&lt;navn&gt;, monteres når det bruges. Mac: i Finder ved login.</Text>
      </Stack>
    </Panel>
  );
}

/**
 * Indstillinger → Netværk og drev: brugeren, som netværksdrev (uden eget login) og agenten på WinSetup-sharen logger ind med.
 * Samme nøgler som før (share_user/share_pass) – gemmes med sidens "Gem indstillinger".
 */
export function FileServerLoginPanel({ s, f, set, ro }: { s: Settings; f: Record<string, string>; set: (k: string, v: string) => void; ro: boolean }) {
  const login = fileServerLogin(s);
  return (
    <Panel title="Login til netværksdrev og WinSetup" right={login.state === 'ok' ? <Badge color="teal" variant="light">Login sat</Badge> : undefined}>
      <Stack gap="sm">
        <Text size="sm" c="dimmed">Netværksdrev logger ind med denne bruger (undtagen drev med eget login), og agenten bruger den, når den henter eller gemmer drivere på WinSetup-sharen.</Text>
        {login.state !== 'ok' && (
          <Alert color="orange" p="xs">
            <Text size="sm"><b>{login.state === 'none' ? 'Intet login sat.' : 'Ingen adgangskode gemt.'}</b> Netværksdrev uden eget login kan ikke forbindes, før du skriver brugeren og adgangskoden her.</Text>
          </Alert>
        )}
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Bruger på filserveren" placeholder="fx pc-drev" disabled={ro} value={f.share_user ?? ''} onChange={(e) => set('share_user', e.currentTarget.value)} autoComplete="off" />
          <PasswordInput label="Adgangskode" disabled={ro} placeholder={s.hasSharePass ? '(uændret)' : ''} value={f.share_pass ?? ''} onChange={(e) => set('share_pass', e.currentTarget.value)} autoComplete="new-password" />
        </SimpleGrid>
        <Text size="xs" c="dimmed">Brugeren oprettes på filserveren (på Unraid under Users) og skal have adgang til de delte mapper. Tryk Gem indstillinger øverst.</Text>
      </Stack>
    </Panel>
  );
}
