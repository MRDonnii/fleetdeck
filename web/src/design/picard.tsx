// "Lav SD-kort" til Raspberry Pi (forslag A: guide i trin). Server: src/Fleetdeck.Server/Services/PiCardService.cs.
// Kortet bærer kun en engangskode (24 timer, én brug); alt andet hentes over netværket, når Pi'en har meldt sig.
import { useEffect, useMemo, useState } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Loader, Modal, Paper, Progress, Radio, SegmentedControl, Select, SimpleGrid, Stack, Stepper, TagsInput, Text, Textarea, TextInput, ThemeIcon, Timeline } from '@mantine/core';
import { IconCircleCheck, IconDeviceSdCard, IconDownload, IconFileZip, IconHammer, IconRefresh, IconShieldLock, IconSparkles } from '@tabler/icons-react';
import { ApiError, post, put, useApi } from '../api';
import { CopyButton } from './copy';
import { ago } from '../format';
import type { Profile } from '../types';

type Point = { name: string; size: number; created: string; system: string | null; imageId: string | null; imageTitle: string | null; error: string | null; masked: string[]; enabled: string[] };
type Step = { step: string; label: string; at: string | null; running?: boolean; detail?: string | null; failed?: boolean };
type Service = { name: string; state: string };
export type PiCard = {
  id: number; name: string; mode: 'ny' | 'erstat'; kitName: string | null; imageTitle: string | null; status: 'bygger' | 'klar' | 'fejl' | 'slettet';
  progress: number; message: string; file: string | null; sha256: string | null; size: number; created: string; expires: string; downloaded: string | null;
  machineId: number | null; failedAt: string | null; steps: Step[]; services: Service[];
  settings: { user: string; ipMode: string; address: string; gateway: string; dns: string; iface: string; exclude: string[]; configLines: string[]; cmdlineFlags: string[]; keys: string[] } | null;
};
type Plan = {
  machine: { id: number; name: string; label: string; protected: boolean; ip: string | null; model: string | null; os: string | null; user: string | null; profileId: number | null; profile: string | null };
  points: Point[]; profile: { exclude: string[]; configLines: string; cmdlineFlags: string }; cards: PiCard[]; tools: boolean;
};
type Img = { id: string; title: string; debian: string; arch: string; models: string[]; usable: boolean; ready: boolean };

const MB = (n: number) => `${Math.round(n / 1048576)} MB`;
const MODELS = ['Raspberry Pi 5', 'Raspberry Pi 4', 'Raspberry Pi 3 Model B+', 'Raspberry Pi 3 Model B', 'Raspberry Pi 2', 'Raspberry Pi Zero 2 W'];

/** Guiden. Med <c>machineId</c>: "Erstat kortet" (eller helt nyt system med samme navn); uden: en helt ny Pi. */
export function PiCardWizard({ machineId, opened, onClose }: { machineId?: number; opened: boolean; onClose: () => void }) {
  const { data: plan } = useApi<Plan>(['picard-plan', machineId], opened && machineId ? `/api/picard/plan/${machineId}` : null);
  const { data: images = [] } = useApi<Img[]>(['picard-images'], opened ? '/api/picard/images' : null);
  const { data: profiles = [] } = useApi<Profile[]>(['profiles'], opened && !machineId ? '/api/profiles' : null);
  const [step, setStep] = useState(0);
  const [mode, setMode] = useState<'erstat' | 'ny'>(machineId ? 'erstat' : 'ny');
  const [kit, setKit] = useState<string | null>(null);
  const [imageId, setImageId] = useState<string | null>('bookworm-armhf-lite');
  const [model, setModel] = useState<string | null>(MODELS[1]);
  const [name, setName] = useState('');
  const [user, setUser] = useState('');
  const [keys, setKeys] = useState('');
  const [ipMode, setIpMode] = useState<'dhcp' | 'static'>('dhcp');
  const [address, setAddress] = useState('');
  const [gateway, setGateway] = useState('');
  const [dns, setDns] = useState('');
  const [exclude, setExclude] = useState<string[]>([]);
  const [configLines, setConfigLines] = useState('');
  const [flags, setFlags] = useState('');
  const [save, setSave] = useState(true);
  const [profileId, setProfileId] = useState<string | null>(null);
  const [confirm, setConfirm] = useState('');
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [cardId, setCardId] = useState<number | null>(null);
  const { data: card } = useApi<PiCard>(['picard', cardId], cardId ? `/api/picard/${cardId}` : null, { refetchInterval: 2000 });

  // udfyld fra maskinen, profilen og det seneste kort, når planen kommer
  useEffect(() => {
    if (!plan) return;
    const m = plan.machine, last = plan.cards.find((c) => c.settings)?.settings;
    setName(m.name.toLowerCase());
    setUser(last?.user ?? m.user ?? '');
    setKeys((last?.keys ?? []).join('\n'));
    setKit(plan.points.find((p) => p.imageId)?.name ?? plan.points[0]?.name ?? null);
    setExclude(plan.profile.exclude);
    setConfigLines(plan.profile.configLines);
    setFlags(plan.profile.cmdlineFlags);
    if (last?.ipMode === 'static') { setIpMode('static'); setAddress(last.address); setGateway(last.gateway); setDns(last.dns); }
    else if (m.ip) { setIpMode('static'); setAddress(`${m.ip}/24`); setGateway(m.ip.replace(/\.\d+$/, '.1')); }
  }, [plan]);
  useEffect(() => { if (card?.status === 'klar' && step === 2) setStep(3); }, [card?.status, step]);
  useEffect(() => { if (!opened) { setStep(0); setCardId(null); setErr(null); setConfirm(''); } }, [opened]);

  const point = plan?.points.find((p) => p.name === kit) ?? null;
  const base = mode === 'erstat' ? (point?.imageTitle ?? null) : images.find((i) => i.id === imageId)?.title ?? null;
  const usableImages = useMemo(() => images.filter((i) => i.usable), [images]);
  const canNext0 = mode === 'ny' ? !!imageId : !!point?.imageId;
  const prot = !!plan?.machine.protected && mode === 'erstat';

  const build = async () => {
    setErr(null); setBusy(true);
    try {
      const c = await post<PiCard>('/api/picard/build', {
        mode, machineId: machineId ?? null, kit: mode === 'erstat' ? kit : null, imageId: mode === 'ny' ? imageId : point?.imageId,
        name, user, sshKeys: keys, ipMode, address, gateway, dns, iface: 'eth0', profileId: profileId ? Number(profileId) : null,
        exclude, configLines, cmdlineFlags: flags, saveToProfile: mode === 'erstat' && save, confirm,
      });
      setCardId(c.id); setStep(2);
    } catch (e) { setErr(e instanceof ApiError || e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  const title = machineId ? `Lav SD-kort til ${plan?.machine.label ?? '…'}` : 'Ny Raspberry Pi';
  return (
    <Modal opened={opened} onClose={onClose} size="lg" title={<Group gap="xs"><IconDeviceSdCard size={20} /><Text fw={700}>{step === 3 ? 'Kortet er klar' : title}</Text></Group>}>
      {machineId && !plan ? <Group justify="center" p="xl"><Loader /></Group> : (
        <Stepper active={step} size="sm" allowNextStepsSelect={false} mb="md">
          <Stepper.Step label="Kilde">
            <Stack gap="sm">
              {plan && !plan.tools && <Alert color="red">Fleetdeck mangler værktøjerne sfdisk, mtools og xz. Opdatér containeren.</Alert>}
              {machineId ? <>
                <Radio.Group value={mode} onChange={(v) => setMode(v as 'erstat' | 'ny')}>
                  <Stack gap="xs">
                    <Choice value="erstat" icon={<IconRefresh size={18} />} title="Erstat kortet" text="Pi'en genskabes fra gendannelsespunktet: samme navn, IP, tjenester og data." />
                    <Choice value="ny" icon={<IconSparkles size={18} />} title="Helt nyt system" text="Et rent system med samme navn og profilens programmer." />
                  </Stack>
                </Radio.Group>
                {mode === 'erstat' && <>
                  <Text fw={600} size="sm" mt="xs">Gendannelsespunkt</Text>
                  {plan!.points.length === 0 ? <Alert color="orange">Maskinen har intet gendannelsespunkt endnu. Slå gendannelsespunkter til i profilen, eller tag et under fanen Gendannelse.</Alert> : (
                    <Radio.Group value={kit} onChange={setKit}>
                      <Stack gap={6}>{plan!.points.map((p, i) => (
                        <Paper key={p.name} withBorder p="xs" radius="md">
                          <Radio value={p.name} label={<Group gap={6}><Text size="sm" fw={600}>{new Date(p.created).toLocaleString('da-DK', { dateStyle: 'medium', timeStyle: 'short' })}</Text>
                            {i === 0 && <Badge size="xs">Seneste</Badge>}<Text size="xs" c="dimmed">{MB(p.size)} · {p.system ?? 'ukendt system'}</Text></Group>}
                            description={p.error ?? undefined} disabled={!p.imageId} />
                        </Paper>))}
                      </Stack>
                    </Radio.Group>)}
                </>}
              </> : (
                <SimpleGrid cols={{ base: 1, sm: 2 }}>
                  <Select label="Model" data={MODELS} value={model} onChange={setModel} allowDeselect={false} comboboxProps={{ withinPortal: true }} />
                  <Select label="Styresystem" value={imageId} onChange={setImageId} allowDeselect={false} comboboxProps={{ withinPortal: true }}
                    data={usableImages.map((i) => ({ value: i.id, label: i.title }))}
                    description={images.some((i) => !i.usable) ? 'Trixie kommer, når Fleetdecks førstestart er afprøvet på det.' : undefined} />
                </SimpleGrid>
              )}
              {mode === 'ny' && machineId && <Select label="Styresystem" value={imageId} onChange={setImageId} allowDeselect={false} comboboxProps={{ withinPortal: true }}
                data={usableImages.map((i) => ({ value: i.id, label: i.title }))} />}
              <Group justify="flex-end" mt="sm"><Button variant="default" onClick={onClose}>Annullér</Button><Button disabled={!canNext0} onClick={() => setStep(1)}>Næste</Button></Group>
            </Stack>
          </Stepper.Step>

          <Stepper.Step label="Indstillinger">
            <Stack gap="sm">
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <TextInput label="Værtsnavn" value={name} onChange={(e) => setName(e.currentTarget.value)} disabled={mode === 'erstat'} styles={{ input: { fontFamily: 'monospace' } }} />
                <TextInput label="Bruger" value={user} onChange={(e) => setUser(e.currentTarget.value)} description="Ingen adgangskode – login med SSH-nøgle" styles={{ input: { fontFamily: 'monospace' } }} />
              </SimpleGrid>
              <Textarea label="Offentlige SSH-nøgler" autosize minRows={2} value={keys} onChange={(e) => setKeys(e.currentTarget.value)} placeholder="ssh-ed25519 AAAA… bruger@pc"
                description="Én pr. linje. Kun den offentlige del (.pub)." styles={{ input: { fontFamily: 'monospace', fontSize: 12 } }} />
              <SegmentedControl value={ipMode} onChange={(v) => setIpMode(v as 'dhcp' | 'static')} data={[{ value: 'static', label: 'Fast IP' }, { value: 'dhcp', label: 'DHCP' }]} />
              {ipMode === 'static' && <SimpleGrid cols={{ base: 1, sm: 3 }}>
                <TextInput label="IP-adresse" value={address} onChange={(e) => setAddress(e.currentTarget.value)} placeholder="192.168.1.50/24" styles={{ input: { fontFamily: 'monospace' } }} />
                <TextInput label="Gateway" value={gateway} onChange={(e) => setGateway(e.currentTarget.value)} placeholder="192.168.1.1" styles={{ input: { fontFamily: 'monospace' } }} />
                <TextInput label="DNS" value={dns} onChange={(e) => setDns(e.currentTarget.value)} placeholder="som gateway" styles={{ input: { fontFamily: 'monospace' } }} />
              </SimpleGrid>}
              {!machineId && <Select label="Profil" value={profileId} onChange={setProfileId} comboboxProps={{ withinPortal: true }} description="Pi'en får profilens programmer, når den har meldt sig."
                data={[{ value: '0', label: 'Ingen profil' }, ...profiles.map((p) => ({ value: String(p.id), label: p.name }))]} />}
              {mode === 'erstat' && <TagsInput label="Udelad ved nyt kort" value={exclude} onChange={setExclude} splitChars={[',', ' ']} clearable
                description="Stier i gendannelsespunktet, der kun hørte til den gamle opsætning (uden / foran, * som jokertegn)." />}
              <Textarea label="Ekstra config.txt-linjer" autosize minRows={1} value={configLines} onChange={(e) => setConfigLines(e.currentTarget.value)} placeholder="dtoverlay=w1-gpio,gpiopin=4"
                description="Flettes ind under [all]. Resten af det nye korts config.txt beholdes." styles={{ input: { fontFamily: 'monospace', fontSize: 12 } }} />
              <TextInput label="Ekstra cmdline-flag" value={flags} onChange={(e) => setFlags(e.currentTarget.value)} placeholder="cfg80211.ieee80211_regdom=DK"
                description="Kun en kort liste af sikre flag. Det nye korts root=PARTUUID beholdes altid." styles={{ input: { fontFamily: 'monospace', fontSize: 12 } }} />
              {mode === 'erstat' && plan?.machine.profile && <Checkbox checked={save} onChange={(e) => setSave(e.currentTarget.checked)} label={`Husk udelad-listen og boot-linjerne i profilen ${plan.machine.profile}`} />}
              {base && <Alert color="green" icon={<IconCircleCheck size={18} />} p="xs"><Text size="sm"><b>Basisbillede: {base}.</b> {mode === 'erstat' ? 'Valgt ud fra gendannelsespunktet. ' : ''}Tjekkes mod SHA-256 før brug.</Text></Alert>}
              {point && point.masked.length > 0 && <Text size="xs" c="dimmed">Forbliver slået fra: {point.masked.join(', ')}</Text>}
              <SecureNote />
              {prot && <TextInput label={`${plan!.machine.label} er beskyttet. Skriv maskinens navn for at bekræfte`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} styles={{ input: { fontFamily: 'monospace' } }} />}
              {err && <Alert color="red">{err}</Alert>}
              <Group justify="flex-end"><Button variant="default" onClick={() => setStep(0)}>Tilbage</Button>
                <Button leftSection={<IconHammer size={16} />} loading={busy} onClick={build} disabled={prot && confirm.trim().toLowerCase() !== plan!.machine.label.toLowerCase() && confirm.trim().toLowerCase() !== plan!.machine.name.toLowerCase()}>Byg kort</Button></Group>
            </Stack>
          </Stepper.Step>

          <Stepper.Step label="Byg">
            {card && <BuildProgress card={card} />}
          </Stepper.Step>

          <Stepper.Step label="Skriv og start">
            {card && <Ready card={card} onClose={onClose} />}
          </Stepper.Step>
        </Stepper>
      )}
    </Modal>
  );
}

function Choice({ value, icon, title, text }: { value: string; icon: React.ReactNode; title: string; text: string }) {
  return (
    <Paper withBorder p="sm" radius="md">
      <Radio value={value} label={<Group gap={8} wrap="nowrap">{icon}<div><Text fw={600} size="sm">{title}</Text><Text size="xs" c="dimmed">{text}</Text></div></Group>} />
    </Paper>
  );
}

function SecureNote() {
  return (
    <Alert color="yellow" icon={<IconShieldLock size={18} />} p="xs">
      <Text size="sm"><b>Ingen adgangskoder eller nøgler på kortet.</b> Kun en engangskode til netop denne maskine, gyldig i 24 timer. Resten hentes over netværket, når Pi'en har meldt sig.</Text>
    </Alert>
  );
}

function BuildProgress({ card }: { card: PiCard }) {
  if (card.status === 'fejl') return <Alert color="red" title="Kortet kunne ikke bygges">{card.message}</Alert>;
  return (
    <Stack gap="sm">
      <Paper withBorder p="md" radius="md">
        <Group justify="space-between" mb={6}><Text fw={600}>Bygger kortet</Text><Text size="sm" c="dimmed">{card.progress} %</Text></Group>
        <Progress value={card.progress} animated />
        <Text size="sm" c="dimmed" mt={8}>{card.message}</Text>
      </Paper>
      <SecureNote />
      <Text size="xs" c="dimmed">Det tager typisk 3–8 minutter. Du kan lukke vinduet; kortet bygges videre og findes på maskinens side.</Text>
    </Stack>
  );
}

function Ready({ card, onClose }: { card: PiCard; onClose: () => void }) {
  return (
    <Stack gap="sm">
      <Paper withBorder p="md" radius="md">
        <Group wrap="nowrap">
          <ThemeIcon size={42} variant="light" radius="md"><IconFileZip size={24} /></ThemeIcon>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Text fw={600} truncate>{card.file}</Text>
            <Group gap={6}><Text size="xs" c="dimmed">{MB(card.size)} · SHA-256</Text><Code style={{ fontSize: 11 }}>{card.sha256?.slice(0, 12)}…</Code>
              {card.sha256 && <CopyButton value={card.sha256}>{({ copied, copy }) => <Button size="compact-xs" variant="subtle" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>}</Group>
          </div>
          <Button component="a" href={`/api/picard/${card.id}/download`} leftSection={<IconDownload size={16} />}>Hent</Button>
        </Group>
      </Paper>
      <Paper withBorder p="md" radius="md">
        <Text fw={600} mb={6}>Sådan skriver du kortet</Text>
        <Stack gap={4}>
          {['Åbn Raspberry Pi Imager → Vælg styresystem → Brug brugerdefineret → vælg filen.', 'Vælg SD-kortet.',
            'Svar NEJ til "Tilpas indstillinger" – ellers overskriver Imager Fleetdecks filer.',
            `Skriv kortet${card.mode === 'erstat' ? ', luk Pi\'en pænt ned, og skift kortet på det aftalte tidspunkt' : ', og sæt det i Pi\'en'}. Pi'en melder sig selv.`].map((t, i) => (
            <Group key={i} gap={8} wrap="nowrap" align="flex-start"><Badge size="sm" circle variant="light">{i + 1}</Badge><Text size="sm">{t}</Text></Group>))}
        </Stack>
      </Paper>
      <Text size="xs" c="dimmed">Billedet slettes af sig selv om 24 timer{card.mode === 'erstat' ? '. Det gamle kort røres aldrig – læg det i igen, hvis noget går galt.' : '.'}</Text>
      <Group justify="flex-end"><Button variant="default" onClick={onClose}>Luk</Button></Group>
    </Stack>
  );
}

/** Maskinens side: det seneste kort og tidslinjen for første start. */
export function PiCardStatus({ machineId }: { machineId: number }) {
  const { data: cards = [] } = useApi<PiCard[]>(['picard-cards', machineId], `/api/picard/cards?machineId=${machineId}`, { refetchInterval: 10000 });
  const c = cards.find((x) => x.status !== 'slettet' || x.steps.some((s) => s.step === 'tilmeldt' && s.at));
  if (!c) return null;
  const done = c.steps.filter((s) => s.at).length;
  return (
    <Paper withBorder p="md" radius="md">
      <Group justify="space-between" mb="sm"><Group gap={6}><IconDeviceSdCard size={18} /><Text fw={600}>{c.mode === 'erstat' ? 'Nyt SD-kort' : 'SD-kort'}</Text></Group>
        <Text size="xs" c="dimmed">{c.status === 'bygger' ? `bygger · ${c.progress} %` : c.status === 'fejl' ? 'fejlede' : `bygget ${ago(c.created)}`}</Text></Group>
      {c.status === 'fejl' && <Alert color="red" mb="sm">{c.message}</Alert>}
      <Timeline active={done - 1} bulletSize={18} lineWidth={2}>
        {c.steps.map((s) => (
          <Timeline.Item key={s.step} color={s.failed ? 'red' : undefined} title={<Group gap={6}><Text size="sm" fw={s.running ? 700 : 500}>{s.label}</Text>{s.running && <Loader size={12} />}</Group>}>
            <Text size="xs" c="dimmed">{s.at ? new Date(s.at).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }) : s.running ? (s.detail || 'i gang') : ''}</Text>
          </Timeline.Item>))}
      </Timeline>
      {c.services.length > 0 && <Stack gap={4} mt="sm">{c.services.map((s) => (
        <Group key={s.name + s.state} gap={8}><Badge size="xs" variant="light" color={s.state === 'kører' ? 'green' : s.state === 'fejl' ? 'red' : 'gray'}>{s.state}</Badge><Code style={{ fontSize: 11 }}>{s.name}</Code></Group>))}</Stack>}
      {c.mode === 'erstat' && <Text size="xs" c="dimmed" mt="sm">Går noget galt: læg det gamle kort i igen. Det er urørt.</Text>}
    </Paper>
  );
}

type Netboot = {
  machine: { id: number; name: string; mac: string | null; model: string | null }; enabled: boolean; serial: string | null; serialFromAgent: boolean; knownSerials: string[];
  files: number; kernel: boolean; startElf: boolean; bootcode: boolean; bootcodeAtRoot: boolean; cmdline: string | null; root: string | null; nfsRoot: string | null; folder: string | null; pxe: boolean;
};

/** Netboot som nødstart: Fleetdecks egen opstartsserver svarer kun de Pi'er, der er slået til her. */
export function PiNetbootPanel({ machineId }: { machineId: number }) {
  const { data: nb, refetch } = useApi<Netboot>(['picard-netboot', machineId], `/api/picard/netboot/${machineId}`, { refetchInterval: 30000 });
  const [serial, setSerial] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => { if (nb?.serial) setSerial(nb.serial); }, [nb?.serial]);
  if (!nb) return null;
  const set = async (on: boolean) => {
    setBusy(true); setErr(null);
    try { await put(`/api/picard/netboot/${machineId}`, { on, serial: serial || null }); await refetch(); }
    catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  const ok = nb.startElf && nb.kernel && nb.bootcode && !!nb.cmdline;
  const row = (good: boolean, text: string) => <Group gap={6} wrap="nowrap"><Badge size="xs" variant="light" color={good ? 'green' : 'orange'}>{good ? 'OK' : 'mangler'}</Badge><Text size="sm">{text}</Text></Group>;
  return (
    <Paper withBorder p="md" radius="md">
      <Group justify="space-between" mb="xs">
        <Text fw={600}>Netboot som nødstart</Text>
        <Badge variant="light" color={nb.enabled ? 'green' : 'gray'}>{nb.enabled ? 'Slået til' : 'Slået fra'}</Badge>
      </Group>
      <Text size="xs" c="dimmed" mb="sm">Hvis SD-kortet svigter, kan Pi'en starte over netværket fra Fleetdeck. Fleetdeck svarer kun netop denne Pi. Pi'en kører normalt fra sit kort.</Text>
      <Stack gap={4}>
        {!nb.pxe && <Alert color="orange" p="xs">Netværksboot er slået fra i containeren (PXE=0).</Alert>}
        <TextInput label="Serienummer" size="xs" value={serial} onChange={(e) => setSerial(e.currentTarget.value.trim().toLowerCase())} maw={180}
          description={nb.serialFromAgent ? 'Fra agenten' : nb.knownSerials.length ? `Mapper i PXE/tftp: ${nb.knownSerials.join(', ')}` : 'Står i /proc/device-tree/serial-number (sidste 8 tegn)'} styles={{ input: { fontFamily: 'monospace' } }} />
        {row(nb.startElf && nb.kernel, `Opstartsfiler i ${nb.folder ?? 'PXE/tftp/<serienummer>'} (${nb.files} filer)`)}
        {row(nb.bootcode, nb.bootcodeAtRoot ? 'bootcode.bin i roden af TFTP' : 'bootcode.bin (lægges i roden, når netboot slås til)')}
        {row(!!nb.cmdline, nb.nfsRoot ? `Rodfilsystem over NFS: ${nb.nfsRoot}` : nb.root ? `root=${nb.root}` : 'cmdline.txt')}
        {nb.nfsRoot && <Text size="xs" c="dimmed">NFS-delingen ligger på værten (fx Unraid → Shares → NFS) og skal være delt med Pi'en. En container kan ikke dele NFS uden ekstra rettigheder.</Text>}
        {err && <Alert color="red" p="xs">{err}</Alert>}
        <Group justify="flex-end" mt={6}>
          {nb.enabled ? <Button size="xs" variant="default" color="red" loading={busy} onClick={() => set(false)}>Slå netboot fra</Button>
            : <Button size="xs" loading={busy} disabled={!ok && !serial} onClick={() => set(true)}>Slå netboot til</Button>}
        </Group>
      </Stack>
    </Paper>
  );
}
