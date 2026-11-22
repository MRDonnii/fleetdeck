import type { ReactNode } from 'react';
import { Badge, Stack, Text } from '@mantine/core';
import { KeyValue } from '../components';
import { TPM_KIND, tpmBitLocker, tpmManufacturer, tpmPresent, tpmReady, type Shown, type TpmEncryption, type TpmInfo } from './tpmText';

const badge = (s: Shown | null) => (s ? <Badge variant="light" color={s.tone}>{s.text}</Badge> : '–');

/** TPM-afsnittet i "Sikkerhed": til stede, version, producent, firmware, klar og om BitLocker bruger den. */
export function TpmSection({ tpm, encryption, platform }: { tpm?: TpmInfo | null; encryption?: TpmEncryption | null; platform: string }) {
  if (!tpm) return null;
  const rows: [string, ReactNode][] = [['Til stede', badge(tpmPresent(tpm, platform))]];
  if (tpm.present) {
    rows.push(
      ['Version', tpm.version ?? '–'],
      ['Type', tpm.kind ? TPM_KIND[tpm.kind] ?? tpm.kind : '–'],
      ['Producent', tpmManufacturer(tpm) ?? '–'],
      ['Firmware', tpm.firmware ?? '–'],
      ['Klar', badge(tpmReady(tpm))],
    );
  }
  const bl = tpmBitLocker(encryption, platform);
  if (bl) rows.push(['Bruges af BitLocker', badge(bl)]);
  if (tpm.attention?.length) rows.push(['Bemærk', <Stack gap={0}>{tpm.attention.map((a) => <Text key={a} size="sm">{a}</Text>)}</Stack>]);
  return (
    <>
      <Text fw={600} mt="md" mb={4}>TPM</Text>
      <KeyValue rows={rows} />
      {tpm.error && <Text size="xs" c="dimmed" mt={4}>{tpm.error}</Text>}
    </>
  );
}
