import { Badge, Button, Group, Stack, Text } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconBolt, IconPlayerPlay, IconPower, IconRefresh } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { post, useAction, useApi } from '../api';
import { Panel } from '../components';
import type { MachineDetail } from '../types';

interface AmtStatus { present: boolean; mode?: string | null; version?: string | null; activated: boolean; managed: boolean; ip?: string | null;
  linkUp?: boolean | null; infoError?: string | null; power?: number | null; powerName: string; error?: string | null }

/** Intel AMT (vPro): strøm direkte i hardwaren – virker også, når Windows er hængt eller pc'en er slukket. */
export function AmtPanel({ m, isAdmin }: { m: MachineDetail; isAdmin: boolean }) {
  const qc = useQueryClient();
  const { data: s } = useApi<AmtStatus>(['amt', m.id], `/api/machines/${m.id}/amt`, { refetchInterval: 30_000 });
  const act = useAction((action: string) => post('/api/machines/actions', { machineIds: [m.id], action }), 'Sendt til Intel AMT');
  const run = (action: string) => act.mutate(action, { onSuccess: () => setTimeout(() => qc.invalidateQueries({ queryKey: ['amt', m.id] }), 4000) });
  const ask = (title: string, text: string, label: string, action: string, color?: string) => modals.openConfirmModal({
    title, children: <Text size="sm">{text}</Text>, labels: { confirm: label, cancel: 'Annullér' }, confirmProps: { color }, onConfirm: () => run(action) });
  const on = s?.power === 2;
  const badge = !s ? null : s.managed && s.activated ? <Badge color={s.error ? 'red' : on ? 'teal' : 'gray'} variant="light">{s.error ? 'Kan ikke nås' : s.powerName}</Badge>
    : s.activated ? <Badge color="yellow" variant="light">Aktiveret – anden adgangskode</Badge> : <Badge color="gray" variant="light">Ikke sat op</Badge>;
  return (
    <Panel title="Intel AMT (vPro)" right={badge}>
      <Stack gap="xs">
        {!s ? <Text size="sm" c="dimmed">Henter status…</Text> : s.managed && s.activated ? (<>
          <Text size="sm">Fleetdeck styrer strømmen direkte i hardwaren{s.ip ? ` (${s.ip})` : ''} – også når Windows er hængt, eller pc'en er slukket.
            {s.version ? ` AMT ${s.version}.` : ''}</Text>
          {s.error && <Text size="xs" c="red">{s.error}</Text>}
          {isAdmin && <Group gap="xs">
            <Button size="xs" leftSection={<IconPlayerPlay size={14} />} disabled={on} loading={act.isPending}
              onClick={() => ask(`Tænd ${m.name}?`, 'Tænder pc\'en via Intel AMT.', 'Tænd', 'amt_on')}>Tænd</Button>
            <Button size="xs" variant="default" color="orange" leftSection={<IconRefresh size={14} />} loading={act.isPending}
              onClick={() => ask(`Hård genstart af ${m.name}?`, 'Genstarter med det samme som på reset-knappen. Ikke gemt arbejde går tabt. Brug den, når Windows er hængt.', 'Genstart nu', 'amt_reset', 'orange')}>Hård genstart</Button>
            <Button size="xs" variant="default" color="red" leftSection={<IconPower size={14} />} disabled={s.power != null && !on} loading={act.isPending}
              onClick={() => ask(`Sluk ${m.name} hårdt?`, 'Slukker med det samme som ved at holde tænd-knappen inde. Ikke gemt arbejde går tabt.', 'Sluk nu', 'amt_off', 'red')}>Sluk hårdt</Button>
            <Button size="xs" variant="subtle" color="gray" onClick={() => ask('Fjern Intel AMT-opsætningen?', 'AMT deaktiveres på pc\'en, og Fleetdeck glemmer adgangskoden. Det kan sættes op igen bagefter.', 'Fjern', 'amt_deactivate', 'red')}>Fjern opsætning</Button>
          </Group>}
        </>) : s.activated ? (
          <Text size="sm">AMT er aktiveret på pc'en, men med en adgangskode Fleetdeck ikke kender. Nulstil den i Intel MEBx (Ctrl+P ved opstart, "Unconfigure Network Access"), så kan Fleetdeck sætte den op.</Text>
        ) : (<>
          <Text size="sm">Pc'en har Intel AMT{s.version ? ` ${s.version}` : ''}, men det er ikke sat op. Med AMT kan Fleetdeck tænde, slukke og hård-genstarte pc'en direkte i hardwaren – også når Windows er hængt, eller Wake-on-LAN ikke virker.</Text>
          {s.infoError && <Text size="xs" c="dimmed">{s.infoError}</Text>}
          {isAdmin && <Group><Button size="xs" leftSection={<IconBolt size={14} />} loading={act.isPending} disabled={!m.online}
            onClick={() => ask(`Opsæt Intel AMT på ${m.name}?`, 'AMT aktiveres i klient-tilstand med Intels eget værktøj, og Fleetdeck laver og gemmer en stærk adgangskode (krypteret). Det tager et minut. Strømstyring virker bagefter uden at nogen skal godkende; fjernskærm via AMT kræver i denne tilstand en kode, der vises på pc\'ens skærm.', 'Opsæt', 'amt_activate')}>
            Opsæt Intel AMT</Button>{!m.online && <Text size="xs" c="dimmed">Pc'en skal være tændt</Text>}</Group>}
        </>)}
      </Stack>
    </Panel>
  );
}
