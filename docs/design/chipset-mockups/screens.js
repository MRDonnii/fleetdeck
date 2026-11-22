// Skitse til "Installér AMD chipset-driver" (fanen Opdateringer på en Windows-pc). Alle navne og versioner er eksempler.
const L = require('../terminal-mockups/lib');
const { ic, badge, btn } = L;
const mark = (html, label) => html.split('</a>').map(a => a.endsWith(label) ? a.replace(/class="(nav|)( on)? ?"/, (m, n) => n === 'nav' ? 'class="nav on"' : 'class="on"') : a.replace(/class="nav on"/, 'class="nav"').replace(/<a class="on">/, '<a class="">')).join('</a>');
const ROWS = [
  ['AMD Chipset Software (chipset-driver)', 'chipset:amd', '6.10.17.152', '8.09.28.1124', ['Chipset-driver', 'teal'], 'Installér driver'],
  ['AMD Software: Adrenalin Edition (grafikdriver)', 'gpu:amd', '26.10.1', '26.11.2', ['Grafikdriver', 'red'], 'Installér driver'],
  ['Mozilla Firefox', 'Mozilla.Firefox', '131.0', '132.0.1', null, 'Opdatér'],
  ['2026-11 Kumulativ opdatering til Windows 11', 'wu:0b3a1c7e-11…', '–', 'KB5050001', ['Windows Update', 'blue'], 'Opdatér'],
];
const COL = { teal: '#0d9488', red: 'var(--bad)', blue: 'var(--pri)' };
const head = (m) => `<div class="row" style="margin-bottom:${m ? 8 : 12}px;flex-wrap:wrap">${ic('brand-windows', 22)}<h1 class="grow">ARBEJDS-PC</h1>${badge('Online', 'var(--ok)')}</div>
  <div class="row dim" style="margin-bottom:12px;flex-wrap:wrap;font-size:12.5px">Windows 11 Pro 25H2 · AMD Ryzen 7 5800X · B550 · 192.0.2.31</div>
  <div class="row" style="gap:18px;border-bottom:1px solid var(--bd);margin-bottom:14px;padding-bottom:8px;overflow:hidden">${['Overblik', 'Programmer', 'Opdateringer', 'Jobs', 'Hardware'].map(t => `<b style="font-weight:${t === 'Opdateringer' ? 700 : 500};${t === 'Opdateringer' ? 'color:var(--pri)' : 'color:var(--dim)'};white-space:nowrap">${t}</b>`).join('')}</div>`;
const table = () => `<div class="card" style="overflow:hidden"><div class="row" style="padding:9px 14px;font-size:12px;color:var(--dim);font-weight:600"><span style="flex:3">Program</span><span style="flex:1.4">Id</span><span style="flex:1">Nu</span><span style="flex:1">Ny</span><span style="width:170px"></span></div>
  ${ROWS.map(([n, id, c, a, src, b]) => `<div class="row" style="padding:9px 14px;border-top:1px solid var(--bd);font-size:13px"><span class="row" style="flex:3;gap:6px;min-width:0"><span class="tr">${n}</span>${src ? badge(src[0], COL[src[1]]) : ''}</span><span class="mono dim tr" style="flex:1.4;font-size:11.5px">${id}</span><span class="mono" style="flex:1">${c}</span><span class="mono" style="flex:1;font-weight:700">${a}</span><span style="width:170px;display:flex;justify-content:flex-end">${btn(b, { s: 'padding:3px 10px;font-size:12px' })}</span></div>`).join('')}</div>`;
const mlist = () => `<div class="col" style="gap:8px">${ROWS.map(([n, , c, a, src, b]) => `<div class="card" style="padding:10px 12px"><div class="row" style="gap:6px;flex-wrap:wrap"><b class="grow" style="font-size:13px">${n}</b>${src ? badge(src[0], COL[src[1]]) : ''}</div><div class="row" style="margin-top:6px"><span class="mono dim grow" style="font-size:12px">${c} → <b style="color:var(--tx)">${a}</b></span>${btn(b, { s: 'padding:3px 10px;font-size:12px' })}</div></div>`).join('')}</div>`;
const dialog = (m) => `<div class="row" style="margin-bottom:12px">${ic('cpu', 22)}<h2 class="grow">Installér AMD chipset-driver 8.09.28.1124</h2>${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>
  <div class="col" style="gap:12px;font-size:13.5px">
  <span>Fleetdeck henter AMD Chipset Software 8.09.28.1124 (ca. 80 MB) direkte fra AMD, kontrollerer, at pakken er signeret af Advanced Micro Devices, og installerer den uden vinduer.</span>
  <div class="row" style="align-items:flex-start;gap:9px;padding:10px 12px;border-radius:var(--r);background:var(--chip)">${ic('user-shield', 17)}<span><b>Kræver en indlogget administrator.</b> Pakken køres i brugerens session med administratorrettigheder – som SYSTEM fejler AMD's installation (MSI-fejl 1316). Er ingen logget ind, stopper jobbet med en forklaring.</span></div>
  <div class="warnbox">${ic('alert-triangle', 17)}<span><b>Kan ikke rulles tilbage til 6.x.</b> Pc'en har i dag 6.10.17.152. Efter 7.x eller nyere kan 6.x kun installeres igen, hvis AMD Chipset Software afinstalleres, og mappen <span class="mono">Qt_Dependencies</span> i <span class="mono">C:\\Program Files (x86)\\AMD\\Chipset_Software</span> slettes først.</span></div>
  <div class="row dim" style="font-size:12.5px;gap:6px">${ic('refresh', 14)}<span>Skærmen kan blinke kort. Genstart pc'en bagefter, så de nye drivere bliver brugt.</span></div></div>
  <div class="row" style="justify-content:flex-end;margin-top:16px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Installér', { k: 'p', i: 'download' })}</div>`;
const desktop = () => `<div class="app">${mark(L.sidebar(), 'Maskiner')}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0)}${table()}</div></div>
  <div class="ov"><div class="modal" style="max-width:600px">${dialog(0)}</div></div></div>`;
const iphone = () => `<div class="app"><div class="main">${L.mhead('ARBEJDS-PC', '')}<div class="col grow" style="overflow:hidden;padding:12px">${head(1)}${mlist()}</div>${mark(L.tabbar(), 'Maskiner')}</div>
  <div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;max-height:94%;overflow:hidden">${dialog(1)}</div></div></div>`;
module.exports = { desktop, iphone };
