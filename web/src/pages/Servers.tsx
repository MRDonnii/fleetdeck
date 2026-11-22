import { Link } from 'react-router';
import { Badge, Group, Paper, SimpleGrid, Stack, Text, ThemeIcon, Title } from '@mantine/core';
import { IconServer2 } from '@tabler/icons-react';
import { useApi } from '../api';
import { Empty, Loading } from '../components';
import { kindOf } from '../design/platform';
import type { MachineDetail, MachineRow } from '../types';
import { UnraidTab } from './Unraid';

/** Servere & Docker: hver Unraid-server med array, containere og VM'er (samme visning som maskinens Unraid-fane). */
export function ServersPage() {
  const { data: ms } = useApi<MachineRow[]>(['machines'], '/api/machines', { refetchInterval: 30_000 });
  if (!ms) return <Loading />;
  const servers = ms.filter((m) => kindOf(m) === 'unraid');
  return (
    <Stack gap="lg">
      <div><Title order={2} style={{ letterSpacing: '-0.02em' }}>Servere &amp; Docker</Title>
        <Text c="dimmed" size="sm" mt={2}>{servers.length ? `${servers.length} server${servers.length > 1 ? 'e' : ''}` : 'Ingen servere endnu'}</Text></div>
      {servers.length === 0 && <Empty>Tilføj din Unraid-server under Tilføj maskine → Linux → Selve Unraid-serveren.</Empty>}
      {servers.length > 1 && (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
          {servers.map((s) => (
            <Paper key={s.id} p="md" component={Link} to={`/maskiner/${s.id}/unraid`} style={{ textDecoration: 'none', color: 'inherit' }}>
              <Group gap="sm"><ThemeIcon variant="light" color="orange" radius="md" size={40}><IconServer2 size={20} /></ThemeIcon>
                <div><Text fw={650}>{s.name}</Text><Text size="xs" c="dimmed">{s.os}</Text></div>
                <Badge ml="auto" variant="light" color={s.online ? 'teal' : 'gray'}>{s.online ? 'Online' : 'Offline'}</Badge></Group>
            </Paper>
          ))}
        </SimpleGrid>
      )}
      {servers.length === 1 && <ServerDetail id={servers[0].id} />}
    </Stack>
  );
}

function ServerDetail({ id }: { id: number }) {
  const { data: m } = useApi<MachineDetail>(['machine', String(id)], `/api/machines/${id}`, { refetchInterval: 20_000 });
  if (!m) return <Loading />;
  if (!m.health.unraid) return <Empty>Serveren har ikke sendt sin oversigt endnu.</Empty>;
  return (
    <>
      <Group gap="sm"><Text fw={650} size="lg"><Link to={`/maskiner/${m.id}`}>{m.name}</Link></Text><Text c="dimmed" size="sm">{m.facts.model}</Text></Group>
      <UnraidTab m={m} />
    </>
  );
}
