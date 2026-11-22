// Skitser til "Opdatér alt: det, der blev sprunget over" (fanen Opdateringer på en Windows-pc).
// Forslag A: én flad liste under Status. Forslag B: grupperet efter grund, med "Skjul dem, der anbefales ikke".
// Alle navne og versioner er eksempler.
const L = require('../terminal-mockups/lib');
const { ic, badge, btn } = L;
const mark = (html, label) => html.split('</a>').map(a => a.endsWith(label) ? a.replace(/class="(nav|)( on)? ?"/, (m, n) => n === 'nav' ? 'class="nav on"' : 'class="on"') : a.replace(/class="nav on"/, 'class="nav"').replace(/<a class="on">/, '<a class="">')).join('</a>');
const COL = { teal: '#0d9488', red: 'var(--bad)', blue: 'var(--pri)', gray: 'var(--dim)' };
const sbtn = (t, k = '') => btn(t, { k, s: 'height:26px;padding:0 9px;font-size:12px' });
// [navn, kilde, grund, detalje, knap, gruppe, anbefales ikke]
const SKIP = [
  ['Skærmproducent – Monitor – 1.0.0.0', ['Windows Update', 'blue'], 'Valgfri driver fra Windows Update', 'Installér den enkeltvis, hvis du vil have den.', 'Installér', 'driver', 'Driveren er fra 2015 – over 3 år gammel'],
  ['USB-controller – USB – 1.16.0.0', ['Windows Update', 'blue'], 'Valgfri driver fra Windows Update', 'Installér den enkeltvis, hvis du vil have den.', 'Installér', 'driver', 'Erstatter Windows\' egen driver'],
  ['AMD Software: Adrenalin Edition (grafikdriver)', ['Grafikdriver', 'red'], 'Grafikdriver', 'Skærmen blinker under installationen – brug Installér driver.', 'Installér driver', 'driver', null],
  ['Musikprogram', null, 'Kræver brugerens session', 'Installeres pr. bruger – ingen var logget ind.', 'Prøv igen', 'user', null],
  ['Eksempel Editor', null, 'Fejlede', 'fejlkode 0x8A150011 (installationsprogrammet sluttede med en fejl)', 'Prøv igen', 'fail', null],
  ['Teksteditor', null, 'Ignoreret', 'Opdateres aldrig – låst på denne maskine.', null, 'ignored', null],
];
const GROUPS = [['driver', 'cpu', 'Drivere, der ikke installeres stille'], ['user', 'user', 'Kræver brugerens session'], ['fail', 'alert-triangle', 'Fejlede'], ['ignored', 'lock', 'Ignoreret']];
const HIDDEN = [['Lydprocessor (APO) – 2019-06-01', 'Skjult af Fleetdeck'], ['Printerdriver – 2014-02-11', 'Skjult i Windows']];
const tabs = () => `<div class="row" style="flex:none;gap:18px;border-bottom:1px solid var(--bd);margin-bottom:14px;padding-bottom:8px;overflow:hidden">${['Overblik', 'Programmer', 'Opdateringer', 'Jobs', 'Hardware'].map(t => `<b style="font-weight:${t === 'Opdateringer' ? 700 : 500};${t === 'Opdateringer' ? 'color:var(--pri)' : 'color:var(--dim)'};white-space:nowrap">${t}</b>`).join('')}</div>`;
const head = (m) => `<div class="row" style="flex:none;margin-bottom:${m ? 8 : 12}px;flex-wrap:wrap">${ic('brand-windows', 22)}<h1 class="grow">ARBEJDS-PC</h1>${badge('Online', 'var(--ok)')}</div>${tabs()}`;
const notRec = (t) => `<span title="${t}">${badge('Anbefales ikke', 'var(--warn)', 'alert-circle')}</span>`;
const status = (m) => `<div class="card" style="padding:12px 14px;margin-bottom:12px"><div class="row" style="flex-wrap:wrap"><h3 class="grow">Status</h3>${m ? '' : sbtn('Sluk når færdig')}${btn('Opdatér alt (6)', { s: 'height:28px;font-size:12.5px' })}</div>
  <div class="row" style="margin-top:8px;font-size:12.5px;flex-wrap:wrap"><span class="dim">Sidst:</span><span>Opdatér alt: alle programmer</span>${badge('Gennemført', 'var(--ok)')}<span class="dim">for 12 min. siden · vis resultat</span></div>
  <div class="row" style="margin-top:4px;font-size:12.5px"><span class="dim">7 opdateret · 6 sprunget over</span></div></div>`;
// Forslag A: flad liste
const flat = (m) => `<div class="card" style="padding:12px 14px;margin-bottom:12px"><div class="row"><h3 class="grow">Sprunget over i Opdatér alt (6)</h3></div>
  ${SKIP.map(([n, src, why, det, b, , nr]) => `<div class="row" style="padding:8px 0;border-top:1px solid var(--bd);align-items:flex-start;${m ? 'flex-wrap:wrap' : ''}"><div class="col grow"><div class="row" style="gap:6px;flex-wrap:wrap"><b style="font-size:13px">${n}</b>${src ? badge(src[0], COL[src[1]]) : ''}${nr ? notRec(nr) : ''}</div>
  <span style="font-size:12.5px"><b style="font-weight:600">${why}</b> <span class="dim">– ${det}${nr ? ` ${nr}.` : ''}</span></span></div>
  <div class="row" style="gap:6px">${b ? sbtn(b) : ''}${nr ? sbtn('Skjul', 'g') : ''}</div></div>`).join('')}</div>`;
// Forslag B: grupperet efter grund
const grouped = (m) => `<div class="card" style="padding:12px 14px;margin-bottom:12px"><div class="row" style="flex-wrap:wrap"><div class="col grow"><h3>6 blev sprunget over i Opdatér alt</h3><span class="dim" style="font-size:12px">for 12 min. siden – resten er opdateret</span></div>${sbtn('Skjul de 2, der anbefales ikke')}</div>
  ${GROUPS.map(([g, i, t]) => { const rows = SKIP.filter(s => s[5] === g); return `<div style="margin-top:10px"><div class="row dim" style="font-size:12px;font-weight:650;text-transform:uppercase;letter-spacing:.04em;gap:6px">${ic(i, 14)}${t} (${rows.length})</div>
  ${rows.map(([n, src, why, det, b, , nr]) => `<div class="row" style="padding:7px 0;border-bottom:1px solid var(--bd);align-items:flex-start;${m ? 'flex-wrap:wrap' : ''}"><div class="col grow"><div class="row" style="gap:6px;flex-wrap:wrap"><span style="font-size:13px;font-weight:550">${n}</span>${src ? badge(src[0], COL[src[1]]) : ''}${nr ? notRec(nr) : ''}</div>
  <span class="dim" style="font-size:12px">${det}${nr ? ` ${nr}.` : ''}</span></div><div class="row" style="gap:6px">${b ? sbtn(b) : ''}${nr ? sbtn('Skjul', 'g') : ''}</div></div>`).join('')}</div>`; }).join('')}</div>`;
const hidden = () => `<div class="card" style="padding:10px 14px"><div class="row" style="font-size:12.5px"><span class="dim">${ic('eye-off', 15)}</span><span class="grow">2 skjulte opdateringer – de tilbydes ikke længere</span><span style="color:var(--pri);font-weight:600">Skjul listen</span></div>
  ${HIDDEN.map(([n, w]) => `<div class="row" style="padding:7px 0;border-top:1px solid var(--bd);margin-top:6px"><span class="grow tr" style="font-size:13px">${n}</span><span class="dim" style="font-size:12px">${w}</span>${sbtn('Vis igen')}</div>`).join('')}</div>`;
const page = (v, m) => `${head(m)}${status(m)}${v === 'A' ? flat(m) : grouped(m)}${hidden()}`;
const desktop = (v) => () => `<div class="app">${mark(L.sidebar(), 'Maskiner')}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px;max-width:1120px">${page(v, 0)}</div></div></div>`;
const iphone = (v) => () => `<div class="app"><div class="main">${L.mhead('ARBEJDS-PC', '')}<div class="col grow" style="overflow:hidden;padding:12px">${page(v, 1)}</div>${mark(L.tabbar(), 'Maskiner')}</div></div>`;
module.exports = { desktop, iphone };
