import { useState } from 'react';
import { Alert, Anchor, Badge, Box, Button, Card, Group, List, Menu, Modal, Paper, SimpleGrid, Stack, Text, TextInput, Title } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconBulb, IconLifebuoy } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { notifyErr, notifyOk, post, useApi } from './api';
import { useMe } from './auth';
import { ago, JOB, SEV } from './format';
import { fixButton, fixStatus, sinceText, stepParts, stepsOf, type FixTone } from './design/alertHelpText';
import type { AlertRow, FixRisk, HelperFix, Insight, JobRow, MachineDetail } from './types';
import { AlertList, Empty, JobModal, Panel, StatusBadge } from './components';

export const RISK: Record<FixRisk, { label: string; color: string }> = {
  safe: { label: 'Sikker', color: 'green' }, confirm: { label: 'Kræver klik', color: 'yellow' }, danger: { label: 'Kræver bekræftelse', color: 'red' },
};

/** Kør en rettelse: sikre køres med det samme, de andre kræver bekræftelse. Danger kræver maskinens navn. */
export function useRunFix() {
  const qc = useQueryClient();
  const [danger, setDanger] = useState<{ machineId: number; machine: string; fix: HelperFix } | null>(null);
  const exec = async (url: string, body: object = {}) => {
    try {
      const r = await post<{ message: string }>(url, body);
      notifyOk(r.message); qc.invalidateQueries();
    } catch (e) { notifyErr(e); }
  };
  const fixAlert = (a: AlertRow) => {
    if (!a.fix) return;
    if (a.fix.risk === 'safe') return exec(`/api/alerts/${a.id}/fix`);
    modals.openConfirmModal({
      title: a.fix.title,
      children: <Stack gap={6}><Text size="sm" fw={600}>{a.title}</Text><Text size="sm">{a.fix.description ?? a.what} Det udføres på <b>{a.machine}</b>.</Text></Stack>,
      labels: { confirm: fixButton(a.fix)?.label ?? 'Ret nu', cancel: 'Annullér' }, onConfirm: () => exec(`/api/alerts/${a.id}/fix`),
    });
  };
  const runTool = (machineId: number, machine: string, f: HelperFix) => {
    if (f.risk === 'danger') return setDanger({ machineId, machine, fix: f });
    if (f.risk === 'safe') return exec(`/api/machines/${machineId}/helper/${f.id}`);
    modals.openConfirmModal({
      title: f.title, children: <Text size="sm">{f.description} Det udføres på <b>{machine}</b>.</Text>,
      labels: { confirm: 'Kør', cancel: 'Annullér' }, onConfirm: () => exec(`/api/machines/${machineId}/helper/${f.id}`),
    });
  };
  const node = danger && <DangerModal {...danger} onClose={() => setDanger(null)} onRun={(confirm) => exec(`/api/machines/${danger.machineId}/helper/${danger.fix.id}`, { confirm })} />;
  return { fixAlert, runTool, node };
}

function DangerModal({ machine, fix, onClose, onRun }: { machine: string; fix: HelperFix; onClose: () => void; onRun: (c: string) => Promise<void> }) {
  const [v, setV] = useState('');
  return (
    <Modal opened onClose={onClose} title={fix.title}>
      <Stack>
        <Alert color="red" title="Pas på">{fix.description}</Alert>
        <TextInput label={`Skriv maskinens navn (${machine}) for at bekræfte`} value={v} onChange={(e) => setV(e.currentTarget.value)} autoFocus />
        <Group justify="flex-end"><Button variant="default" onClick={onClose}>Annullér</Button>
          <Button color="red" disabled={v.trim().toUpperCase() !== machine.toUpperCase()} onClick={async () => { await onRun(v); onClose(); }}>{fix.title}</Button></Group>
      </Stack>
    </Modal>
  );
}

const TONE: Record<FixTone, string> = { running: 'blue', ok: 'teal', failed: 'red', info: 'dimmed' };

/** Et trin med tekst i anførselstegn (knapper og indstillinger) i fed. */
function Step({ text }: { text: string }) {
  return <>{stepParts(text).map((p, i) => (p.bold ? <b key={i}>{p.text}</b> : <span key={i}>{p.text}</span>))}</>;
}

export interface AlertActions {
  withMachine?: boolean;
  onAck?: (id: number) => void;
  onFix?: (a: AlertRow) => void;
  onIgnore?: (a: AlertRow, all: boolean) => void;
  onJob?: (id: number) => void;
}

/**
 * Hjælpeboksen under en alarm (design A): "Sådan løser du det" med nummererede trin, den store knap, når der er en rettelse,
 * og Kvittér / Ignorér ▾ som stille knapper i samme række. Status for seneste rettelse står nederst.
 */
export function AlertHelp({ a, withMachine, onAck, onFix, onIgnore, onJob }: { a: AlertRow } & AlertActions) {
  const steps = stepsOf(a).length ? stepsOf(a) : a.what ? [a.what] : [];
  const btn = fixButton(a.fix);
  const status = fixStatus(a);
  const busy = a.fixJob?.status === 'pending' || a.fixJob?.status === 'running';
  const statusLine = status && (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <Text size="xs" c={TONE[status.tone]} style={{ flex: 1 }}>{status.text}</Text>
      {a.fixJob && onJob && <Anchor size="xs" onClick={(e) => { e.stopPropagation(); onJob(a.fixJob!.id); }} style={{ whiteSpace: 'nowrap' }}>Åbn jobbet</Anchor>}
    </Group>
  );
  if (a.resolved) return statusLine ? <Box mt={6}>{statusLine}</Box> : null;
  if (!steps.length && !btn) return null;
  const showFix = btn && onFix;
  return (
    <Box mt="sm" p="sm" onClick={(e) => e.stopPropagation()} style={{ cursor: 'default', borderRadius: 'var(--mantine-radius-sm)',
      background: 'color-mix(in srgb, var(--mantine-color-blue-6) 8%, transparent)', border: '1px solid color-mix(in srgb, var(--mantine-color-blue-6) 35%, transparent)' }}>
      <Text fw={650} size="sm" c="light-dark(var(--mantine-color-blue-7), var(--mantine-color-blue-3))" mb={4}>Sådan løser du det</Text>
      {steps.length > 0 && (
        <List type="ordered" size="sm" spacing={2} mb={showFix || onAck || onIgnore ? 'sm' : 0} styles={{ itemWrapper: { display: 'inline' } }}>
          {steps.map((s, i) => <List.Item key={i}><Step text={s} /></List.Item>)}
        </List>
      )}
      {(showFix || (onAck && !a.acknowledged) || a.acknowledged || onIgnore) && (
        <Group gap="xs" wrap="wrap">
          {showFix && <Button size="sm" loading={busy} onClick={() => onFix!(a)}>{btn!.icon} {btn!.label}</Button>}
          {a.acknowledged ? <Text size="xs" c="dimmed">Kvitteret af {a.ackBy}</Text>
            : onAck && <Button size="sm" variant="subtle" color="gray" c="dimmed" onClick={() => onAck(a.id)}>Kvittér</Button>}
          {onIgnore && (
            <Menu position="bottom-start" withinPortal>
              <Menu.Target><Button size="sm" variant="subtle" color="gray" c="dimmed">Ignorér ▾</Button></Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>Ignorér "{a.title}"</Menu.Label>
                <Menu.Item onClick={() => onIgnore(a, false)}>{withMachine ? `Kun på ${a.machine}` : 'Kun på denne maskine'}</Menu.Item>
                <Menu.Item onClick={() => onIgnore(a, true)}>På alle maskiner</Menu.Item>
              </Menu.Dropdown>
            </Menu>
          )}
        </Group>
      )}
      {statusLine && <Box mt="xs">{statusLine}</Box>}
    </Box>
  );
}

/** Et alarmkort (design A): mærke + tydelig titel, hvad der er sket og hvad det betyder, og hjælpeboksen. */
export function AlertCard({ a, onOpen, ...act }: { a: AlertRow; onOpen?: () => void } & AlertActions) {
  const border = { critical: 'red', warning: 'yellow', info: 'cyan' } as const;
  return (
    <Paper withBorder p="sm" radius="sm" onClick={onOpen}
      style={{ borderLeft: `3px solid var(--mantine-color-${border[a.severity]}-6)`, cursor: onOpen ? 'pointer' : undefined, opacity: a.resolved ? 0.75 : 1 }}>
      <Group justify="space-between" wrap="nowrap" align="flex-start" gap="xs">
        <Group gap={8} wrap="wrap" style={{ minWidth: 0 }}>
          <StatusBadge map={SEV} value={a.severity} />
          {act.withMachine && <Text fw={600} size="sm" c="dimmed">{a.machine}</Text>}
          <Text fw={650} size="md">{a.title}</Text>
        </Group>
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{sinceText(a.firstSeen, a.resolved)}</Text>
      </Group>
      <Text size="sm" c="dimmed" mt={4}>{a.detail}</Text>
      {a.why && !a.resolved && <Text size="sm" mt={4}>{a.why}</Text>}
      <AlertHelp a={a} {...act} />
    </Paper>
  );
}

/** Forklaring af et fejlet job med knapper til næste skridt. */
export function InsightBox({ insight, job, onDone }: { insight: Insight; job: JobRow; onDone: () => void }) {
  const { isAdmin } = useMe();
  const qc = useQueryClient();
  const { data: catalog = [] } = useApi<HelperFix[]>(['helper-catalog'], '/api/helper/catalog');
  const act = async (a: string) => {
    try {
      if (a === 'retry') await post(`/api/jobs/${job.id}/retry`);
      else if (a === 'retry_user') await post(`/api/jobs/${job.id}/retry?runAs=user`);
      else if (a === 'retry_system') await post(`/api/jobs/${job.id}/retry?runAs=system`);
      else if (a.startsWith('tool:')) await post(`/api/machines/${job.machineId}/helper/${a.slice(5)}`);
      notifyOk('Sat i kø'); qc.invalidateQueries(); onDone();
    } catch (e) { notifyErr(e); }
  };
  const label = (a: string) => a === 'retry' ? 'Prøv igen' : a === 'retry_user' ? 'Prøv igen som bruger' : a === 'retry_system' ? 'Prøv igen som system'
    : catalog.find((f) => `tool:${f.id}` === a)?.title ?? a;
  return (
    <Alert color={job.status === 'waiting_user' ? 'yellow' : 'orange'} icon={<IconBulb size={18} />} title={insight.title}>
      <Text size="sm">{insight.text}</Text>
      {isAdmin && insight.actions.length > 0 && <Group gap={6} mt="sm">{insight.actions.map((a) => <Button key={a} size="xs" variant="white" onClick={() => act(a)}>{label(a)}</Button>)}</Group>}
    </Alert>
  );
}

/** Fanen "Hjælper" på en maskine: værktøjskasse + historik. */
export function HelperTab({ m }: { m: MachineDetail }) {
  const { isAdmin } = useMe();
  const { data: catalog = [] } = useApi<HelperFix[]>(['helper-catalog'], '/api/helper/catalog');
  const run = useRunFix();
  const [open, setOpen] = useState<number | null>(null);
  const tools = catalog.filter((f) => f.tool && (!f.platforms || f.platforms.includes(m.platform)));
  const groups = [...new Set(tools.map((t) => t.group))];
  const fixable = m.alertList.filter((a) => !a.resolved && a.fix);
  const history = m.jobs.filter((j) => j.source === 'helper').slice(0, 15);
  return (
    <>
      <Panel title={<Group gap={8}><IconLifebuoy size={20} />Problemer hjælperen kan rette</Group>}>
        {fixable.length ? (
          <AlertList alerts={fixable} onFix={isAdmin ? run.fixAlert : undefined} />
        ) : <Empty>Ingen åbne problemer med en rettelse. Brug værktøjerne nedenfor til fejlfinding.</Empty>}
      </Panel>
      {groups.map((g) => (
        <Panel key={g} title={g}>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
            {tools.filter((t) => t.group === g).map((t) => (
              <Card key={t.id} withBorder padding="md">
                <Group justify="space-between" mb={4} wrap="nowrap"><Title order={5}>{t.title}</Title><Badge size="xs" variant="light" color={RISK[t.risk].color}>{RISK[t.risk].label}</Badge></Group>
                <Text size="xs" c="dimmed" mb="sm">{t.description}</Text>
                {isAdmin && <Button size="xs" variant={t.risk === 'danger' ? 'outline' : 'light'} color={t.risk === 'danger' ? 'red' : undefined}
                  onClick={() => run.runTool(m.id, m.name, t)}>Kør</Button>}
              </Card>
            ))}
          </SimpleGrid>
        </Panel>
      ))}
      <Panel title="Hjælperens seneste arbejde" p={0}>
        {history.length ? (
          <Stack gap={0}>{history.map((j) => (
            <Group key={j.id} justify="space-between" px="md" py={8} style={{ borderBottom: '1px solid var(--mantine-color-default-border)', cursor: 'pointer' }} onClick={() => setOpen(j.id)}>
              <Group gap={8}><StatusBadge map={JOB} value={j.status} /><Text size="sm">{j.title.replace(/^Hjælper: /, '')}</Text></Group>
              <Text size="xs" c="dimmed">{j.createdBy} · {ago(j.created)}</Text>
            </Group>))}
          </Stack>
        ) : <Empty>Hjælperen har ikke kørt noget på denne maskine endnu</Empty>}
      </Panel>
      <JobModal id={open} onClose={() => setOpen(null)} />
      {run.node}
    </>
  );
}
