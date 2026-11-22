import { useEffect, useState } from 'react';
import { Alert, Anchor, Badge, Button, Card, Code, Container, Group, List, SegmentedControl, Stack, Text, Title } from '@mantine/core';
import { CopyButton } from '../design/copy';
import { IconBrandApple, IconBrandUbuntu, IconBrandWindows, IconDownload, IconKey } from '@tabler/icons-react';

type Info = { server: string; version: string; windows: boolean; linux: { x64: boolean; arm64: boolean; arm: boolean }; mac?: { arm64: boolean; x64: boolean; x64Legacy?: boolean }; macApp?: { arm64: boolean; x64: boolean; x64Legacy?: boolean }; accessRequests?: boolean };

/**
 * Offentlig downloadside (/hent): det link, du sender til den, der skal have agenten på sin computer.
 * Viser kun installationsfilerne og vejledningen – intet andet fra Fleetdeck. Tilmeldingsnøglen kan følge med
 * i linket efter # (#k=…), så den aldrig sendes til serveren eller havner i en log.
 */
export function DownloadPage() {
  const [info, setInfo] = useState<Info | null>(null);
  const [error, setError] = useState(false);
  const key = new URLSearchParams(window.location.hash.slice(1)).get('k') ?? '';
  const ua = navigator.userAgent;
  const [os, setOs] = useState(/Mac/i.test(ua) ? 'mac' : /Linux|X11/i.test(ua) && !/Android/i.test(ua) ? 'linux' : 'windows');
  const base = document.baseURI.replace(/\/$/, '');
  useEffect(() => {
    fetch(`${base}/api/agent/downloads`).then((r) => (r.ok ? r.json() : Promise.reject())).then(setInfo).catch(() => setError(true));
  }, [base]);
  const server = info?.server ?? base;
  const cmd = (c: string) => (
    <Group wrap="nowrap" align="stretch">
      <Code block style={{ flex: 1, overflowX: 'auto', whiteSpace: 'nowrap' }}>{c}</Code>
      <CopyButton value={c}>{({ copied, copy }) => <Button onClick={copy} color={copied ? 'green' : undefined}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
    </Group>
  );
  // uden nøgle: "Anmod om adgang", hvis serveren tager imod det herfra – ellers en pladsholder
  const ask = !key && !!info?.accessRequests;
  const q = key ? `?key=${key}` : ask ? '' : '?key=DIN-NØGLE';
  return (
    <Container size={760} py="xl">
      <Stack gap="lg">
        <div>
          <Group gap="xs"><Title order={2}>Forbind din computer til Fleetdeck</Title>{info && <Badge variant="light">v{info.version}</Badge>}</Group>
          <Text c="dimmed" mt={4}>
            Fleetdeck holder computeren opdateret og sund: programmer installeres og opdateres automatisk, og der holdes øje med
            diskplads, antivirus og temperatur. Hent det lille program til din computer herunder. Det tager et par minutter.
          </Text>
        </div>
        {key
          ? <Alert icon={<IconKey />} color="green" title="Du har fået en tilmeldingsnøgle med linket">Den er allerede sat ind i kommandoerne herunder. Du kan også kopiere den alene:
              <Group mt="xs" gap="xs"><Code>{key}</Code><CopyButton value={key}>{({ copied, copy }) => <Button size="compact-xs" variant="light" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér nøgle'}</Button>}</CopyButton></Group></Alert>
          : ask
            ? <Alert icon={<IconKey />} color="blue" title="Ingen nøgle? Installer og vælg «Anmod om adgang»">Den, der styrer computerne, får besked og godkender computeren.
                Du får en kode på skærmen (fx <Code>7K4-Q2M</Code>), som han beder dig læse op. Kommandoerne herunder virker uden nøgle.</Alert>
            : <Alert icon={<IconKey />} color="yellow" title="Du skal også bruge en tilmeldingsnøgle">Den får du af den, der styrer computerne. Uden nøglen kan computeren ikke forbindes.</Alert>}
        {error && <Alert color="red">Serveren svarer ikke lige nu. Prøv igen om lidt.</Alert>}
        <SegmentedControl fullWidth value={os} onChange={setOs} data={[
          { value: 'windows', label: <Group gap={6} justify="center"><IconBrandWindows size={16} />Windows</Group> },
          { value: 'linux', label: <Group gap={6} justify="center"><IconBrandUbuntu size={16} />Linux / Raspberry Pi</Group> },
          { value: 'mac', label: <Group gap={6} justify="center"><IconBrandApple size={16} />Mac</Group> },
        ]} />

        {os === 'windows' && <Card withBorder padding="lg">
          <Stack>
            <Button size="lg" leftSection={<IconDownload />} component="a" href={`${base}/api/agent/download`} disabled={info ? !info.windows : false}>
              Hent Fleetdeck.exe</Button>
            <List type="ordered" spacing="xs">
              <List.Item>Åbn filen, når den er hentet (dobbeltklik), og svar <b>Ja</b>, når Windows spørger om lov.</List.Item>
              <List.Item>Tryk <b>Find automatisk</b>, eller skriv serverens adresse: <Code>{server}</Code></List.Item>
              <List.Item>{ask ? <>Ingen nøgle: tryk <b>Har du ingen nøgle? Anmod om adgang</b>, og giv koden til den, der styrer computerne.</>
                : <>Indsæt tilmeldingsnøglen{key ? ' (kopiér den ovenfor)' : ''}, vælg eventuelle udvidelser, og tryk <b>Forbind</b>.</>}</List.Item>
            </List>
            <Text size="sm" c="dimmed">Viser Windows "Windows beskyttede din pc", så tryk <b>Flere oplysninger</b> → <b>Kør alligevel</b>.
              Programmet er ikke signeret med et købt certifikat, men det kommer fra din egen server.</Text>
            <Text size="sm" fw={600} mt="sm">Eller med en kommando</Text>
            <Text size="sm">Højreklik på Start → <b>Terminal (administrator)</b>, indsæt og tryk Enter:</Text>
            {cmd(`irm "${server}/install.ps1${q}" | iex`)}
          </Stack>
        </Card>}

        {os === 'linux' && <Card withBorder padding="lg">
          <Stack>
            <Text size="sm">Åbn en terminal (på Ubuntu: <b>Ctrl+Alt+T</b>), indsæt og tryk Enter. Kommandoen finder selv ud af, om det er en
              almindelig pc eller en Raspberry Pi, og beder om din adgangskode:</Text>
            {cmd(`curl -fsSL "${server}/install.sh${q}" | sudo bash`)}
            <Text size="sm" fw={600} mt="sm">Eller med små vinduer</Text>
            <Text size="sm">Hent agenten til din computer, og kør <Code>chmod +x fleetdeck-agent && ./fleetdeck-agent setup</Code> i mappen, hvor den ligger.</Text>
            <Group>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=linux&arch=x64`} disabled={info ? !info.linux.x64 : false}>Almindelig pc (x64)</Button>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=linux&arch=arm64`} disabled={info ? !info.linux.arm64 : false}>Raspberry Pi 64-bit</Button>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=linux&arch=arm`} disabled={info ? !info.linux.arm : false}>Raspberry Pi 32-bit</Button>
            </Group>
            <Text size="sm" c="dimmed">Virker på Ubuntu, Debian, Linux Mint, Pop!_OS, Zorin, Raspberry Pi OS, Fedora, RHEL/Rocky/Alma, openSUSE,
              Arch/Manjaro/EndeavourOS, Alpine, Void, Gentoo, Solus, Fedora Atomic/Bazzite og SteamOS – med systemd, OpenRC, runit eller SysV-init.
              Kommandoen finder selv den rigtige agent. Er du i tvivl om Raspberry Pi'en er 32- eller 64-bit, så skriv <Code>uname -m</Code>:
              <Code>aarch64</Code> = 64-bit, <Code>armv7l</Code> = 32-bit.</Text>
            <Text size="sm" c="dimmed">Alpine har hverken bash, curl eller sudo fra start. Kør først som root: <Code>apk add bash curl</Code> – og så kommandoen ovenfor uden <Code>sudo</Code>.</Text>
          </Stack>
        </Card>}

        {os === 'mac' && <Card withBorder padding="lg">
          <Stack>
            <Group grow>
              <Button size="lg" leftSection={<IconDownload />} component="a" href={`${base}/api/agent/download?platform=mac&kind=dmg&arch=arm64`} disabled={info ? !info.macApp?.arm64 : false}>
                Hent Fleetdeck (Apple Silicon)</Button>
              <Button size="lg" variant="light" leftSection={<IconDownload />} component="a" href={`${base}/api/agent/download?platform=mac&kind=dmg&arch=x64`} disabled={info ? !info.macApp?.x64 : false}>
                Intel-Mac</Button>
            </Group>
            {info?.macApp?.x64Legacy && <Group grow>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=mac&kind=dmg&arch=x64-legacy`}>
                Ældre Intel-Mac (macOS 10.15 og 11)</Button>
            </Group>}
            <Text size="sm" c="dimmed">Fleetdeck kræver macOS 12 (Monterey) eller nyere – på en Intel-Mac med macOS 10.15 (Catalina) eller 11 (Big Sur)
              bruger du "Ældre Intel-Mac". Hvilken macOS du har, står under Apple-menuen → <b>Om denne Mac</b>.</Text>
            <List type="ordered" spacing="xs">
              <List.Item>Åbn den hentede fil, og træk <b>Fleetdeck</b> over i <b>Programmer</b>.</List.Item>
              <List.Item>Åbn Fleetdeck fra Programmer. Første gang siger macOS, at appen ikke kan kontrolleres:
                på <b>macOS 15 og nyere</b> trykker du <b>Udført</b> og går til <b>Systemindstillinger → Anonymitet og sikkerhed</b> og trykker
                {' '}<b>Åbn alligevel</b> ud for Fleetdeck. På <b>macOS 14 og ældre</b>: højreklik på Fleetdeck → <b>Åbn</b> → <b>Åbn</b>.</List.Item>
              <List.Item>Tryk <b>Find automatisk</b>, eller skriv serverens adresse: <Code>{server}</Code></List.Item>
              <List.Item>Indsæt tilmeldingsnøglen{key ? ' (kopiér den ovenfor)' : ''}, tryk <b>Test forbindelse</b>, vælg en profil og tryk <b>Installér og forbind</b>. Mac'en beder om din adgangskode.</List.Item>
              <List.Item>Giv Fleetdeck <b>Fuld diskadgang</b>, når vinduet viser hvordan. Bagefter ligger Fleetdeck i menulinjen øverst til højre.</List.Item>
            </List>
            <Text size="sm" c="dimmed">Ses ikonet ikke? På en MacBook med notch skjuler macOS ikoner, der ikke er plads til – luk et par andre ikoner,
              eller tjek <b>Systemindstillinger → Menulinje</b>. Fleetdeck kan altid åbnes fra Programmer eller Spotlight (⌘ + mellemrum).</Text>
            <Text size="sm" c="dimmed">Appen er ikke købt-signeret hos Apple – derfor advarslen første gang. Den kommer fra din egen server.
              På Mac'er med Apple silicon skal ejeren selv bekræfte macOS-opdateringer i Softwareopdatering; Fleetdeck henter dem og åbner vinduet.</Text>
            <Text size="sm" fw={600} mt="sm">Eller med en kommando</Text>
            <Text size="sm">Åbn <b>Terminal</b> (Programmer → Hjælpeværktøjer, eller tryk ⌘ + mellemrum og skriv Terminal), indsæt og tryk Enter.
              Du bliver bedt om din adgangskode (der vises ingen tegn, mens du skriver). Kommandoen lægger både agenten og Fleetdeck-appen
              (menulinjen) på:</Text>
            {cmd(`curl -fsSL "${server}/install.sh${q}" | sudo bash`)}
            <Text size="sm" fw={600} mt="sm">Eller kun agenten</Text>
            <Text size="sm">Hent agenten, og kør <Code>chmod +x fleetdeck-agent && ./fleetdeck-agent setup</Code> i Terminal i mappen, hvor den ligger.
              Fleetdeck-appen (menulinjen) henter tjenesten selv bagefter, når nogen er logget ind.</Text>
            <Group>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=mac&arch=arm64`} disabled={info ? !info.mac?.arm64 : false}>Apple Silicon (M1–M4)</Button>
              <Button variant="default" leftSection={<IconDownload size={16} />} component="a" href={`${base}/api/agent/download?platform=mac&arch=x64`} disabled={info ? !info.mac?.x64 : false}>Intel-Mac</Button>
            </Group>
            <Text size="sm" c="dimmed">Usikker på hvilken Mac du har? Æble-menuen → Om denne Mac: står der <b>Chip Apple M…</b>, er det Apple Silicon.
              Kommandoen vælger selv.</Text>
          </Stack>
        </Card>}

        <Text size="xs" c="dimmed">Spørgsmål? Kontakt den, der sendte dig linket. <Anchor size="xs" href={base}>Fleetdeck</Anchor></Text>
      </Stack>
    </Container>
  );
}
