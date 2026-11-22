// Skitser til "Genstart nu", vagten over genstart (hænger), strøm via Home Assistant og "Gør klar til Wake-on-LAN".
// Alle navne, adresser og tal er opdigtede (RFC 5737).
const L = require('../terminal-mockups/lib');
const { ic, dot, badge, btn, header, mhead } = L;
const mark = (html, cls, label) => html.split('</a>').map(a => a.endsWith(label) ? a.replace(/class="(nav|)( on)? ?"/, cls === 'nav' ? 'class="nav on"' : 'class="on"') : a.replace(/class="nav on"/, 'class="nav"').replace(/<a class="on">/, '<a class="">')).join('</a>');
const side = () => mark(L.sidebar(), 'nav', 'Maskiner');
const tabbar = () => mark(L.tabbar(), 'tab', 'Maskiner');
const wrap = (dev, inner, o = {}) => dev === 'm'
  ? `<div class="app"><div class="main">${mhead(o.title || 'ARBEJDS-PC', '')}<div class="col grow" style="overflow:hidden">${inner}</div>${tabbar()}</div>${o.over || ''}</div>`
  : `<div class="app">${side()}<div class="main">${header()}<div class="col grow" style="overflow:hidden">${inner}</div></div>${o.over || ''}</div>`;
const over = (w, body, dev) => `<div class="ov" style="${dev === 'm' ? 'padding:0;align-items:flex-end' : ''}"><div class="modal" style="max-width:${w}px;${dev === 'm' ? 'border-radius:16px 16px 0 0' : ''}">${body}</div></div>`;
const panel = (t, body, right = '', s = '') => `<div class="card" style="padding:16px;${s}"><div class="row" style="margin-bottom:12px"><h3 class="grow">${t}</h3>${right}</div>${body}</div>`;
const head = (dev, state) => `<div class="row" style="margin-bottom:${dev === 'm' ? 8 : 12}px;flex-wrap:wrap">${ic('device-desktop', 22)}<h1 class="grow">ARBEJDS-PC</h1>${state}</div>
  <div class="row dim" style="margin-bottom:14px;flex-wrap:wrap;font-size:12.5px">Windows 11 Pro · MSI-bundkort · Intel I211 · 192.0.2.41 · 00-00-5E-00-53-41</div>`;
const tabs = (on) => `<div class="row" style="gap:18px;border-bottom:1px solid var(--bd);margin-bottom:16px;padding-bottom:8px;overflow:hidden">${['Overblik', 'Programmer', 'Opdateringer', 'Strøm', 'Jobs'].map(t => `<b style="font-weight:${t === on ? 700 : 500};${t === on ? 'color:var(--pri)' : 'color:var(--dim)'};white-space:nowrap">${t}</b>`).join('')}</div>`;
const box = (col, icon, title, body, actions = '') => `<div style="display:flex;gap:11px;padding:13px 14px;border-radius:var(--r);border:1px solid color-mix(in srgb,${col} 45%,transparent);background:color-mix(in srgb,${col} 10%,transparent);margin-bottom:14px"><span style="color:${col}">${ic(icon, 20)}</span><div class="col grow" style="gap:6px"><b>${title}</b><span style="font-size:12.5px">${body}</span>${actions ? `<div class="row" style="flex-wrap:wrap;gap:8px;margin-top:4px">${actions}</div>` : ''}</div></div>`;
const kv = (k, v, o = '') => `<div class="row" style="padding:7px 0;border-bottom:1px solid var(--bd);font-size:12.5px"><span class="dim" style="width:150px;flex:none">${k}</span><span class="grow tr">${v}</span>${o}</div>`;
const check = (t, v, ok = 1) => `<div class="row" style="padding:5px 0;font-size:12.5px"><span style="color:${ok === 1 ? 'var(--ok)' : ok === 0 ? 'var(--dim)' : 'var(--warn)'}">${ic(ok === 1 ? 'circle-check' : ok === 0 ? 'circle-dashed' : 'alert-triangle', 16)}</span><span class="grow">${t}</span><b style="font-weight:600">${v}</b></div>`;
const jobs = () => `<div class="col" style="font-size:12.5px">${[
  ['Genstart', 'Hænger', 'var(--bad)', 'i dag 14.05 · ikke tilbage efter 5 min.'],
  ['Opdatér alle programmer', 'OK', 'var(--ok)', 'i dag 13.58'],
  ['Test af Wake-on-LAN', 'OK', 'var(--ok)', 'i går 21.12 · oppe efter 48 sek.'],
].map(([t, s, c, d]) => `<div class="row" style="padding:8px 0;border-bottom:1px solid var(--bd)"><span class="grow tr"><b style="font-weight:600">${t}</b><br><span class="dim">${d}</span></span>${badge(s, c)}</div>`).join('')}</div>`;
const strom = (dev) => panel('Strøm (Home Assistant)', `${kv('Kontakt', '<span class="mono">switch.kontor_pc</span>')}${kv('Strømmåler', '<span class="mono">sensor.kontor_pc_effekt</span>', badge('98 W nu', 'var(--warn)', 'bolt'))}${kv('Tændt over', '15 W')}
  <div class="dim" style="font-size:11.5px;margin-top:8px">"Sluk og tænd strømmen" tilbydes kun, når en genstart hænger, og måleren viser strøm.</div>`, btn('Ret', { k: 'g', i: 'pencil', z: 14 }));
const wol = (dev, tested = true) => panel('Wake-on-LAN', `<div class="dim" style="font-size:12px;margin-bottom:6px">Windows-siden (sat af Fleetdeck · backup i ProgramData)</div>
  ${check('Wake on Magic Packet', 'Enabled')}${check('Enable PME', 'Enabled')}${check('Wake on Link Settings', 'Disabled')}${check('Tillad at enheden vækker computeren', 'Til')}${check('Hurtig start', 'Fra')}
  <div class="dim" style="font-size:12px;margin:10px 0 6px">I BIOS/UEFI (tjek selv – kan ikke sættes fra Windows)</div>
  ${check('ErP / EuP Ready', 'Off', 0)}${check('Restore on AC Power Loss', 'Power On', 0)}${check('Resume by PCI-E / Wake on LAN', 'On', 0)}
  ${tested ? `<div class="row" style="margin-top:10px;padding:9px 11px;border-radius:var(--r);background:color-mix(in srgb,var(--ok) 10%,transparent);font-size:12.5px"><span style="color:var(--ok)">${ic('circle-check', 16)}</span><span class="grow">Sidste test i går 21.12: oppe <b>48 sek.</b> efter magic packet</span></div>` : ''}
  <div class="row" style="gap:8px;margin-top:12px;flex-wrap:wrap">${btn('Gør klar til WoL', { k: 'p', i: 'plug-connected' })}${btn('Test: sluk, væk, mål', { i: 'player-play' })}${btn('Fortryd', { k: 'g' })}</div>`);
const hungBox = (dev) => box('var(--bad)', 'alert-octagon', 'Genstarten hænger', `Planlagt kl. 14.05. Pc'en er ikke tilbage efter 5 min., men strømmåleren viser <b>98 W</b>. Beskeden er sendt.`,
  btn('Sluk og tænd strømmen', { k: 'd', i: 'plug-off' }) + btn('Hård genstart (Intel AMT)', { i: 'refresh' }) + (dev === 'm' ? '' : btn('Vent lidt endnu', { k: 'g' })));
const pad = (dev) => `padding:${dev === 'm' ? 12 : 22}px;overflow:hidden`;
const machine = (dev, o = {}) => `<div style="${pad(dev)}">${head(dev, o.state ?? badge('Hænger', 'var(--bad)', 'alert-octagon'))}${tabs(o.tab || 'Overblik')}${o.box ?? hungBox(dev)}
  <div class="row" style="align-items:flex-start;gap:16px;flex-wrap:${dev === 'm' ? 'wrap' : 'nowrap'}"><div class="col grow" style="gap:14px;min-width:260px">${o.left ?? (strom(dev) + panel('Seneste jobs', jobs()))}</div>
  ${dev === 'm' ? '' : `<div class="col" style="width:390px;flex:none;gap:14px">${o.right ?? wol(dev)}</div>`}</div></div>`;

// Windows-skrivebordet: nedtællingen med "Genstart nu" og menuen ved bakkeikonet
const countdown = (dev) => `<div class="app" style="background:linear-gradient(160deg,#1e3a5f,#0f172a);display:block">
  <div class="card" style="position:absolute;left:50%;top:40px;transform:translateX(-50%);width:428px;padding:20px 24px;box-shadow:0 20px 60px rgba(0,0,0,.4)">
    <div class="row" style="margin-bottom:10px">${L.logo(22)}<b>Fleetdeck</b></div>
    <h2>Pc'en genstarter snart</h2><div class="dim" style="margin:4px 0 12px">Gem dit arbejde og luk dine programmer.</div>
    <div class="card" style="padding:12px 16px;background:var(--chip)"><div class="dim">Kl. 14.05 – om</div><div style="font-size:30px;font-weight:800">4:12</div>
      <div style="height:6px;border-radius:3px;background:var(--bd);margin-top:6px"><div style="width:18%;height:6px;border-radius:3px;background:var(--pri)"></div></div></div>
    <div class="row" style="justify-content:flex-end;gap:8px;margin-top:14px">${btn('Genstart nu', { i: 'refresh' })}${btn('Udsæt 60 min.')}${btn('OK', { k: 'p' })}</div>
    <div class="dim" style="font-size:11.5px;margin-top:10px">"Genstart nu" genstarter med det samme og skrives i jobbet i Fleetdeck.</div>
  </div>
  <div class="card" style="position:absolute;right:16px;bottom:56px;width:330px;padding:6px;font-size:13px">
    ${['Fleetdeck · ARBEJDS-PC · forbundet', '—', 'Åbn status…', 'Opdatér alt nu', '—', 'Udsæt genstarten 60 min. (kl. 14.05)', 'Genstart nu', '—', 'Åbn Fleetdeck i browseren'].map(t => t === '—' ? '<div style="height:1px;background:var(--bd);margin:4px 0"></div>' : `<div style="padding:6px 10px;border-radius:6px;${t === 'Genstart nu' ? 'background:var(--priL);font-weight:600' : t.startsWith('Fleetdeck') ? 'color:var(--dim)' : ''}">${t}</div>`).join('')}
  </div>
  <div style="position:absolute;left:0;right:0;bottom:0;height:44px;background:rgba(15,23,42,.92);display:flex;align-items:center;justify-content:flex-end;padding:0 14px;gap:10px;color:#cbd5e1;font-size:12px">${L.logo(18)}<span>14.00</span></div></div>`;

const confirm = (dev) => over(460, `<h2 style="margin-bottom:8px">Sluk og tænd strømmen på ARBEJDS-PC?</h2>
  <div style="font-size:13px;margin-bottom:12px">Strømmåleren viser <b>98 W</b> – pc'en hænger med strøm på. Kontakten <span class="mono">switch.kontor_pc</span> slukkes i 10 sekunder og tændes igen. Ikke gemt arbejde går tabt.</div>
  <div class="warnbox" style="margin-bottom:14px">${ic('info-circle', 16)}<span>Pc'en starter selv igen, hvis BIOS har "Restore on AC Power Loss = Power On". Ellers sender Fleetdeck Wake-on-LAN bagefter.</span></div>
  <div class="row" style="justify-content:flex-end;gap:8px">${btn('Annullér', { k: 'g' })}${btn('Sluk og tænd', { k: 'd', i: 'plug-off' })}</div>`, dev);

const screens = {
  nedtaelling: (dev) => countdown(dev),
  haenger: (dev) => wrap(dev, machine(dev)),
  bekraeft: (dev) => wrap(dev, machine(dev), { over: confirm(dev) }),
  wol: (dev) => wrap(dev, machine(dev, { tab: 'Strøm', state: badge('Genstarter…', 'var(--pri)', 'refresh'), box: box('var(--pri)', 'refresh', 'Genstarter…', 'Sendt kl. 14.05. Jobbet lykkes først, når pc\'en er tilbage med en ny opstartstid (venter højst 5 min.).'),
    left: dev === 'm' ? wol(dev) : strom(dev) + panel('Seneste jobs', jobs().replace('Hænger', 'Genstarter…').replace('var(--bad)', 'var(--pri)')), right: wol(dev) })),
};
const NAMES = ['nedtaelling', 'haenger', 'bekraeft', 'wol'];
module.exports = { screens, NAMES };
