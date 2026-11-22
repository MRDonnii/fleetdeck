import { ThemeIcon } from '@mantine/core';
import { IconBrandApple, IconBrandDebian, IconBrandRedhat, IconBrandSteam, IconBrandUbuntu, IconBrandWindows, IconCpu, IconServer2 } from '@tabler/icons-react';

/** Hvilken slags maskine: ud fra agentens platform og styresystemets navn/model. */
export type Kind = 'windows' | 'mac' | 'linux' | 'pi' | 'unraid';

export function kindOf(m: { platform: string; os?: string | null; model?: string | null }): Kind {
  if (m.platform === 'mac') return 'mac';
  if (m.platform === 'windows') return 'windows';
  const os = `${m.os ?? ''} ${m.model ?? ''}`.toLowerCase();
  if (os.includes('unraid')) return 'unraid';
  if (os.includes('raspberry') || os.includes('raspbian')) return 'pi';
  return 'linux';
}

export const KIND: Record<Kind, { label: string; color: string; Icon: typeof IconBrandWindows }> = {
  windows: { label: 'Windows', color: '#0ea5e9', Icon: IconBrandWindows },
  mac: { label: 'Mac', color: '#8b8b94', Icon: IconBrandApple },
  linux: { label: 'Linux', color: '#22c55e', Icon: IconBrandUbuntu },
  pi: { label: 'Raspberry Pi', color: '#e11d48', Icon: IconCpu },
  unraid: { label: 'Server', color: '#f97316', Icon: IconServer2 },
};

/**
 * Linux-distroer: logo hvor der findes et, ellers et mærke med distroens farve og forkortelse.
 * Nøglen er ID fra os-release.
 */
export const DISTRO: Record<string, { label: string; color: string; Icon?: typeof IconBrandWindows; short?: string }> = {
  ubuntu: { label: 'Ubuntu', color: '#e95420', Icon: IconBrandUbuntu },
  kubuntu: { label: 'Kubuntu', color: '#0079c1', Icon: IconBrandUbuntu },
  xubuntu: { label: 'Xubuntu', color: '#2c8bcf', Icon: IconBrandUbuntu },
  debian: { label: 'Debian', color: '#d70a53', Icon: IconBrandDebian },
  raspbian: { label: 'Raspberry Pi OS', color: '#c51a4a', Icon: IconCpu },
  linuxmint: { label: 'Linux Mint', color: '#87cf3e', short: 'Mi' },
  pop: { label: 'Pop!_OS', color: '#48b9c7', short: 'Po' },
  elementary: { label: 'elementary OS', color: '#64baff', short: 'el' },
  zorin: { label: 'Zorin OS', color: '#15a6f0', short: 'Zo' },
  mx: { label: 'MX Linux', color: '#3f5a7a', short: 'MX' },
  devuan: { label: 'Devuan', color: '#4c4c4c', short: 'Dv' },
  kali: { label: 'Kali Linux', color: '#557c94', short: 'Ka' },
  fedora: { label: 'Fedora', color: '#51a2da', short: 'Fe' },
  rhel: { label: 'Red Hat', color: '#ee0000', Icon: IconBrandRedhat },
  centos: { label: 'CentOS', color: '#a14f8c', short: 'Ce' },
  rocky: { label: 'Rocky Linux', color: '#10b981', short: 'Ro' },
  almalinux: { label: 'AlmaLinux', color: '#0f4266', short: 'Al' },
  ol: { label: 'Oracle Linux', color: '#c74634', short: 'Or' },
  amzn: { label: 'Amazon Linux', color: '#ff9900', short: 'Am' },
  bazzite: { label: 'Bazzite', color: '#8a2be2', short: 'Bz' },
  opensuse: { label: 'openSUSE', color: '#73ba25', short: 'Su' },
  'opensuse-leap': { label: 'openSUSE Leap', color: '#73ba25', short: 'Su' },
  'opensuse-tumbleweed': { label: 'openSUSE Tumbleweed', color: '#73ba25', short: 'Su' },
  'opensuse-microos': { label: 'openSUSE MicroOS', color: '#73ba25', short: 'Su' },
  sles: { label: 'SUSE', color: '#30ba78', short: 'SU' },
  arch: { label: 'Arch Linux', color: '#1793d1', short: 'Ar' },
  manjaro: { label: 'Manjaro', color: '#35bf5c', short: 'Ma' },
  endeavouros: { label: 'EndeavourOS', color: '#7f3fbf', short: 'En' },
  garuda: { label: 'Garuda', color: '#c96df0', short: 'Ga' },
  cachyos: { label: 'CachyOS', color: '#00ccff', short: 'Ca' },
  artix: { label: 'Artix', color: '#10a0cc', short: 'Ax' },
  steamos: { label: 'SteamOS', color: '#1a9fff', Icon: IconBrandSteam },
  alpine: { label: 'Alpine', color: '#0d597f', short: 'Ap' },
  gentoo: { label: 'Gentoo', color: '#54487a', short: 'Ge' },
  void: { label: 'Void', color: '#478061', short: 'Vo' },
  solus: { label: 'Solus', color: '#5294e2', short: 'So' },
  nixos: { label: 'NixOS', color: '#5277c3', short: 'Nx' },
  slackware: { label: 'Slackware', color: '#000000', short: 'Sl' },
};

export const distroOf = (id?: string | null) => (id ? DISTRO[id.toLowerCase()] : undefined);

export function KindIcon({ kind, size = 34, distro }: { kind: Kind; size?: number; distro?: string | null }) {
  const d = kind === 'linux' ? distroOf(distro) : undefined;
  if (d) {
    return (
      <ThemeIcon size={size} radius="md" variant="light" title={d.label}
        style={{ background: `color-mix(in srgb, ${d.color} 16%, transparent)`, color: d.color }}>
        {d.Icon ? <d.Icon size={size * 0.52} stroke={1.8} /> : <b style={{ fontSize: size * 0.36, letterSpacing: '-0.03em' }}>{d.short}</b>}
      </ThemeIcon>
    );
  }
  const k = KIND[kind];
  return (
    <ThemeIcon size={size} radius="md" variant="light" style={{ background: `color-mix(in srgb, ${k.color} 14%, transparent)`, color: k.color }}>
      <k.Icon size={size * 0.52} stroke={1.8} />
    </ThemeIcon>
  );
}
