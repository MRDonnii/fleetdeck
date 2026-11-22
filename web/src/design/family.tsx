import { useEffect, useMemo, useRef, useState } from 'react';
import { Button, Checkbox, Group, Modal, PasswordInput, SegmentedControl, SimpleGrid, Stack, Text, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import qrcode from 'qrcode-generator';
import { notifyErr, notifyOk, post, put, useApi } from '../api';
import type { IpadList, UserRow } from '../types';
import { devMeta } from './devices';

export const SLOTS = 336;
export const DAYS = ['Man', 'Tir', 'Ons', 'Tor', 'Fre', 'Lør', 'Søn'];

/** Standard for et barn: sengetid 20–07 før skoledage og 21–08 i weekenden (samme som serveren). */
export function defaultBedtime(): string {
  const c = Array<string>(SLOTS).fill('0');
  for (let d = 0; d < 7; d++) {
    const eve = d <= 3 || d === 6 ? 40 : 42;
    for (let s = eve; s < 48; s++) c[d * 48 + s] = 'b';
    const morning = d === 5 || d === 6 ? 16 : 14;
    for (let s = 0; s < morning; s++) c[d * 48 + s] = 'b';
  }
  return c.join('');
}

export interface Paint { ch: string; label: string; color: string }

/**
 * Ugeskema: 7 dage × 48 halve timer. Træk med fingeren eller musen for at male med den valgte farve
 * (virker også på iPad/telefon).
 */
export function WeekGrid({ value, onChange, paints, readOnly }: { value: string; onChange: (v: string) => void; paints: Paint[]; readOnly?: boolean }) {
  const [paint, setPaint] = useState(paints[paints.length - 1].ch);
  const v = (value || '').padEnd(SLOTS, paints[0].ch).slice(0, SLOTS);
  const cur = useRef(v); cur.current = v;
  const down = useRef(false);
  const color = (ch: string) => paints.find((p) => p.ch === ch)?.color ?? 'var(--fd-border)';
  const set = (i: number) => {
    if (readOnly || i < 0 || i >= SLOTS || cur.current[i] === paint) return;
    const next = cur.current.slice(0, i) + paint + cur.current.slice(i + 1);
    cur.current = next; onChange(next);
  };
  const at = (x: number, y: number) => {
    const el = document.elementFromPoint(x, y) as HTMLElement | null;
    const i = el?.dataset.i; if (i !== undefined) set(Number(i));
  };
  useEffect(() => { const up = () => { down.current = false; }; window.addEventListener('pointerup', up); return () => window.removeEventListener('pointerup', up); }, []);
  const hours = Array.from({ length: 24 }, (_, h) => h);
  return (
    <Stack gap={8}>
      {!readOnly && (
        <Group gap="xs" wrap="wrap">
          <SegmentedControl size="xs" value={paint} onChange={setPaint}
            data={paints.map((p) => ({ value: p.ch, label: <Group gap={6} wrap="nowrap"><span className="fd-swatch" style={{ background: p.color }} />{p.label}</Group> }))} />
          <Text size="xs" c="dimmed">Vælg en farve og træk hen over felterne.</Text>
        </Group>
      )}
      <div className="fd-week" style={{ touchAction: readOnly ? 'auto' : 'none' }}
        onPointerDown={(e) => { if (readOnly) return; down.current = true; (e.target as HTMLElement).releasePointerCapture?.(e.pointerId); at(e.clientX, e.clientY); }}
        onPointerMove={(e) => { if (down.current) at(e.clientX, e.clientY); }}>
        <span />
        {hours.map((h) => <span key={h} className="fd-week-h" style={{ gridColumn: `span 2` }}>{h % 3 === 0 ? String(h).padStart(2, '0') : ''}</span>)}
        {DAYS.map((d, di) => (
          <div key={d} style={{ display: 'contents' }}>
            <span className="fd-week-d">{d}</span>
            {Array.from({ length: 48 }, (_, s) => {
              const i = di * 48 + s;
              return <i key={s} data-i={i} style={{ background: color(v[i]) }} className={s % 2 === 0 ? 'fd-week-l' : undefined} />;
            })}
          </div>
        ))}
      </div>
    </Stack>
  );
}

export function Qr({ text, size = 240 }: { text: string; size?: number }) {
  const svg = useMemo(() => {
    const q = qrcode(0, 'L'); q.addData(text, 'Byte'); q.make();
    return q.createSvgTag({ cellSize: 4, margin: 4, scalable: true });   // fuld adresse inkl. http:// og stillezone på 4 moduler
  }, [text]);
  return <div style={{ width: size, height: size, background: '#fff', borderRadius: 8, padding: 4 }} dangerouslySetInnerHTML={{ __html: svg }} />;
}

export const PERM_LABELS: Record<string, string> = {
  pause: 'Pause internet og give ekstra tid',
  rules: 'Ændre regler, sengetid og YouTube',
};
/** Forældre styrer kun barnet. Opdatering, Apple-styring, backup og sletning er administratorens (serveren håndhæver det). */
export const PARENT_PERMS = ['pause', 'rules'];
export const ROLE_LABELS: Record<string, string> = { admin: 'Administrator', viewer: 'Læser', parent: 'Forælder' };

/** Opret eller ret en bruger – inkl. rollen Forælder med adgang til udvalgte iPads. */
export function UserModal({ user, opened, onClose }: { user: UserRow | null; opened: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const { data } = useApi<IpadList>(['ipads'], opened ? '/api/ipads' : null);
  const ipads = data?.ipads ?? [];
  const blank = { username: '', password: '', role: 'parent', ipadAll: false, ipads: [] as number[], perms: ['pause', 'rules'] };
  const [f, setF] = useState(blank);
  useEffect(() => {
    if (!opened) return;
    setF(user ? { username: user.username, password: '', role: user.role, ipadAll: user.ipadAll, ipads: user.ipads, perms: user.perms.length ? user.perms.filter((x) => PARENT_PERMS.includes(x)) : blank.perms } : blank);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, user]);
  const toggle = <T,>(list: T[], x: T) => (list.includes(x) ? list.filter((y) => y !== x) : [...list, x]);
  const save = async () => {
    try {
      if (user) await put(`/api/users/${user.id}`, { role: f.role, ipadAll: f.ipadAll, ipads: f.ipads, perms: f.perms });
      else await post('/api/users', f);
      notifyOk(user ? 'Brugeren er opdateret' : 'Brugeren er oprettet'); qc.invalidateQueries(); onClose();
    } catch (e) { notifyErr(e); }
  };
  return (
    <Modal opened={opened} onClose={onClose} title={user ? `Ret bruger: ${user.username}` : 'Ny bruger'} size="lg">
      <Stack>
        {!user && <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Brugernavn" value={f.username} onChange={(e) => setF({ ...f, username: e.currentTarget.value })} autoComplete="off" />
          <PasswordInput label="Adgangskode (min. 8 tegn)" value={f.password} onChange={(e) => setF({ ...f, password: e.currentTarget.value })} autoComplete="new-password" />
        </SimpleGrid>}
        <div>
          <Text size="sm" fw={500} mb={4}>Rolle</Text>
          <SegmentedControl fullWidth value={f.role} onChange={(v) => setF({ ...f, role: v })}
            data={[{ value: 'admin', label: 'Administrator' }, { value: 'parent', label: 'Forælder – kun iPads' }, { value: 'viewer', label: 'Læser – kun se' }]} />
          <Text size="xs" c="dimmed" mt={4}>
            {f.role === 'admin' ? 'Må alt: pc\'er, servere, iPads og indstillinger.' : f.role === 'viewer' ? 'Kan se pc\'er og servere, men ikke ændre noget. Ser ikke iPads.' : 'Ser kun iPad-siden og kun de iPads, der er valgt herunder.'}
          </Text>
        </div>
        {f.role === 'parent' && <>
          <div>
            <Text size="sm" fw={500} mb={6}>Hvilke enheder (iPads, Apple TV …)?</Text>
            <Checkbox mb={8} label="Alle enheder (også dem der kommer til senere)" checked={f.ipadAll} onChange={(e) => setF({ ...f, ipadAll: e.currentTarget.checked })} />
            {!f.ipadAll && (ipads.length ? <SimpleGrid cols={{ base: 1, sm: 2 }} spacing={6}>{ipads.map((i) => (
              <Checkbox key={i.id} label={`${i.name} · ${devMeta(i.deviceType).one}`} checked={f.ipads.includes(i.id)} onChange={() => setF({ ...f, ipads: toggle(f.ipads, i.id) })} />))}</SimpleGrid>
              : <Text size="sm" c="dimmed">Der er ingen enheder endnu – vælg "Alle enheder", eller tilføj enheder først.</Text>)}
          </div>
          <div>
            <Text size="sm" fw={500} mb={6}>Hvad må brugeren?</Text>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing={6}>{Object.entries(PERM_LABELS).map(([k, l]) => (
              <Checkbox key={k} label={l} checked={f.perms.includes(k)} onChange={() => setF({ ...f, perms: toggle(f.perms, k) })} />))}</SimpleGrid>
            <Text size="xs" c="dimmed" mt={6}>Forældre ser kun barnets styring (status, spær/tillad, pause og ekstra tid, regler, YouTube og aktivitet). Opsætning, Apple-styring, backup og sletning er kun for administratorer.</Text>
          </div>
        </>}
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button><Button onClick={save}>{user ? 'Gem' : 'Opret'}</Button></Group>
      </Stack>
    </Modal>
  );
}
