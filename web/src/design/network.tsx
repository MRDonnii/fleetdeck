import { useState } from 'react';
import { Badge, Box, Button, Code, Collapse, Group, Stack, Text, ThemeIcon, Tooltip } from '@mantine/core';
import { IconChevronDown, IconChevronUp, IconDeviceMobile, IconNetwork, IconPlug, IconWifi, IconWifiOff } from '@tabler/icons-react';
import { KeyValue, Panel } from '../components';
import {
  detailRows, hiddenText, NET_KIND, netStatus, signalBars, signalTone, speedLine, speedWarn, summaryText, visibleAdapters,
  type NetAdapter, type NetworkInfo,
} from './networkText';

const toneColor = (t: string) => (t === 'gray' ? 'gray' : t);

function KindIcon({ a }: { a: NetAdapter }) {
  const off = a.status !== 'connected' && a.status !== 'limited';
  const icon = a.kind === 'wifi' ? (off ? <IconWifiOff size={18} /> : <IconWifi size={18} />)
    : a.kind === 'mobile' ? <IconDeviceMobile size={18} /> : a.kind === 'ethernet' ? <IconNetwork size={18} /> : <IconPlug size={18} />;
  return <Tooltip label={NET_KIND[a.kind]}><ThemeIcon variant="light" color={off ? 'gray' : a.primary ? 'blue' : 'teal'} size={34} radius="md">{icon}</ThemeIcon></Tooltip>;
}

/** Lille signalbjælke med fire streger, farvet efter styrken. */
export function SignalBars({ pct }: { pct?: number | null }) {
  const n = signalBars(pct);
  const c = `var(--mantine-color-${signalTone(pct)}-6)`;
  return (
    <Tooltip label={pct != null ? `Signal ${pct} %` : 'Signal ukendt'}>
      <Group gap={2} align="flex-end" wrap="nowrap" style={{ height: 14 }} aria-label={pct != null ? `Signal ${pct} %` : 'Signal ukendt'}>
        {[1, 2, 3, 4].map((i) => (
          <span key={i} style={{ width: 4, height: 3 + i * 3, borderRadius: 1, background: i <= n ? c : 'var(--mantine-color-default-border)' }} />
        ))}
      </Group>
    </Tooltip>
  );
}

function AdapterRow({ a }: { a: NetAdapter }) {
  const [open, setOpen] = useState(false);
  const st = netStatus(a);
  const speed = speedLine(a);
  const up = a.status === 'connected' || a.status === 'limited';
  return (
    <Box py={8} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
      <Group gap="sm" wrap="nowrap" align="center">
        <KindIcon a={a} />
        <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
          <Group gap={6} wrap="wrap">
            <Text fw={600} size="sm">{a.name}</Text>
            {a.primary && <Badge size="xs" variant="filled" color="blue">Primær</Badge>}
            {/* Oppe er en klar grøn (fyldt med prik), så man med ét blik kan se, at adapteren er tilsluttet. */}
            <Badge size="xs" variant={st.tone === 'green' ? 'filled' : 'light'} color={toneColor(st.tone)}
              leftSection={st.tone === 'green' ? <Box component="span" w={6} h={6} style={{ borderRadius: 999, background: 'currentColor', display: 'inline-block' }} /> : undefined}>{st.text}</Badge>
            {a.virtual && <Badge size="xs" variant="outline" color="gray">Virtuel</Badge>}
          </Group>
          <Group gap={8} wrap="wrap" style={{ rowGap: 2 }}>
            {speed && <Text size="xs" fw={speedWarn(a) ? 700 : 400} c={speedWarn(a) ? 'orange' : 'dimmed'}>{speed}</Text>}
            {up && a.ipv4[0] && <Code fz="xs">{a.ipv4[0]}</Code>}
            {a.kind === 'wifi' && up && a.wifi && (
              <Group gap={4} wrap="nowrap"><SignalBars pct={a.wifi.signalPct} />
                {a.wifi.signalPct != null && <Text size="xs" c={signalTone(a.wifi.signalPct) === 'red' ? 'red' : 'dimmed'}>{a.wifi.signalPct} %</Text>}
                {a.wifi.ssid && <Text size="xs" c="dimmed" lineClamp={1}>{a.wifi.ssid}</Text>}
              </Group>
            )}
            {a.description && <Text size="xs" c="dimmed" lineClamp={1} style={{ minWidth: 0 }}>{a.description}</Text>}
          </Group>
        </Stack>
        <Button size="compact-xs" variant="subtle" onClick={() => setOpen((o) => !o)} rightSection={open ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
          aria-expanded={open}>Detaljer</Button>
      </Group>
      <Collapse expanded={open}>
        <Box pl={{ base: 0, sm: 46 }} pt={6}><KeyValue rows={detailRows(a)} /></Box>
      </Collapse>
    </Box>
  );
}

/** Netværk på maskinens side: en kompakt række pr. adapter (primær øverst), med detaljer der folder ud. */
export function NetworkPanel({ net }: { net?: NetworkInfo | null }) {
  const [showAll, setShowAll] = useState(false);
  if (!net) return null;
  const list = visibleAdapters(net, showAll);
  const hidden = hiddenText(net, showAll);
  const sum = summaryText(net);
  return (
    <Panel title="Netværk" right={sum ? <Text size="sm" c="dimmed">{sum}</Text> : undefined}>
      {list.length === 0 && <Text size="sm" c="dimmed">Ingen netværksadaptere fundet.</Text>}
      {list.map((a) => <AdapterRow key={a.name} a={a} />)}
      {(hidden || net.wifiNote || net.error) && (
        <Group justify="space-between" mt={6} gap="xs">
          <Stack gap={0}>
            {net.wifiNote && <Text size="xs" c="dimmed">{net.wifiNote}</Text>}
            {net.error && <Text size="xs" c="dimmed">{net.error}</Text>}
          </Stack>
          {hidden && <Button size="compact-xs" variant="subtle" color="gray" onClick={() => setShowAll((s) => !s)}>{hidden}</Button>}
        </Group>
      )}
    </Panel>
  );
}
