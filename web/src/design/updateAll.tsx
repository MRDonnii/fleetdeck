import { useState } from 'react';
import { Group, Stack, Text, ThemeIcon, UnstyledButton } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconHandStop, IconMessage, IconRefresh } from '@tabler/icons-react';

export type UpdateMode = 'auto' | 'manual';
interface Target { name: string; platform: string; os?: string | null; upgrades?: number }

const sysName = (ts: Target[]) => {
  const p = new Set(ts.filter((t) => !/unraid/i.test(t.os ?? '')).map((t) => t.platform));
  return p.size === 1 && p.has('windows') ? 'Windows-opdateringer' : p.size === 1 && p.has('mac') ? 'macOS-opdateringer' : 'systemopdateringer';
};

function Choice({ active, icon, title, text, onClick }: { active: boolean; icon: React.ReactNode; title: string; text: string; onClick: () => void }) {
  return (
    <UnstyledButton onClick={onClick} className="fd-choice" data-active={active || undefined}>
      <Group wrap="nowrap" align="flex-start" gap="sm">
        <ThemeIcon variant={active ? 'filled' : 'light'} color={active ? 'blue' : 'gray'} radius="md" size="lg">{icon}</ThemeIcon>
        <div><Text fw={600} size="sm">{title}</Text><Text size="xs" c="dimmed" mt={2}>{text}</Text></div>
      </Group>
    </UnstyledButton>
  );
}

function Picker({ targets, onChange }: { targets: Target[]; onChange: (m: UpdateMode) => void }) {
  const [mode, setMode] = useState<UpdateMode>('auto');
  const pick = (m: UpdateMode) => { setMode(m); onChange(m); };
  const nas = targets.some((t) => /unraid/i.test(t.os ?? ''));
  const ups = targets.reduce((s, t) => s + (t.upgrades ?? 0), 0);
  return (
    <Stack gap="sm">
      {ups > 0 && <Text size="sm">{ups} program{ups === 1 ? '' : 'mer'} har en ny version{targets.length > 1 ? ` på ${targets.length} maskiner` : ''}.</Text>}
      <Choice active={mode === 'auto'} icon={<IconRefresh size={18} />} title="Automatisk – den klarer det hele (anbefalet)"
        text={`Installerer ${sysName(targets)} og opdaterer alle programmer. Kræver det en genstart, genstarter maskinen selv bagefter med 5 minutters nedtælling på skærmen.`}
        onClick={() => pick('auto')} />
      <Choice active={mode === 'manual'} icon={<IconHandStop size={18} />} title="Manuelt – kun programmerne"
        text="Opdaterer programmerne, men installerer ikke systemopdateringer og genstarter ikke. Du bestemmer selv, hvornår det sker."
        onClick={() => pick('manual')} />
      <Group gap={6} wrap="nowrap"><IconMessage size={14} style={{ flexShrink: 0, opacity: 0.6 }} />
        <Text size="xs" c="dimmed">Den, der sidder ved maskinen, får en besked om, hvad der sker. Åbne programmer kan blive lukket et øjeblik.</Text></Group>
      {nas && <Text size="xs" c="dimmed">Unraid-servere opdaterer kun containere og plugins og genstartes aldrig af sig selv.</Text>}
    </Stack>
  );
}

/** "Opdatér alt": spørg om automatisk (system + programmer + genstart) eller manuelt (kun programmer), og kør så. */
export function askUpdateAll(targets: Target[], run: (mode: UpdateMode) => void, title?: string) {
  let mode: UpdateMode = 'auto';
  modals.openConfirmModal({
    title: title ?? (targets.length === 1 ? `Opdatér alt på ${targets[0].name}?` : `Opdatér alt på ${targets.length} maskiner?`),
    size: 'lg',
    children: <Picker targets={targets} onChange={(m) => { mode = m; }} />,
    labels: { confirm: 'Start opdatering', cancel: 'Annullér' },
    confirmProps: { disabled: targets.length === 0 },
    onConfirm: () => run(mode),
  });
}
