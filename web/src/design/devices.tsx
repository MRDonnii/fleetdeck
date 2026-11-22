import type { ComponentType } from 'react';
import { IconBrandAndroid, IconDeviceMobile, IconDeviceTablet } from '@tabler/icons-react';
import type { DeviceType, IpadRow } from '../types';

type IconC = ComponentType<{ size?: number; stroke?: number }>;

/** Apple TV: et fjernsyn (Tablers IconDeviceTv) med et lille Apple-logo på skærmen. */
export function IconAppleTv({ size = 24, stroke = 2 }: { size?: number; stroke?: number }) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={stroke}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M3 9a2 2 0 0 1 2 -2h14a2 2 0 0 1 2 2v9a2 2 0 0 1 -2 2h-14a2 2 0 0 1 -2 -2l0 -9" />
      <path d="M16 3l-4 4l-4 -4" />
      <g transform="translate(8.2 9.5) scale(0.33)" fill="currentColor" stroke="none">
        <path d="M8.286 7.008c-3.216 0 -4.286 3.23 -4.286 5.92c0 3.229 2.143 8.072 4.286 8.072c1.165 -.05 1.799 -.538 3.214 -.538c1.406 0 1.607 .538 3.214 .538s4.286 -3.229 4.286 -5.381c-.03 -.011 -2.649 -.434 -2.679 -3.23c-.02 -2.335 2.589 -3.179 2.679 -3.228c-1.096 -1.606 -3.162 -2.113 -3.75 -2.153c-1.535 -.12 -3.032 1.077 -3.75 1.077c-.729 0 -2.036 -1.077 -3.214 -1.077z" />
        <path d="M12 4a2 2 0 0 0 2 -2a2 2 0 0 0 -2 2" stroke="currentColor" strokeWidth={2.4} />
      </g>
    </svg>
  );
}

export interface DeviceMeta {
  type: DeviceType;
  /** Sti til listen (detaljer: `${path}/:id/:tab?`). */
  path: string;
  /** Flertal til menu og faner. */
  plural: string;
  one: string;
  /** Bestemt form: "iPad'en", "Apple TV'et" … */
  the: string;
  icon: IconC;
  /** iOS: DNS-profil via QR, låst profil, USB, MDM og Apps. */
  ios: boolean;
  kinds: [string, string];
}

export const DEVICES: Record<DeviceType, DeviceMeta> = {
  ipad: { type: 'ipad', path: '/ipads', plural: 'iPads', one: 'iPad', the: "iPad'en", icon: IconDeviceTablet, ios: true, kinds: ['Børne-iPad', 'Familie-iPad'] },
  iphone: { type: 'iphone', path: '/iphones', plural: 'iPhones', one: 'iPhone', the: "iPhonen", icon: IconDeviceMobile, ios: true, kinds: ['Børne-iPhone', 'Familie-iPhone'] },
  appletv: { type: 'appletv', path: '/appletv', plural: 'Apple TV', one: 'Apple TV', the: "Apple TV'et", icon: IconAppleTv, ios: false, kinds: ['Til børnene', 'Til hele familien'] },
  android: { type: 'android', path: '/android', plural: 'Android', one: 'Android-enhed', the: 'enheden', icon: IconBrandAndroid, ios: false, kinds: ['Børne-enhed', 'Familie-enhed'] },
};
export const DEVICE_ORDER: DeviceType[] = ['ipad', 'iphone', 'appletv', 'android'];
/** Typer, der altid vises i menuen (de andre vises først, når der er en enhed af typen). */
export const ALWAYS_SHOWN: DeviceType[] = ['ipad', 'appletv'];

export const devMeta = (t: DeviceType | undefined | null): DeviceMeta => DEVICES[t ?? 'ipad'] ?? DEVICES.ipad;
export const kindLabel = (r: Pick<IpadRow, 'kind' | 'deviceType'>) => devMeta(r.deviceType).kinds[r.kind === 'family' ? 1 : 0];

/** Apples model-id'er → navne (Apple Configurator og USB melder "AppleTV5,3"). */
const APPLE_TV_MODELS: Record<string, string> = {
  'AppleTV5,3': 'Apple TV HD', 'AppleTV6,2': 'Apple TV 4K', 'AppleTV11,1': 'Apple TV 4K (2. gen)', 'AppleTV14,1': 'Apple TV 4K (3. gen)',
};
export const modelName = (m: string | null | undefined) => (m ? APPLE_TV_MODELS[m.trim()] ?? m : m ?? null);
export const APPLE_TV_MODEL_CHOICES = ['Apple TV HD', 'Apple TV 4K', 'Apple TV 4K (2. gen)', 'Apple TV 4K (3. gen)'];

/** Linjen under navnet: "Apple TV HD · stue" / "Emma · iPad 10. gen". */
export function deviceSubtitle(r: Pick<IpadRow, 'kind' | 'deviceType' | 'owner' | 'model'>) {
  const model = modelName(r.model);
  const parts = r.deviceType === 'appletv' ? [model, r.owner] : [r.owner, model];
  return parts.filter(Boolean).join(' · ') || kindLabel(r);
}

/** Hurtigknapper på kortet: Apple TV handler om streaming, iPad/iPhone om apps og spil. */
export const QUICK_BY_TYPE: Record<DeviceType, string[][]> = {
  // hver post er mulige AdGuard-id'er/navne for samme tjeneste – den første, AdGuard kender, bruges
  appletv: [['netflix'], ['youtube'], ['disneyplus', 'disney+'], ['hbomax', 'max', 'hbo'], ['twitch'], ['viaplay'], ['tv2play', 'tv2'], ['drtv', 'dr']],
  ipad: [['youtube'], ['tiktok'], ['roblox'], ['snapchat'], ['instagram'], ['fortnite'], ['minecraft'], ['discord']],
  iphone: [['youtube'], ['tiktok'], ['snapchat'], ['instagram'], ['roblox'], ['fortnite'], ['discord'], ['whatsapp']],
  android: [['youtube'], ['tiktok'], ['roblox'], ['snapchat'], ['instagram'], ['fortnite'], ['minecraft'], ['discord']],
};
export const QUICK_GROUPS_BY_TYPE: Record<DeviceType, [string, string][]> = {
  appletv: [['streaming', 'Video'], ['gaming', 'Spil']],
  ipad: [['gaming', 'Spil'], ['social_network', 'Sociale medier'], ['streaming', 'Video']],
  iphone: [['gaming', 'Spil'], ['social_network', 'Sociale medier'], ['streaming', 'Video']],
  android: [['gaming', 'Spil'], ['social_network', 'Sociale medier'], ['streaming', 'Video']],
};

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9+]/g, '');
/** Finder AdGuard-tjenesterne for en hurtigliste (match på id eller navn). */
export function pickServices<T extends { id: string; name: string }>(services: T[], wanted: string[][]): T[] {
  const out: T[] = [];
  for (const alts of wanted) {
    const hit = alts.map((a) => services.find((s) => s.id === a) ?? services.find((s) => norm(s.name) === norm(a))).find(Boolean);
    if (hit && !out.includes(hit)) out.push(hit);
  }
  return out;
}
