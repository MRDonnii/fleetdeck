import { Alert, Badge, Button, Code, Group, NumberInput, PasswordInput, SimpleGrid, Stack, Text, TextInput, ThemeIcon } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconAlertOctagon, IconCircleCheck, IconCircleDashed, IconPlayerPlay, IconPlugConnected, IconPlugOff, IconRefresh } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { post, put, useAction, useApi } from '../api';
import { Panel } from '../components';
import { ago, fmt, JOB } from '../format';
import type { JobStatus, MachineDetail } from '../types';

interface PowerJob { id: number; type: string; title: string; status: JobStatus; step: string; powerAt: string | null; hungAt: string | null; finished: string | null; wolTest: boolean }
interface WolJob { id: number; title: string; status: JobStatus; finished: string | null; output: string }
interface PowerStatus {
  switch: string | null; protected: boolean; label: string; sensor: string | null; minWatts: number; haConfigured: boolean; watts: number | null; wattsError: string | null; hangMinutes: number;
  job: PowerJob | null; canCycle: boolean; cycleWhy: string | null;
  wol: { supported: boolean; hasMac: boolean; prepared: boolean; jobs: WolJob[] };
  bios: { name: string; value: string; why: string }[];
}

/** De linjer i klargøringens output, der viser netkortets indstillinger ("Navn = værdi"). */
export function wolLines(output: string): { name: string; value: string; ok: boolean }[] {
  return output.split('\n').map((l) => l.trim()).filter((l) => / = /.test(l) && !l.startsWith('Husk'))
    .map((l) => { const [name, rest] = l.split(' = ', 2); return { name, value: rest.replace(/\s*\(.*\)$/, ''), ok: !/kunne ikke/i.test(l) }; })
    .filter((x, i, a) => a.findIndex((y) => y.name === x.name) === i).slice(0, 8);
}

/** Strøm og genstart på maskinens side: genstart der hænger, kontakt med strømmåler i Home Assistant og Wake-on-LAN. */
export function PowerPanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const qc = useQueryClient();
  const { data: s } = useApi<PowerStatus>(['power', m.id], `/api/machines/${m.id}/power`, { refetchInterval: 20_000 });
  const refresh = () => setTimeout(() => qc.invalidateQueries({ queryKey: ['power', m.id] }), 1500);
  const [edit, setEdit] = useState(false);
  const [f, setF] = useState({ switch: '', sensor: '', minWatts: 15 });
  useEffect(() => { if (s && !edit) setF({ switch: s.switch ?? '', sensor: s.sensor ?? '', minWatts: s.minWatts }); }, [s, edit]);
  const save = useAction(() => put(`/api/machines/${m.id}/power`, f), 'Gemt');
  const cycle = useAction((confirmName: string | undefined) => post<{ note: string }>(`/api/machines/${m.id}/power/cycle`, { confirm: true, confirmName }), (r) => r.note);
  const wol = useAction((action: string) => post(`/api/machines/${m.id}/wol/${action}`), 'Sendt til maskinen');
  if (!s) return null;
  const j = s.job;
  const hung = j?.status === 'hung';
  const waiting = j?.status === 'restarting';
  const reboot = j?.type === 'reboot';

  const askCycle = () => {
    let typed = '';
    modals.openConfirmModal({
      title: `Sluk og tænd strømmen på ${s.label}?`,
      children: <Stack gap="xs">
        <Text size="sm">Strømmåleren viser <b>{Math.round(s.watts ?? 0)} W</b>. Kontakten <Code>{s.switch}</Code> slukkes i 10 sekunder og tændes igen. Ikke gemt arbejde går tabt.</Text>
        <Text size="xs" c="dimmed">Pc'en starter selv igen, hvis BIOS har "Restore on AC Power Loss = Power On". Ellers sender Fleetdeck Wake-on-LAN bagefter.</Text>
        {s.protected && <TextInput label="Maskinen er beskyttet – skriv dens navn" onChange={(e) => { typed = e.currentTarget.value; }} />}
      </Stack>,
      labels: { confirm: 'Sluk og tænd', cancel: 'Annullér' }, confirmProps: { color: 'red' },
      onConfirm: () => cycle.mutate(s.protected ? typed : undefined, { onSuccess: refresh }),
    });
  };
  const ask = (title: string, text: string, label: string, action: string) => modals.openConfirmModal({
    title, children: <Text size="sm">{text}</Text>, labels: { confirm: label, cancel: 'Annullér' }, onConfirm: () => wol.mutate(action, { onSuccess: refresh }) });

  const lastPrep = s.wol.jobs.find((x) => x.title.startsWith('Gør klar') || x.title.startsWith('Fortryd'));
  const lastTest = s.wol.jobs.find((x) => x.title.startsWith('Test'));
  const lines = lastPrep?.status === 'success' && lastPrep.title.startsWith('Gør klar') ? wolLines(lastPrep.output) : [];
  const watts = s.watts != null ? <Badge variant="light" color={s.watts >= s.minWatts ? 'orange' : 'gray'}>{Math.round(s.watts)} W nu</Badge> : null;

  return (
    <Panel title="Strøm og genstart" right={j && (hung || waiting) ? <Badge color={JOB[j.status].color} variant="light">{reboot || !waiting ? JOB[j.status].label : 'Slukker…'}</Badge> : watts}>
      <Stack gap="sm">
        {hung && j && <Alert color="red" icon={<IconAlertOctagon size={18} />} title={reboot ? 'Genstarten hænger' : 'Slukningen hænger'}>
          <Stack gap={6}>
            <Text size="sm">Planlagt {j.powerAt ? fmt(j.powerAt) : ''}. {j.wolTest ? 'Pc\'en kom ikke op igen.' : `Maskinen er ikke ${reboot ? 'tilbage' : 'slukket'} efter ${s.hangMinutes} min.`}
              {s.watts != null && <> Strømmåleren viser <b>{Math.round(s.watts)} W</b>.</>}</Text>
            {isAdmin && <Group gap="xs">
              <Button size="xs" color="red" leftSection={<IconPlugOff size={14} />} disabled={!s.canCycle} loading={cycle.isPending} onClick={askCycle}>Sluk og tænd strømmen</Button>
              {!s.canCycle && s.cycleWhy && <Text size="xs" c="dimmed">{s.cycleWhy}</Text>}
            </Group>}
            {m.health.amt?.present && <Text size="xs" c="dimmed">Pc'en har Intel AMT: "Hård genstart" i AMT-panelet virker også, når Windows er hængt.</Text>}
          </Stack>
        </Alert>}
        {waiting && j && <Alert color="blue" icon={<IconRefresh size={18} />} title={j.wolTest ? 'Tester Wake-on-LAN…' : reboot ? 'Genstarter…' : 'Slukker…'}>
          <Text size="sm">{j.step || 'Venter på maskinen'}. Planlagt {j.powerAt ? fmt(j.powerAt) : ''}; jobbet lykkes først, når {reboot ? 'maskinen er tilbage med en ny opstartstid' : 'maskinen er slukket'} (venter højst {s.hangMinutes} min.).</Text>
        </Alert>}

        <div>
          <Group justify="space-between" mb={4}><Text fw={600} size="sm">Kontakt i Home Assistant</Text>
            {isAdmin && !edit && <Button size="compact-xs" variant="subtle" onClick={() => setEdit(true)}>Ret</Button>}</Group>
          {!s.haConfigured && <Text size="xs" c="dimmed">Sæt Home Assistant-API op under Indstillinger for at bruge en kontakt med strømmåler.</Text>}
          {edit ? <Stack gap="xs">
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              <TextInput label="Kontakt" placeholder="switch.kontor_pc" value={f.switch} onChange={(e) => setF({ ...f, switch: e.currentTarget.value })} />
              <TextInput label="Strømmåler" placeholder="sensor.kontor_pc_effekt" value={f.sensor} onChange={(e) => setF({ ...f, sensor: e.currentTarget.value })} />
              <NumberInput label="Tændt over (W)" min={1} max={2000} value={f.minWatts} onChange={(v) => setF({ ...f, minWatts: Number(v) || 15 })} />
            </SimpleGrid>
            <Group gap="xs"><Button size="xs" loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: () => { setEdit(false); refresh(); } })}>Gem</Button>
              <Button size="xs" variant="default" onClick={() => setEdit(false)}>Annullér</Button></Group>
          </Stack> : s.switch || s.sensor ? <Stack gap={2}>
            <Text size="sm">Kontakt <Code>{s.switch ?? '–'}</Code> · måler <Code>{s.sensor ?? '–'}</Code> {watts}</Text>
            {s.wattsError && <Text size="xs" c="red">{s.wattsError}</Text>}
            <Text size="xs" c="dimmed">Over {s.minWatts} W = tændt. "Sluk og tænd strømmen" tilbydes kun, når en genstart hænger, og måleren viser strøm.</Text>
          </Stack> : <Text size="xs" c="dimmed">Ingen kontakt valgt.</Text>}
        </div>

        <div>
          <Group justify="space-between" mb={4}><Text fw={600} size="sm">Wake-on-LAN</Text>
            {s.wol.prepared && <Badge size="sm" variant="light" color="teal">Gjort klar</Badge>}</Group>
          {s.wol.supported && <>
            <Text size="xs" c="dimmed" mb={4}>Windows-siden sætter Fleetdeck selv (med backup): Wake on Magic Packet = Enabled, Enable PME = Enabled, Wake on Link Settings = Disabled, "Tillad at enheden vækker computeren" og Hurtig start fra.</Text>
            {lines.map((l) => <Group key={l.name} gap={6} wrap="nowrap"><ThemeIcon size="xs" variant="transparent" color={l.ok ? 'teal' : 'orange'}><IconCircleCheck size={14} /></ThemeIcon>
              <Text size="xs" style={{ flex: 1 }}>{l.name}</Text><Text size="xs" fw={600}>{l.value}</Text></Group>)}
          </>}
          <Text size="xs" c="dimmed" mt={6} mb={2}>I BIOS/UEFI (tjek selv – kan ikke sættes fra styresystemet):</Text>
          {s.bios.map((b) => <Group key={b.name} gap={6} wrap="nowrap" title={b.why}><ThemeIcon size="xs" variant="transparent" color="gray"><IconCircleDashed size={14} /></ThemeIcon>
            <Text size="xs" style={{ flex: 1 }}>{b.name}</Text><Text size="xs" fw={600}>{b.value}</Text></Group>)}
          {lastTest && lastTest.status !== 'restarting' && lastTest.status !== 'pending' && lastTest.status !== 'running' &&
            <Alert mt="xs" p="xs" color={lastTest.status === 'success' ? 'teal' : 'orange'} icon={lastTest.status === 'success' ? <IconCircleCheck size={16} /> : <IconAlertOctagon size={16} />}>
              <Text size="xs">Sidste test {lastTest.finished ? ago(lastTest.finished) : ''}: {lastTest.output.split('\n').filter((l) => /Wake-on-LAN (virker|virkede)/.test(l)).pop()?.replace(/^\[Fleetdeck [^\]]*\]\s*/, '') ?? JOB[lastTest.status].label}</Text>
            </Alert>}
          {isAdmin && <Group gap="xs" mt="xs">
            {s.wol.supported && <Button size="xs" leftSection={<IconPlugConnected size={14} />} loading={wol.isPending} disabled={!m.online}
              onClick={() => ask(`Gør ${m.name} klar til Wake-on-LAN?`, 'Fleetdeck gemmer de nuværende værdier og sætter netkortet og Hurtig start op til Wake-on-LAN. Netkortets nye indstillinger gælder fra næste genstart.', 'Gør klar', 'prepare')}>Gør klar til WoL</Button>}
            <Button size="xs" variant="default" leftSection={<IconPlayerPlay size={14} />} loading={wol.isPending} disabled={!m.online || !s.wol.hasMac || waiting}
              onClick={() => ask(`Test Wake-on-LAN på ${m.name}?`, 'Pc\'en slukker om 1 minut (brugeren får besked). Når den er slukket, sender Fleetdeck et magic packet og måler, hvor længe den er om at komme op.', 'Start testen', 'test')}>Test: sluk, væk, mål</Button>
            {s.wol.supported && s.wol.prepared && <Button size="xs" variant="subtle" color="gray" loading={wol.isPending} disabled={!m.online}
              onClick={() => ask('Fortryd Wake-on-LAN-opsætningen?', 'De gemte værdier fra før sættes tilbage på netkortet og Hurtig start.', 'Fortryd', 'restore')}>Fortryd</Button>}
          </Group>}
        </div>
      </Stack>
    </Panel>
  );
}

/** Indstillinger: Home Assistants REST-API til kontakter med strømmåler, og hvornår en genstart "hænger". */
export function HaApiPanel({ isAdmin }: { isAdmin: boolean }) {
  const { data: s, refetch } = useApi<{ url: string; hasToken: boolean; hangMinutes: number }>(['power-ha'], '/api/power/ha');
  const [f, setF] = useState({ url: '', token: '', hangMinutes: 5 });
  useEffect(() => { if (s) setF({ url: s.url, token: '', hangMinutes: s.hangMinutes }); }, [s]);
  const save = useAction(() => put('/api/power/ha', f), 'Gemt');
  const test = useAction(() => post<{ status: string }>('/api/power/ha/test'), (r) => r.status);
  if (!s) return null;
  return (
    <Panel title="Home Assistant-API (strøm)" right={<Badge variant="light" color={s.url && s.hasToken ? 'teal' : 'gray'}>{s.url && s.hasToken ? 'Sat op' : 'Slået fra'}</Badge>}>
      <Stack>
        <Text size="sm" c="dimmed">Bruges, når en genstart hænger: maskinens side kan pege på en kontakt (switch.…) med strømmåler, og Fleetdeck kan slukke og tænde strømmen –
          altid med bekræftelse og kun, når måleren viser strøm. Lav en adgangsnøgle (long-lived access token) under din profil i Home Assistant.</Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <TextInput label="Adresse" placeholder="http://homeassistant.local:8123" disabled={!isAdmin} value={f.url} onChange={(e) => setF({ ...f, url: e.currentTarget.value })} />
          <PasswordInput label="Adgangsnøgle" placeholder={s.hasToken ? '(uændret)' : ''} disabled={!isAdmin} value={f.token} onChange={(e) => setF({ ...f, token: e.currentTarget.value })} />
          <NumberInput label="Hænger efter (min.)" description="Efter det planlagte tidspunkt" min={1} max={120} disabled={!isAdmin} value={f.hangMinutes} onChange={(v) => setF({ ...f, hangMinutes: Number(v) || 5 })} />
        </SimpleGrid>
        {isAdmin && <Group gap="xs">
          <Button size="xs" loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: () => { void refetch(); } })}>Gem</Button>
          <Button size="xs" variant="default" loading={test.isPending} disabled={!s.url || !s.hasToken} onClick={() => test.mutate(undefined)}>Test forbindelsen</Button>
        </Group>}
      </Stack>
    </Panel>
  );
}
