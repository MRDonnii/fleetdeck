// Skitser til siden "ESP-flash". To forslag: Command (lys) = liste + detalje, Aurora (mørk) = kort i et gitter.
// Alle adresser er opdigtede (RFC 5737); firmwarerne er eksempler.
const L = require('../terminal-mockups/lib');
const { ic, badge, btn, field, header, mhead, logo } = L;

const NAV = [['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['download', 'Opdateringer'], ['activity-heartbeat', 'Helbred'], ['apps', 'Programmer'], ['adjustments', 'Profiler']];
const NAV2 = [['heart-rate-monitor', 'Overvågning'], ['list-check', 'Jobs'], ['server-2', 'Servere & Docker'], ['network', 'Udrulning'], ['usb', 'ESP-flash'], ['settings', 'Indstillinger']];
const side = () => `<aside class="side"><div class="brand">${logo()}<b>fleetdeck</b></div>${NAV.map(([i, l]) => `<a class="nav">${ic(i, 17)}${l}</a>`).join('')}<div class="grp">Drift</div>${NAV2.map(([i, l]) => `<a class="nav ${l === 'ESP-flash' ? 'on' : ''}">${ic(i, 17)}${l}</a>`).join('')}</aside>`;
const tabbar = () => `<nav class="tabbar">${[['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['usb', 'ESP-flash'], ['activity-heartbeat', 'Helbred'], ['menu-2', 'Mere']].map(([i, l]) => `<a class="${l === 'ESP-flash' ? 'on' : ''}">${ic(i, 20)}${l}</a>`).join('')}</nav>`;
const wrap = (dev, inner, o = {}) => dev === 'm'
  ? `<div class="app"><div class="main">${mhead('ESP-flash', '')}<div class="col grow" style="overflow:hidden;padding:14px">${inner}</div>${tabbar()}</div>${o.over || ''}</div>`
  : `<div class="app">${side()}<div class="main">${header()}<div class="col grow" style="overflow:hidden;padding:22px 26px">${inner}</div></div>${o.over || ''}</div>`;
const over = (w, body, dev) => `<div class="ov" style="${dev === 'm' ? 'padding:0;align-items:flex-end' : ''}"><div class="modal" style="max-width:${w}px;${dev === 'm' ? 'border-radius:16px 16px 0 0' : ''}">${body}</div></div>`;
const box = (kind, icon, html, s = '') => `<div class="row" style="align-items:flex-start;gap:10px;padding:11px 13px;border-radius:var(--r);border:1px solid color-mix(in srgb,var(--${kind}) 40%,transparent);background:color-mix(in srgb,var(--${kind}) 9%,transparent);font-size:12.5px;${s}"><span style="color:var(--${kind})">${ic(icon, 18)}</span><div class="grow">${html}</div></div>`;

const FW = [
  { id: 'demo-zigbee-h2', n: 'Zigbee-sensor', chip: 'ESP32-H2', v: '1.8.2-h2', d: '27. okt', kind: 'Zigbee', lab: 'Zigbee router · Z2M', ok: 1, sum: 'Presence-sensor til Zigbee2MQTT. Fungerer også som Zigbee-router.', feat: ['Presence, bevægelse, lys og afstande', '3 afstandszoner og lysstyring', 'Opdateres trådløst efter første flash'] },
  { id: 'demo-zigbee-dual-h2', n: 'Zigbee-sensor (to radarer)', chip: 'ESP32-H2', v: '1.8.2-h2', d: '27. okt', kind: 'Zigbee', lab: 'Zigbee router · Z2M', ok: 0, sum: 'Som Zigbee-sensoren, men med to radarer og zoner på kortet.', feat: [] },
  { id: 'demo-climate-c3', n: 'Klimastyring', chip: 'ESP32-C3', v: '1.4.5', d: '30. okt', kind: 'Wi-Fi', lab: 'Wi-Fi · ESPHome', ok: 1, sum: 'Styrer en varmepumpe fra Home Assistant.', feat: ['Temperatur, tilstand og blæser', 'Wi-Fi sendes med ét klik efter flash'] },
  { id: 'demo-ble-proxy-c3', n: 'Bluetooth-proxy', chip: 'ESP32-C3', v: '1.0.1', d: '30. okt', kind: 'Wi-Fi', lab: 'Wi-Fi · ESPHome · Bluetooth-proxy', ok: 0, sum: 'Bluetooth-proxy til Home Assistant.', feat: [] },
];
const status = (f) => f.ok ? badge('Testet', 'var(--ok)') : badge('Ikke testet endnu', 'var(--warn)');
const kindIc = (f) => f.kind === 'Zigbee' ? 'hexagon' : 'wifi';
const srcLine = () => `<div class="row dim" style="font-size:12px;gap:6px">${ic('cloud-check', 15)}Firmware fra 30. okt 16.54 · hentet fra GitHub · opdateres af sig selv${'&nbsp;'}<span style="color:var(--pri);font-weight:600">Søg efter ny</span></div>`;
const httpsOk = () => box('ok', 'shield-check', `<b>Sikker forbindelse.</b> Sæt ESP'en i USB på denne pc og tryk <b>Flash via USB</b>. Virker i Chrome og Edge – intet skal installeres.`);
const wifiBox = (dev) => `<div class="card" style="padding:12px 14px;margin-top:12px"><div class="row" style="margin-bottom:8px">${ic('wifi', 17)}<b class="grow">Wi-Fi efter flash</b>${badge('Gemt krypteret', 'var(--ok)', 'lock')}</div><div class="dim" style="font-size:12.5px;margin-bottom:10px">Fleetdeck sender netværket <b style="color:var(--tx)">Hjemmenet-IoT</b> til enheden over USB (Improv), så ingen skal skrive koden.</div><div class="row">${btn('Send Wi-Fi', { k: 'p', i: 'send' })}${dev === 'm' ? '' : btn('Skift Wi-Fi', { i: 'pencil' })}</div></div>`;
const detail = (f, dev) => `<div class="card col" style="padding:${dev === 'm' ? 14 : 18}px">
  <div class="row" style="margin-bottom:6px"><span style="color:var(--pri)">${ic(kindIc(f), 24)}</span><h2 class="grow">${f.n}</h2>${status(f)}</div>
  <div class="dim" style="font-size:12.5px;margin-bottom:10px">${f.chip} · v${f.v} · ${f.d} · ${f.lab}</div>
  <div style="margin-bottom:10px">${f.sum}</div>
  ${f.feat.length ? `<ul style="margin:0 0 12px;padding-left:18px;line-height:1.6">${f.feat.map(x => `<li>${x}</li>`).join('')}</ul>` : ''}
  <div class="row" style="gap:16px;margin-bottom:14px;font-size:12.5px;color:var(--pri);font-weight:600"><span>Ledninger</span><span>Efter flash</span><span>Nyt i v${f.v}</span></div>
  ${dev === 'm' ? box('warn', 'device-desktop', 'USB-flash kræver Chrome eller Edge på en computer. Åbn siden på pc\'en, ESP\'en sidder i.') : `<div class="row">${btn('Flash via USB', { k: 'p', i: 'bolt' })}<span class="dim" style="font-size:12px">Tøm enheden først, hvis den har haft anden firmware.</span></div>`}
  ${f.kind === 'Wi-Fi' ? wifiBox(dev) : ''}
</div>`;
const listRow = (f, on) => `<div class="row" style="padding:11px 12px;border-radius:var(--r);${on ? 'background:var(--priL);' : ''}border-bottom:1px solid var(--bd)"><span style="color:${on ? 'var(--pri)' : 'var(--dim)'}">${ic(kindIc(f), 20)}</span><div class="grow"><div class="tr" style="font-weight:600">${f.n}</div><div class="dim tr" style="font-size:12px">${f.chip} · v${f.v}</div></div>${f.ok ? '' : badge('Utestet', 'var(--warn)')}</div>`;
const chips = (sel) => `<div class="row" style="gap:6px;flex-wrap:wrap">${['Alle 4', 'ESP32-H2 2', 'ESP32-C3 2'].map((t, i) => `<span class="badge" style="font-size:12px;padding:5px 10px;${i === sel ? 'background:var(--pri);color:#fff' : 'background:var(--chip);color:var(--tx)'}">${t}</span>`).join('')}</div>`;
const title = (dev) => `<div class="row" style="margin-bottom:6px">${ic('usb', 24)}<h1 class="grow">ESP-flash</h1>${dev === 'm' ? '' : btn('Indstillinger', { i: 'settings' })}</div>`;

// ---------- Command: liste til venstre, detalje til højre ----------
const wifiModal = (dev) => over(460, `<div class="row" style="margin-bottom:12px">${ic('wifi', 20)}<h2 class="grow">Send Wi-Fi til Klimastyring</h2></div><ol style="margin:0 0 14px;padding-left:20px;line-height:1.7"><li>Luk installationsvinduet, hvis det stadig er åbent.</li><li>Lad ESP'en sidde i USB.</li><li>Tryk <b>Vælg enhed</b> og vælg samme port som før.</li></ol>${box('ok', 'check', '<b>Enheden er på Wi-Fi.</b> Den dukker op i Home Assistant under Enheder → ESPHome.')}<div class="row" style="justify-content:flex-end;margin-top:16px">${btn('Luk')}${btn('Åbn enheden', { k: 'p', i: 'external-link' })}</div>`, dev);
const httpsPage = (dev) => wrap(dev, `${title(dev)}<div style="height:8px"></div>${box('bad', 'shield-x', `<b>USB virker kun på en sikker side.</b> Du er på <code>http://192.0.2.10:8585</code>. Åbn Fleetdeck over https på port 8586 – så kan Chrome og Edge tale med ESP'en.`)}
    <div class="row" style="margin:14px 0 18px">${btn('Åbn https-siden', { k: 'p', i: 'external-link' })}${btn('Hent certifikat', { i: 'certificate' })}</div>
    <div class="card" style="padding:18px;max-width:880px"><h2 style="margin-bottom:4px">Få pc'en til at stole på Fleetdeck</h2><div class="dim" style="font-size:12.5px;margin-bottom:14px">Fleetdeck bruger sit eget certifikat. Gør dette én gang på hver pc – ellers siger browseren "Ikke sikker".</div>
    <div class="row" style="gap:18px;border-bottom:1px solid var(--bd);padding-bottom:8px;margin-bottom:12px">${['Windows', 'Mac', 'Linux'].map((t, i) => `<b style="${i === 0 ? 'color:var(--pri)' : 'color:var(--dim);font-weight:500'}">${t}</b>`).join('')}</div>
    <ol style="margin:0;padding-left:20px;line-height:1.8"><li>Tryk <b>Hent certifikat</b> og åbn filen <code>fleetdeck.crt</code>.</li><li>Vælg <b>Installér certifikat → Lokal computer</b>.</li><li>Vælg <b>Placer alle certifikater i: Nøglecentre, der har tillid til rodcertifikater</b>.</li><li>Genstart Chrome/Edge og åbn https-siden.</li></ol>
    <div class="row dim" style="margin-top:14px;font-size:12px;gap:6px">${ic('fingerprint', 15)}Tjek at fingeraftrykket er <code class="mono">3F:A1:…:9C:02</code></div></div>`);
const detailPage = (dev) => wrap(dev, `<div class="row dim" style="margin-bottom:10px">${ic('chevron-left', 16)}Alle firmwarer</div>${detail(FW[2], dev)}`);
const cInner = (dev) => dev === 'm'
  ? `${title(dev)}${srcLine()}<div style="height:10px"></div>${box('warn', 'device-desktop', '<b>Fra telefonen kan der ikke flashes over USB.</b> Åbn Fleetdeck på pc\'en, ESP\'en sidder i.')}<div style="height:12px"></div>${chips(0)}<div class="card col" style="margin-top:12px;padding:4px">${FW.map((f, i) => listRow(f, i === 2)).join('')}</div>`
  : `${title(dev)}${srcLine()}<div style="height:14px"></div>${httpsOk()}<div style="height:14px"></div>
     <div class="row" style="align-items:flex-start;gap:18px"><div class="col" style="width:330px;gap:10px">${chips(0)}<div class="card col" style="padding:4px">${FW.map((f, i) => listRow(f, i === 2)).join('')}</div></div><div class="grow">${detail(FW[2], dev)}</div></div>`;
const C = { katalog: (dev) => wrap(dev, cInner(dev)), detalje: detailPage, https: httpsPage, wifi: (dev) => wrap(dev, cInner(dev), { over: wifiModal(dev) }) };

// ---------- Aurora: kort i et gitter, filtre øverst ----------
const card = (f) => `<div class="card col" style="padding:16px;gap:8px"><div class="row"><span style="width:36px;height:36px;border-radius:10px;display:grid;place-items:center;background:var(--priL);color:var(--pri)">${ic(kindIc(f), 20)}</span><div class="grow"><b class="tr" style="display:block">${f.n}</b><span class="dim" style="font-size:12px">${f.chip} · v${f.v} · ${f.d}</span></div></div><div class="row" style="gap:6px">${badge(f.lab, 'var(--pri)')}${status(f)}</div><div class="dim" style="font-size:12.5px;min-height:36px">${f.sum}</div><div class="row">${btn('Flash via USB', { k: 'p', i: 'bolt' })}${f.kind === 'Wi-Fi' ? btn('Send Wi-Fi', { i: 'wifi' }) : ''}</div></div>`;
const bInner = (dev) => dev === 'm'
  ? `${title(dev)}${srcLine()}<div style="height:10px"></div>${box('warn', 'device-desktop', 'Fra telefonen kan der ikke flashes over USB. Åbn siden på pc\'en.')}<div style="height:12px"></div>${chips(0)}<div class="col" style="gap:10px;margin-top:12px">${FW.map(f => `<div class="card row" style="padding:12px"><span style="color:var(--pri)">${ic(kindIc(f), 20)}</span><div class="grow"><b class="tr" style="display:block">${f.n}</b><span class="dim" style="font-size:12px">${f.chip} · v${f.v}</span></div>${status(f)}</div>`).join('')}</div>`
  : `${title(dev)}${srcLine()}<div style="height:14px"></div>${httpsOk()}<div class="row" style="margin:16px 0 14px">${chips(0)}<span style="flex:1"></span>${['Alle', 'Zigbee', 'Wi-Fi'].map((t, i) => `<span class="badge" style="font-size:12px;padding:5px 10px;${i === 0 ? 'background:var(--pri);color:#fff' : 'background:var(--chip);color:var(--tx)'}">${t}</span>`).join('')}</div><div style="display:grid;grid-template-columns:repeat(2,1fr);gap:14px">${FW.map(card).join('')}</div>`;
const B = { katalog: (dev) => wrap(dev, bInner(dev)), detalje: detailPage, https: httpsPage, wifi: (dev) => wrap(dev, bInner(dev), { over: wifiModal(dev) }) };

const NAMES = ['katalog', 'https', 'wifi'];
const MOBILE = ['katalog', 'detalje'];
module.exports = { screens: { C, B }, NAMES, MOBILE };
