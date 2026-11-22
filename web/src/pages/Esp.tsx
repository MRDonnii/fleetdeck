import { useEffect, useMemo, useState, type ReactNode } from 'react';
import {
  ActionIcon, Alert, Anchor, Badge, Button, Code, CopyButton, Group, List, Modal, Paper, PasswordInput, SegmentedControl, SimpleGrid, Stack, Table,
  Tabs, Text, TextInput, ThemeIcon, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useQueryClient } from '@tanstack/react-query';
import {
  IconAlertTriangle, IconBolt, IconCertificate, IconCheck, IconChevronLeft, IconCloudCheck, IconCopy, IconDeviceDesktop, IconExternalLink,
  IconDownload, IconFingerprint, IconHexagon, IconInfoCircle, IconRefresh, IconSend, IconSettings, IconShieldCheck, IconUsb, IconWifi,
} from '@tabler/icons-react';
import { del, notifyErr, notifyOk, post, put, useApi } from '../api';
import { useMe } from '../auth';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { catalogNotice, filters, generatedLabel, needsWifi, platformOf, releases, tested, type EspCatalogFile, type EspFirmware } from '../esp/catalog';
import { provisionWifi, serialApi, type ProvisionResult } from '../esp/improv';
import { espEnv, probeAgent, type AgentProbe } from '../esp/local';

// ESP Web Tools (Apache-2.0) ligger i web/public/esp-web-tools – samme kopi som ESP Flasher bruger.
declare module 'react' {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace JSX {
    interface IntrinsicElements {
      'esp-web-install-button': DetailedHTMLProps<HTMLAttributes<HTMLElement>, HTMLElement> & { manifest?: string };
    }
  }
}

interface EspHttps {
  enabled: boolean; secure: boolean; host: string; error?: string | null; port?: number; url?: string; names?: string[]; covers?: boolean;
  selfSigned?: boolean; canAddName?: boolean; fingerprint?: string; validTo?: string;
}
interface EspStatus {
  firmware: { source: string; generated: string; live: boolean; checked: string | null; changed: string | null; error: string; count: number };
  catalogUrl: string | null; hasToken: boolean;
  wifi: { ssid: string | null; has: boolean; hasPassword: boolean };
  https: EspHttps;
  /** Porten, Fleetdeck-agenten lytter på (kun på pc'en selv): http://localhost:porten er en sikker side uden certifikat. */
  local?: { port: number };
}

/** Adresse under <base href>, så siden også virker under en understi. */
const abs = (rel: string) => new URL(rel, document.baseURI).href;
let toolsLoaded: Promise<unknown> | null = null;
const loadTools = () => (toolsLoaded ??= import(/* @vite-ignore */ abs('esp-web-tools/install-button.js')).catch((e) => { toolsLoaded = null; throw e; }));

/** Er vi allerede på pc'ens egen adresse (gennem agenten)? */
const onLocalhost = () => ['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname);
/** Telefon eller tablet: USB-flash kræver en pc, uanset hvilken adresse siden har. */
const isMobile = () => /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
/** ESP Flasher gennem Fleetdeck-agenten på denne pc. */
const localEspUrl = (port: number) => `http://localhost:${port}/esp`;

const SOURCE: Record<string, string> = { app: '', github: 'hentet fra nettet' };
/** Eksempel på en katalog-adresse (GitHub-mappe med catalog.json). */
const CATALOG_EXAMPLE = 'https://github.com/EJER/REPO/tree/main/mappe';

export function EspPage() {
  const { isAdmin } = useMe();
  const qc = useQueryClient();
  const { data: st } = useApi<EspStatus>(['esp'], '/api/esp', { refetchInterval: 60_000 });
  const { data: cat, error: catErr } = useApi<EspCatalogFile>(['esp', 'catalog', st?.firmware.generated ?? ''], st ? '/api/esp/fw/catalog.json' : null);
  const narrow = useMediaQuery('(max-width: 48em)');
  const [chip, setChip] = useState('any');
  const [kind, setKind] = useState('any');
  const [sel, setSel] = useState<string | null>(null);
  const [settings, setSettings] = useState(false);
  const [wifiFor, setWifiFor] = useState<EspFirmware | null>(null);
  const [checking, setChecking] = useState(false);
  const canUsb = typeof window !== 'undefined' && window.isSecureContext && !!serialApi();

  useEffect(() => { loadTools().catch(() => { /* vises som manglende knap */ }); }, []);

  const f = useMemo(() => filters(cat?.firmwares ?? [], chip, kind), [cat, chip, kind]);
  const chosen = f.items.find((x) => x.id === sel) ?? (narrow ? undefined : f.items[0]);

  if (!st) return <Loading />;
  const refresh = () => qc.invalidateQueries({ queryKey: ['esp'] });
  const check = async () => {
    setChecking(true);
    try {
      const r = await post<{ updated: boolean; firmware: EspStatus['firmware'] }>('/api/esp/check');
      if (r.updated) notifyOk('Ny firmware er hentet');
      else if (r.firmware.error) notifyErr(new Error(`Kunne ikke tjekke: ${r.firmware.error}`));
      else notifyOk('Du har den nyeste firmware');
      await refresh();
    } catch (e) { notifyErr(e); } finally { setChecking(false); }
  };

  const fw = st.firmware;
  const notice = catalogNotice(fw.live, fw.count);
  const openSettings = () => setSettings(true);
  const howTo = isAdmin
    ? <>Åbn <Anchor component="button" size="sm" onClick={openSettings}>Indstillinger</Anchor> øverst på siden og skriv adressen på mappen med catalog.json (samme format som ESP Flasher), fx <Code>{CATALOG_EXAMPLE}</Code>. Er repoet privat, så tilføj en GitHub-nøgle: et fine-grained token med Contents: Read-only til kun det repo.</>
    : <>Bed en administrator om at sætte et katalog op under ESP-flash → Indstillinger.</>;
  const noticeBox = notice === 'setup'
    ? <Alert color="blue" icon={<IconInfoCircle />} title="Intet firmwarekatalog sat op">
        <Text size="sm">Fleetdeck har ingen firmware med. Listen hentes fra et katalog, du selv angiver – fx en mappe i dit eget GitHub-repo. {howTo}</Text>
      </Alert>
    : notice === 'stale'
      ? <Alert color="yellow" icon={<IconInfoCircle />} title="Intet firmwarekatalog sat op">
          <Text size="sm">Listen herunder er den, Fleetdeck hentede sidst, og den bliver ikke opdateret. {howTo}</Text>
        </Alert>
      : notice === 'empty'
        ? <Alert color="blue" icon={<IconInfoCircle />} title="Ingen firmware endnu">
            <Text size="sm">{fw.error ? `Kataloget kunne ikke hentes: ${fw.error}` : fw.checked ? 'Kataloget er tomt.' : 'Kataloget er ikke hentet endnu – det sker inden for et minut.'}{isAdmin && <> Ret adressen eller nøglen under <Anchor component="button" size="sm" onClick={openSettings}>Indstillinger</Anchor>.</>}</Text>
          </Alert>
        : null;
  const srcLine = (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <IconCloudCheck size={15} style={{ color: 'var(--mantine-color-dimmed)', flex: 'none', marginTop: 2 }} />
      <Tooltip label={fw.error ? `Sidste tjek fejlede: ${fw.error}` : fw.checked ? `Sidst tjekket ${new Date(fw.checked).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' })}` : 'Ikke tjekket endnu'}>
        <Text size="xs" c={fw.error ? 'orange' : 'dimmed'}>
          {[fw.generated && `Firmware fra ${generatedLabel(fw.generated)}`, SOURCE[fw.source] ?? fw.source, fw.live ? 'opdateres af sig selv' : 'henter ikke fra nettet'].filter(Boolean).join(' · ')}
        </Text>
      </Tooltip>
      {isAdmin && fw.live && <Anchor size="xs" component="button" onClick={check} disabled={checking} style={{ flex: 'none' }}>{checking ? 'Søger …' : 'Søg efter ny'}</Anchor>}
    </Group>
  );

  const list = (
    <Stack gap="xs" w={narrow ? '100%' : 330} style={{ flex: 'none' }}>
      <SegmentedControl size="xs" value={f.chip} onChange={setChip} data={f.chips.map((c) => ({ value: c.value, label: `${c.label} ${c.count}` }))} />
      {f.kinds.length > 2 && <SegmentedControl size="xs" value={f.kind} onChange={setKind} data={f.kinds.map((c) => ({ value: c.value, label: `${c.label} ${c.count}` }))} />}
      <Paper withBorder radius="md" p={4}>
        {f.items.length === 0 && <Empty>Ingen firmware passer til filteret.</Empty>}
        {f.items.map((x) => (
          <UnstyledButton key={x.id} onClick={() => setSel(x.id)} w="100%" p="sm"
            style={{ borderRadius: 8, background: chosen?.id === x.id ? 'var(--mantine-primary-color-light)' : undefined }}>
            <Group gap="sm" wrap="nowrap">
              <ThemeIcon variant={chosen?.id === x.id ? 'light' : 'transparent'} color={chosen?.id === x.id ? undefined : 'gray'} size={30}>
                {x.kind === 'zigbee' ? <IconHexagon size={19} /> : <IconWifi size={19} />}
              </ThemeIcon>
              <div style={{ minWidth: 0, flex: 1 }}>
                <Text fw={600} size="sm" truncate>{x.name}</Text>
                <Text size="xs" c="dimmed" truncate>{x.chip} · v{x.version}</Text>
              </div>
              {!tested(x) && <Badge size="xs" variant="light" color="orange">Utestet</Badge>}
            </Group>
          </UnstyledButton>
        ))}
      </Paper>
    </Stack>
  );

  return (
    <>
      <PageHeader title={<Group gap={8}><IconUsb size={24} />ESP-flash</Group>}
        sub="Flash ESP32-firmware fra browseren over USB – på den pc, ESP'en sidder i. Intet skal installeres.">
        {isAdmin && <Button variant="default" leftSection={<IconSettings size={16} />} onClick={() => setSettings(true)}>Indstillinger</Button>}
      </PageHeader>
      <Stack gap="md">
        {notice !== 'setup' && srcLine}
        {noticeBox}
        <Environment https={st.https} localPort={st.local?.port ?? 8587} canUsb={canUsb} narrow={!!narrow} isAdmin={isAdmin} onChanged={refresh} />
        {catErr && <Alert color="red" icon={<IconAlertTriangle />}>Firmwarelisten kunne ikke hentes: {(catErr as Error).message}</Alert>}
        {!cat && !catErr && <Loading />}
        {cat && cat.firmwares.length > 0 && (narrow
          ? (chosen
            ? <Stack gap="xs"><Anchor component="button" size="sm" onClick={() => setSel(null)}><Group gap={4}><IconChevronLeft size={16} />Alle firmwarer</Group></Anchor>
              <Detail fw={chosen} canUsb={canUsb} wifi={st.wifi} isAdmin={isAdmin} onWifi={() => setWifiFor(chosen)} onSettings={() => setSettings(true)} /></Stack>
            : list)
          : <Group align="flex-start" gap="lg" wrap="nowrap">{list}<div style={{ flex: 1, minWidth: 0 }}>
            {chosen ? <Detail fw={chosen} canUsb={canUsb} wifi={st.wifi} isAdmin={isAdmin} onWifi={() => setWifiFor(chosen)} onSettings={() => setSettings(true)} /> : null}
          </div></Group>)}
        {!onLocalhost() && <TrustHelp https={st.https} open={false} />}
      </Stack>
      {settings && <SettingsModal st={st} onClose={() => setSettings(false)} onSaved={refresh} />}
      {wifiFor && <WifiModal fw={wifiFor} onClose={() => setWifiFor(null)} />}
    </>
  );
}

function Box({ color, icon, children }: { color: string; icon: ReactNode; children: ReactNode }) {
  return <Alert color={color} icon={icon} radius="md" styles={{ message: { fontSize: 'var(--mantine-font-size-sm)' } }}>{children}</Alert>;
}

/** Kan denne browser flashe? Sikker side + Web Serial; ellers hvorfor ikke, og hvad man gør. */
function Environment({ https, localPort, canUsb, narrow, isAdmin, onChanged }: {
  https: EspHttps; localPort: number; canUsb: boolean; narrow: boolean; isAdmin: boolean; onChanged: () => void;
}) {
  const [busy, setBusy] = useState(false);
  // på en usikker side (http gennem serveren): svarer Fleetdeck-agenten på denne pc? Ellers vises de to valg i stedet for en død USB-knap
  const [agent, setAgent] = useState<AgentProbe>('checking');
  const [probeNo, setProbeNo] = useState(0);
  const needProbe = !canUsb && !window.isSecureContext && !narrow && !isMobile();
  useEffect(() => {
    if (!needProbe) return;
    let live = true;
    setAgent('checking');
    probeAgent(localPort).then((r) => { if (live) setAgent(r); });
    return () => { live = false; };
  }, [needProbe, localPort, probeNo]);
  const onRetry = () => setProbeNo((n) => n + 1);
  const addName = async () => {
    setBusy(true);
    try { await post('/api/esp/https/name', { host: https.host }); notifyOk(`Certifikatet dækker nu ${https.host}. Hent og installér det igen.`); onChanged(); }
    catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const nameWarn = https.enabled && https.covers === false && !onLocalhost() && (
    <Box color="orange" icon={<IconCertificate />}>
      <b>Certifikatet dækker ikke adressen {https.host}.</b> Browseren kalder siden usikker, også når certifikatet er installeret.
      {' '}Det dækker: {https.names?.join(', ') || 'ingen navne'}.
      {isAdmin && https.canAddName && <Group mt="xs"><Button size="xs" variant="light" color="orange" loading={busy} onClick={addName}>Tilføj {https.host} til certifikatet</Button>
        <Text size="xs" c="dimmed">Samme nøgle – agenterne mærker intet.</Text></Group>}
    </Box>
  );
  const env = espEnv({ canUsb, mobile: narrow || isMobile(), secure: window.isSecureContext, agent });
  if (env === 'usb')
    return <>{nameWarn}<Box color="teal" icon={<IconShieldCheck />}><b>{onLocalhost() ? 'Klar til USB gennem Fleetdeck-agenten på denne pc.' : 'Sikker forbindelse.'}</b> Sæt ESP'en i USB på denne pc og tryk <b>Flash via USB</b>. Virker i Chrome og Edge.</Box></>;
  if (env === 'mobile')
    return <Box color="orange" icon={<IconDeviceDesktop />}><b>Fra en telefon eller tablet kan der ikke flashes over USB.</b> Det skal gøres fra en pc (Windows, Mac eller Linux) med Chrome eller Edge, hvor ESP'en sidder i USB. Vælg <b>ESP Flasher</b> i Fleetdeck-ikonet på pc'en, eller åbn denne side dér.</Box>;
  if (env === 'browser')
    return <Box color="orange" icon={<IconDeviceDesktop />}><b>Denne browser kan ikke tale med USB.</b> Åbn siden i Chrome eller Edge på pc'en, ESP'en sidder i.</Box>;
  const local = localEspUrl(localPort);
  if (env === 'choose')
    return (
      <>
        <Box color="orange" icon={<IconAlertTriangle />}>
          <b>USB-flash virker ikke på denne side endnu.</b> Siden er åbnet gennem serveren uden https, og Chrome og Edge taler kun med USB på en sikker side.
          {' '}Fleetdeck kunne ikke se Fleetdeck-agenten på denne pc (localhost:{localPort} svarede ikke – browseren kan også have spærret for at spørge). Vælg en af vejene:
          <SimpleGrid cols={{ base: 1, sm: 3 }} mt="sm" spacing="sm">
            <Paper withBorder radius="md" p="sm">
              <Text fw={600} size="sm">1. Pc'en har agenten</Text>
              <Text size="xs" c="dimmed" mt={4} mb="xs">Åbn ESP Flasher gennem agenten på denne pc. Første gang logger du ind igen. Også i ikonets menu: <b>ESP Flasher</b>. Intet certifikat.</Text>
              <Button size="sm" component="a" href={local} target="_blank" rel="noopener" leftSection={<IconExternalLink size={16} />}>Åbn ESP Flasher på denne pc</Button>
            </Paper>
            <Paper withBorder radius="md" p="sm">
              <Text fw={600} size="sm">2. Installér agenten på denne pc</Text>
              <Text size="xs" c="dimmed" mt={4} mb="xs">Hent Fleetdeck-agenten. Har du ingen nøgle, så vælg <b>Anmod om adgang</b> – administratoren godkender pc'en. Bagefter: vælg <b>ESP Flasher</b> i Fleetdeck-ikonet. Intet certifikat.</Text>
              <Button size="sm" variant="default" component="a" href={abs('hent')} target="_blank" rel="noopener" leftSection={<IconDownload size={16} />}>Installér agenten på denne pc</Button>
            </Paper>
            <Paper withBorder radius="md" p="sm">
              <Text fw={600} size="sm">3. Brug https-adressen med certifikat</Text>
              {https.enabled && https.url
                ? <>
                  <Text size="xs" c="dimmed" mt={4} mb="xs">Uden agent: pc'en skal stole på Fleetdecks certifikat én gang (vejledningen er nederst på siden). Derefter virker USB på https-adressen.</Text>
                  <Button size="sm" variant="default" component="a" href={https.url} leftSection={<IconCertificate size={16} />}>Brug https-adressen med certifikat</Button>
                </>
                : <Text size="xs" c="dimmed" mt={4}>Https er ikke slået til på serveren{https.error ? ` (${https.error})` : ''}. {isAdmin ? 'Slå det til under Indstillinger øverst på siden.' : 'Bed administratoren slå det til.'}</Text>}
            </Paper>
          </SimpleGrid>
          <Text size="xs" c="dimmed" mt="sm">Har du lige installeret eller startet agenten? <Anchor size="xs" component="button" onClick={onRetry}>Spørg igen</Anchor></Text>
        </Box>
        {nameWarn}
      </>
    );
  return (
    <>
      <Box color="blue" icon={<IconUsb />}>
        <b>USB virker kun på en sikker side.</b> Åbn ESP Flasher gennem Fleetdeck-agenten på denne pc – så kan Chrome og Edge tale med ESP'en, uden certifikat.
        <Group mt="sm" gap="xs" align="center">
          <Button size="sm" component="a" href={local} target="_blank" rel="noopener" leftSection={<IconExternalLink size={16} />}>Åbn ESP Flasher på denne pc</Button>
          <Text size="xs" c="dimmed">{agent === 'yes' ? 'Fleetdeck-agenten svarer på denne pc.' : 'Kræver Fleetdeck-agenten på pc\'en.'} Første gang logger du ind igen. Også i ikonets menu: <b>ESP Flasher</b>.</Text>
        </Group>
      </Box>
      {https.enabled
        ? <Text size="xs" c="dimmed">Ingen agent på pc'en? Brug <Anchor size="xs" href={https.url}>https-siden</Anchor> i stedet – den kræver, at pc'en stoler på Fleetdecks certifikat (se nederst).</Text>
        : https.error && <Text size="xs" c="dimmed">Https er ikke slået til ({https.error}).</Text>}
      {nameWarn}
    </>
  );
}

function Detail({ fw, canUsb, wifi, isAdmin, onWifi, onSettings }: {
  fw: EspFirmware; canUsb: boolean; wifi: EspStatus['wifi']; isAdmin: boolean; onWifi: () => void; onSettings: () => void;
}) {
  const [tab, setTab] = useState<string | null>(null);
  const rel = releases(fw);
  useEffect(() => setTab(null), [fw.id]);
  return (
    <Panel>
      <Group gap="sm" mb={4} wrap="nowrap">
        <ThemeIcon variant="light" size={34}>{fw.kind === 'zigbee' ? <IconHexagon size={20} /> : <IconWifi size={20} />}</ThemeIcon>
        <Title order={3} style={{ flex: 1, minWidth: 0 }}>{fw.name}</Title>
        {tested(fw) ? <Badge variant="light" color="teal">Testet</Badge> : <Badge variant="light" color="orange">{fw.status || 'Ikke testet endnu'}</Badge>}
      </Group>
      <Text size="sm" c="dimmed" mb="sm">{[fw.chip, `v${fw.version}`, fw.date, fw.kindLabel].filter(Boolean).join(' · ')}</Text>
      {fw.summary && <Text mb="sm">{fw.summary}</Text>}
      {!!fw.features?.length && <List size="sm" mb="sm" spacing={2}>{fw.features.map((x) => <List.Item key={x}>{x}</List.Item>)}</List>}
      <Group gap="md" mb="sm">
        {!!fw.wiring?.length && <Anchor size="sm" fw={600} component="button" onClick={() => setTab(tab === 'wiring' ? null : 'wiring')}>Ledninger</Anchor>}
        {fw.after && <Anchor size="sm" fw={600} component="button" onClick={() => setTab(tab === 'after' ? null : 'after')}>Efter flash</Anchor>}
        {rel.length > 0 && <Anchor size="sm" fw={600} component="button" onClick={() => setTab(tab === 'news' ? null : 'news')}>Nyt i v{rel[0].version}</Anchor>}
      </Group>
      {tab === 'wiring' && <Table withTableBorder mb="sm" fz="sm"><Table.Tbody>{fw.wiring!.map(([a, b]) => <Table.Tr key={a + b}><Table.Td>{a}</Table.Td><Table.Td c="dimmed">→</Table.Td><Table.Td>{b}</Table.Td></Table.Tr>)}</Table.Tbody></Table>}
      {tab === 'after' && <Text size="sm" mb="sm">{fw.after}</Text>}
      {tab === 'news' && <Stack gap={6} mb="sm">{rel.slice(0, 3).map((r) => <div key={r.version}><Text size="sm" fw={600}>v{r.version}{r.title ? ` – ${r.title}` : ''}</Text>
        <List size="sm" spacing={0}>{(r.items ?? []).map((x) => <List.Item key={x}>{x}</List.Item>)}</List></div>)}</Stack>}
      {fw.ota && <Text size="sm" c="dimmed" mb="sm">🔄 {fw.ota}</Text>}
      {canUsb
        ? <Group gap="sm">
          <esp-web-install-button manifest={abs(`api/esp/fw/${fw.manifest}`)}>
            <Button slot="activate" leftSection={<IconBolt size={16} />}>Flash via USB</Button>
            <Text slot="unsupported" size="sm" c="dimmed">USB-flash kræver Chrome eller Edge på en computer.</Text>
            <Text slot="not-allowed" size="sm" c="dimmed">USB-flash kræver en sikker side. Åbn ESP Flasher på denne pc (se øverst).</Text>
          </esp-web-install-button>
          <Text size="xs" c="dimmed">Vælg "Tøm enheden", hvis den har haft anden firmware.</Text>
        </Group>
        : <Alert color="orange" icon={<IconDeviceDesktop />} p="xs">USB-flash kræver Chrome eller Edge på en pc og en sikker side – se øverst på siden, hvordan du kommer videre.</Alert>}
      {needsWifi(fw) && (
        <Paper withBorder radius="md" p="sm" mt="md">
          <Group gap={8} mb={6}><IconWifi size={17} /><Text fw={600} style={{ flex: 1 }}>Wi-Fi efter flash</Text>
            {wifi.hasPassword && <Badge size="sm" variant="light" color="teal">Gemt krypteret</Badge>}</Group>
          {wifi.has
            ? <><Text size="sm" c="dimmed" mb="xs">Fleetdeck sender {wifi.ssid ? <b>{wifi.ssid}</b> : 'det gemte Wi-Fi'} til enheden over USB (Improv), så ingen skal skrive koden.</Text>
              <Group gap="xs">
                {isAdmin ? <Button size="xs" leftSection={<IconSend size={15} />} onClick={onWifi} disabled={!canUsb}>Send Wi-Fi</Button>
                  : <Text size="xs" c="dimmed">Kun en administrator kan sende Wi-Fi-koden.</Text>}
                {isAdmin && <Button size="xs" variant="default" onClick={onSettings}>Skift Wi-Fi</Button>}
              </Group></>
            : <Text size="sm" c="dimmed">Gem dit Wi-Fi i {isAdmin ? <Anchor component="button" size="sm" onClick={onSettings}>indstillingerne</Anchor> : 'indstillingerne (administrator)'}, så kan det sendes til enheden med ét klik.</Text>}
        </Paper>
      )}
    </Panel>
  );
}

const CMD = {
  windows: 'certutil -user -addstore Root fleetdeck.crt',
  mac: 'sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain fleetdeck.crt',
  linux: 'certutil -d sql:$HOME/.pki/nssdb -A -t "P,," -n Fleetdeck -i fleetdeck.crt',
};

/** Sådan kommer pc'en til at stole på Fleetdecks eget certifikat (én gang pr. pc). */
function TrustHelp({ https, open }: { https: EspHttps; open: boolean }) {
  const [show, setShow] = useState(open);
  if (!https.enabled) return null;
  const cmd = (k: keyof typeof CMD) => (
    <Group gap={4} wrap="nowrap" mt={6}><Code style={{ flex: 1, minWidth: 0, overflowX: 'auto', whiteSpace: 'nowrap' }}>{CMD[k]}</Code>
      <CopyButton value={CMD[k]}>{({ copied, copy }) => <ActionIcon variant="subtle" onClick={copy} aria-label="Kopiér">{copied ? <IconCheck size={15} /> : <IconCopy size={15} />}</ActionIcon>}</CopyButton></Group>
  );
  return (
    <Panel title="Reserve: https med Fleetdecks eget certifikat" right={!open && <Anchor size="sm" component="button" onClick={() => setShow(!show)}>{show ? 'Skjul' : 'Vis'}</Anchor>}>
      {show && <>
        <Text size="sm" c="dimmed" mb="sm">
          {https.selfSigned ? 'Fleetdeck bruger sit eget certifikat.' : 'Fleetdeck bruger et certifikat, du selv har givet det.'} Gør dette én gang på hver pc – ellers siger browseren "Ikke sikker".
        </Text>
        <Group gap="xs" mb="sm">
          <Button size="xs" variant="default" component="a" href={abs('api/esp/cert')} download="fleetdeck.crt" leftSection={<IconCertificate size={15} />}>Hent certifikat</Button>
          {https.url && !https.secure && <Button size="xs" variant="light" component="a" href={https.url} leftSection={<IconExternalLink size={15} />}>Åbn https-siden</Button>}
        </Group>
        <Tabs defaultValue={platformOf(navigator.userAgent)}>
          <Tabs.List><Tabs.Tab value="windows">Windows</Tabs.Tab><Tabs.Tab value="mac">Mac</Tabs.Tab><Tabs.Tab value="linux">Linux</Tabs.Tab></Tabs.List>
          <Tabs.Panel value="windows" pt="sm"><List type="ordered" size="sm" spacing={4}>
            <List.Item>Tryk <b>Hent certifikat</b> og åbn filen <Code>fleetdeck.crt</Code>.</List.Item>
            <List.Item>Vælg <b>Installér certifikat</b> → <b>Aktuel bruger</b> → <b>Placer alle certifikater i følgende lager</b> → <b>Nøglecentre for rodcertifikater, der er tillid til</b> (Trusted Root Certification Authorities).</List.Item>
            <List.Item>Luk Chrome/Edge helt og åbn https-siden igen.</List.Item>
            <List.Item>Eller i PowerShell i mappen med filen:{cmd('windows')}</List.Item>
          </List></Tabs.Panel>
          <Tabs.Panel value="mac" pt="sm"><List type="ordered" size="sm" spacing={4}>
            <List.Item>Tryk <b>Hent certifikat</b> og dobbeltklik på <Code>fleetdeck.crt</Code> – det åbner Nøgleringsadgang.</List.Item>
            <List.Item>Dobbeltklik på <b>Fleetdeck</b> → <b>Tillid</b> → <b>Ved brug af dette certifikat: Hav altid tillid</b>, og luk med din kode.</List.Item>
            <List.Item>Luk Chrome/Edge helt og åbn https-siden igen.</List.Item>
            <List.Item>Eller i Terminal:{cmd('mac')}</List.Item>
          </List></Tabs.Panel>
          <Tabs.Panel value="linux" pt="sm"><List type="ordered" size="sm" spacing={4}>
            <List.Item>Tryk <b>Hent certifikat</b>.</List.Item>
            <List.Item>Chrome/Edge: åbn <Code>chrome://certificate-manager</Code> → <b>Lokale certifikater</b> → <b>Tilpasset</b> → <b>Importér</b>, og giv det tillid til websteder.</List.Item>
            <List.Item>Eller i en terminal (pakken <Code>libnss3-tools</Code>):{cmd('linux')}</List.Item>
          </List></Tabs.Panel>
        </Tabs>
        {https.fingerprint && <Group gap={6} mt="md" wrap="nowrap"><IconFingerprint size={15} style={{ flex: 'none' }} />
          <Text size="xs" c="dimmed" style={{ wordBreak: 'break-all' }}>Tjek, at certifikatets SHA-256-fingeraftryk er <Code fz="xs">{https.fingerprint}</Code>. Det dækker: {https.names?.join(', ')}.</Text></Group>}
      </>}
    </Panel>
  );
}

function SettingsModal({ st, onClose, onSaved }: { st: EspStatus; onClose: () => void; onSaved: () => void }) {
  const [url, setUrl] = useState(st.catalogUrl ?? '');
  const [token, setToken] = useState('');
  const [clearToken, setClearToken] = useState(false);
  const [ssid, setSsid] = useState(st.wifi.ssid ?? '');
  const [pass, setPass] = useState('');
  const [busy, setBusy] = useState(false);
  const save = async () => {
    setBusy(true);
    try {
      if (url !== (st.catalogUrl ?? '') || token || clearToken) await put('/api/esp/settings', { catalogUrl: url, token: token || null, clearToken: clearToken && !token });
      if (ssid.trim() && (ssid.trim() !== (st.wifi.ssid ?? '') || pass)) await put('/api/esp/wifi', { ssid, password: pass || null });
      notifyOk('Gemt');
      onSaved(); onClose();
    } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  const forget = async () => {
    setBusy(true);
    try { await del('/api/esp/wifi'); notifyOk('Wi-Fi er slettet'); onSaved(); onClose(); } catch (e) { notifyErr(e); } finally { setBusy(false); }
  };
  return (
    <Modal opened onClose={onClose} title="ESP-flash: indstillinger" size="lg">
      <Stack>
        <TextInput label="Firmwarekatalog" description="Adressen på mappen med catalog.json (samme format som ESP Flasher) – en GitHub-mappe eller en almindelig web-adresse. Tom: intet katalog."
          value={url} onChange={(e) => setUrl(e.currentTarget.value)} placeholder={CATALOG_EXAMPLE} />
        <PasswordInput label="GitHub-nøgle (kun privat repo)" value={token} onChange={(e) => { setToken(e.currentTarget.value); setClearToken(false); }} maxLength={255}
          description="Et fine-grained token fra GitHub → Settings → Developer settings: kun det ene repo, Contents: Read-only. Gemmes krypteret og vises aldrig igen."
          placeholder={st.hasToken && !clearToken ? '•••••• gemt – skriv en ny for at ændre' : 'Tom for et offentligt repo'}
          rightSectionWidth={st.hasToken && !clearToken && !token ? 70 : undefined}
          rightSection={st.hasToken && !clearToken && !token ? <Anchor size="xs" component="button" c="red" onClick={() => setClearToken(true)}>Fjern</Anchor> : undefined} />
        {clearToken && <Text size="xs" c="dimmed" mt={-8}>Nøglen fjernes, når du gemmer.</Text>}
        <Title order={5} mt="sm">Wi-Fi til nye enheder</Title>
        <Text size="sm" c="dimmed" mt={-8}>Sendes til Wi-Fi-enheder over USB efter flash. Koden gemmes krypteret og kommer ikke med i opsætningsfilen. ESP-enheder kan kun 2,4 GHz.</Text>
        <TextInput label="Wi-Fi-navn (SSID)" value={ssid} onChange={(e) => setSsid(e.currentTarget.value)} placeholder="Dit 2,4 GHz-netværk" maxLength={32} />
        <PasswordInput label="Wi-Fi-kode" value={pass} onChange={(e) => setPass(e.currentTarget.value)} maxLength={63}
          placeholder={st.wifi.hasPassword ? '•••••• gemt – skriv en ny for at ændre' : 'Tom for et åbent net'} />
        <Group justify="space-between">
          {st.wifi.has ? <Button variant="subtle" color="red" onClick={forget} loading={busy}>Slet Wi-Fi</Button> : <span />}
          <Group gap="xs"><Button variant="default" onClick={onClose}>Annullér</Button><Button onClick={save} loading={busy}>Gem</Button></Group>
        </Group>
      </Stack>
    </Modal>
  );
}

function WifiModal({ fw, onClose }: { fw: EspFirmware; onClose: () => void }) {
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const [res, setRes] = useState<ProvisionResult | null>(null);
  const go = async () => {
    const serial = serialApi();
    if (!serial) return;
    let port;
    try { port = await serial.requestPort(); } catch { return; }   // brugeren lukkede portvælgeren
    setBusy(true); setRes(null);
    try {
      const w = await post<{ ssid: string; password: string }>('/api/esp/wifi/use');
      setRes(await provisionWifi(port, w.ssid, w.password, setStatus));
    } catch (e) {
      const err = e as Error;
      setRes({ ok: false, message: err.name === 'InvalidStateError' || /open/i.test(err.message) ? 'Porten er optaget – luk installationsvinduet og prøv igen' : err.message });
    } finally { setBusy(false); setStatus(''); }
  };
  const links = (res?.urls ?? []).filter((u) => /^https?:\/\//.test(u));
  return (
    <Modal opened onClose={onClose} title={<Group gap={8}><IconWifi size={20} />Send Wi-Fi til {fw.name}</Group>}>
      <List type="ordered" size="sm" spacing={4} mb="md">
        <List.Item>Luk installationsvinduet, hvis det stadig er åbent.</List.Item>
        <List.Item>Lad ESP'en sidde i USB.</List.Item>
        <List.Item>Tryk <b>Vælg enhed</b> og vælg samme port som før.</List.Item>
      </List>
      {busy && <Group gap="xs" mb="sm"><IconRefresh size={16} className="fd-spin" /><Text size="sm">{status || 'Forbinder …'}</Text></Group>}
      {res && (res.ok
        ? <Alert color="teal" icon={<IconCheck />} mb="sm"><b>{res.message}.</b> {fw.after_wifi ?? 'Den dukker op i Home Assistant under Enheder → ESPHome som fundet enhed.'}
          {links.length > 0 && <Group mt="xs">{links.map((u) => <Button key={u} size="xs" component="a" href={u} target="_blank" rel="noopener" leftSection={<IconExternalLink size={14} />}>Åbn enheden</Button>)}</Group>}</Alert>
        : <Alert color="red" icon={<IconAlertTriangle />} mb="sm">{res.message}</Alert>)}
      <Group justify="flex-end" gap="xs">
        <Button variant="default" onClick={onClose}>Luk</Button>
        <Button onClick={go} loading={busy} leftSection={<IconUsb size={16} />}>{res ? 'Prøv igen' : 'Vælg enhed'}</Button>
      </Group>
    </Modal>
  );
}
