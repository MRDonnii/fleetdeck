import { Alert, Code, Group, Stack, Text } from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconAlertTriangle, IconRefresh, IconUserShield } from '@tabler/icons-react';

/** AMD's drivere installeres med AMD's eget program og skal bekræftes først (de kan få skærmen til at blinke). */
export const AMD_GPU = 'gpu:amd';
export const AMD_CHIPSET = 'chipset:amd';
export const isAmdDriver = (id: string) => id === AMD_GPU || id === AMD_CHIPSET;

const major = (v: string | null | undefined) => { const n = parseInt((v ?? '').split('.')[0], 10); return Number.isFinite(n) ? n : null; };

/**
 * Hvor kraftigt skal dialogen advare om, at chipset-pakken ikke kan rulles tilbage?
 * 'first' = pc'en har 6.x (eller en ukendt version) og får 7.x eller nyere: herefter kan 6.x ikke installeres uden oprydning.
 * 'note'  = pc'en har allerede 7.x eller nyere: samme regel, bare til orientering.
 * null    = den nye version er selv ældre end 7.x.
 */
export function chipsetRollback(current: string | null | undefined, available: string | null | undefined): 'first' | 'note' | null {
  const to = major(available), from = major(current);
  if (to === null || to < 7) return null;
  return from !== null && from >= 7 ? 'note' : 'first';
}

interface Up { wingetId: string; name: string; current?: string | null; available?: string | null }

/** Bekræftelsen ud for AMD's grafik- eller chipset-driver. Kalder onConfirm, når brugeren trykker Installér. */
export function askAmdDriver(u: Up, onConfirm: () => void) {
  if (u.wingetId === AMD_GPU) {
    modals.openConfirmModal({
      title: `Installér AMD-driver ${u.available ?? ''}`,
      children: <Text size="sm">Fleetdeck henter AMD's fulde pakke (ca. 1,7 GB), kontrollerer at den er signeret af AMD og installerer den uden vinduer. Skærmen bliver sort et par gange undervejs, så gem dit arbejde først.</Text>,
      labels: { confirm: 'Installér', cancel: 'Annullér' }, onConfirm,
    });
    return;
  }
  const rollback = chipsetRollback(u.current, u.available);
  const unknown = !major(u.current);
  modals.openConfirmModal({
    title: `Installér AMD chipset-driver ${u.available ?? ''}`,
    size: 'lg',
    children: (
      <Stack gap="sm">
        <Text size="sm">Fleetdeck henter AMD Chipset Software {u.available} (ca. 80 MB) direkte fra AMD, kontrollerer, at pakken er signeret af Advanced Micro Devices, og installerer den uden vinduer.</Text>
        <Alert variant="light" color="gray" icon={<IconUserShield size={18} />} title="Kræver en indlogget administrator">
          Pakken køres i brugerens session med administratorrettigheder – som SYSTEM fejler AMD's installation (MSI-fejl 1316). Er ingen logget ind, eller er brugeren ikke administrator, stopper jobbet med en forklaring.
        </Alert>
        {unknown && <Text size="sm" c="dimmed">AMD Chipset Software står ikke længere i programlisten (det sker efter en installation, der rullede tilbage). Driverne er der stadig, og installationen lægger posten tilbage.</Text>}
        {rollback && (
          <Alert variant="light" color="orange" icon={<IconAlertTriangle size={18} />} title={rollback === 'first' ? 'Kan ikke rulles tilbage til 6.x' : 'Husk: 6.x kan ikke installeres igen uden oprydning'}>
            {rollback === 'first' && !unknown && <>Pc'en har i dag {u.current}. </>}
            Efter 7.x eller nyere kan 6.x kun installeres igen, hvis AMD Chipset Software afinstalleres, og mappen <Code>Qt_Dependencies</Code> i <Code>C:\Program Files (x86)\AMD\Chipset_Software</Code> slettes først.
          </Alert>
        )}
        <Group gap={6} wrap="nowrap"><IconRefresh size={14} /><Text size="xs" c="dimmed">Skærmen kan blinke kort. Genstart pc'en bagefter, så de nye drivere bliver brugt.</Text></Group>
      </Stack>
    ),
    labels: { confirm: 'Installér', cancel: 'Annullér' }, onConfirm,
  });
}
