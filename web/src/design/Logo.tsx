import { useId } from 'react';

/** Fleetdecks mærke: et dæk (tre linjer) og en prik – flåden. */
export function LogoMark({ size = 30 }: { size?: number }) {
  const id = `fd-g${useId().replace(/:/g, '')}`;
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="var(--fd-logo-a)" /><stop offset="1" stopColor="var(--fd-logo-b)" />
        </linearGradient>
      </defs>
      <rect width="32" height="32" rx="9" fill={`url(#${id})`} />
      <g stroke="#fff" strokeWidth="2.6" strokeLinecap="round"><path d="M8 10h16M8 16h11M8 22h6" /></g>
      <circle cx="23.5" cy="21.5" r="2.6" fill="#fff" />
    </svg>
  );
}

export function Logo({ size = 30 }: { size?: number }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
      <LogoMark size={size} />
      <span style={{ fontWeight: 800, fontSize: size * 0.58, letterSpacing: '-0.02em' }}>fleetdeck</span>
    </span>
  );
}
