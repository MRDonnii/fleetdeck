import { useEffect, useMemo, useState } from 'react';
import { Button, Group, Select, SimpleGrid, Stack, Text } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { notifyErr, notifyOk, put, useApi } from '../api';
import { Panel } from '../components';

type Opt = { id: string; name: string };
interface InstallLocaleInfo {
  language: string; keyboard: string; timeZone: string; windowsTimeZone: string;
  options: { languages: (Opt & { keyboard: string })[]; keyboards: Opt[]; serverTimeZone: string };
}

/** Udrulning: sprog, tastatur og tidszone, som Windows, Ubuntu og iPads får, når Fleetdeck installerer dem. */
export function InstallLocalePanel({ canEdit }: { canEdit: boolean }) {
  const qc = useQueryClient();
  const { data } = useApi<InstallLocaleInfo>(['install-locale'], '/api/settings/install-locale');
  const [f, setF] = useState<{ language: string; keyboard: string; timeZone: string } | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (data) setF({ language: data.language, keyboard: data.keyboard, timeZone: data.timeZone }); }, [data]);
  const zones = useMemo(() => {
    let list: string[] = [];
    try { list = Intl.supportedValuesOf('timeZone'); } catch { /* ældre browser */ }
    return [...new Set([data?.timeZone ?? '', ...list].filter(Boolean))].sort();
  }, [data?.timeZone]);
  if (!data || !f) return null;
  const dirty = f.language !== data.language || f.keyboard !== data.keyboard || f.timeZone !== data.timeZone;
  const save = async () => {
    setBusy(true);
    try { await put('/api/settings/install-locale', f); notifyOk('Gemt'); await qc.invalidateQueries({ queryKey: ['install-locale'] }); }
    catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Panel title="Sprog, tastatur og tidszone" right={canEdit && dirty ? <Button size="xs" onClick={save} loading={busy}>Gem</Button> : undefined}>
      <Stack gap="xs">
        <Text size="sm" c="dimmed">Bruges af netværksinstallationer (Windows og Ubuntu), USB-nøglen fra WinSetup og når iPads gøres overvågede.</Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <Select label="Sprog" data={data.options.languages.map((l) => ({ value: l.id, label: l.name }))} value={f.language} allowDeselect={false} disabled={!canEdit}
            onChange={(v) => v && setF({ ...f, language: v })} />
          <Select label="Tastatur" data={data.options.keyboards.map((k) => ({ value: k.id, label: k.name }))} value={f.keyboard} allowDeselect={false} disabled={!canEdit}
            onChange={(v) => v && setF({ ...f, keyboard: v })} />
          <Select label="Tidszone" data={zones} value={f.timeZone} searchable allowDeselect={false} limit={200} disabled={!canEdit}
            onChange={(v) => v && setF({ ...f, timeZone: v })} />
        </SimpleGrid>
        <Group gap="xs"><Text size="xs" c="dimmed">Windows-tidszone: {data.windowsTimeZone} · serverens tidszone: {data.options.serverTimeZone}</Text></Group>
      </Stack>
    </Panel>
  );
}
