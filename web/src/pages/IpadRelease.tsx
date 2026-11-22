import { useState } from 'react';
import { Alert, Button, Code, Group, List, Modal, Paper, Radio, Stack, Text, TextInput, Title } from '@mantine/core';
import { useNavigate } from 'react-router';
import { IconLockOpen, IconPlaneDeparture } from '@tabler/icons-react';
import { post, useAction, useApi } from '../api';
import { JOB } from '../format';
import { CopyButton } from '../design/copy';
import { JobLive, StatusBadge } from '../components';
import type { IpadReleaseInfo, IpadRow, JobStatus } from '../types';

interface LoosenResult { msg: string; steps: string[]; removalPassword?: string | null; jobId?: number | null }
type Route = { via: 'mac' | 'usb'; machineId: number; udid?: string; label: string };

/** Mac'er og pc'er, iPad'en sidder i lige nu (Mac'er: seneste liste fra en Mac, der er online). */
function routes(x: IpadReleaseInfo): Route[] {
  return [
    ...x.macs.filter((m) => m.online).map((m): Route => ({ via: 'mac', machineId: m.id, label: `${m.name} (Mac med Apple Configurator)` })),
    ...x.usb.map((u): Route => ({ via: 'usb', machineId: u.machineId, udid: u.udid, label: `${u.name} (iPad via USB)` })),
  ];
}
const key = (r: Route) => `${r.via}:${r.machineId}:${r.udid ?? ''}`;

/** Opsætning → "Frigiv iPad" (kun administrator): når barnet er gammelt nok. */
export function ReleaseSection({ r }: { r: IpadRow }) {
  const nav = useNavigate();
  const { data: x } = useApi<IpadReleaseInfo>(['ipads', r.id, 'release'], `/api/ipads/${r.id}/release`, { refetchInterval: 10_000 });
  const [pick, setPick] = useState<string | null>(null);
  const [loosened, setLoosened] = useState<LoosenResult | null>(null);
  const [full, setFull] = useState(false);
  const [confirm, setConfirm] = useState('');
  const loosen = useAction((a: { via?: string; machineId?: number; udid?: string }) => post<LoosenResult>(`/api/ipads/${r.id}/release/loosen`, a), (res) => res.msg);
  const release = useAction((a: { via: string; machineId: number; udid?: string; confirm: string }) => post<{ msg: string; jobId: number }>(`/api/ipads/${r.id}/release/full`, a), (res) => res.msg);
  if (!x) return null;

  const rs = routes(x);
  const chosen = rs.find((y) => key(y) === pick) ?? rs[0] ?? null;
  const job = x.job;
  const running = job && (job.status === 'running' || job.status === 'pending');
  const doLoosen = () => loosen.mutate(x.staticLocked && chosen ? { via: chosen.via, machineId: chosen.machineId, udid: chosen.udid } : { via: 'manual' }, { onSuccess: setLoosened });

  return (
    <Paper withBorder radius="md" p="md">
      <Group gap={8} mb={4}><IconPlaneDeparture size={18} /><Title order={5}>Frigiv iPad</Title></Group>
      <Text size="sm" c="dimmed" mb="sm">Når barnet er gammelt nok til at styre sin iPad selv. Vælg, hvor meget der skal slippes – iPad'ens indhold (apps, billeder, beskeder) bliver i begge tilfælde.</Text>

      {rs.length > 1 && (
        <Radio.Group label="Brug" value={chosen ? key(chosen) : null} onChange={setPick} mb="sm">
          <Stack gap={4} mt={4}>{rs.map((y) => <Radio key={key(y)} value={key(y)} label={y.label} />)}</Stack>
        </Radio.Group>
      )}
      {rs.length === 1 && <Text size="sm" mb="sm">iPad'en sidder i <b>{rs[0].label}</b>.</Text>}

      <Stack gap="md">
        <div>
          <Text fw={600} size="sm">1. Løsn styring</Text>
          <Text size="sm" c="dimmed">Den låste profil fjernes{x.mdm?.locked ? ' via Apple-styring' : x.staticLocked ? (chosen ? ` med ${chosen.label.split(' (')[0]}` : ' – med fjernelseskoden på iPad\'en (sæt den i en Mac eller pc for at gøre det automatisk)') : ''}, iPad'en bliver en familie-iPad, og skemaer (skoletid, sengetid), spærrede tjenester og YouTube-tider fjernes. Intet slettes, og iPad'en er stadig overvåget og i Fleetdeck.</Text>
          <Button mt="xs" variant="default" leftSection={<IconLockOpen size={16} />} loading={loosen.isPending} disabled={!!running} onClick={doLoosen}>Løsn styring</Button>
          {loosened && (
            <Alert mt="xs" color="blue" variant="light">
              <List size="sm" spacing={2}>{loosened.steps.map((s) => <List.Item key={s}>{s}</List.Item>)}</List>
              {loosened.removalPassword && <Group gap={6} mt="xs">
                <Text size="sm">Fjernelseskode:</Text><Code>{loosened.removalPassword}</Code>
                <CopyButton value={loosened.removalPassword}>{({ copied, copy }) => <Button size="compact-xs" variant="default" onClick={copy}>{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
              </Group>}
            </Alert>
          )}
        </div>

        <div>
          <Text fw={600} size="sm">2. Frigiv helt</Text>
          <Text size="sm" c="dimmed">Fleetdeck tager en krypteret backup (også en kopi i Fleetdeck), sletter iPad'en og lægger backuppen tilbage – uden at gøre den overvåget igen. Bagefter afmeldes Apple-styring, og iPad'en fjernes fra AdGuard og Fleetdeck.</Text>
          <Alert mt="xs" color="yellow" variant="light">
            <b>Indholdet kommer tilbage – overvågningen gør ikke.</b> Apps, billeder, beskeder og indstillinger gendannes fra backuppen. Begrænsninger, låst profil og husstandens overvågning er væk for altid (en ny overvågning kræver en sletning). Barnet skal logge ind på sin Apple-konto igen bagefter; har iPad'en Find min iPad slået til, skal den slås fra først. Den sidste backup bliver liggende i Fleetdecks datamappe som sikkerhed.
          </Alert>
          {!chosen && <Text size="sm" c="orange" mt="xs">Sæt iPad'en i en Mac med Apple Configurator eller en pc med "iPad via USB" (og link den), for at frigive den helt.</Text>}
          <Button mt="xs" color="red" variant="light" disabled={!chosen || !!running} onClick={() => { setConfirm(''); setFull(true); }}>Frigiv helt…</Button>
        </div>

        {job && (
          <div>
            <Group gap={6}><StatusBadge map={JOB} value={job.status} /><Text size="sm">{job.title}</Text></Group>
            <JobLive job={{ id: job.id, status: job.status as JobStatus, progress: job.progress ?? undefined, step: job.step ?? undefined, stepSince: undefined }} />
            {!running && job.output && <Code block mt={4} style={{ whiteSpace: 'pre-wrap', maxHeight: 220, overflow: 'auto' }}>{job.output}</Code>}
            {job.action === 'release' && job.status === 'success' && <Text size="sm" c="green" mt={4}>Færdig – iPad'en er frigivet. <Button size="compact-xs" variant="subtle" onClick={() => nav('/ipads')}>Til iPads</Button></Text>}
          </div>
        )}
      </Stack>

      <Modal opened={full} onClose={() => setFull(false)} title={`Frigiv ${r.name} helt?`} centered>
        <Stack gap="sm">
          <Text size="sm">iPad'en bliver <b>slettet</b> og derefter gendannet fra en ny krypteret backup{chosen ? <> via <b>{chosen.label}</b></> : null}. Det tager typisk 30–90 minutter. Lad den sidde i kablet og rør den ikke.</Text>
          <Text size="sm">Indholdet kommer tilbage. Overvågning, låst profil og regler kommer <b>ikke</b> tilbage, og Fleetdeck glemmer iPad'en, når den er færdig.</Text>
          {chosen?.via === 'usb' && <Text size="sm" c="dimmed">Via USB afprøves det stadig – tag også en backup i Finder/Apple Devices, hvis indholdet er vigtigt.</Text>}
          <TextInput label={`Skriv iPad'ens navn (${r.name}) for at bekræfte`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} data-autofocus />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setFull(false)}>Annullér</Button>
            <Button color="red" loading={release.isPending} disabled={!chosen || confirm.trim().toLocaleLowerCase('da') !== r.name.trim().toLocaleLowerCase('da')}
              onClick={() => chosen && release.mutate({ via: chosen.via, machineId: chosen.machineId, udid: chosen.udid, confirm }, { onSuccess: () => setFull(false) })}>Frigiv helt</Button>
          </Group>
        </Stack>
      </Modal>
    </Paper>
  );
}
