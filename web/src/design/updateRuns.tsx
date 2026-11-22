import { Alert, Badge, Group, Progress, Stack, Text, Tooltip } from '@mantine/core';
import { ago, fmt } from '../format';
import type { MaintenanceRun } from '../types';
import { KIND, MAINT_STATE, reportGroups, roundDots } from './updateRunsText';

/** Runderne som små prikker: færdige, den aktuelle og dem, der er tilbage. */
function Rounds({ run }: { run: MaintenanceRun }) {
  const color = { done: 'var(--mantine-color-green-6)', current: 'var(--mantine-color-blue-6)', todo: 'var(--mantine-color-gray-4)' };
  return (
    <Group gap={4} aria-label={`Runde ${run.round ?? 1} af ${run.maxRounds ?? 4}`}>
      {roundDots(run).map((d, i) => <span key={i} style={{ width: 22, height: 6, borderRadius: 3, background: color[d], display: 'inline-block' }} />)}
      <Text size="xs" c="dimmed" ml={4}>{run.reboots ?? 0} genstart(er)</Text>
    </Group>
  );
}

function List({ title, color, rows }: { title: string; color?: string; rows: { text: string; note?: string }[] }) {
  if (!rows.length) return null;
  return (
    <Stack gap={2}>
      <Text size="xs" fw={600} c={color} tt="uppercase">{title} ({rows.length})</Text>
      {rows.slice(0, 8).map((r, i) => (
        <Text key={i} size="sm" lh={1.35}>{r.text}{r.note && <Text span size="xs" c="dimmed"> – {r.note}</Text>}</Text>
      ))}
      {rows.length > 8 && <Text size="xs" c="dimmed">… og {rows.length - 8} mere</Text>}
    </Stack>
  );
}

/** Seneste vedligeholdelse/opdateringskørsel: runde og fase, status og præcis hvad der mangler. */
export function MaintenanceRunView({ run, autoUpdate, profile }: { run: MaintenanceRun | null; autoUpdate?: { off: boolean; blocked: string | null; noProfile?: boolean }; profile?: string | null }) {
  const rep = run?.report;
  const active = run && !run.finished;
  return (
    <Stack gap="sm">
      {autoUpdate?.blocked ? (
        <Alert color="gray" title="Opdateres aldrig automatisk" p="xs">{autoUpdate.blocked}. Brug Opdatér ud for den enkelte linje.</Alert>
      ) : autoUpdate?.noProfile ? (
        <Alert color="orange" title="Ingen profil" p="xs">Maskinen har ingen profil, så intet opdateres automatisk. Tildel en profil under Rediger.</Alert>
      ) : autoUpdate?.off && (
        <Alert color="orange" title="Automatisk opdatering er slået fra" p="xs">
          Maskinen bliver kun opdateret, når nogen trykker på knapperne. Slå vedligeholdelse til i profilen{profile ? ` (${profile})` : ''}.
        </Alert>
      )}
      {!run ? <Text size="sm" c="dimmed">Ingen vedligeholdelse endnu. Slå natlig vedligeholdelse til i profilen, eller tryk "Vedligehold nu".</Text> : (
        <>
          <Group gap={6} wrap="wrap">
            <Badge variant="light" color={MAINT_STATE[run.state]?.color ?? 'gray'}>{MAINT_STATE[run.state]?.label ?? run.state}</Badge>
            {run.catchUp && <Tooltip label="Maskinen var slukket eller offline i vinduet – indhentet stille, genstart kun når den er ledig"><Badge variant="outline" color="gray">Indhentet</Badge></Tooltip>}
            <Text size="sm" c="dimmed">{KIND[run.kind ?? 'maintenance'] ?? 'Kørsel'} · {ago(run.created)}</Text>
          </Group>
          {run.engine && (run.round ?? 0) > 0 && <Rounds run={run} />}
          {active && run.phase && <Text size="sm" fw={600}>{run.phase}</Text>}
          {active && run.currentJob?.status === 'running' && run.currentJob.progress != null && <Progress value={run.currentJob.progress} size="sm" animated />}
          {active && run.currentJob?.step && !run.currentJob.step.startsWith(run.phase ?? '\u0000') && <Text size="xs" c="dimmed">{run.currentJob.step}</Text>}
          {active && run.step === '' && run.catchUpUntil && <Text size="xs" c="dimmed">Kører, når maskinen kommer online – senest {fmt(run.catchUpUntil)}.</Text>}
          {run.summary && <Text size="sm">{run.summary}</Text>}
          {rep && (
            <Stack gap={8}>
              {reportGroups(rep, !!active).map((g) => <List key={g.key} title={g.title} color={g.color} rows={g.rows} />)}
            </Stack>
          )}
        </>
      )}
    </Stack>
  );
}
