// Styresystem ud for hver maskine: et mærke (Windows / macOS / Linux) og en kort, præcis tekst
// ("Windows 11 Pro 26H2", "macOS 13.7.8 Ventura", "Ubuntu 26.04.1 LTS", "Raspberry Pi OS 12 (bookworm)").
// Ren logik, så den kan testes med node (web/tests/osLabel.test.ts).

export type OsInput = { platform: string; os?: string | null; osVersion?: string | null };

export const OS_BADGE: Record<string, { label: string; color: string }> = {
  windows: { label: 'Windows', color: 'blue' },
  mac: { label: 'macOS', color: 'gray' },
  linux: { label: 'Linux', color: 'orange' },
};

export function osBadge(m: OsInput): { label: string; color: string } | null {
  return OS_BADGE[m.platform] ?? null;
}

// macOS' navne efter hovedversion (10.x efter de to første tal)
const MAC_NAMES: Record<string, string> = {
  '10.13': 'High Sierra', '10.14': 'Mojave', '10.15': 'Catalina',
  '11': 'Big Sur', '12': 'Monterey', '13': 'Ventura', '14': 'Sonoma', '15': 'Sequoia', '26': 'Tahoe',
};

export function macName(version: string | null | undefined): string | null {
  const v = (version ?? '').trim();
  const m = v.match(/^(\d+)(?:\.(\d+))?/);
  if (!m) return null;
  const key = m[1] === '10' && m[2] ? `10.${m[2]}` : m[1];
  return MAC_NAMES[key] ?? null;
}

/** Den præcise tekst, eller null når agenten ikke har meldt styresystemet. */
export function osDetail(m: OsInput): string | null {
  const os = (m.os ?? '').replace(/\s+/g, ' ').trim();
  const ver = (m.osVersion ?? '').trim();
  if (m.platform === 'windows') {
    if (!os) return null;
    const name = os.replace(/^Microsoft\s+/i, '');
    // "26H2"/"24H2" eller ældre "2009"-agtige udgivelser; byggenumre (10.0.x) vises ikke her
    return /^(\d{2}H\d|\d{4})$/i.test(ver) && !name.includes(ver) ? `${name} ${ver.toUpperCase()}` : name;
  }
  if (m.platform === 'mac') {
    const base = os || (ver ? `macOS ${ver}` : '');
    if (!base) return null;
    const name = macName(ver || base.replace(/^macOS\s+/i, ''));
    return name && !base.includes(name) ? `${base} ${name}` : base;
  }
  if (!os) return null;
  return os
    .replace(/^Raspbian GNU\/Linux/i, 'Raspberry Pi OS')
    .replace(/\s+GNU\/Linux/i, '')
    .trim();
}
