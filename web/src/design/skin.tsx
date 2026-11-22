import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { createTheme, type MantineColorsTuple, type MantineThemeOverride } from '@mantine/core';

/** Fleetdecks to stilarter. Command: rolig og skarp. Aurora: farveovergange, glas og større runding. */
export type Skin = 'command' | 'aurora';

const indigo: MantineColorsTuple = ['#eef0ff', '#dde0ff', '#b9bdfb', '#9399f6', '#7378f1', '#5f63ee', '#5459ee', '#4549d4', '#3c40be', '#3036a8'];
const fleet: MantineColorsTuple = ['#e8f1ff', '#d2e0ff', '#a3bffa', '#719cf6', '#4a7ff2', '#3270f0', '#2563eb', '#1854d1', '#0c4abc', '#003fa6'];

export function buildTheme(skin: Skin): MantineThemeOverride {
  const aurora = skin === 'aurora';
  return createTheme({
    primaryColor: aurora ? 'indigo' : 'fleet',
    colors: { fleet, indigo },
    primaryShade: { light: 6, dark: 5 },
    defaultRadius: aurora ? 'lg' : 'md',
    fontFamily: '"Inter Variable", Inter, "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif',
    fontFamilyMonospace: '"JetBrains Mono", "Cascadia Code", Consolas, monospace',
    headings: { fontFamily: '"Inter Variable", Inter, system-ui, sans-serif', fontWeight: '700' },
    radius: aurora ? { xs: '6px', sm: '8px', md: '12px', lg: '16px', xl: '22px' } : { xs: '4px', sm: '6px', md: '8px', lg: '12px', xl: '16px' },
    defaultGradient: aurora ? { from: 'cyan.5', to: 'indigo.5', deg: 135 } : { from: 'fleet.5', to: 'fleet.7', deg: 135 },
    components: {
      Paper: { defaultProps: { withBorder: true } },
      Card: { defaultProps: { withBorder: true } },
      Button: { defaultProps: { radius: aurora ? 'md' : 'sm' } },
      Badge: { defaultProps: { radius: 'sm' } },
    },
  });
}

export type NavPos = 'side' | 'top';
const Ctx = createContext<{ skin: Skin; setSkin: (s: Skin) => void; nav: NavPos; setNav: (n: NavPos) => void }>(
  { skin: 'command', setSkin: () => {}, nav: 'side', setNav: () => {} });
export const useSkin = () => useContext(Ctx);

/** Husker stilarten i browseren og sætter data-skin på <html>, så CSS'en kan skifte flader og farver. */
export function SkinProvider({ children }: { children: (skin: Skin) => ReactNode }) {
  const [skin, setSkinState] = useState<Skin>(() => (localStorage.getItem('fd-skin') as Skin) || 'command');
  useEffect(() => { document.documentElement.dataset.skin = skin; }, [skin]);
  const setSkin = (s: Skin) => { localStorage.setItem('fd-skin', s); setSkinState(s); };
  const [nav, setNavState] = useState<NavPos>(() => (localStorage.getItem('fd-nav') as NavPos) || 'side');
  const setNav = (n: NavPos) => { localStorage.setItem('fd-nav', n); setNavState(n); };
  return <Ctx.Provider value={{ skin, setSkin, nav, setNav }}>{children(skin)}</Ctx.Provider>;
}
