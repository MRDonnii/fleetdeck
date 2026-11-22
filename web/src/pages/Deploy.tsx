import { useState } from 'react';
import { Badge, Button, Group, List, Progress, ScrollArea, SimpleGrid, Stack, Switch, Table, Text } from '@mantine/core';
import { IconMessageQuestion } from '@tabler/icons-react';
import { del, post, put, useAction, useApi } from '../api';
import { useMe } from '../auth';
import { ago, fmt } from '../format';
import { Empty, Loading, PageHeader, Panel } from '../components';
import { InstallLocalePanel } from '../design/installLocale';
import { AVAIL, DeployWizard, FIELD_LABEL, OsIcon, type DeployData as WizardData, type Field } from './DeployWizard';

type DeployRow = {
  id: number; mac: string; os: 'windows' | 'ubuntu'; series: string; variant: string; mode: 'clean' | 'managed' | 'profile'; profileId: number | null;
  profile: string | null; pcName: string; userName: string; fullName: string; packages: string; autoDisk: boolean; machineId: number | null;
  status: 'ready' | 'installing' | 'done' | 'cancelled' | 'expired'; created: string; createdBy: string; started: string | null; finished: string | null; notes: string;
  osId: string; title: string; askFields: Field[]; encrypt: boolean; admin: boolean; hasSshKey: boolean;
};
type DeployData = WizardData & { deployments: DeployRow[] };

const STATUS: Record<DeployRow['status'], { label: string; color: string }> = {
  ready: { label: 'Klar – venter på netværksboot', color: 'blue' },
  installing: { label: 'Installerer', color: 'yellow' },
  done: { label: 'Færdig', color: 'green' },
  cancelled: { label: 'Annulleret', color: 'gray' },
  expired: { label: 'Udløbet', color: 'gray' },
};

function what(d: DeployRow) {
  return d.os === 'windows' && d.mode === 'profile' && d.profile ? `${d.title.replace(/ \(profil\)$/, '')} – profil ${d.profile}` : d.title;
}

export function DeployPage() {
  const { isAdmin } = useMe();
  const { data } = useApi<DeployData>(['deploy'], '/api/deploy', { refetchInterval: 4_000 });
  const [creating, setCreating] = useState(false);
  const act = useAction(({ id, action }: { id: number; action: string }) => post(`/api/deploy/${id}/${action}`), 'Opdateret');
  const download = useAction((id: string) => post(`/api/os/${id}/download`), 'Hentningen er startet – den kører videre på serveren');
  const remove = useAction((id: string) => del(`/api/os/${id}`), 'Slettet');
  const untested = useAction((on: boolean) => put('/api/deploy/untested', { on }), 'Gemt');
  if (!data) return <Loading />;

  return (
    <>
      <PageHeader title="Udrulning" sub="Installér Windows og Linux over netværket. Tomme felter spørges der om på maskinen.">
        {isAdmin && <Button onClick={() => setCreating(true)}>+ Ny installation</Button>}
      </PageHeader>

      <Stack>
        <Panel title="Udrulninger" p={0}>
          {data.deployments.length ? (
            <ScrollArea type="auto">
              <Table highlightOnHover miw={760} verticalSpacing="xs">
                <Table.Thead><Table.Tr><Table.Th>Status</Table.Th><Table.Th>Hvad</Table.Th><Table.Th>Pc-navn</Table.Th><Table.Th>Bruger</Table.Th>
                  <Table.Th>MAC</Table.Th><Table.Th>Oprettet</Table.Th><Table.Th /></Table.Tr></Table.Thead>
                <Table.Tbody>{data.deployments.map((d) => (
                  <Table.Tr key={d.id}>
                    <Table.Td><Badge variant="light" color={STATUS[d.status].color}>{STATUS[d.status].label}</Badge></Table.Td>
                    <Table.Td><Group gap={6} wrap="nowrap"><OsIcon family={data.os.find((o) => o.id === d.osId)?.family ?? d.os} size={16} />
                      <Text size="sm">{what(d)}</Text></Group>{d.notes && <Text size="xs" c="dimmed">{d.notes}</Text>}
                      {d.askFields.filter((f) => f !== 'disk').length > 0 && <Group gap={4} mt={2}><IconMessageQuestion size={12} color="var(--mantine-color-yellow-6)" />
                        <Text size="xs" c="dimmed">Spørges på maskinen: {d.askFields.map((f) => FIELD_LABEL[f]?.toLowerCase() ?? f).join(', ')}</Text></Group>}</Table.Td>
                    <Table.Td><Text size="sm" fw={600}>{d.pcName || '–'}</Text></Table.Td>
                    <Table.Td><Text size="sm">{d.userName || '–'}</Text></Table.Td>
                    <Table.Td><Text size="sm" ff="monospace">{d.mac}</Text></Table.Td>
                    <Table.Td><Text size="sm" title={fmt(d.created)}>{ago(d.created)}</Text><Text size="xs" c="dimmed">{d.createdBy}</Text></Table.Td>
                    <Table.Td>{isAdmin && <Group gap={4} justify="flex-end" wrap="nowrap">
                      {(d.status === 'ready' || d.status === 'installing')
                        ? <Button size="compact-xs" variant="default" onClick={() => act.mutate({ id: d.id, action: 'cancel' })}>Annullér</Button>
                        : <>
                          <Button size="compact-xs" variant="default" onClick={() => act.mutate({ id: d.id, action: 'again' })}>Igen</Button>
                          <Button size="compact-xs" variant="subtle" color="red" onClick={() => act.mutate({ id: d.id, action: 'delete' })}>Slet</Button>
                        </>}
                    </Group>}</Table.Td>
                  </Table.Tr>))}
                </Table.Tbody>
              </Table>
            </ScrollArea>
          ) : <Empty>Ingen udrulninger endnu. Du kan også bare boote en pc fra netværket og vælge i menuen.</Empty>}
        </Panel>

        <SimpleGrid cols={{ base: 1, md: 2 }}>
          <Panel title="Styresystemer på serveren">
            <Stack gap="sm">{data.os.filter((o) => o.availability !== 'omitted').map((o) => {
              const pct = o.total ? Math.round((o.bytes / o.total) * 100) : 0;
              return (
                <div key={o.id} style={{ opacity: o.availability === 'ready' ? 1 : 0.7 }}>
                  <Group justify="space-between" wrap="nowrap">
                    <Group gap={8} wrap="nowrap"><OsIcon family={o.family} />
                      <div>
                        <Group gap={6}><Text fw={600} size="sm">{o.title}</Text>
                          {o.availability !== 'ready' && <Badge size="xs" variant="light" color={AVAIL[o.availability].color}>{AVAIL[o.availability].label}</Badge>}</Group>
                        <Text size="xs" c={o.state === 'failed' ? 'red' : 'dimmed'}>
                          {o.state === 'downloading' ? o.message
                            : o.state === 'failed' ? `Fejl: ${o.message}`
                            : o.ready ? `Klar${o.version ? `: ${o.version}` : ''}${o.sizeGb ? ` · ${o.sizeGb} GB` : ''}${o.downloaded ? ` · hentet ${ago(o.downloaded)}` : ''}`
                            : o.missing}
                        </Text>
                      </div>
                    </Group>
                    {isAdmin && o.canDownload && o.state !== 'downloading' && (o.availability === 'ready' || data.untested) && <Group gap={4} wrap="nowrap">
                      <Button size="compact-xs" variant={o.ready ? 'default' : 'filled'} onClick={() => download.mutate(o.id)}>{o.ready ? 'Opdatér' : 'Hent'}</Button>
                      {o.ready && <Button size="compact-xs" variant="subtle" color="red" onClick={() => remove.mutate(o.id)}>Slet</Button>}
                    </Group>}
                  </Group>
                  {o.state === 'downloading' && o.total > 0 && <Progress mt={6} value={pct} animated />}
                </div>
              );
            })}
            {data.os.filter((o) => o.availability === 'omitted').map((o) => <Text key={o.id} size="xs" c="dimmed">{o.title}: {o.note}</Text>)}
            {isAdmin && <Switch size="sm" checked={data.untested} onChange={(e) => untested.mutate(e.currentTarget.checked)}
              label="Afprøvning: vis styresystemer, der kommer senere" description="Kun til at afprøve den automatiske installation i en VM, før den slås til for alle." />}
            </Stack>
          </Panel>

          <Panel title="Sådan virker det">
            <List size="sm" spacing={6}>
              <List.Item>Boot pc'en fra netværket (F12/F11/Esc ved opstart, eller vælg "UEFI PXE" i en VM).</List.Item>
              <List.Item><b>Windows:</b> log ind med brugeren til WinSetup-sharet. Har du lavet en udrulning her, kører den direkte. Ellers vælger du i menuen: helt ren, med mine ting eller en profil – og skriver pc-navn og bruger.</List.Item>
              <List.Item><b>Linux:</b> lav en installation her med maskinens MAC-adresse. Så får den GRUB-bootfilen i stedet for Windows' og installerer selv.</List.Item>
              <List.Item>Står et felt tomt i guiden, spørger installationen om det på skærmen. Som standard stopper den ved valg af disk – intet slettes uden at du vælger det.</List.Item>
              <List.Item>Samme pc-navn som før = Fleetdeck genkender pc'en og beholder dens profil og historik.</List.Item>
            </List>
          </Panel>
        </SimpleGrid>
        <InstallLocalePanel canEdit={isAdmin} />
      </Stack>
      {creating && <DeployWizard data={data} onClose={() => setCreating(false)} onDownload={(id) => download.mutate(id)} />}
    </>
  );
}
