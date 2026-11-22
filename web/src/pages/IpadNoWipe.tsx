import { useState } from 'react';
import { Alert, Badge, Button, Checkbox, Code, Group, Paper, Radio, Select, Stack, Stepper, Text, TextInput, Title } from '@mantine/core';
import { IconShieldCheck } from '@tabler/icons-react';
import { post, useAction, useApi } from '../api';
import { ago, JOB } from '../format';
import { JobLive, Loading, StatusBadge } from '../components';
import type { IpadRow, JobStatus, NoWipeBackup, NoWipeInfo } from '../types';

/** Det samme som NoWipeMethods.SuperviseOnlyLoss på serveren. */
const LOSS = {
  back: 'Apps hentes igen fra App Store, og det, der ligger i iCloud (fotos med iCloud-fotos, kontakter, kalender, noter, beskeder i iCloud og app-data, der synkroniseres), kommer tilbage, når der logges ind på Apple-kontoen under Indstillinger.',
  lost: 'Alt, der kun lå lokalt på iPad\'en: fotos og videoer uden iCloud-fotos, spil-fremskridt og app-data uden iCloud, indstillinger, wifi-koder og hjemmeskærmens opstilling.',
};
const STEP_LABEL: Record<string, string> = {
  check: 'Tjek', backup: 'Ny backup', fetch: 'Hent backup', erase: 'Slet', wait: 'Genstart', supervise: 'Overvåg', restore: 'Gendan', profile: 'Låst profil', verify: 'Resultat',
};
const steps = (method: string, fresh: boolean) => [
  'check', ...(fresh ? ['backup'] : []), ...(method === 'restore_no_system' ? ['fetch'] : []), 'erase', 'wait', 'supervise',
  ...(method === 'restore_no_system' ? ['restore'] : []), 'profile', 'verify',
];
const size = (b: number) => (b >= 1 << 30 ? `${(b / (1 << 30)).toLocaleString('da-DK', { maximumFractionDigits: 1 })} GB` : `${Math.round(b / (1 << 20))} MB`);
const when = (s: string) => new Date(s.endsWith('Z') ? s : s + 'Z').toLocaleString('da-DK', { dateStyle: 'short', timeStyle: 'short' });

/** Opsætning (administrator): "Overvåg uden datatab" – slet, overvåg og læg backuppen tilbage, via Mac'en eller en pc med "iPad via USB". */
export function NoWipeSection({ r }: { r: IpadRow }) {
  const { data } = useApi<NoWipeInfo>(['ipads', r.id, 'nowipe'], `/api/ipads/${r.id}/nowipe`, { refetchInterval: 5_000 });
  const [route, setRoute] = useState<string | null>(null);
  const [method, setMethod] = useState('restore_no_system');
  const [fresh, setFresh] = useState(true);
  const [pick, setPick] = useState('auto');
  const [typed, setTyped] = useState('');
  const start = useAction((a: object) => post<{ msg: string }>(`/api/ipads/${r.id}/nowipe`, a), (x) => x.msg);
  if (!data) return <Loading />;

  const routes = [
    ...data.usb.map((u) => ({ value: `usb:${u.machineId}:${u.udid}`, label: `Pc: ${u.name} – iPad'en er sat i${u.firmware ? ` (iPadOS ${u.firmware})` : ''}` })),
    ...data.macs.map((m) => ({ value: `mac:${m.id}`, label: `Mac: ${m.name}${m.online ? '' : ' (offline)'}${m.seen ? ' – har set iPad\'en' : ''}` })),
  ];
  const sel = route ?? routes[0]?.value ?? null;
  const job = data.job;
  const running = !!job && ['pending', 'running'].includes(job.status);
  const ok = typed.trim().toLocaleLowerCase('da') === data.name.trim().toLocaleLowerCase('da');
  const chosen = data.backups.find((b) => `${b.store}:${b.set}` === pick);
  const offerFallback = job && job.status === 'failed' && job.method === 'restore_no_system' && job.output.includes('Overvåget uden gendannelse');
  const go = () => {
    if (!sel) return;
    const [kind, id, udid] = sel.split(':');
    start.mutate({ route: kind, machineId: Number(id), udid, method, freshBackup: fresh, store: chosen?.store, set: chosen?.set, confirm: typed },
      { onSuccess: () => setTyped('') });
  };

  return (
    <Paper withBorder radius="md" p="md" mt="md">
      <Group gap={8} mb="xs"><IconShieldCheck size={18} /><Title order={6}>Overvåg uden datatab</Title></Group>
      <Text size="sm" c="dimmed" mb="sm">
        iPad'en slettes, gøres overvåget og får sin backup tilbage. Intet slettes, før backuppen er valgt, hentet og tjekket med husstandens backup-kode.
        Der tages aldrig en ny backup af en tom iPad eller en, der står i opsætningen, og den valgte backup ryddes aldrig op.
      </Text>
      {!data.linked && <Alert color="yellow" mb="sm">Link først iPad'en til en tilsluttet enhed (via Mac'en eller USB ovenfor).</Alert>}

      {job && <Paper withBorder radius="sm" p="xs" mb="sm">
        <Group gap="xs" justify="space-between" wrap="nowrap">
          <Text size="sm" fw={600} truncate>{job.title}</Text>
          <Group gap={6} wrap="nowrap"><Text size="xs" c="dimmed">{ago(job.finished ?? job.created)}</Text><StatusBadge map={JOB} value={job.status} /></Group>
        </Group>
        <JobLive job={{ id: job.id, status: job.status as JobStatus, progress: job.progress, step: job.step, stepSince: job.stepSince }} />
        <Progress job={job} />
        {job.output && <Code block mt={6} style={{ whiteSpace: 'pre-wrap', maxHeight: 320, overflow: 'auto' }}>{job.output}</Code>}
        {offerFallback && <Alert color="orange" mt="xs" title="Overvågningen fulgte ikke med gendannelsen">
          <Text size="sm">iPad'en har sine data igen. Vil du have den overvåget alligevel, kan du vælge "Overvåget uden gendannelse" nedenfor (se hvad der går tabt).</Text>
          <Button size="xs" mt={6} variant="light" color="orange" onClick={() => { setMethod('supervise_only'); setFresh(true); }}>Vælg "Overvåget uden gendannelse"</Button>
        </Alert>}
      </Paper>}

      <Stack gap="sm">
        <Select label="Via" placeholder={routes.length ? undefined : 'Ingen Mac eller pc med iPad\'en'} data={routes} value={sel} onChange={setRoute} allowDeselect={false}
          description="En pc med udvidelsen &quot;iPad via USB&quot;, hvor iPad'en er sat i, eller en Mac med Apple Configurator (pymobiledevice3 installeres selv på Mac'en)." />
        <Radio.Group label="Metode" value={method} onChange={setMethod}>
          <Stack gap={4} mt={4}>
            <Radio value="restore_no_system" label={<>Gendan uden systemfiler <Badge size="xs" color="orange" variant="light">afprøves</Badge></>}
              description="Slet → overvåg → læg backuppen tilbage uden systemfiler, så overvågningen forhåbentlig bliver. På iPadOS 27 og nyere bruges en almindelig gendannelse. Overvågningen tjekkes til sidst." />
            <Radio value="supervise_only" label="Overvåget uden gendannelse"
              description="Slet → overvåg. Backuppen lægges ikke tilbage, men bliver liggende i Fleetdeck." />
          </Stack>
        </Radio.Group>
        {method === 'supervise_only' && <Alert color="orange" variant="light" title="Det koster at springe gendannelsen over">
          <Text size="sm"><b>Kommer igen:</b> {LOSS.back}</Text>
          <Text size="sm" mt={4}><b>Går tabt på iPad'en:</b> {LOSS.lost}</Text>
          <Text size="sm" mt={4}>Vælg <b>ikke</b> "gendan fra iCloud-backup" bagefter – det fjerner overvågningen.</Text>
        </Alert>}
        <Checkbox checked={fresh} onChange={(e) => setFresh(e.currentTarget.checked)} label="Tag en ny krypteret backup først"
          description="Springes over, hvis iPad'en er tom eller står i opsætningen – så bruges den seneste gode backup." />
        <Radio.Group label="Backup, der lægges tilbage" value={pick} onChange={setPick}>
          <Stack gap={6} mt={4}>
            <Radio value="auto" label="Automatisk: den nyeste gode backup" description="Krypteret, koden tjekket, ikke af en tom iPad og mindst halvt så stor som iPad'ens data (uden apps)." />
            {data.backups.map((b) => <Radio key={`${b.store}:${b.set}`} value={`${b.store}:${b.set}`} label={<BackupLabel b={b} />}
              description={b.ineligible ? `Vælges ikke automatisk: ${b.ineligible}` : undefined} />)}
            {data.backups.length === 0 && <Text size="sm" c="dimmed">Der er ingen backup af iPad'en i Fleetdeck endnu – sæt flueben i "Tag en ny krypteret backup først".</Text>}
          </Stack>
        </Radio.Group>
        {chosen?.ineligible && <Alert color="yellow" variant="light">Du har valgt en backup, som reglerne ikke ville vælge ({chosen.ineligible}). Den bruges, fordi du har valgt den.</Alert>}
        <Alert color="red" variant="light" title="iPad'en bliver slettet">
          <Text size="sm">Slå <b>Find min iPad</b> fra, sæt iPad'en til strøm og kabel, og lås den op. Rør den ikke, mens jobbet kører – Fleetdeck siger til, hvis du skal trykke på noget.
            Det tager typisk 30–120 minutter.</Text>
        </Alert>
        <TextInput label={`Skriv iPad'ens navn for at bekræfte: ${data.name}`} value={typed} onChange={(e) => setTyped(e.currentTarget.value)}
          error={typed.length > 0 && !ok ? 'Navnet passer ikke' : undefined} />
        <Group justify="flex-end">
          <Button color="red" leftSection={<IconShieldCheck size={16} />} loading={start.isPending} disabled={!ok || !sel || running || !data.linked} onClick={go}>
            {method === 'supervise_only' ? 'Slet og overvåg uden gendannelse' : 'Slet, overvåg og gendan'}
          </Button>
        </Group>
      </Stack>
    </Paper>
  );
}

function BackupLabel({ b }: { b: NoWipeBackup }) {
  return (
    <Group gap={6} wrap="wrap">
      <Text size="sm">{when(b.created)} · {size(b.bytes)} · {b.store === 'mac' ? 'fra Mac' : 'fra pc'}{b.deviceName ? ` · ${b.deviceName}` : ''}</Text>
      {b.encrypted === true ? <Badge size="xs" color="green" variant="light">Krypteret</Badge>
        : b.encrypted === false ? <Badge size="xs" color="red" variant="light">Ikke krypteret</Badge> : <Badge size="xs" color="gray" variant="light">Kryptering ukendt</Badge>}
      {b.verified === true && <Badge size="xs" color="green" variant="light">Koden tjekket</Badge>}
      {b.sourceEmpty === true && <Badge size="xs" color="red" variant="light">Tom iPad</Badge>}
      {b.pinned && <Badge size="xs" color="blue" variant="light">Fastholdt</Badge>}
    </Group>
  );
}

/** Trinene ("Trin 3/9: Sletter iPad'en – …" i trinteksten). */
function Progress({ job }: { job: NonNullable<NoWipeInfo['job']> }) {
  const m = /Trin (\d+)\/(\d+): ([^–]+)(?:– (.*))?/.exec(job.step ?? '');
  const ids = steps(job.method, job.freshBackup);
  const done = job.status === 'success';
  const n = m ? Number(m[1]) - 1 : -1;
  return (
    <Stack gap={6} mt="xs">
      <Stepper active={done ? ids.length : Math.max(n, 0)} size="xs" iconSize={22} allowNextStepsSelect={false} wrap>
        {ids.map((id) => <Stepper.Step key={id} label={STEP_LABEL[id]} />)}
      </Stepper>
      {m?.[4] && ['pending', 'running'].includes(job.status) && <Alert color="orange" variant="light" p="xs"><Text size="sm"><b>Nu:</b> {m[4]}</Text></Alert>}
    </Stack>
  );
}
