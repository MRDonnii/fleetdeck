// Skitser til "Printere: find dem selv og vælg, hvilke pc'er de skal på".
// Command = lister med "Fundne printere" øverst og et vindue med flueben. Aurora = kort i et gitter og pc'er som kort i vinduet.
// Alle navne og adresser er eksempler (RFC 5737).
const L = require('../terminal-mockups/lib');
const { ic, badge, btn, dot } = L;
const FOUND = [
  { n: 'Canon TS8350 series', ip: '192.0.2.44', src: 'Bonjour', by: 'set af ARBEJDS-PC og STUE-PC', dl: 1, i: 'printer' },
  { n: 'HP LaserJet M110w', ip: '192.0.2.45', src: 'Netværksscanning', by: 'serverens net 192.0.2.0/24', dl: 1, i: 'printer' },
  { n: 'Brother HL-L2350DW', ip: '192.0.2.46', src: 'Installeret på pc', by: 'KONTOR-LAPTOP', dl: 0, i: 'printer' },
  { n: 'Epson ET-2850', ip: '192.0.2.47', src: 'WSD', by: 'set af STUE-PC', dl: null, i: 'printer' },
];
const MINE = [{ n: 'Kontorprinter', m: 'Canon MF642C', ip: '192.0.2.40', st: 'Klar', on: 'Hjemmekontor · 3 maskiner' }];
const PCS = [
  { n: 'ARBEJDS-PC', os: 'brand-windows', p: 'Hjemmekontor', on: 1, c: 1 },
  { n: 'KONTOR-LAPTOP', os: 'brand-windows', p: 'Hjemmekontor', on: 1, c: 1 },
  { n: 'STUE-PC', os: 'brand-windows', p: '', on: 1, c: 0 },
  { n: 'macbook-air', os: 'brand-apple', p: '', on: 0, c: 1 },
  { n: 'mediecenter', os: 'brand-ubuntu', p: '', on: 1, c: 0 },
];
const side = () => L.sidebar().replace('class="nav on"', 'class="nav "').replace('Profiler</a>', 'Profiler</a><a class="nav on">' + ic('printer', 17) + 'Printere</a>');
const drv = (d) => d == null ? badge('Ukendt', 'var(--dim)') : d ? badge('IPP Everywhere', 'var(--pri)') : badge('Kræver driver', 'var(--warn)');
const src = (f) => badge(f.src, f.src === 'Bonjour' ? 'var(--ok)' : f.src === 'WSD' ? '#a855f7' : 'var(--dim)', f.src === 'Bonjour' ? 'broadcast' : f.src === 'WSD' ? 'brand-windows' : f.src.startsWith('Inst') ? 'device-desktop' : 'radar');
const status = (m) => `<div class="row" style="gap:8px;padding:9px 12px;border-radius:var(--r);background:var(--chip);font-size:12.5px;${m ? 'flex-wrap:wrap' : ''}">
  ${ic('radar', 16)}<span class="grow"><b>Fleetdeck søger selv</b> hver 6. time · sidst i dag 08.12 · Bonjour, serverens net 192.0.2.0/24 og 3 pc'er</span>${m ? '' : btn('Søg nu', { i: 'refresh', s: 'height:28px;font-size:12px' }) + btn('', { k: 'g i', i: 'settings', s: 'height:28px' })}</div>`;
const chk = (on) => `<span style="width:17px;height:17px;border-radius:4px;flex:none;display:grid;place-items:center;${on ? 'background:var(--pri);color:#fff' : 'border:1.5px solid var(--bd);background:var(--card)'}">${on ? ic('check', 12, 3) : ''}</span>`;
// ---------------------------------------------------------------- Command: lister + vindue med flueben
const foundRows = (m) => `<div class="card" style="overflow:hidden">${FOUND.map((f, i) => `<div class="row" style="padding:${m ? '10px 12px' : '10px 14px'};gap:12px;${i ? 'border-top:1px solid var(--bd)' : ''};${i === 0 ? 'background:var(--priL)' : ''}">
  ${ic(f.i, 18)}<span class="col grow"><b class="tr">${f.n}</b><span class="dim tr" style="font-size:11.5px"><span class="mono">${f.ip}</span> · ${m ? f.src : f.by}</span></span>
  ${m ? '' : `<span style="width:150px">${src(f)}</span><span style="width:110px">${drv(f.dl)}</span>`}${btn(m ? 'Opsæt' : 'Opsæt på…', { k: i === 0 ? 'p' : '', i: 'plus', z: 13, s: 'height:28px;padding:0 10px;font-size:12px' })}</div>`).join('')}</div>`;
const mineRows = () => `<div class="card" style="overflow:hidden">${MINE.map((p) => `<div class="row" style="padding:10px 14px;gap:12px">${ic('printer', 18)}<span class="col grow"><b>${p.n} ${dot(1)}</b><span class="dim" style="font-size:11.5px">${p.m} · <span class="mono">${p.ip}</span></span></span>${drv(1)}<span class="dim" style="width:180px;font-size:12px">${p.on}</span>${btn('', { k: 'g i', i: 'dots' })}</div>`).join('')}</div>`;
const setup = (m) => `<div class="row" style="margin-bottom:6px">${ic('printer', 20)}<h2 class="grow">Opsæt Canon TS8350 series</h2>${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>
  <div class="dim" style="font-size:12.5px;margin-bottom:12px"><span class="mono">ipp://192.0.2.44/ipp/print</span> · ${drv(1)}</div>
  <div class="col" style="gap:4px;margin-bottom:10px"><b style="font-size:12.5px">Navn på pc'erne</b><span class="inp"><em>Canon TS8350 series</em></span></div>
  <b style="font-size:12.5px">Profiler</b>
  <div class="row" style="gap:10px;padding:8px 0;font-size:13px">${chk(1)}<span class="grow">Hjemmekontor</span><span class="dim" style="font-size:12px">2 maskiner</span></div>
  <b style="font-size:12.5px">Pc'er</b>
  <div class="col" style="margin-top:4px">${PCS.map((p) => `<div class="row" style="gap:10px;padding:7px 0;font-size:13px;border-top:1px solid var(--bd)">${chk(p.c)}${ic(p.os, 16)}<span class="grow tr">${p.n}${p.p ? ` <span class="dim" style="font-size:11.5px">· ${p.p}</span>` : ''}</span>${p.on ? '' : '<span class="dim" style="font-size:11.5px">offline – installeres senere</span>'}</div>`).join('')}</div>
  <div class="row" style="gap:10px;margin-top:10px;font-size:13px">${chk(1)}<span>Gør til standardprinter på pc'er, der ikke har en</span></div>
  <div class="dim" style="font-size:12px;margin-top:8px">Agenterne installerer printeren uden driver og lægger den på igen, hvis nogen fjerner den.</div>
  <div class="row" style="justify-content:flex-end;margin-top:14px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér', { s: m ? 'height:42px' : '' })}${btn('Opsæt på 3 pc\'er', { k: 'p', i: 'check', s: m ? 'height:42px' : '' })}</div>`;
const head = (m) => `<div class="row" style="margin-bottom:${m ? 8 : 14}px;gap:10px"><div class="grow"><h1>Printere</h1>${m ? '' : '<div class="dim" style="margin-top:3px">Fleetdeck finder printerne på netværket. Vælg, hvilke pc\'er de skal på – agenterne installerer dem og holder dem på.</div>'}</div>
  ${btn(m ? '' : 'Tilføj med adresse', { i: 'plus', k: m ? 'i' : '' })}</div>`;
const sec = (t, n) => `<div class="row" style="margin:16px 0 8px"><b style="font-size:13.5px">${t}</b>${n ? badge(n, 'var(--pri)') : ''}</div>`;
const cmdDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0)}${status(0)}
  ${sec('Fundne printere', '4 nye')}${foundRows(0)}${sec('I Fleetdeck')}${mineRows()}</div></div>
  <div class="ov"><div class="modal" style="max-width:520px">${setup(0)}</div></div></div>`;
const tabs = () => L.tabbar().replace(/<a class="on">/g, '<a class="">').replace(/<a class="">(<svg[^]*?<\/svg>)Mere<\/a>/, '<a class="on">$1Mere</a>');
const cmdIphone = () => `<div class="app"><div class="main">${L.mhead('Printere', btn('', { k: 'g i', i: 'refresh' }))}<div class="col grow" style="overflow:hidden;padding:12px">${status(1)}${sec('Fundne printere', '4 nye')}${foundRows(1)}${sec('I Fleetdeck')}</div>${tabs()}</div>
  <div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;padding:18px;max-height:92%;overflow:hidden">${setup(1)}</div></div></div>`;
// ---------------------------------------------------------------- Aurora: kort i et gitter + pc'er som kort i vinduet
const acard = (f, i) => `<div class="card col" style="padding:16px;gap:10px;${i === 0 ? 'border-color:color-mix(in srgb,var(--pri) 60%,transparent)' : ''}">
  <div class="row" style="gap:10px"><span style="width:40px;height:40px;border-radius:12px;display:grid;place-items:center;background:var(--priL);color:var(--pri)">${ic('printer', 21)}</span><span class="col grow"><b class="tr" style="font-size:14.5px">${f.n}</b><span class="mono dim" style="font-size:12px">${f.ip}</span></span></div>
  <div class="row" style="gap:6px;flex-wrap:wrap">${src(f)}${drv(f.dl)}</div>
  <div class="dim tr" style="font-size:12px">${f.by}</div>
  <div class="row">${btn('Opsæt på…', { k: i === 0 ? 'p' : '', i: 'plus', s: 'height:30px;font-size:12.5px;width:100%' })}</div></div>`;
const pcCard = (p) => `<div class="card row" style="padding:10px 12px;gap:10px;${p.c ? 'border-color:var(--pri);background:var(--priL)' : ''}">${chk(p.c)}${ic(p.os, 18)}<span class="col grow"><b class="tr">${p.n}</b><span class="dim tr" style="font-size:11.5px">${p.p || 'ingen profil'}${p.on ? '' : ' · offline'}</span></span></div>`;
const aDialog = (m) => `<div class="row" style="margin-bottom:6px">${ic('printer', 22)}<h2 class="grow">Opsæt Canon TS8350 series</h2>${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>
  <div class="dim" style="font-size:12.5px;margin-bottom:12px"><span class="mono">192.0.2.44</span> · IPP Everywhere · fundet med Bonjour</div>
  <div class="row" style="gap:6px;margin-bottom:10px">${badge('Profilen Hjemmekontor', 'var(--pri)', 'check')}${badge('+ profil', 'var(--dim)')}</div>
  <div style="display:grid;grid-template-columns:${m ? '1fr' : '1fr 1fr'};gap:8px">${PCS.map(pcCard).join('')}</div>
  <label class="row" style="gap:8px;font-size:13px;margin-top:12px"><span style="width:34px;height:20px;border-radius:99px;background:var(--pri);position:relative;display:inline-block"><i style="position:absolute;right:2px;top:2px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span>Standardprinter, hvis pc'en ikke har en</label>
  <div class="row" style="justify-content:flex-end;margin-top:16px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Opsæt på 3 pc\'er', { k: 'p', i: 'check' })}</div>`;
const auroraDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0)}${status(0)}${sec('Fundne printere', '4 nye')}
  <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:14px">${FOUND.map(acard).join('')}</div>${sec('I Fleetdeck')}${mineRows()}</div></div>
  <div class="ov"><div class="modal" style="max-width:640px">${aDialog(0)}</div></div></div>`;
const auroraIphone = () => `<div class="app"><div class="main">${L.mhead('Printere', btn('', { k: 'p i', i: 'plus' }))}<div class="col grow" style="overflow:hidden;padding:12px;gap:10px">${status(1)}${FOUND.slice(0, 2).map(acard).join('')}</div>${tabs()}</div>
  <div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;max-height:94%;overflow:hidden">${aDialog(1)}</div></div></div>`;
module.exports = { command: { desktop: cmdDesktop, iphone: cmdIphone }, aurora: { desktop: auroraDesktop, iphone: auroraIphone } };
