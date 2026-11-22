import { useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Card, Group, Modal, PasswordInput, Progress, Radio, SegmentedControl, Select, SimpleGrid, Stack, Stepper, Switch, Table, Text, TextInput, Textarea, UnstyledButton,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import {
  IconAlertTriangle, IconBrandDebian, IconBrandRedhat, IconBrandUbuntu, IconBrandWindows, IconCircleCheck, IconDatabase, IconHandClick, IconInfoCircle, IconLeaf,
  IconLock, IconMessageQuestion, IconNetwork, IconServer, IconServer2, IconWand,
} from '@tabler/icons-react';
import { post, useAction, useApi } from '../api';

export type Field = 'name' | 'user' | 'password' | 'language' | 'keyboard' | 'timezone' | 'disk';
export type BlankRule = { kind: 'ask' | 'auto' | 'required' | 'na'; text: string };
export type OsRow = {
  id: string; title: string; kind: string; series: string | null; family: string; familyTitle: string; osVersion: string; variants: string[]; method: string;
  availability: 'ready' | 'later' | 'omitted'; note: string | null; warning: string | null; canEncrypt: boolean; missing: string; blank: Record<Field, BlankRule>;
  ready: boolean; version: string | null; sizeGb: number | null; downloaded: string | null; state: 'idle' | 'downloading' | 'failed'; bytes: number; total: number;
  message: string; canDownload: boolean;
};
export type DeployData = {
  os: OsRow[]; profiles: { id: number; name: string }[]; defaultProfileId: number | null; defaultUser: string; untested: boolean;
  machines: { id: number; name: string; label: string; mac: string | null; user: string | null; profileId: number | null; lastSeen: string | null }[];
};
type Opt = { id: string; name: string };
type LocaleInfo = { language: string; keyboard: string; timeZone: string; options: { languages: (Opt & { keyboard: string })[]; keyboards: Opt[] } };

export const FIELD_LABEL: Record<Field, string> = {
  name: 'Navn', user: 'Bruger', password: 'Adgangskode', language: 'Sprog', keyboard: 'Tastatur', timezone: 'Tidszone', disk: 'Valg af disk',
};
const STEPS = ['Styresystem', 'Maskinen', 'Bruger', 'Sprog', 'Disk', 'Programmer', 'Opsummering'];

export function OsIcon({ family, size = 18 }: { family: string; size?: number }) {
  switch (family) {
    case 'windows': return <IconBrandWindows size={size} />;
    case 'windows-server': return <IconServer size={size} />;
    case 'ubuntu': return <IconBrandUbuntu size={size} />;
    case 'debian': return <IconBrandDebian size={size} />;
    case 'proxmox': return <IconServer2 size={size} />;
    case 'mint': return <IconLeaf size={size} />;
    default: return <IconBrandRedhat size={size} />;
  }
}

export const AVAIL: Record<OsRow['availability'], { label: string; color: string }> = {
  ready: { label: 'Klar', color: 'green' }, later: { label: 'Kommer senere', color: 'gray' }, omitted: { label: 'Udeladt', color: 'gray' },
};

/** Hvad der sker med et tomt felt (gult mærke = spørges på maskinen). */
function BlankHint({ rule, empty }: { rule: BlankRule; empty: boolean }) {
  if (!empty || rule.kind === 'na') return null;
  if (rule.kind === 'ask') return <Badge size="sm" variant="light" color="yellow" leftSection={<IconMessageQuestion size={12} />}>Spørges på maskinen</Badge>;
  if (rule.kind === 'auto') return <Badge size="sm" variant="light" color="gray" leftSection={<IconWand size={12} />}>{rule.text}</Badge>;
  return <Badge size="sm" variant="light" color="red">Skal udfyldes</Badge>;
}

const label = (text: string, rule: BlankRule, empty: boolean) => (
  <Group gap={8} wrap="nowrap" component="span"><span>{text}</span><BlankHint rule={rule} empty={empty} /></Group>
);

/** Guiden "Ny installation": familie → version → variant, maskinen, bruger, sprog, disk, programmer og opsummering. */
export function DeployWizard({ data, onClose, onDownload }: { data: DeployData; onClose: () => void; onDownload: (id: string) => void }) {
  const wide = useMediaQuery('(min-width: 48em)') ?? true;
  const { data: loc } = useApi<LocaleInfo>(['install-locale'], '/api/settings/install-locale');
  const choosable = (o: OsRow) => o.availability === 'ready' || (o.availability === 'later' && data.untested);
  const families = useMemo(() => {
    const m = new Map<string, OsRow[]>();
    for (const o of data.os) m.set(o.family, [...(m.get(o.family) ?? []), o]);
    return [...m.values()];
  }, [data.os]);
  const firstReady = data.os.find((o) => o.ready && choosable(o)) ?? data.os[0];

  const [step, setStep] = useState(0);
  const [osId, setOsId] = useState(firstReady.id);
  const os = data.os.find((o) => o.id === osId)!;
  const isWin = os.method === 'autounattend';
  const isServer = os.family === 'windows-server';
  const [variant, setVariant] = useState(os.variants.includes('server') && !isWin ? 'server' : os.variants[0]);
  const [machineId, setMachineId] = useState<string | null>(null);
  const [mac, setMac] = useState('');
  const [pcName, setPcName] = useState('');
  const [mode, setMode] = useState<'clean' | 'managed' | 'profile'>('managed');
  const [profileId, setProfileId] = useState<string | null>(data.defaultProfileId ? String(data.defaultProfileId) : null);
  const [linuxProfile, setLinuxProfile] = useState<string | null>(null);
  const [userName, setUserName] = useState(data.defaultUser);
  const [fullName, setFullName] = useState('');
  const [pw1, setPw1] = useState('');
  const [pw2, setPw2] = useState('');
  const [sshKey, setSshKey] = useState('');
  const [admin, setAdmin] = useState(true);
  const [lang, setLang] = useState<string | null>(null);
  const [kb, setKb] = useState<string | null>(null);
  const [tz, setTz] = useState<string | null>(null);
  const [localeTouched, setLocaleTouched] = useState(false);
  const [autoDisk, setAutoDisk] = useState(false);
  const [encrypt, setEncrypt] = useState(false);
  const [packages, setPackages] = useState('');

  // Sprog m.m. forudfyldes fra Indstillinger (første gang data er der)
  const language = localeTouched ? lang : lang ?? loc?.language ?? null;
  const keyboard = localeTouched ? kb : kb ?? loc?.keyboard ?? null;
  const timeZone = localeTouched ? tz : tz ?? loc?.timeZone ?? null;
  const zones = useMemo(() => {
    let list: string[] = [];
    try { list = Intl.supportedValuesOf('timeZone'); } catch { /* ældre browser */ }
    return [...new Set([loc?.timeZone ?? '', ...list].filter(Boolean))].sort();
  }, [loc?.timeZone]);

  const pickOs = (o: OsRow) => {
    setOsId(o.id);
    setVariant(o.variants.includes('server') && o.method !== 'autounattend' ? 'server' : o.variants[0]);
    if (o.method === 'autounattend') { setPcName(pcName.toUpperCase()); if (o.family === 'windows-server') setMode('clean'); }
    else { setPcName(pcName.toLowerCase()); setUserName(userName.toLowerCase().replace(/[^a-z0-9_-]/g, '')); }
    if (!o.canEncrypt) setEncrypt(false);
  };
  const pickMachine = (v: string | null) => {
    setMachineId(v);
    const m = data.machines.find((x) => String(x.id) === v);
    if (!m) return;
    if (m.mac) setMac(m.mac);
    setPcName(isWin ? m.name.toUpperCase() : m.name.toLowerCase());
    if (m.user) setUserName(m.user.split('\\').pop() ?? '');
    if (m.profileId) { setProfileId(String(m.profileId)); setLinuxProfile(String(m.profileId)); }
  };

  // Hvad står tomt og spørges om på maskinen?
  const values: Record<Field, boolean> = {
    name: pcName.trim().length > 0, user: userName.trim().length > 0, password: !isWin && pw1.length > 0,
    language: !!language, keyboard: !!keyboard, timezone: !!timeZone, disk: autoDisk,
  };
  const fields = (Object.keys(FIELD_LABEL) as Field[]).filter((f) => !(isWin && f === 'password'));
  const asked = fields.filter((f) => !values[f] && os.blank[f].kind === 'ask');
  const automatic = fields.filter((f) => !values[f] && os.blank[f].kind === 'auto');
  const missing = fields.filter((f) => !values[f] && os.blank[f].kind === 'required');

  const problem = (s: number): string | null => {
    if (s === 0) { if (!choosable(os)) return os.note ?? 'Kan ikke vælges endnu'; if (!os.ready) return 'Styresystemet er ikke hentet endnu'; }
    if (s === 1 && !/^([0-9a-f]{2}[-:. ]?){5}[0-9a-f]{2}$/i.test(mac.trim())) return 'Skriv maskinens MAC-adresse';
    if (s === 1 && isWin && mode === 'profile' && !profileId) return 'Vælg en profil';
    if (s === 2 && !isWin && pw1 !== pw2) return 'De to adgangskoder er ikke ens';
    if (s === 2 && !isWin && sshKey.trim() && !userName.trim()) return 'En SSH-nøgle kræver et brugernavn';
    if (s === 6 && missing.length) return `Skal udfyldes for ${os.title}: ${missing.map((f) => FIELD_LABEL[f].toLowerCase()).join(', ')}`;
    return null;
  };

  const save = useAction(() => post('/api/deploy', {
    mac, os: osId, mode: isWin ? mode : null, profileId: isWin ? (mode === 'profile' && profileId ? Number(profileId) : null) : (linuxProfile ? Number(linuxProfile) : null),
    pcName, userName, fullName, password: isWin ? null : pw1, variant, packages: isWin ? '' : packages, machineId: machineId ? Number(machineId) : null, notes: '',
    autoDisk, encrypt, sshKey: isWin ? null : sshKey, admin, language: language ?? '', keyboard: keyboard ?? '', timeZone: timeZone ?? '',
  }), 'Installationen er klar – boot maskinen fra netværket');

  const err = problem(step);
  const pct = os.total ? Math.round((os.bytes / os.total) * 100) : 0;
  const familyOf = families.find((f) => f[0].family === os.family) ?? [os];

  const body = [
    // 1. Styresystem
    <Stack gap="sm" key="os">
      <Text size="sm" fw={600}>Familie</Text>
      <SimpleGrid cols={{ base: 2, sm: 3 }} spacing="xs">
        {families.map((f) => {
          const best = f.find((o) => o.ready && choosable(o)) ?? f.find(choosable) ?? f[0];
          const avail = f.some((o) => o.availability === 'ready') ? 'ready' : f.some((o) => o.availability === 'later') ? 'later' : 'omitted';
          const on = f[0].family === os.family;
          return (
            <UnstyledButton key={f[0].family} onClick={() => pickOs(best)}>
              <Card withBorder padding="xs" h="100%" style={{
                ...(on ? { borderColor: 'var(--mantine-primary-color-filled)', boxShadow: '0 0 0 2px var(--mantine-primary-color-light)' } : {}), opacity: avail === 'ready' ? 1 : 0.65 }}>
                <Group gap={8} wrap="nowrap"><OsIcon family={f[0].family} /><Text fw={600} size="sm" truncate>{f[0].familyTitle}</Text></Group>
                <Group justify="space-between" mt={4} gap={4}>
                  <Text size="xs" c="dimmed">{f.filter((o) => o.availability !== 'omitted').map((o) => o.osVersion).join(' · ')}</Text>
                  <Badge size="xs" variant="light" color={AVAIL[avail].color}>{AVAIL[avail].label}</Badge>
                </Group>
              </Card>
            </UnstyledButton>
          );
        })}
      </SimpleGrid>
      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        {familyOf.length > 1 && <div>
          <Text size="sm" fw={600} mb={4}>Version</Text>
          <SegmentedControl fullWidth value={os.id} onChange={(v) => pickOs(data.os.find((o) => o.id === v)!)}
            data={familyOf.map((o) => ({ value: o.id, label: o.osVersion }))} />
        </div>}
        {os.variants.length > 1 && <div>
          <Text size="sm" fw={600} mb={4}>Variant</Text>
          <SegmentedControl fullWidth value={variant} onChange={setVariant}
            data={os.variants.map((v) => ({ value: v, label: v === 'desktop' ? (isServer ? 'Med skrivebord' : 'Skrivebord') : (isServer ? 'Core (uden skrivebord)' : 'Server') }))} />
        </div>}
      </SimpleGrid>
      {os.warning && <Alert variant="light" color="orange" icon={<IconAlertTriangle size={16} />}>{os.warning}</Alert>}
      {os.availability !== 'ready' && <Alert variant="light" color="gray" icon={<IconInfoCircle size={16} />}>
        {os.note}{os.availability === 'later' && data.untested && ' Afprøvning er slået til, så den kan vælges til test i en VM.'}
      </Alert>}
      {os.availability !== 'omitted' && (os.ready
        ? <Alert variant="light" color="green" icon={<IconCircleCheck size={16} />}>
          Hentet: {os.title}{os.version && !os.title.includes(os.version) ? ` ${os.version}` : ''}{os.sizeGb ? ` · ${os.sizeGb} GB` : ''}{os.kind !== 'windows' && ' · kontrolleret mod SHA-256'}
        </Alert>
        : <Card withBorder padding="sm">
          <Group justify="space-between" wrap="nowrap">
            <Text size="sm" c={os.state === 'failed' ? 'red' : 'dimmed'}>{os.state === 'downloading' ? os.message : os.state === 'failed' ? `Fejl: ${os.message}` : os.missing}</Text>
            {os.canDownload && os.state !== 'downloading' && <Button size="xs" onClick={() => onDownload(os.id)}>Hent</Button>}
          </Group>
          {os.state === 'downloading' && os.total > 0 && <Progress mt={6} value={pct} animated />}
        </Card>)}
    </Stack>,

    // 2. Maskinen
    <Stack gap="sm" key="machine">
      <Select label="Kendt maskine (geninstallation, valgfri)" description="Udfylder MAC, navn, bruger og profil" clearable searchable value={machineId} onChange={pickMachine}
        data={data.machines.map((m) => ({ value: String(m.id), label: `${m.label}${m.label !== m.name ? ` (${m.name})` : ''}${m.mac ? ` · ${m.mac}` : ''}` }))} />
      <TextInput label="MAC-adresse" description="Står i VM-indstillingerne, på netværksboot-skærmen eller under Maskiner" placeholder="52-54-00-12-34-56" styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
        value={mac} onChange={(e) => setMac(e.currentTarget.value)} />
      <TextInput label={label(isWin ? 'Pc-navn' : 'Værtsnavn', os.blank.name, !values.name)} value={pcName}
        description={isWin ? 'Højst 15 tegn: A-Z, 0-9 og -' : 'a-z, 0-9 og -'}
        onChange={(e) => setPcName(isWin ? e.currentTarget.value.toUpperCase() : e.currentTarget.value.toLowerCase())} />
      {isWin ? <>
        <div>
          <Text size="sm" fw={500} mb={4}>Profil</Text>
          <SegmentedControl fullWidth value={mode} onChange={(v) => setMode(v as typeof mode)}
            data={[{ value: 'clean', label: 'Helt ren' }, { value: 'managed', label: 'Med mine ting' }, { value: 'profile', label: 'Vælg profil' }]} />
          <Text size="xs" c="dimmed" mt={4}>
            {mode === 'clean' ? 'Kun Windows. Ingen programmer, ingen styring fra Fleetdeck.'
              : mode === 'managed' ? 'Maskinens egen profil (eller standardprofilen) med programmer, dine filer, baggrund og opsætning.'
              : 'Programmer og indstillinger fra den valgte profil – fx en børne-pc.'}
          </Text>
        </div>
        {mode === 'profile' && <Select label="Profil" data={data.profiles.map((p) => ({ value: String(p.id), label: p.name }))} value={profileId} onChange={setProfileId} allowDeselect={false} />}
      </> : <Select label="Profil (valgfri)" description="Fleetdeck-agenten lægger profilen på, når maskinen har meldt sig" clearable value={linuxProfile} onChange={setLinuxProfile}
        data={data.profiles.map((p) => ({ value: String(p.id), label: p.name }))} />}
    </Stack>,

    // 3. Bruger
    <Stack gap="sm" key="user">
      {os.blank.user.kind !== 'na' && <SimpleGrid cols={{ base: 1, sm: isWin ? 1 : 2 }}>
        <TextInput label={label(isWin ? 'Windows-bruger' : 'Brugernavn', os.blank.user, !values.user)} value={userName}
          description={isWin ? (isServer ? 'Tom = kun Administrator' : 'Adgangskoden vælges ved første login') : 'Små bogstaver, fx anna'}
          onChange={(e) => setUserName(isWin ? e.currentTarget.value : e.currentTarget.value.toLowerCase())} />
        {!isWin && <TextInput label="Fulde navn" value={fullName} onChange={(e) => setFullName(e.currentTarget.value)} />}
      </SimpleGrid>}
      {isWin ? <Alert variant="light" icon={<IconInfoCircle size={16} />}>{os.blank.password.text}. Fleetdeck gemmer ingen Windows-adgangskoder.</Alert> : <>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <PasswordInput label={label(os.method === 'proxmox' ? 'Root-adgangskode' : 'Adgangskode', os.blank.password, !values.password)} value={pw1}
            description="Tom = du skriver den på maskinen. Ellers gemmes den kun som hash (sha512-crypt)." onChange={(e) => setPw1(e.currentTarget.value)} />
          <PasswordInput label="Gentag adgangskode" value={pw2} onChange={(e) => setPw2(e.currentTarget.value)} error={pw2 && pw1 !== pw2 ? 'Ikke ens' : undefined} />
        </SimpleGrid>
        <Textarea label="SSH-nøgle (valgfri)" description="Offentlig nøgle på én linje, fx ssh-ed25519 AAAA… navn" styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }} autosize minRows={1}
          value={sshKey} onChange={(e) => setSshKey(e.currentTarget.value)} />
        {os.blank.user.kind !== 'na' && <Switch checked={admin} onChange={(e) => setAdmin(e.currentTarget.checked)} label="Administrator"
          description={admin ? 'Brugeren får sudo' : 'Brugeren får ikke sudo – Fleetdeck-agenten styrer stadig maskinen'} />}
      </>}
    </Stack>,

    // 4. Sprog og region
    <Stack gap="sm" key="locale">
      <Text size="sm" c="dimmed">Forudfyldt fra Indstillinger. Tøm et felt for at vælge det på maskinen.</Text>
      <Select label={label('Sprog', os.blank.language, !values.language)} clearable value={language}
        data={(loc?.options.languages ?? []).map((l) => ({ value: l.id, label: l.name }))}
        onChange={(v) => { setLocaleTouched(true); setLang(v); setKb(keyboard); setTz(timeZone); }} />
      <Select label={label('Tastatur', os.blank.keyboard, !values.keyboard)} clearable value={keyboard}
        data={(loc?.options.keyboards ?? []).map((k) => ({ value: k.id, label: k.name }))}
        onChange={(v) => { setLocaleTouched(true); setKb(v); setLang(language); setTz(timeZone); }} />
      <Select label={label('Tidszone', os.blank.timezone, !values.timezone)} clearable searchable limit={200} value={timeZone} data={zones}
        onChange={(v) => { setLocaleTouched(true); setTz(v); setLang(language); setKb(keyboard); }} />
    </Stack>,

    // 5. Disk
    <Stack gap="sm" key="disk">
      <Radio.Group value={autoDisk ? 'auto' : 'ask'} onChange={(v) => setAutoDisk(v === 'auto')}>
        <Stack gap="xs">
          {os.blank.disk.kind !== 'required' && <Radio.Card value="ask" p="sm" radius="md">
            <Group wrap="nowrap" align="flex-start"><Radio.Indicator /><IconHandClick size={20} />
              <div style={{ flex: 1 }}><Group gap={8}><Text fw={600} size="sm">Vælg disk på maskinen</Text>{!autoDisk && <BlankHint rule={os.blank.disk} empty />}</Group>
                <Text size="xs" c="dimmed">Installationen stopper ved valg af disk. Intet slettes, før du vælger.</Text></div></Group>
          </Radio.Card>}
          <Radio.Card value="auto" p="sm" radius="md">
            <Group wrap="nowrap" align="flex-start"><Radio.Indicator /><IconDatabase size={20} />
              <div style={{ flex: 1 }}><Text fw={600} size="sm">Hele den første disk</Text>
                <Text size="xs" c="dimmed">Disken slettes og bruges uden at spørge – god til VM'er.</Text></div></Group>
          </Radio.Card>
        </Stack>
      </Radio.Group>
      {os.canEncrypt && <Switch checked={encrypt} onChange={(e) => setEncrypt(e.currentTarget.checked)} color="orange"
        label={<Group gap={6} component="span"><IconLock size={15} /><span>Kryptér disken ({isWin ? 'BitLocker' : 'LUKS'})</span></Group>}
        description={isWin ? 'Slås til ved første login, når pc\'en har en TPM. Gendannelsesnøglen gemmes af Fleetdeck-agenten.'
          : 'Krypteringsnøglen skrives på maskinen og gemmes aldrig i Fleetdeck.'} />}
    </Stack>,

    // 6. Programmer
    <Stack gap="sm" key="apps">
      {isWin ? <Alert variant="light" icon={<IconInfoCircle size={16} />}>
        {mode === 'clean' ? 'Helt ren: ingen programmer.' : mode === 'managed' ? 'Programmerne fra maskinens profil (eller standardprofilen) installeres efter første login.'
          : `Programmerne fra profilen "${data.profiles.find((p) => String(p.id) === profileId)?.name ?? '?'}" installeres efter første login.`}
      </Alert> : <>
        <Text size="sm" c="dimmed">
          {linuxProfile ? `Profilen "${data.profiles.find((p) => String(p.id) === linuxProfile)?.name}" lægges på af agenten bagefter. ` : ''}
          Ekstra pakker installeres under selve installationen.
        </Text>
        <Textarea label="Ekstra pakker (valgfri)" description={`${os.method === 'kickstart' ? 'dnf' : 'apt'}-navne adskilt af mellemrum, fx: htop git`}
          value={packages} onChange={(e) => setPackages(e.currentTarget.value)} autosize minRows={2} />
      </>}
    </Stack>,

    // 7. Opsummering
    <Stack gap="sm" key="sum">
      <Table withTableBorder variant="vertical" layout="fixed">
        <Table.Tbody>
          {[
            ['Styresystem', `${os.title}${os.variants.length > 1 ? ` · ${variant === 'desktop' ? 'skrivebord' : isServer ? 'Core' : 'server'}` : ''}`],
            ['Maskine', `${pcName || '–'} · ${mac || '–'}`],
            ['Bruger', isWin ? (userName || '–') : `${userName || '–'}${userName ? (admin ? ' (administrator)' : ' (ikke administrator)') : ''}${sshKey.trim() ? ' · SSH-nøgle' : ''}`],
            ['Sprog og region', [loc?.options.languages.find((l) => l.id === language)?.name, loc?.options.keyboards.find((k) => k.id === keyboard)?.name, timeZone].map((x) => x ?? '–').join(' · ')],
            ['Disk', `${autoDisk ? 'Hele den første disk' : 'Vælges på maskinen'}${encrypt ? ' · krypteret' : ''}`],
            ['Programmer', isWin ? { clean: 'Helt ren', managed: 'Med mine ting', profile: 'Profil' }[mode] : packages.trim() || 'Ingen ekstra'],
          ].map(([k, v]) => <Table.Tr key={k}><Table.Th w={wide ? 150 : 120}>{k}</Table.Th><Table.Td>{v}</Table.Td></Table.Tr>)}
        </Table.Tbody>
      </Table>
      {asked.length > 0 && <Alert variant="light" color="yellow" icon={<IconMessageQuestion size={16} />} title="Spørges på maskinen">
        {asked.map((f) => FIELD_LABEL[f].toLowerCase()).join(', ')}. Du skal være ved maskinen, når den installerer.
      </Alert>}
      {automatic.map((f) => <Text key={f} size="xs" c="dimmed"><IconWand size={12} /> {FIELD_LABEL[f]}: {os.blank[f].text}.</Text>)}
      {asked.length === 0 && <Text size="sm" c="dimmed">Installationen kører helt af sig selv.</Text>}
      <Text size="xs" c="dimmed"><IconNetwork size={12} /> Boot maskinen fra netværket (F12/F11/Esc ved opstart, eller "UEFI PXE" i en VM).</Text>
    </Stack>,
  ];

  return (
    <Modal opened onClose={onClose} fullScreen={!wide} size={1000} zIndex={300} title={<Group gap={8}><IconNetwork size={20} /><Text fw={700}>Ny installation</Text></Group>}>
      <Stack>
        {wide
          ? <Stepper active={step} onStepClick={(s) => s < step && setStep(s)} size="xs" iconSize={26} allowNextStepsSelect={false}
            styles={{ stepLabel: { fontSize: 12.5 }, separator: { marginInline: 6 }, stepBody: { marginInlineStart: 6 } }}>
            {STEPS.map((s) => <Stepper.Step key={s} label={s} />)}
          </Stepper>
          : <Group justify="space-between"><Text size="sm" fw={700} c="blue">Trin {step + 1} af 7 <Text span c="dimmed" fw={400}>· {STEPS[step]}</Text></Text>
            <Progress w={90} value={((step + 1) / 7) * 100} size="sm" /></Group>}
        {body[step]}
        {err && step > 0 && <Text size="sm" c="red">{err}</Text>}
        <Group justify="space-between" pt="xs" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <Button variant="subtle" onClick={() => (step === 0 ? onClose() : setStep(step - 1))}>{step === 0 ? 'Annullér' : 'Tilbage'}</Button>
          {step < 6
            ? <Button disabled={!!err} onClick={() => setStep(step + 1)}>Næste</Button>
            : <Button disabled={!!err} loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Gør klar</Button>}
        </Group>
      </Stack>
    </Modal>
  );
}
