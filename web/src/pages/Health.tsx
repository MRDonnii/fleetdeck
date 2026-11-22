import { useState } from 'react';
import { useNavigate } from 'react-router';
import { Anchor, Paper, ScrollArea, SimpleGrid, Switch, Table, Text } from '@mantine/core';
import { post, useAction, useApi } from '../api';
import { useMe } from '../auth';
import type { AlertRow, MachineRow } from '../types';
import { AlertList, HealthCell, Loading, OnlineDot, PageHeader, Panel, UsageBar } from '../components';
import { useRunFix } from '../helper';

export function HealthPage() {
  const nav = useNavigate();
  const { isAdmin } = useMe();
  const [resolved, setResolved] = useState(false);
  const { data: ms } = useApi<MachineRow[]>(['machines'], '/api/machines', { refetchInterval: 30_000 });
  const { data: alerts = [] } = useApi<AlertRow[]>(['alerts', resolved], `/api/alerts?openOnly=${!resolved}`, { refetchInterval: 30_000 });
  const ack = useAction((id: number) => post(`/api/alerts/${id}/ack`), 'Kvitteret');
  const ignore = useAction((b: { id: number; key: string; title: string; all: boolean }) => post(`/api/machines/${b.id}/ignore-alert`, b), 'Ignoreres fremover');
  const runFix = useRunFix();
  if (!ms) return <Loading />;
  const shown = resolved ? alerts : alerts.filter((a) => !a.acknowledged);   // en kvitteret alarm er set og forsvinder fra listen
  const crit = alerts.filter((a) => !a.resolved && !a.acknowledged && a.severity === 'critical').length;
  const warn = alerts.filter((a) => !a.resolved && !a.acknowledged && a.severity === 'warning').length;
  const avg = ms.length ? Math.round(ms.reduce((s, m) => s + (m.healthScore ?? 100), 0) / ms.length) : null;
  return (
    <>
      <PageHeader title="Helbred" sub="Alarmer og helbredsscore for alle maskiner">
        <Switch label="Vis løste og kvitterede" checked={resolved} onChange={(e) => setResolved(e.currentTarget.checked)} />
      </PageHeader>
      <SimpleGrid cols={{ base: 3 }} mb="md">
        {[['Kritiske', crit, crit ? 'red' : 'green'], ['Advarsler', warn, warn ? 'yellow' : 'green'], ['Gns. score', avg ?? '–', undefined]].map(([l, v, c]) => (
          <Paper key={l as string} withBorder p="md"><Text size="xs" c="dimmed" tt="uppercase" fw={600}>{l}</Text><Text fz={28} fw={700} c={c as string | undefined}>{v}</Text></Paper>
        ))}
      </SimpleGrid>
      <Panel title="Maskiner" p={0}>
        <ScrollArea type="auto"><Table highlightOnHover miw={500}><Table.Thead><Table.Tr><Table.Th>Maskine</Table.Th><Table.Th>Helbred</Table.Th><Table.Th>CPU</Table.Th><Table.Th>RAM</Table.Th><Table.Th ta="right">Alarmer</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>{ms.map((m) => (
            <Table.Tr key={m.id} style={{ cursor: 'pointer' }} onClick={() => nav(`/maskiner/${m.id}/helbred`)}>
              <Table.Td><Anchor component="span" fw={600}><OnlineDot online={m.online} /> {m.name}</Anchor></Table.Td><Table.Td><HealthCell state={m.healthState} score={m.healthScore} /></Table.Td>
              <Table.Td><UsageBar value={m.cpu} /></Table.Td><Table.Td><UsageBar value={m.ram} warn={85} bad={95} /></Table.Td><Table.Td ta="right">{m.alerts || ''}</Table.Td>
            </Table.Tr>))}</Table.Tbody></Table></ScrollArea>
      </Panel>
      <Panel title="Alarmer"><AlertList alerts={shown} withMachine onAck={isAdmin ? (id) => ack.mutate(id) : undefined} onFix={isAdmin ? runFix.fixAlert : undefined}
        onIgnore={isAdmin ? (a, all) => ignore.mutate({ id: a.machineId, key: a.key, title: a.title, all }) : undefined} />{runFix.node}</Panel>
      <Text size="sm" c="dimmed">Grænser og beskeder til Discord eller ntfy sættes under Indstillinger.</Text>
    </>
  );
}
