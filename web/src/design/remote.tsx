import { useState } from 'react';
import { ActionIcon, Button, Group, Modal, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { CopyButton } from './copy';
import { IconScreenShare, IconWorldWww } from '@tabler/icons-react';
import { get, notifyErr, put, useAction } from '../api';

/** Hvor RustDesk hentes til den enhed, man sidder ved. */
function rustdeskDownload(): { label: string; url: string } {
  const ua = navigator.userAgent;
  if (/iPhone|iPad|iPod/i.test(ua) || (/Macintosh/i.test(ua) && navigator.maxTouchPoints > 1)) return { label: 'RustDesk i App Store', url: 'https://apps.apple.com/app/rustdesk-remote-desktop/id1581225015' };
  if (/Android/i.test(ua)) return { label: 'RustDesk til Android', url: 'https://play.google.com/store/apps/details?id=com.carriez.flutter_hbb' };
  return { label: 'Hent RustDesk', url: 'https://rustdesk.com/download' };
}

/**
 * Fjernskrivebord med ét klik: henter ID og den faste adgangskode, vækker skærmen (serveren) og åbner RustDesk på denne enhed.
 * Åbner RustDesk ikke (appen mangler, eller browseren afviser linket), vises ID, adgangskode og hentelink.
 */
export function RemoteButton({ id, name, remote, size, fullWidth, disabledLook }: {
  id: number; name: string; remote?: { id: string; error?: string | null } | null;
  size?: 'xs' | 'sm' | 'md' | 'compact-sm'; fullWidth?: boolean; disabledLook?: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const [info, setInfo] = useState<{ id: string; password?: string; link: string } | null>(null);
  const [missed, setMissed] = useState(false);
  if (!remote?.id) {
    return disabledLook ? <Button size={size} fullWidth={fullWidth} variant="light" color="teal" disabled leftSection={<IconScreenShare size={15} />}
      title="Fjernskrivebord er ikke sat op (installér udvidelsen Fjernskrivebord)">Forbind</Button> : null;
  }
  const launch = (link: string) => {
    setMissed(false);
    // forlader siden ikke fokus inden for 2,5 sek., åbnede RustDesk ikke
    let left = false;
    const away = () => { left = true; };
    window.addEventListener('blur', away, { once: true });
    document.addEventListener('visibilitychange', away, { once: true });
    try { (window.top ?? window).location.href = link; } catch { window.location.href = link; }
    setTimeout(() => { window.removeEventListener('blur', away); document.removeEventListener('visibilitychange', away); if (!left) setMissed(true); }, 2500);
  };
  const open = async (e?: React.MouseEvent) => {
    e?.preventDefault(); e?.stopPropagation();
    setBusy(true);
    try {
      const d = await get<{ id: string; password?: string; link: string }>(`/api/machines/${id}/remote`);
      setInfo(d);
      launch(d.link);
    } catch (err) { notifyErr(err); } finally { setBusy(false); }
  };
  const dl = rustdeskDownload();
  return (
    <>
      <Button size={size} fullWidth={fullWidth} variant="light" color="teal" leftSection={<IconScreenShare size={15} />} loading={busy} onClick={open}
        title={remote.error ?? `RustDesk ${remote.id} – forbinder uden at nogen skal godkende på maskinen`}>Forbind</Button>
      <Modal opened={!!info} onClose={() => setInfo(null)} title={`Fjernskrivebord · ${name}`} centered>
        {info && (
          <Stack onClick={(e) => e.stopPropagation()}>
            <Button component="a" href={info.link} target="_top" size="md" color="teal" leftSection={<IconScreenShare size={18} />}
              onClick={(e: React.MouseEvent) => { e.preventDefault(); launch(info.link); }}>Åbn RustDesk</Button>
            <Text size="sm" c="dimmed">Skærmen på {name} vækkes samtidig. Er den sort et øjeblik, så vent op til 20 sekunder – en kiosk skal lige starte siden igen.</Text>
            {missed && <Text size="sm" c="orange">RustDesk åbnede ikke på denne enhed. Installér appen, og tryk igen – eller skriv ID og adgangskode ind i RustDesk selv.</Text>}
            <Group grow>
              <CopyButton value={info.id}>{({ copied, copy }) => <Button variant="default" onClick={copy}>{copied ? 'Kopieret' : `ID ${info.id}`}</Button>}</CopyButton>
              {info.password && <CopyButton value={info.password}>{({ copied, copy }) => <Button variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér adgangskode'}</Button>}</CopyButton>}
            </Group>
            <Button component="a" href={dl.url} target="_blank" rel="noreferrer" variant="subtle">{dl.label}</Button>
            {remote.error && <Text size="xs" c="red">{remote.error}</Text>}
          </Stack>
        )}
      </Modal>
    </>
  );
}

/**
 * "Forbind": fjernskrivebord (RustDesk), maskinens webflade (fx Unraid eller en styring med egen webflade) – eller begge.
 * Har maskinen kun en webflade, åbner knappen den i en ny fane.
 */
export function ConnectButton({ web, compact, ...p }: Parameters<typeof RemoteButton>[0] & { web?: string | null; compact?: boolean }) {
  const hasRemote = !!p.remote?.id;
  if (!hasRemote && web) {
    return <Button component="a" href={web} target="_blank" rel="noopener" size={p.size} fullWidth={p.fullWidth} variant="light" color="teal"
      leftSection={<IconWorldWww size={15} />} title={`Åbn webfladen (${web})`} onClick={(e: React.MouseEvent) => e.stopPropagation()}>Forbind</Button>;
  }
  if (hasRemote && web && !compact) {
    return (
      <Group gap={6} wrap="nowrap">
        <RemoteButton {...p} />
        <Tooltip label={`Åbn webfladen (${web})`}><ActionIcon component="a" href={web} target="_blank" rel="noopener" variant="light" color="teal" size={p.size === 'md' ? 36 : 'lg'}>
          <IconWorldWww size={18} /></ActionIcon></Tooltip>
      </Group>
    );
  }
  return <RemoteButton {...p} />;
}

/** Felt på maskinens side: hvilken webflade "Forbind" åbner. */
export function WebUrlField({ id, value, auto }: { id: number; value?: string | null; auto?: boolean }) {
  const [v, setV] = useState(value ?? '');
  const save = useAction(() => put(`/api/machines/${id}/weburl`, { url: v }), 'Webfladen er gemt');
  return (
    <Group align="flex-end" gap="xs" wrap="nowrap">
      <TextInput style={{ flex: 1 }} label="Webflade" description={auto ? 'Fundet automatisk – skriv en anden for at ændre' : '"Forbind" åbner den, fx http://192.168.1.12:8080'}
        placeholder="http://…" value={v} onChange={(e) => setV(e.currentTarget.value)} leftSection={<IconWorldWww size={16} />} />
      <Button variant="default" loading={save.isPending} disabled={v === (value ?? '')} onClick={() => save.mutate(undefined)}>Gem</Button>
    </Group>
  );
}
