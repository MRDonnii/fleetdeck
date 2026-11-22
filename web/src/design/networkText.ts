// Netværk-afsnittet på maskinens side: tekster, farver og rækkefølge (rene funktioner, testet i tests/network.test.ts).

export type NetKind = 'ethernet' | 'wifi' | 'mobile' | 'other';
export type NetState = 'connected' | 'limited' | 'unplugged' | 'disconnected' | 'disabled' | 'unknown';

export interface WifiLink {
  ssid?: string | null; bssid?: string | null; signalPct?: number | null; dbm?: number | null; band?: string | null; channel?: number | null;
  frequencyMhz?: number | null; rxMbps?: number | null; txMbps?: number | null; radio?: string | null; security?: string | null;
}
export interface NetAdapter {
  name: string; description?: string | null; kind: NetKind; virtual: boolean; status: NetState; primary: boolean;
  speedMbps?: number | null; maxSpeedMbps?: number | null; fullDuplex?: boolean | null; mac?: string | null; mtu?: number | null; vlanId?: number | null;
  ipv4: string[]; ipv6: string[]; ipv6Enabled?: boolean | null; gateways: string[]; dns: string[]; dnsSuffix?: string | null; dhcp?: boolean | null;
  wifi?: WifiLink | null;
  rxBytes?: number | null; txBytes?: number | null; rxRate?: number | null; txRate?: number | null;
  rxErrors?: number | null; txErrors?: number | null; rxDrops?: number | null; txDrops?: number | null;
  newErrors?: number | null; newPackets?: number | null; sampleSeconds?: number | null; linkChanges24h?: number | null; linkChangedAt?: string | null;
}
export interface NetworkInfo { adapters: NetAdapter[]; virtualCount: number; portable?: boolean | null; wifiNote?: string | null; source?: string | null; error?: string | null }
/** Maskinlistens lille status (serverens NetParse.Brief). */
export interface NetBrief { kind: NetKind; speedMbps?: number | null; signalPct?: number | null; warn: boolean }

export type Tone = 'green' | 'yellow' | 'red' | 'gray' | 'blue';
export interface Shown { text: string; tone: Tone }

export const NET_KIND: Record<NetKind, string> = { ethernet: 'Kablet', wifi: 'Wi-Fi', mobile: 'Mobil', other: 'Andet' };

const STATE: Record<NetState, Shown> = {
  connected: { text: 'Tilsluttet', tone: 'green' },
  limited: { text: 'Ingen IP-adresse', tone: 'yellow' },
  unplugged: { text: 'Kabel ude', tone: 'gray' },
  disconnected: { text: 'Ingen forbindelse', tone: 'gray' },
  disabled: { text: 'Slået fra', tone: 'gray' },
  unknown: { text: 'Ukendt', tone: 'gray' },
};

/** Status-badgen. Den primære adapter uden forbindelse er rød (maskinen er så reelt offline på den). */
export function netStatus(a: NetAdapter): Shown {
  const s = STATE[a.status] ?? STATE.unknown;
  return a.primary && s.tone === 'gray' && a.status !== 'unknown' ? { ...s, tone: 'red' } : s;
}

const da = (n: number, d = 1) => n.toLocaleString('da-DK', { maximumFractionDigits: d });

/** 100 → "100 Mbit/s", 1000 → "1 Gbit/s", 2500 → "2,5 Gbit/s". */
export function speedText(mbps?: number | null): string | null {
  if (mbps == null || mbps <= 0) return null;
  return mbps >= 1000 ? `${da(mbps / 1000)} Gbit/s` : `${da(mbps)} Mbit/s`;
}

/** Kablet adapter på 10/100 Mbit/s eller half duplex: hastigheden vises i advarselsfarve. */
export function speedWarn(a: NetAdapter): boolean {
  if (a.kind !== 'ethernet' || a.status !== 'connected') return false;
  return (a.speedMbps != null && a.speedMbps > 0 && a.speedMbps <= 100) || a.fullDuplex === false;
}

/** Hastigheden med duplex, fx "100 Mbit/s (half duplex)". Wi-Fi: link-hastigheden. */
export function speedLine(a: NetAdapter): string | null {
  const s = speedText(a.speedMbps ?? (a.wifi?.rxMbps != null ? Math.round(a.wifi.rxMbps) : null));
  if (!s) return null;
  return a.fullDuplex === false ? `${s} (half duplex)` : s;
}

/** Wi-Fi-signal: grøn ≥ 60 %, gul ≥ 40 %, ellers rød. */
export function signalTone(pct?: number | null): Tone {
  if (pct == null) return 'gray';
  return pct >= 60 ? 'green' : pct >= 40 ? 'yellow' : 'red';
}

/** Antal streger (0–4) i den lille signalbjælke. */
export function signalBars(pct?: number | null): number {
  if (pct == null) return 0;
  return pct >= 75 ? 4 : pct >= 50 ? 3 : pct >= 30 ? 2 : pct > 0 ? 1 : 0;
}

/** Bytes → "512 B", "1,5 MB", "2,3 GB", "1,2 TB". */
export function bytesText(n?: number | null): string | null {
  if (n == null || n < 0) return null;
  const u = ['B', 'kB', 'MB', 'GB', 'TB', 'PB'];
  let i = 0; let v = n;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return `${da(v, i === 0 ? 0 : 1)} ${u[i]}`;
}

/** Bytes/s → bit/s som netkort måles i: "0 bit/s", "850 kbit/s", "12,4 Mbit/s". */
export function rateText(bytesPerSec?: number | null): string | null {
  if (bytesPerSec == null || bytesPerSec < 0) return null;
  const b = bytesPerSec * 8;
  if (b < 1000) return `${da(b, 0)} bit/s`;
  if (b < 1e6) return `${da(b / 1e3)} kbit/s`;
  if (b < 1e9) return `${da(b / 1e6)} Mbit/s`;
  return `${da(b / 1e9, 2)} Gbit/s`;
}

/** Maskinlisten: "Kablet 1 Gbit/s", "Wi-Fi 72 %", "Mobil". */
export function briefText(b?: NetBrief | null): string | null {
  if (!b) return null;
  if (b.kind === 'wifi') return b.signalPct != null ? `Wi-Fi ${b.signalPct} %` : 'Wi-Fi';
  const s = speedText(b.speedMbps);
  return s ? `${NET_KIND[b.kind]} ${s}` : NET_KIND[b.kind];
}

/** Afsnittets overskrift til højre, fx "Kablet 1 Gbit/s · 192.168.1.20". */
export function summaryText(net?: NetworkInfo | null): string | null {
  const p = net?.adapters.find((a) => a.primary);
  if (!p) return null;
  const kind = p.kind === 'wifi' ? (p.wifi?.signalPct != null ? `Wi-Fi ${p.wifi.signalPct} %` : 'Wi-Fi') : [NET_KIND[p.kind], speedText(p.speedMbps)].filter(Boolean).join(' ');
  return [kind, p.ipv4[0]].filter(Boolean).join(' · ');
}

/** De adaptere, der vises: virtuelle kun med "Vis alle" (eller hvis de er den primære). */
export function visibleAdapters(net: NetworkInfo, showAll: boolean): NetAdapter[] {
  return net.adapters.filter((a) => showAll || !a.virtual || a.primary);
}

/** "3 virtuelle skjult" – teksten til "Vis alle"-knappen. Null = intet skjult. */
export function hiddenText(net: NetworkInfo, showAll: boolean): string | null {
  if (!net.virtualCount) return null;
  if (showAll) return 'Skjul virtuelle';
  return `Vis alle (${net.virtualCount} virtuel${net.virtualCount === 1 ? '' : 'le'} skjult)`;
}

const yesNo = (b?: boolean | null) => (b == null ? null : b ? 'Ja' : 'Nej');
const pair = (rx?: string | null, tx?: string | null) => (rx == null && tx == null ? null : `↓ ${rx ?? '–'}   ↑ ${tx ?? '–'}`);
const sum = (a?: number | null, b?: number | null) => (a == null && b == null ? null : (a ?? 0) + (b ?? 0));

/** Tid siden, fx "for 12 min. siden" (now gives med i testene). */
export function sinceText(iso?: string | null, now = Date.now()): string | null {
  if (!iso) return null;
  const min = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60000));
  if (min < 1) return 'lige nu';
  if (min < 60) return `for ${min} min. siden`;
  const h = Math.round(min / 60);
  return h < 48 ? `for ${h} t. siden` : `for ${Math.round(h / 24)} dage siden`;
}

/** Fejltællerne: "0" eller "rx 12 · tx 0 (+12 nu)". */
export function errorsText(a: NetAdapter): string | null {
  const total = sum(a.rxErrors, a.txErrors);
  if (total == null) return null;
  const now = a.newErrors ? ` (+${a.newErrors.toLocaleString('da-DK')} siden sidst)` : '';
  return total === 0 ? '0' : `${(a.rxErrors ?? 0).toLocaleString('da-DK')} ind · ${(a.txErrors ?? 0).toLocaleString('da-DK')} ud${now}`;
}

/** Detaljerne, der folder ud under en adapter. Rækker uden værdi udelades. */
export function detailRows(a: NetAdapter, now = Date.now()): [string, string][] {
  const w = a.wifi;
  const rows: [string, string | null][] = [
    ['Hardware', a.description ?? null],
    ['Type', `${NET_KIND[a.kind]}${a.virtual ? ' (virtuel)' : ''}`],
    ['MAC-adresse', a.mac ?? null],
    ['IPv4', a.ipv4.join(', ') || null],
    ['IPv6', a.ipv6Enabled === false ? 'Slået fra' : a.ipv6.length ? a.ipv6.join(', ') : yesNo(a.ipv6Enabled)],
    ['Gateway', a.gateways.join(', ') || null],
    ['DNS', a.dns.join(', ') || null],
    ['DNS-suffiks', a.dnsSuffix ?? null],
    ['DHCP', yesNo(a.dhcp)],
    ['Hastighed', speedLine(a)],
    ['Kortet kan', speedText(a.maxSpeedMbps)],
    ['Duplex', a.fullDuplex == null ? null : a.fullDuplex ? 'Fuld' : 'Halv (half duplex)'],
    ['MTU', a.mtu != null ? String(a.mtu) : null],
    ['VLAN', a.vlanId != null ? String(a.vlanId) : null],
  ];
  if (w) rows.push(
    ['Netværk (SSID)', w.ssid ?? null],
    ['Access point (BSSID)', w.bssid ?? null],
    ['Signal', w.signalPct != null ? `${w.signalPct} %${w.dbm != null ? ` (${w.dbm} dBm)` : ''}` : w.dbm != null ? `${w.dbm} dBm` : null],
    ['Bånd og kanal', [w.band, w.channel != null ? `kanal ${w.channel}` : null, w.frequencyMhz ? `${w.frequencyMhz} MHz` : null].filter(Boolean).join(' · ') || null],
    ['Wi-Fi-standard', w.radio ?? null],
    ['Sikkerhed', w.security ?? null],
    ['Link-hastighed', w.rxMbps != null || w.txMbps != null ? pair(w.rxMbps != null ? `${da(w.rxMbps)} Mbit/s` : null, w.txMbps != null ? `${da(w.txMbps)} Mbit/s` : null) : null],
  );
  rows.push(
    ['Trafik nu', pair(rateText(a.rxRate), rateText(a.txRate))],
    ['Trafik i alt', pair(bytesText(a.rxBytes), bytesText(a.txBytes))],
    ['Fejl', errorsText(a)],
    ['Tabte pakker', sum(a.rxDrops, a.txDrops) == null ? null : sum(a.rxDrops, a.txDrops) === 0 ? '0' : `${(a.rxDrops ?? 0).toLocaleString('da-DK')} ind · ${(a.txDrops ?? 0).toLocaleString('da-DK')} ud`],
    ['Link op/ned (24 t)', a.linkChanges24h != null ? `${a.linkChanges24h} gang${a.linkChanges24h === 1 ? '' : 'e'}${a.linkChangedAt ? `, senest ${sinceText(a.linkChangedAt, now)}` : ''}` : null],
  );
  return rows.filter((r): r is [string, string] => r[1] != null && r[1] !== '');
}
