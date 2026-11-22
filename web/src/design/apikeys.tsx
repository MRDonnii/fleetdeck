// Indstillinger → Adgang for programmer: Login fra Kontrolrum (SSO, til personer) og API-nøgler (til programmer) på én side.
import { useState, type ReactNode } from 'react';
import {
  ActionIcon, Alert, Badge, Box, Button, Checkbox, Code, Group, Modal, Paper, Radio, Select, SimpleGrid, Stack, Switch, Table, Text, TextInput, ThemeIcon, Title, Tooltip,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { IconAlertTriangle, IconCheck, IconCopy, IconHome, IconKey, IconLock, IconLogin2, IconPlus, IconRefresh, IconTrash, IconUsers, IconWorld } from '@tabler/icons-react';
import { del, post, put, useAction, useApi } from '../api';
import { CopyButton } from './copy';
import { ROLE_LABELS } from './family';
import { Empty, Loading } from '../components';
import {
  EXPIRY, SCOPES, dateText, expiresText, headerExample, keySummary, lastUsedText, ownEndpoints, scopeInfo, unused90, usageTone,
  type ApiKeyRow, type ApiKeysInfo, type Scope,
} from './apikeysText';

type SsoInfo = {
  enabled: boolean; unreadable: boolean; fingerprint: string | null; created: string | null; createdBy: string | null; endpoint: string;
  lastUsed: { at: string; user: string; ip: string } | null;
  users: { id: number; username: string; role: string; twoFactor: boolean; allowed: boolean }[];
};

const mono = { fontFamily: 'var(--mantine-font-family-monospace)' } as const;

function Card({ icon, color, title, badge, right, sub, children }: { icon: ReactNode; color: string; title: ReactNode; badge?: ReactNode; right?: ReactNode; sub: ReactNode; children?: ReactNode }) {
  return (
    <Paper withBorder radius="md" p={0} style={{ overflow: 'hidden' }}>
      <Box p="md">
        <Group justify="space-between" align="flex-start" gap="sm">
          <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
            <ThemeIcon variant="light" color={color} radius="md" size="lg">{icon}</ThemeIcon>
            <Group gap="xs" style={{ minWidth: 0 }}><Title order={4}>{title}</Title>{badge}</Group>
          </Group>
          {right && <Group gap="xs">{right}</Group>}
        </Group>
        <Text size="sm" c="dimmed" mt="xs">{sub}</Text>
      </Box>
      {children}
    </Paper>
  );
}

function Stat({ label, value, sub, monoValue }: { label: string; value: ReactNode; sub?: ReactNode; monoValue?: boolean }) {
  return (
    <Paper withBorder radius="md" p="sm">
      <Text size="xs" c="dimmed">{label}</Text>
      <Text fw={600} style={monoValue ? mono : undefined}>{value}</Text>
      {sub && <Text size="xs" c="dimmed">{sub}</Text>}
    </Paper>
  );
}

/** "Kopiér nøglen nu – den vises kun én gang". Fælles for API-nøgler og SSO-nøglen. */
export function ShownOnceModal({ shown, onDone }: { shown: { title: string; label: string; summary: string; secret: string; usage: string } | null; onDone: () => void }) {
  const [saved, setSaved] = useState(false);
  const done = () => { setSaved(false); onDone(); };
  return (
    <Modal opened={shown !== null} onClose={() => undefined} withCloseButton={false} closeOnClickOutside={false} closeOnEscape={false} size="lg"
      title={<Group gap="sm"><ThemeIcon color="green" variant="light" radius="md"><IconCheck size={18} /></ThemeIcon><Text fw={700} size="lg">{shown?.title}</Text></Group>}>
      {shown && <Stack gap="md">
        <Alert color="orange" variant="filled" icon={<IconAlertTriangle />} title="Kopiér nøglen nu – den vises kun én gang">
          Bagefter kan hverken du eller Fleetdeck se den igen. Mister du den, sletter du den og laver en ny.
        </Alert>
        <Box>
          <Text fw={500}>{shown.label}</Text>
          <Text size="xs" c="dimmed" mb={6}>{shown.summary}</Text>
          <Group gap="xs" wrap="nowrap" align="stretch">
            <Code block style={{ ...mono, flex: 1, wordBreak: 'break-all', whiteSpace: 'pre-wrap', border: '1px dashed var(--mantine-color-blue-6)', fontSize: 14, fontWeight: 600 }} data-testid="api-key-secret">{shown.secret}</Code>
            <CopyButton value={shown.secret}>{({ copied, copy }) =>
              <Button onClick={copy} leftSection={copied ? <IconCheck size={16} /> : <IconCopy size={16} />} color={copied ? 'green' : 'blue'} h="auto">{copied ? 'Kopieret' : 'Kopiér'}</Button>}</CopyButton>
          </Group>
        </Box>
        <Box>
          <Text fw={500} mb={6}>Sådan bruges den</Text>
          <Code block style={mono}>{shown.usage}</Code>
        </Box>
        <Checkbox checked={saved} onChange={(e) => setSaved(e.currentTarget.checked)} label="Jeg har gemt nøglen i programmet eller et sikkert sted" />
        <Group justify="flex-end"><Button disabled={!saved} onClick={done}>Færdig</Button></Group>
      </Stack>}
    </Modal>
  );
}

/** Fingeraftrykket (8 hex-tegn) som "4F2A-91C0". */
const fp = (f: string | null) => f ? `${f.slice(0, 4)}-${f.slice(4, 8)}`.toUpperCase() : '–';

function SsoCard() {
  const { data } = useApi<{ sso: SsoInfo }>(['kontrolrum'], '/api/settings/kontrolrum');
  const [shown, setShown] = useState<string | null>(null);
  const [usersOpen, setUsersOpen] = useState(false);
  const make = useAction(() => post<{ key: string }>('/api/settings/kontrolrum-sso'), 'SSO-nøglen er lavet');
  const off = useAction(() => del('/api/settings/kontrolrum-sso'), 'Login fra Kontrolrum er slået fra');
  const allow = useAction((a: { id: number; allowed: boolean }) => put(`/api/settings/kontrolrum-sso/users/${a.id}`, { allowed: a.allowed }));
  const sso = data?.sso;
  const create = () => make.mutate(undefined, { onSuccess: (r) => setShown(r.key) });
  const rotate = () => modals.openConfirmModal({ title: 'Ny SSO-nøgle?', children: <Text size="sm">Den gamle nøgle holder op med at virke med det samme. Indsæt den nye i Kontrolrum bagefter.</Text>,
    labels: { confirm: 'Lav ny nøgle', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: create });
  const disable = () => modals.openConfirmModal({ title: 'Slå login fra Kontrolrum fra?', children: <Text size="sm">Nøglen slettes, og Kontrolrum kan ikke længere logge ind. Allerede åbne sessioner fortsætter. API-nøglerne virker som før.</Text>,
    labels: { confirm: 'Slå fra', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: () => off.mutate(undefined) });
  const allowed = sso?.users.filter((u) => u.allowed).length ?? 0;
  return (
    <Card icon={<IconLogin2 size={20} />} color="violet" title="Login fra Kontrolrum (SSO)"
      badge={sso && <Badge color={sso.enabled ? 'green' : 'gray'} variant="light">{sso.enabled ? 'Slået til' : 'Slået fra'}</Badge>}
      right={sso?.enabled
        ? <><Button size="xs" variant="default" leftSection={<IconUsers size={16} />} onClick={() => setUsersOpen(true)}>Vælg brugere</Button>
          <Button size="xs" variant="default" leftSection={<IconRefresh size={16} />} loading={make.isPending} onClick={rotate}>Lav ny SSO-nøgle</Button>
          <Button size="xs" color="red" loading={off.isPending} onClick={disable}>Slå fra</Button></>
        : <><Button size="xs" variant="default" leftSection={<IconUsers size={16} />} onClick={() => setUsersOpen(true)}>Vælg brugere</Button>
          <Button size="xs" loading={make.isPending} onClick={create}>Lav SSO-nøgle</Button></>}
      sub="Browser-login for personer. Nøglen deles kun med Kontrolrum og vises én gang, når den laves. Her ses kun dens fingeraftryk.">
      <Box px="md" pb="md">
        {sso?.unreadable && <Alert color="red" variant="light" mb="sm">Den gemte nøgle kan ikke læses (serverens nøgler er skiftet). Lav en ny nøgle.</Alert>}
        <SimpleGrid cols={{ base: 2, md: 4 }} spacing="sm">
          <Stat label="Nøgle" monoValue value={sso?.enabled ? `SSO · ${fp(sso.fingerprint)}` : '–'} sub="fingeraftryk, ikke selve nøglen" />
          <Stat label="Oprettet" value={sso?.created ? dateText(sso.created) : '–'} sub={sso?.enabled ? (sso.createdBy ? `af ${sso.createdBy}` : undefined) : 'ingen nøgle'} />
          <Stat label="Sidst brugt" value={sso?.lastUsed ? lastUsedText(sso.lastUsed.at) : 'Aldrig'} sub={sso?.lastUsed ? `af ${sso.lastUsed.user} fra ${sso.lastUsed.ip}` : undefined} />
          <Stat label="Brugere" value={sso ? `${allowed} af ${sso.users.length}` : '–'} sub="må logge ind fra Kontrolrum" />
        </SimpleGrid>
        <Text size="xs" c="dimmed" mt="sm">Kontrolrum logger ind med <Code>POST {sso?.endpoint ?? '…/api/auth/kontrolrum'}</Code>. I Kontrolrum lægges nøglen som SSO-nøgle på Fleetdeck-tjenesten.</Text>
      </Box>
      <Modal opened={usersOpen} onClose={() => setUsersOpen(false)} title="Må logges ind fra Kontrolrum">
        <Stack gap="sm">
          <Alert color="orange" variant="light" title="Sikkerhed">
            <Text size="sm">Med SSO stoler Fleetdeck på Kontrolrum som identitetsudbyder for de brugere, der har fluebenet: den, der har nøglen, kan logge ind som dem uden adgangskode og uden kode. Giv kun fluebenet til de brugere, Kontrolrum skal logge ind som.</Text>
          </Alert>
          {sso?.users.length ? <Table><Table.Tbody>{sso.users.map((u) => (
            <Table.Tr key={u.id}>
              <Table.Td><Checkbox label={u.username} checked={u.allowed} disabled={allow.isPending} onChange={(e) => allow.mutate({ id: u.id, allowed: e.currentTarget.checked })} /></Table.Td>
              <Table.Td c="dimmed"><Text size="xs">{ROLE_LABELS[u.role] ?? u.role}{u.twoFactor ? ' · totrinsbekræftelse' : ''}</Text></Table.Td>
            </Table.Tr>))}</Table.Tbody></Table> : <Empty>Ingen brugere</Empty>}
          <Text size="xs" c="dimmed">Standard er fra. Brugernavnet lægges i Kontrolrum under "Automatisk login".</Text>
          <Group justify="flex-end"><Button onClick={() => setUsersOpen(false)}>Færdig</Button></Group>
        </Stack>
      </Modal>
      <ShownOnceModal onDone={() => setShown(null)} shown={shown === null ? null : {
        title: 'SSO-nøglen til Kontrolrum er lavet', label: 'SSO-nøgle', summary: `Login fra Kontrolrum · fingeraftryk ${fp(sso?.fingerprint ?? null)}`,
        secret: shown, usage: 'Kontrolrum → Fleetdeck-tjenesten → SSO-nøgle',
      }} />
    </Card>
  );
}

function NewKeyModal({ opened, onClose, info, onCreated }: { opened: boolean; onClose: () => void; info: ApiKeysInfo; onCreated: (r: { key: string; item: ApiKeyRow }, expiresDays: number) => void }) {
  const [name, setName] = useState('');
  const [scope, setScope] = useState<Scope>('read');
  const [lanOnly, setLanOnly] = useState(true);
  const [expires, setExpires] = useState('0');
  const make = useAction((b: object) => post<{ key: string; item: ApiKeyRow }>('/api/settings/api-keys', b));
  const reset = () => { setName(''); setScope('read'); setLanOnly(true); setExpires('0'); };
  const submit = () => make.mutate({ name: name.trim(), scope, lanOnly, expiresDays: Number(expires) },
    { onSuccess: (r) => { onCreated(r, Number(expires)); reset(); } });
  return (
    <Modal opened={opened} onClose={() => { reset(); onClose(); }} size="lg"
      title={<Group gap="sm"><ThemeIcon variant="light" color="blue" radius="md"><IconKey size={18} /></ThemeIcon><Text fw={700} size="lg">Ny API-nøgle</Text></Group>}>
      <Stack gap="md">
        <TextInput label="Navn" placeholder="fx Home Assistant" maxLength={64} data-autofocus value={name} onChange={(e) => setName(e.currentTarget.value)}
          description="Navnet på programmet, der skal bruge nøglen. Det står i listen og i loggen." inputWrapperOrder={['label', 'input', 'description']} />
        <Radio.Group label="Rettighed" value={scope} onChange={(v) => setScope(v as Scope)}>
          <Stack gap="xs" mt={6}>
            {SCOPES.map((s) => {
              const own = ownEndpoints(s.id, info.endpoints);
              return (
                <Radio.Card key={s.id} value={s.id} radius="md" p="sm" style={scope === s.id ? { borderColor: 'var(--mantine-color-blue-6)' } : undefined}>
                  <Group wrap="nowrap" align="flex-start">
                    <Radio.Indicator mt={3} />
                    <Box>
                      <Text fw={600}>{s.label}</Text>
                      <Text size="sm" c="dimmed">{s.text}</Text>
                      <Text size="xs" c="dimmed" mt={2}>
                        {own.length ? own.map((e) => <Code key={e.method + e.path} mr={4}>{e.method} {e.path}</Code>)
                          : s.id === 'read' ? 'Ingen kald endnu' : 'Ingen ekstra kald endnu – giver i dag det samme som Kun læse.'}
                      </Text>
                    </Box>
                  </Group>
                </Radio.Card>
              );
            })}
          </Stack>
        </Radio.Group>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          <Switch checked={lanOnly} onChange={(e) => setLanOnly(e.currentTarget.checked)} size="md"
            label="Kun fra hjemmenettet" description="Afvis kald udefra, også gennem tunnellen." />
          <Select label="Udløber" data={EXPIRY} value={expires} onChange={(v) => setExpires(v ?? '0')} allowDeselect={false} />
        </SimpleGrid>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => { reset(); onClose(); }}>Annullér</Button>
          <Button leftSection={<IconPlus size={16} />} disabled={!name.trim()} loading={make.isPending} onClick={submit}>Lav nøglen</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function UsedCell({ k, now }: { k: ApiKeyRow; now: Date }) {
  const tone = usageTone(k, now);
  return (
    <Box>
      <Group gap={6} wrap="nowrap">
        {k.lastUsed && <Box w={9} h={9} style={{ borderRadius: 9, flex: 'none', background: `var(--mantine-color-${tone === 'gray' ? 'gray-6' : tone + '-6'})` }} />}
        <Text size="sm" c={k.lastUsed ? undefined : 'dimmed'}>{lastUsedText(k.lastUsed, now)}</Text>
        {unused90(k, now) && <Badge color="orange" variant="filled" size="xs">Ubrugt i 90 dage</Badge>}
      </Group>
      {k.lastIp && <Text size="xs" c="dimmed" style={mono}>{k.lastIp}</Text>}
    </Box>
  );
}

function NameCell({ k, now }: { k: ApiKeyRow; now: Date }) {
  const exp = expiresText(k.expires, now);
  return (
    <Box>
      <Group gap={6}>
        <Text fw={600}>{k.name}</Text>
        {k.migrated && <Badge color="green" variant="light" size="xs">Flyttet</Badge>}
        {!k.lanOnly && <Badge color="yellow" variant="light" size="xs">Også udefra</Badge>}
      </Group>
      <Text size="xs" c="dimmed" style={mono}>{k.shown}{exp ? ` · ${exp}` : ''}</Text>
    </Box>
  );
}

const ScopeBadge = ({ scope }: { scope: string }) => { const s = scopeInfo(scope); return <Badge color={s.color} variant={s.id === 'admin' ? 'filled' : s.id === 'read' ? 'default' : 'light'}>{s.badge}</Badge>; };

function Actions({ k, now, onDelete, onLan }: { k: ApiKeyRow; now: Date; onDelete: (k: ApiKeyRow) => void; onLan: (k: ApiKeyRow) => void }) {
  return (
    <Group gap={4} wrap="nowrap" justify="flex-end">
      <Tooltip label={k.lanOnly ? 'Kun fra hjemmenettet. Klik for også at tillade kald udefra' : 'Tillader kald udefra. Klik for kun at tillade hjemmenettet'}>
        <ActionIcon variant="subtle" color={k.lanOnly ? 'gray' : 'yellow'} aria-label="Hjemmenet" onClick={() => onLan(k)}>{k.lanOnly ? <IconHome size={18} /> : <IconWorld size={18} />}</ActionIcon>
      </Tooltip>
      <Tooltip label="Slet nøglen">
        <ActionIcon variant={unused90(k, now) ? 'filled' : 'subtle'} color={unused90(k, now) ? 'red' : 'gray'} aria-label={`Slet ${k.name}`} onClick={() => onDelete(k)}><IconTrash size={18} /></ActionIcon>
      </Tooltip>
    </Group>
  );
}

function ApiKeysCard() {
  const { data } = useApi<ApiKeysInfo>(['api-keys'], '/api/settings/api-keys');
  const [open, setOpen] = useState(false);
  const [shown, setShown] = useState<{ title: string; label: string; summary: string; secret: string; usage: string } | null>(null);
  const remove = useAction((id: number) => del(`/api/settings/api-keys/${id}`), 'Nøglen er slettet');
  const lan = useAction((a: { id: number; lanOnly: boolean }) => put(`/api/settings/api-keys/${a.id}`, { lanOnly: a.lanOnly }), 'Nøglen er ændret');
  if (!data) return <Loading />;
  const now = new Date();
  const onDelete = (k: ApiKeyRow) => modals.openConfirmModal({
    title: `Slet nøglen "${k.name}"?`,
    children: <Text size="sm">Programmet, der bruger <Code>{k.shown}</Code>, bliver afvist med det samme. De andre nøgler virker som før.</Text>,
    labels: { confirm: 'Slet nøglen', cancel: 'Annullér' }, confirmProps: { color: 'red' }, onConfirm: () => remove.mutate(k.id),
  });
  const onLan = (k: ApiKeyRow) => modals.openConfirmModal({
    title: k.lanOnly ? `Tillad "${k.name}" udefra?` : `Kun hjemmenettet for "${k.name}"?`,
    children: <Text size="sm">{k.lanOnly ? 'Nøglen kan så også bruges fra internettet og gennem Cloudflare-tunnelen.' : 'Kald udefra og gennem tunnelen bliver afvist. Kald fra hjemmenettet og VPN virker som før.'}</Text>,
    labels: { confirm: k.lanOnly ? 'Tillad udefra' : 'Kun hjemmenettet', cancel: 'Annullér' }, onConfirm: () => lan.mutate({ id: k.id, lanOnly: !k.lanOnly }),
  });
  const rej = data.lastRejected;
  return (
    <Card icon={<IconKey size={20} />} color="blue" title="API-nøgler" badge={<Badge variant="default" radius="sm">{data.keys.length}</Badge>}
      right={<Button leftSection={<IconPlus size={16} />} onClick={() => setOpen(true)}>Ny API-nøgle</Button>}
      sub="Til programmer. Hver nøgle har sit eget navn og sin egen rettighed og kan slettes alene.">
      {data.keys.length === 0 ? <Box px="md" pb="md"><Empty>Ingen API-nøgler endnu</Empty></Box> : <>
        <Box visibleFrom="sm">
          <Table verticalSpacing="sm" horizontalSpacing="md">
            <Table.Thead><Table.Tr><Table.Th>Navn</Table.Th><Table.Th>Rettighed</Table.Th><Table.Th>Oprettet</Table.Th><Table.Th>Sidst brugt</Table.Th><Table.Th /></Table.Tr></Table.Thead>
            <Table.Tbody>{data.keys.map((k) => (
              <Table.Tr key={k.id}>
                <Table.Td><NameCell k={k} now={now} /></Table.Td>
                <Table.Td><ScopeBadge scope={k.scope} /></Table.Td>
                <Table.Td><Text size="sm">{dateText(k.created, now)}</Text><Text size="xs" c="dimmed">{k.migrated ? 'flyttet fra den gamle nøgle' : k.createdBy}</Text></Table.Td>
                <Table.Td><UsedCell k={k} now={now} /></Table.Td>
                <Table.Td><Actions k={k} now={now} onDelete={onDelete} onLan={onLan} /></Table.Td>
              </Table.Tr>))}</Table.Tbody>
          </Table>
        </Box>
        <Stack hiddenFrom="sm" gap={0}>
          {data.keys.map((k) => (
            <Box key={k.id} px="md" py="sm" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
              <Group justify="space-between" align="flex-start" wrap="nowrap"><NameCell k={k} now={now} /><Actions k={k} now={now} onDelete={onDelete} onLan={onLan} /></Group>
              <Group gap="lg" mt={6} align="flex-start">
                <Box><Text size="xs" c="dimmed">Rettighed</Text><ScopeBadge scope={k.scope} /></Box>
                <Box><Text size="xs" c="dimmed">Oprettet</Text><Text size="sm">{dateText(k.created, now)}</Text><Text size="xs" c="dimmed">{k.migrated ? 'flyttet fra den gamle nøgle' : k.createdBy}</Text></Box>
              </Group>
              <Box mt={6}><Text size="xs" c="dimmed">Sidst brugt</Text><UsedCell k={k} now={now} /></Box>
            </Box>))}
        </Stack>
      </>}
      {rej && <Group gap="xs" px="md" py="sm" wrap="nowrap" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <IconAlertTriangle size={16} style={{ flex: 'none' }} />
        <Text size="sm" c="dimmed">Sidst afvist: {lastUsedText(rej.at, now)} fra <Code>{rej.ip}</Code> ({rej.reason}{rej.name ? `: ${rej.name}` : ''})</Text>
      </Group>}
      <Group gap="xs" px="md" py="sm" wrap="nowrap" align="flex-start" style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <IconLock size={16} style={{ flex: 'none', marginTop: 3 }} />
        <Text size="sm" c="dimmed">Nøglerne gemmes kun som SHA-256 og kan ikke vises igen. Send dem som <Code>Authorization: Bearer {data.prefix}…</Code> (eller <Code>X-Api-Key</Code>), aldrig i adressen. Højst {data.limits.readPerMinute} læse-kald og {data.limits.controlPerMinute} styre-kald i minuttet pr. nøgle. Mistet nøgle: slet den og lav en ny.</Text>
      </Group>
      <NewKeyModal opened={open} onClose={() => setOpen(false)} info={data} onCreated={(r, days) => {
        setOpen(false);
        setShown({ title: `Nøglen "${r.item.name}" er lavet`, label: 'API-nøgle', summary: keySummary(r.item.scope, r.item.lanOnly, days), secret: r.key, usage: headerExample(r.key) });
      }} />
      <ShownOnceModal shown={shown} onDone={() => setShown(null)} />
    </Card>
  );
}

/** Hele fanen: forklaring, SSO-kortet og listen med API-nøgler. */
export function ProgramAccessTab() {
  return (
    <Stack gap="md">
      <Box>
        <Title order={3}>Adgang for programmer</Title>
        <Text c="dimmed">Hvem og hvad der må tale med Fleetdeck uden at logge ind med adgangskode</Text>
      </Box>
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        <Paper withBorder radius="md" p="md"><Group wrap="nowrap" align="flex-start" gap="sm">
          <ThemeIcon variant="light" color="violet" radius="md"><IconLogin2 size={18} /></ThemeIcon>
          <Box><Text fw={600}>Til personer: Login fra Kontrolrum</Text><Text size="sm" c="dimmed">Du er logget ind i Kontrolrum og trykker dig herind. Du får dine egne rettigheder, og der står dit navn i loggen.</Text></Box>
        </Group></Paper>
        <Paper withBorder radius="md" p="md"><Group wrap="nowrap" align="flex-start" gap="sm">
          <ThemeIcon variant="light" color="blue" radius="md"><IconKey size={18} /></ThemeIcon>
          <Box><Text fw={600}>Til programmer: API-nøgler</Text><Text size="sm" c="dimmed">Kontrolrum-kort, Home Assistant, scripts. Én nøgle pr. program, så du kan slette én uden at de andre holder op.</Text></Box>
        </Group></Paper>
      </SimpleGrid>
      <SsoCard />
      <ApiKeysCard />
    </Stack>
  );
}
