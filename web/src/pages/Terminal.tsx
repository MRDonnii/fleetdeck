import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Code, Drawer, Group, Loader, Modal, PasswordInput, ScrollArea, SegmentedControl, Select, Stack, Switch, Text, TextInput, Title, Tooltip, UnstyledButton } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { IconBolt, IconLayoutColumns, IconList, IconPencil, IconPlus, IconSearch, IconShieldLock, IconStar, IconStarFilled, IconTerminal2, IconX } from '@tabler/icons-react';
import { ApiError } from '../api';
import { hasUnlock, setLockedHandler, setUnlock, term, type OpenSession, type Targets, type TermCredential, type TermHost, type TermMachine, type TermSnippet } from '../terminal/client';
import { TermView, type TermHandle } from '../terminal/TermView';
import { Vault } from '../terminal/Vault';
import { SnippetPanel } from '../terminal/Snippets';
import '../terminal/terminal.css';

type Gate = 'loading' | 'https' | 'needspin' | 'locked' | 'reauth' | 'open';
interface Tab { key: string; session: OpenSession; status: string; closed: boolean }

const fail = (e: unknown) => notifications.show({ color: 'red', message: e instanceof Error ? e.message : 'Noget gik galt' });

function SetPin({ change, onDone, onCancel }: { change: boolean; onDone: () => void; onCancel?: () => void }) {
  const [password, setPassword] = useState(''); const [pin, setPin] = useState(''); const [again, setAgain] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState('');
  const digits = (v: string) => v.replace(/\D/g, '').slice(0, 8);
  const ok = password.length > 0 && pin.length >= 4 && pin === again;
  const submit = async () => {
    if (!ok) return;
    setBusy(true); setError('');
    try { setUnlock((await term<{ token: string }>('POST', 'pin', { password, pin })).token); onDone(); }
    catch (e) { setError(e instanceof Error ? e.message : 'Pinkoden kunne ikke gemmes'); } finally { setBusy(false); }
  };
  return (
    <Stack align="center" justify="center" h="70vh" gap="sm">
      <IconShieldLock size={40} color="var(--mantine-primary-color-filled)" />
      <Title order={3}>{change ? 'Vælg en ny pinkode' : 'Vælg en pinkode til terminalen'}</Title>
      <Text c="dimmed" size="sm" ta="center" maw={360}>Pinkoden skal være 4 til 8 cifre. Bekræft med adgangskoden til din konto.</Text>
      <PasswordInput w={260} label="Adgangskode" autoFocus value={password} onChange={(e) => setPassword(e.currentTarget.value)} />
      <PasswordInput w={260} label="Ny pinkode" inputMode="numeric" value={pin} onChange={(e) => setPin(digits(e.currentTarget.value))} />
      <PasswordInput w={260} label="Gentag pinkoden" inputMode="numeric" value={again} error={again && pin !== again ? 'Pinkoderne er ikke ens' : undefined} onChange={(e) => setAgain(digits(e.currentTarget.value))} onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }} />
      <Button loading={busy} disabled={!ok} onClick={() => { void submit(); }}>Gem pinkode</Button>
      {onCancel && <Button variant="subtle" size="xs" onClick={onCancel}>Tilbage</Button>}
      {error && <Text c="red" size="sm">{error}</Text>}
    </Stack>
  );
}

function Unlock({ reauth, onDone }: { reauth: boolean; onDone: () => void }) {
  const [code, setCode] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [forgot, setForgot] = useState(false);
  const submit = async () => {
    if (code.length < 4) return;
    setBusy(true); setError('');
    try {
      if (reauth) await term('POST', 'reauth', { code });
      else setUnlock((await term<{ token: string }>('POST', 'unlock', { code })).token);
      onDone();
    } catch (e) { setError(e instanceof Error ? e.message : 'Forkert pinkode'); setCode(''); } finally { setBusy(false); }
  };
  if (forgot) return <SetPin change onDone={onDone} onCancel={() => setForgot(false)} />;
  return (
    <Stack align="center" justify="center" h="70vh" gap="md">
      <IconShieldLock size={40} color="var(--mantine-primary-color-filled)" />
      <Title order={3}>{reauth ? 'Skriv din pinkode igen' : 'Lås terminalen op'}</Title>
      <Text c="dimmed" size="sm" ta="center" maw={360}>{reauth ? 'Terminalen har ikke været brugt i 15 minutter.' : 'Skriv din pinkode.'}</Text>
      <PasswordInput w={220} aria-label="Pinkode" placeholder="Pinkode" inputMode="numeric" autoComplete="off" autoFocus value={code} error={!!error} disabled={busy} onChange={(e) => setCode(e.currentTarget.value.replace(/\D/g, '').slice(0, 8))} onKeyDown={(e) => { if (e.key === 'Enter') void submit(); }} />
      <Button loading={busy} disabled={code.length < 4} onClick={() => { void submit(); }}>Lås op</Button>
      <Button variant="subtle" size="xs" onClick={() => setForgot(true)}>Glemt pinkode?</Button>
      {error && <Text c="red" size="sm">{error}</Text>}
    </Stack>
  );
}

function AddHost({ opened, edit, creds, onClose, onSaved }: { opened: boolean; edit: TermHost | null; creds: TermCredential[]; onClose: () => void; onSaved: () => void }) {
  const empty = { name: '', address: '', port: 22, username: '', group: '', credentialId: null as number | null, protected: false };
  const [f, setF] = useState(empty); const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (opened) setF(edit ? { name: edit.name, address: edit.address, port: edit.port, username: edit.username, group: edit.group ?? '', credentialId: edit.credentialId ?? null, protected: edit.protected } : empty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, edit]);
  const save = async () => {
    setBusy(true);
    try { await (edit ? term('PUT', `hosts/${edit.id}`, f) : term('POST', 'hosts', f)); onSaved(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); }
  };
  const remove = async () => {
    setBusy(true);
    try { await term('DELETE', `hosts/${edit!.id}`); onSaved(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={opened} onClose={onClose} title={edit ? `Redigér ${edit.name}` : 'Tilføj vært'}>
      <Stack gap="sm">
        <TextInput label="Navn" value={f.name} onChange={(e) => setF({ ...f, name: e.currentTarget.value })} data-autofocus />
        <Group grow align="flex-start">
          <TextInput label="Adresse" value={f.address} onChange={(e) => setF({ ...f, address: e.currentTarget.value })} />
          <TextInput label="Port" maw={90} value={String(f.port)} onChange={(e) => setF({ ...f, port: Number(e.currentTarget.value) || 22 })} />
        </Group>
        <Group grow>
          <TextInput label="Brugernavn" value={f.username} onChange={(e) => setF({ ...f, username: e.currentTarget.value })} />
          <TextInput label="Gruppe" value={f.group} onChange={(e) => setF({ ...f, group: e.currentTarget.value })} />
        </Group>
        <Select label="Login fra nøgleboksen" placeholder={creds.length ? 'Vælg login' : 'Opret først et login i nøgleboksen'} data={creds.map((c) => ({ value: String(c.id), label: `${c.name} (${c.kind === 'key' ? 'nøgle' : 'adgangskode'})` }))}
          value={f.credentialId ? String(f.credentialId) : null} onChange={(v) => setF({ ...f, credentialId: v ? Number(v) : null })} />
        <Switch label="Beskyttet: risikable kommandoer kræver, at navnet skrives" checked={f.protected} onChange={(e) => setF({ ...f, protected: e.currentTarget.checked })} />
        <Alert color="yellow" variant="light">Værtsnøglen gemmes ved første forbindelse. Ændrer den sig senere, stopper Fleetdeck forbindelsen, til en administrator har godkendt den nye.</Alert>
        <Group justify="flex-end">{edit && <Button mr="auto" color="red" variant="subtle" loading={busy} onClick={() => { void remove(); }}>Slet vært</Button>}<Button variant="default" onClick={onClose}>Annullér</Button><Button loading={busy} onClick={() => { void save(); }}>Gem vært</Button></Group>
      </Stack>
    </Modal>
  );
}

/** Værtsnøglen er ændret: forbindelsen er stoppet, til en administrator har sammenlignet og godkendt den nye nøgle. */
function HostKeyChanged({ host, onClose, onDone }: { host: TermHost | null; onClose: () => void; onDone: () => void }) {
  const [busy, setBusy] = useState(false);
  const accept = async () => {
    setBusy(true);
    try { await term('POST', `hosts/${host!.id}/accept-hostkey`, { fingerprint: host!.pendingKeyFingerprint }); onDone(); onClose(); } catch (e) { fail(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened={!!host} onClose={onClose} title={`Værtsnøglen på ${host?.name ?? ''} er ændret`}>
      <Stack gap="sm">
        <Alert color="red" variant="light">Det kan skyldes en geninstallation, men det kan også være et forsøg på at udgive sig for værten. Godkend kun, hvis du ved, hvorfor nøglen er ændret.</Alert>
        <div><Text size="xs" c="dimmed">Gemt nøgle</Text><Code block>{host?.hostKeyFingerprint}</Code></div>
        <div><Text size="xs" c="dimmed">Ny nøgle</Text><Code block>{host?.pendingKeyFingerprint}</Code></div>
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button color="red" loading={busy} onClick={() => { void accept(); }}>Godkend den nye nøgle</Button></Group>
      </Stack>
    </Modal>
  );
}

export function TerminalPage() {
  const [gate, setGate] = useState<Gate>('loading');
  const [targets, setTargets] = useState<Targets>({ machines: [], hosts: [] });
  const [tabs, setTabs] = useState<Tab[]>([]); const [active, setActive] = useState<string | null>(null);
  const [q, setQ] = useState(''); const [adding, setAdding] = useState(false);
  const [creds, setCreds] = useState<TermCredential[]>([]); const [view, setView] = useState('hosts'); const [keyHost, setKeyHost] = useState<TermHost | null>(null); const [editHost, setEditHost] = useState<TermHost | null>(null);
  const [snippets, setSnippets] = useState<TermSnippet[]>([]); const [showSnips, setShowSnips] = useState(true); const [snipsOpen, setSnipsOpen] = useState(false);
  const [split, setSplit] = useState<string | null>(null); const [focused, setFocused] = useState<string | null>(null); const [phoneList, setPhoneList] = useState(false);
  const [mods, setMods] = useState({ ctrl: false, alt: false });
  const touch = useMediaQuery('(pointer: coarse)') ?? false;
  const views = useRef<Record<string, TermHandle | null>>({});
  const phone = useMediaQuery('(max-width: 48em)') ?? false;

  const load = useCallback(async () => { try { setTargets(await term<Targets>('GET', 'targets')); setCreds(await term<TermCredential[]>('GET', 'credentials')); setSnippets(await term<TermSnippet[]>('GET', 'snippets')); } catch (e) { if (!(e instanceof ApiError && e.status === 423)) fail(e); } }, []);
  useEffect(() => {
    setLockedHandler((c) => setGate(c));
    void term<{ https: boolean; pinSet: boolean; unlock: string }>('GET', 'state').then((s) => {
      if (!s.https) setGate('https'); else if (!s.pinSet) setGate('needspin');
      else setGate(hasUnlock() && s.unlock === 'active' ? 'open' : s.unlock === 'reauth' ? 'reauth' : 'locked');
    }).catch(fail);
  }, []);
  useEffect(() => { if (gate === 'open') void load(); }, [gate, load]);
  // iPhone: terminalen skal slutte lige over skærmtastaturet
  useEffect(() => {
    const v = window.visualViewport; if (!v) return;
    const set = () => document.documentElement.style.setProperty('--fd-term-vh', `${v.height}px`);
    set(); v.addEventListener('resize', set);
    return () => { v.removeEventListener('resize', set); document.documentElement.style.removeProperty('--fd-term-vh'); };
  }, []);

  const open = async (body: { machineId?: number; hostId?: number }) => {
    try {
      const session = await term<OpenSession>('POST', 'sessions', { ...body, cols: 120, rows: 32 });
      setTabs((t) => [...t, { key: session.id, session, status: '', closed: false }]); setActive(session.id); setFocused(session.id); setPhoneList(false); setView('hosts');
    } catch (e) { fail(e); }
  };
  const close = (key: string) => {
    void term('DELETE', `sessions/${key}`).catch(() => {});
    setTabs((t) => { const n = t.filter((x) => x.key !== key); setActive((a) => (a === key ? n.filter((x) => x.key !== split)[0]?.key ?? n[0]?.key ?? null : a)); return n; });
    setSplit((s) => (s === key ? null : s));
  };
  const patch = (key: string, p: Partial<Tab>) => setTabs((t) => t.map((x) => (x.key === key ? { ...x, ...p } : x)));
  const setPref = async (m: TermMachine, p: { favorite?: boolean; protected?: boolean }) => { try { await term('PUT', `machines/${m.id}`, p); await load(); } catch (e) { fail(e); } };

  const toggleProtected = (m: TermMachine) => {
    if (!m.protected) { void setPref(m, { protected: true }); return; }
    modals.openConfirmModal({ title: `Fjern beskyttelsen af ${m.name}?`, labels: { confirm: 'Fjern', cancel: 'Annullér' }, confirmProps: { color: 'red' },
      children: <Text size="sm">Risikable kommandoer kræver så ikke længere, at maskinens navn skrives.</Text>, onConfirm: () => { void setPref(m, { protected: false }); } });
  };

  const match = useCallback((n: string) => n.toLowerCase().includes(q.trim().toLowerCase()), [q]);
  const machines = useMemo(() => targets.machines.filter((m) => match(m.name)).sort((a, b) => Number(b.favorite) - Number(a.favorite)), [targets, match]);
  const hosts = useMemo(() => targets.hosts.filter((h) => match(h.name) || match(h.address)).sort((a, b) => Number(b.favorite) - Number(a.favorite)), [targets, match]);

  if (gate === 'loading') return <Stack align="center" h="60vh" justify="center"><Loader /></Stack>;
  if (gate === 'https') return <Alert color="yellow" title="Terminalen er ikke tilgængelig herfra" maw={560} mx="auto" mt="xl">Åbn Fleetdeck over https, fra hjemmenettet eller via VPN.</Alert>;
  if (gate === 'needspin') return <SetPin change={false} onDone={() => setGate('open')} />;
  if (gate === 'locked' || gate === 'reauth') return <Unlock reauth={gate === 'reauth'} onDone={() => setGate('open')} />;

  const runTargets = tabs.filter((t) => !t.closed).map((t) => ({ id: t.key, label: t.session.label, protected: t.session.protected, platform: t.session.platform }));
  const showList = !phone || tabs.length === 0 || phoneList;
  const target = focused && tabs.some((t) => t.key === focused) ? focused : active;
  const sendKey = (seq: string) => { views.current[target ?? '']?.send(seq); views.current[target ?? '']?.focus(); };
  const toggleMod = (k: 'ctrl' | 'alt') => { const m = { ...mods, [k]: !mods[k] }; setMods(m); views.current[target ?? '']?.setMods(m); views.current[target ?? '']?.focus(); };
  const toggleSplit = () => setSplit((s) => (s ? null : tabs.find((t) => t.key !== active)?.key ?? null));
  const shown = (key: string) => key === active || key === split;
  const row = (key: string, name: string, sub: string, online: boolean, fav: boolean, prot: boolean, off: string | null | undefined, go: () => void, star?: () => void, edit?: () => void, shield?: () => void) => (
    <Tooltip key={key} label={off} disabled={!off} position="right" multiline w={240}>
      <UnstyledButton className="fd-term-row" data-off={!online || !!off || undefined} onClick={() => { if (online && !off) go(); else if (edit) edit(); }}>
        <span className="fd-dot" style={{ color: online ? 'var(--fd-ok)' : 'var(--fd-off)' }} />
        <span className="fd-term-name"><b>{name}</b><small>{sub}</small></span>
        {shield ? <Tooltip label={prot ? 'Beskyttet maskine: tryk for at fjerne' : 'Markér som beskyttet'}><ActionIcon component="span" variant="subtle" color={prot ? 'yellow' : 'gray'} size="sm" aria-label="Beskyttet maskine" onClick={(e: React.MouseEvent) => { e.stopPropagation(); shield(); }}><IconShieldLock size={15} /></ActionIcon></Tooltip>
          : prot && <Tooltip label="Beskyttet"><IconShieldLock size={15} color="var(--fd-warn)" /></Tooltip>}
        {edit && <ActionIcon component="span" variant="subtle" color="gray" size="sm" aria-label="Redigér" onClick={(e: React.MouseEvent) => { e.stopPropagation(); edit(); }}><IconPencil size={14} /></ActionIcon>}
        {star && <ActionIcon component="span" variant="subtle" color={fav ? 'yellow' : 'gray'} size="sm" aria-label="Favorit" onClick={(e: React.MouseEvent) => { e.stopPropagation(); star(); }}>{fav ? <IconStarFilled size={14} /> : <IconStar size={14} />}</ActionIcon>}
      </UnstyledButton>
    </Tooltip>
  );

  return (
    <div className="fd-term" data-session={(phone && tabs.length > 0 && !phoneList) || undefined}>
      {showList && (
        <aside className="fd-term-list">
          <SegmentedControl size="xs" fullWidth mb={8} value={view} onChange={setView} data={[{ value: 'hosts', label: 'Værter' }, { value: 'vault', label: 'Nøgleboks' }]} />
          <Group gap={6} wrap="nowrap">
            <TextInput flex={1} size="sm" leftSection={<IconSearch size={15} />} placeholder="Søg i værter" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
            <Tooltip label="Tilføj vært"><ActionIcon size="lg" onClick={() => { setEditHost(null); setAdding(true); }} aria-label="Tilføj vært"><IconPlus size={17} /></ActionIcon></Tooltip>
          </Group>
          <ScrollArea flex={1} mt={6}>
            <Text className="fd-group">Maskiner</Text>
            {machines.map((m: TermMachine) => row(`m${m.id}`, m.name, `${m.group ?? m.platform} · Agent`, m.online, m.favorite, m.protected, m.unavailable, () => { void open({ machineId: m.id }); }, () => { void setPref(m, { favorite: !m.favorite }); }, undefined, () => toggleProtected(m)))}
            {hosts.length > 0 && <Text className="fd-group">Andre værter</Text>}
            {hosts.map((h: TermHost) => row(`h${h.id}`, h.name, `${h.username}@${h.address} · SSH`, true, h.favorite, h.protected, h.credentialId ? null : 'Vælg et login til værten', () => { if (h.pendingKeyFingerprint) setKeyHost(h); else void open({ hostId: h.id }); }, undefined, () => { setEditHost(h); setAdding(true); }))}
          </ScrollArea>
        </aside>
      )}
      <section className="fd-term-main" data-hidden={(showList && phone && view !== 'vault') || undefined}>
        {view === 'vault' ? <Vault creds={creds} hosts={targets.hosts} reload={() => { void load(); }} /> : tabs.length === 0 ? (
          <Stack align="center" justify="center" h="100%" gap={6}><IconTerminal2 size={36} opacity={0.5} /><Text c="dimmed">Vælg en maskine for at åbne en terminal.</Text></Stack>
        ) : (
          <>
            <div className="fd-term-tabs">
              {tabs.map((t) => (
                <UnstyledButton key={t.key} className="fd-term-tab" data-active={shown(t.key) || undefined} onClick={() => { if (t.key !== split) setActive(t.key); setFocused(t.key); }}>
                  {t.session.protected && <IconShieldLock size={13} color="var(--fd-warn)" />}{t.session.label}
                  <ActionIcon component="span" size="xs" variant="subtle" color="gray" aria-label="Luk" onClick={(e: React.MouseEvent) => { e.stopPropagation(); close(t.key); }}><IconX size={12} /></ActionIcon>
                </UnstyledButton>
              ))}
            </div>
            <div className="fd-term-tabtools">
              {phone && <Tooltip label="Værter"><ActionIcon variant="subtle" color="gray" aria-label="Værter" onClick={() => setPhoneList(true)}><IconList size={16} /></ActionIcon></Tooltip>}
              {!phone && tabs.length > 1 && <Tooltip label={split ? 'Luk delt visning' : 'Delt visning'}><ActionIcon variant={split ? 'light' : 'subtle'} color="gray" aria-label="Delt visning" onClick={toggleSplit}><IconLayoutColumns size={16} /></ActionIcon></Tooltip>}
              <Tooltip label="Kommandoer"><ActionIcon variant={showSnips ? 'light' : 'subtle'} color="gray" aria-label="Kommandoer" onClick={() => { if (phone) setSnipsOpen(true); else setShowSnips((v) => !v); }}><IconBolt size={16} /></ActionIcon></Tooltip>
            </div>
            <div className="fd-term-panes" data-split={split ? true : undefined}>
              {tabs.map((t) => (
                <div key={t.key} className="fd-term-pane" data-focus={(split && t.key === target) || undefined} style={{ display: shown(t.key) ? 'block' : 'none', order: t.key === split ? 2 : 1 }}>
                  <TermView ref={(h) => { views.current[t.key] = h; }} session={t.session} active={shown(t.key)} onFocus={() => setFocused(t.key)} onModsUsed={() => setMods({ ctrl: false, alt: false })}
                    onStatus={(s) => patch(t.key, { status: s })} onExit={() => patch(t.key, { closed: true })} />
                  {(t.status || t.closed) && <Badge className="fd-term-status" color={t.closed ? 'gray' : 'blue'}>{t.status || 'Forbindelsen er lukket.'}</Badge>}
                </div>
              ))}
            </div>
            {(phone || touch) && (
              <div className="fd-term-keys" onPointerDown={(e) => e.preventDefault()}>
                <button type="button" onClick={() => sendKey('\x1b')}>Esc</button>
                <button type="button" onClick={() => sendKey('\t')}>Tab</button>
                <button type="button" data-on={mods.ctrl || undefined} onClick={() => toggleMod('ctrl')}>Ctrl</button>
                <button type="button" data-on={mods.alt || undefined} onClick={() => toggleMod('alt')}>Alt</button>
                {([['↑', '\x1b[A'], ['↓', '\x1b[B'], ['←', '\x1b[D'], ['→', '\x1b[C'], ['|', '|'], ['/', '/'], ['~', '~'], ['-', '-']] as const).map(([l, s]) => <button key={l} type="button" onClick={() => sendKey(s)}>{l}</button>)}
                <button type="button" onClick={() => views.current[target ?? '']?.paste()}>Indsæt</button>
              </div>
            )}
          </>
        )}
      </section>
      {view === 'hosts' && tabs.length > 0 && showSnips && !phone && (
        <aside className="fd-term-snips"><SnippetPanel snippets={snippets} targets={runTargets} activeId={active} reload={() => { void load(); }} /></aside>
      )}
      <Drawer opened={phone && snipsOpen} onClose={() => setSnipsOpen(false)} position="bottom" size="70%" title="Kommandoer">
        <SnippetPanel snippets={snippets} targets={runTargets} activeId={active} reload={() => { void load(); }} />
      </Drawer>
      <HostKeyChanged host={keyHost} onClose={() => setKeyHost(null)} onDone={() => { void load(); }} />
      <AddHost opened={adding} edit={editHost} creds={creds} onClose={() => setAdding(false)} onSaved={() => { void load(); }} />
    </div>
  );
}
