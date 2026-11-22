// Skitser til "Reservekort på Raspberry Pi": en natlig kopi til et SD-kort i en USB-kortlæser.
// Command (lys) = forslag A: alt i ét panel på Overblik, kortet vælges i en liste i panelet.
// Aurora (mørk) = forslag B: et lille statuspanel på Overblik og et vindue til at vælge og bekræfte kortet.
// Alle navne, adresser og serienumre er eksempler (RFC 5737).
const L = require('../terminal-mockups/lib');
const { ic, badge, btn, dot, prot } = L;
const DISKS = [
  { n: 'Generic STORAGE DEVICE', s: '32 GB', id: 'usb-Generic_STORAGE_DEVICE_000000000819-0:0', ser: '000000000819', on: 1 },
  { n: 'SanDisk Ultra Fit', s: '64 GB', id: 'usb-SanDisk_Ultra_Fit_4C530001-0:0', ser: '4C530001', note: 'Monteret på /media/pi/data – afmonter først', off: 1 },
];
const side = () => L.sidebar().replace('class="nav on"', 'class="nav "').replace('Maskiner</a>', 'Maskiner</a>').replace('<a class="nav ">' + ic('device-desktop', 17) + 'Maskiner', '<a class="nav on">' + ic('device-desktop', 17) + 'Maskiner');
const tabs = () => `<nav class="tabbar">${[['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['terminal-2', 'Terminal'], ['activity-heartbeat', 'Helbred'], ['menu-2', 'Mere']].map(([i, l]) => `<a class="${l === 'Maskiner' ? 'on' : ''}">${ic(i, 20)}<span>${l}</span></a>`).join('')}</nav>`;
const head = (m) => `<div class="row" style="margin-bottom:10px;flex-wrap:wrap">${ic('cpu', 22)}<h1 class="grow">minpi</h1>${m ? '' : prot}${badge('Online', 'var(--ok)')}</div>
  <div class="row dim" style="margin-bottom:12px;flex-wrap:wrap;font-size:12.5px">Raspberry Pi 4 Model B · Pi OS 12 Bookworm 64-bit · 192.0.2.23</div>
  <div class="row" style="gap:18px;border-bottom:1px solid var(--bd);margin-bottom:14px;padding-bottom:8px;overflow:hidden">${['Overblik', 'Programmer', 'Opdateringer', 'Jobs', 'Hændelser'].map(t => `<b style="white-space:nowrap;font-weight:${t === 'Overblik' ? 700 : 500};color:${t === 'Overblik' ? 'var(--pri)' : 'var(--dim)'}">${t}</b>`).join('')}</div>`;
const kv = (rows) => `<div class="col" style="gap:6px;font-size:13px">${rows.map(([k, v]) => `<div class="row"><span class="dim" style="width:120px;flex:none">${k}</span><span class="grow">${v}</span></div>`).join('')}</div>`;
const sw = (on) => `<span style="width:34px;height:20px;border-radius:99px;background:${on ? 'var(--pri)' : 'var(--chip)'};position:relative;display:inline-block;flex:none"><i style="position:absolute;${on ? 'right' : 'left'}:2px;top:2px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span>`;
const radio = (on) => `<span style="width:17px;height:17px;border-radius:50%;flex:none;border:2px solid ${on ? 'var(--pri)' : 'var(--bd)'};display:grid;place-items:center">${on ? '<i style="width:8px;height:8px;border-radius:50%;background:var(--pri)"></i>' : ''}</span>`;
const chk = (on) => `<span style="width:17px;height:17px;border-radius:4px;flex:none;display:grid;place-items:center;${on ? 'background:var(--pri);color:#fff' : 'border:1.5px solid var(--bd)'}">${on ? ic('check', 12, 3) : ''}</span>`;
const diskList = (m) => `<div class="card" style="overflow:hidden">${DISKS.map((d, i) => `<div class="row" style="padding:10px 12px;gap:10px;${i ? 'border-top:1px solid var(--bd)' : ''};${d.off ? 'opacity:.55' : ''}">${radio(d.on)}${ic('device-sd-card', 18)}<span class="col grow" style="min-width:0"><b class="tr">${d.n} · ${d.s}</b><span class="dim tr mono" style="font-size:11px">${m ? 'serienr. ' + d.ser : d.id}</span>${d.note ? `<span style="font-size:11.5px;color:var(--warn)">${d.note}</span>` : ''}</span></div>`).join('')}</div>`;
const confirm = () => `<label class="row" style="gap:8px;align-items:flex-start;font-size:13px;margin-top:10px">${chk(1)}<span>Alt på kortet slettes ved første kopi. Pi'ens eget kort (mmcblk0) kan ikke vælges.</span></label>`;
const statusRows = () => kv([
  ['Kort', 'Generic STORAGE DEVICE · 32 GB'], ['Seneste kopi', `I nat 02.31 ${badge('OK', 'var(--ok)')}`], ['Alder', '9 timer'],
  ['Næste kopi', 'I nat kl. 02.15'], ['Kerne', '<span class="mono">6.6.51+rpt-rpi-v8</span>'], ['Netboot', 'Nødsystemet har samme kerne'],
]);
// ---------------------------------------------------------------- A (Command): alt i ét panel
const panelA = (m) => `<div class="card" style="padding:16px"><div class="row" style="margin-bottom:6px"><b class="grow" style="font-size:15px">Reservekort</b>${badge('Ikke valgt', 'var(--dim)')}</div>
  <div class="dim" style="font-size:12.5px;margin-bottom:10px">En kopi af hele Pi'en til et SD-kort i en USB-kortlæser hver nat. Dør kortet, flytter du reservekortet over og starter igen.</div>
  <b style="font-size:13px">USB-kort, Pi'en kan se</b><div style="height:6px"></div>${diskList(m)}${confirm()}
  <div class="row" style="justify-content:flex-end;margin-top:12px;gap:8px">${btn('Opdatér listen', { i: 'refresh' })}${btn('Brug kortet', { k: 'p', i: 'check' })}</div></div>`;
const netboot = () => `<div class="card" style="padding:16px"><div class="row" style="margin-bottom:6px"><b class="grow" style="font-size:15px">Netboot som nødstart</b>${badge('Slået til', 'var(--ok)')}</div><div class="dim" style="font-size:12.5px">Hvis SD-kortet svigter, kan Pi'en starte over netværket fra Fleetdeck.</div></div>`;
const system = () => `<div class="card" style="padding:16px;height:220px"><b style="font-size:15px">System</b></div>`;
const cmdDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0)}
  <div style="display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start">${panelA(0)}<div class="col" style="gap:16px">${netboot()}${system()}</div></div></div></div></div>`;
const cmdIphone = () => `<div class="app"><div class="main">${L.mhead('minpi', '')}<div class="col grow" style="overflow:hidden;padding:12px;gap:12px">${head(1)}${panelA(1)}</div>${tabs()}</div></div>`;
// ---------------------------------------------------------------- B (Aurora): statuspanel + vindue
const panelB = () => `<div class="card" style="padding:16px"><div class="row" style="margin-bottom:10px">${ic('device-sd-card', 18)}<b class="grow" style="font-size:15px">Reservekort</b>${badge('Kopi i nat', 'var(--ok)')}</div>
  ${statusRows()}<div class="row" style="gap:8px;margin-top:12px;font-size:13px">${sw(1)}<span class="grow">Kopiér hver nat kl. 02.15</span></div>
  <div class="row" style="justify-content:flex-end;margin-top:12px;gap:8px;flex-wrap:wrap">${btn('Skift kort…', { i: 'switch-horizontal' })}${btn('Kopiér nu', { k: 'p', i: 'copy' })}</div></div>`;
const dialog = (m) => `<div class="row" style="margin-bottom:10px">${ic('device-sd-card', 20)}<h2 class="grow">Vælg reservekort</h2>${btn('', { k: 'g i', i: 'x' })}</div>
  <div class="dim" style="font-size:12.5px;margin-bottom:10px">Sæt kortlæseren med et tomt SD-kort i Pi'en. Kortet skal være mindst lige så stort som det, Pi'en bruger (4 GB).</div>
  ${diskList(m)}${confirm()}
  <div class="row" style="justify-content:flex-end;margin-top:16px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Brug kortet', { k: 'p', i: 'check' })}</div>`;
const auroraDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0)}
  <div style="display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start">${panelB()}<div class="col" style="gap:16px">${netboot()}${system()}</div></div></div></div>
  <div class="ov"><div class="modal" style="max-width:600px">${dialog(0)}</div></div></div>`;
const auroraIphone = () => `<div class="app"><div class="main">${L.mhead('minpi', '')}<div class="col grow" style="overflow:hidden;padding:12px;gap:12px">${head(1)}${panelB()}</div>${tabs()}</div>
  <div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;max-height:80%;overflow:hidden">${dialog(1)}</div></div></div>`;
module.exports = { command: { desktop: cmdDesktop, iphone: cmdIphone }, aurora: { desktop: auroraDesktop, iphone: auroraIphone } };
