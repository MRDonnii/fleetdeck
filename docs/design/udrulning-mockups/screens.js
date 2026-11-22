// Skitser til guiden "Ny installation" i Udrulning (flere styresystemer, tomme felter spørges på maskinen).
// Alle navne og adresser er opdigtede (RFC 5737).
const L = require('../terminal-mockups/lib');
const { ic, badge, btn, field, header, mhead } = L;
const NAV = [['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['terminal-2', 'Terminal'], ['download', 'Opdateringer'], ['activity-heartbeat', 'Helbred'], ['apps', 'Programmer'], ['adjustments', 'Profiler'], ['network', 'Udrulning']];
const side = () => `<aside class="side"><div class="brand">${L.logo()}<b>fleetdeck</b></div>${NAV.map(([i, l]) => `<a class="nav ${l === 'Udrulning' ? 'on' : ''}">${ic(i, 17)}${l}</a>`).join('')}</aside>`;
const tabbar = () => `<nav class="tabbar">${[['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['network', 'Udrulning'], ['activity-heartbeat', 'Helbred'], ['menu-2', 'Mere']].map(([i, l]) => `<a class="${l === 'Udrulning' ? 'on' : ''}">${ic(i, 20)}<span>${l}</span></a>`).join('')}</nav>`;
const wrap = (dev, inner, over) => dev === 'm'
  ? `<div class="app"><div class="main">${mhead('Udrulning', '')}<div class="col grow" style="overflow:hidden">${inner}</div>${tabbar()}</div>${over || ''}</div>`
  : `<div class="app">${side()}<div class="main">${header()}<div class="col grow" style="overflow:hidden">${inner}</div></div>${over || ''}</div>`;
const modal = (w, body, dev) => `<div class="ov" style="${dev === 'm' ? 'padding:0;align-items:flex-end' : ''}"><div class="modal" style="width:${dev === 'm' ? '100%' : w + 'px'};max-width:100%;${dev === 'm' ? 'border-radius:16px 16px 0 0;height:94%;display:flex;flex-direction:column;overflow:hidden' : ''}">${body}</div></div>`;

const STEPS = ['Styresystem', 'Maskinen', 'Bruger', 'Sprog', 'Disk', 'Programmer', 'Opsummering'];
const stepper = (act, dev) => dev === 'm'
  ? `<div class="row" style="gap:8px;margin-bottom:12px"><b style="color:var(--pri)">Trin ${act + 1} af 7</b><span class="dim">· ${STEPS[act]}</span><span class="grow"></span><div class="row" style="gap:3px">${STEPS.map((_, i) => `<i style="width:14px;height:4px;border-radius:3px;background:${i <= act ? 'var(--pri)' : 'var(--bd)'}"></i>`).join('')}</div></div>`
  : `<div class="row" style="margin-bottom:18px;gap:4px">${STEPS.map((s, i) => `<span class="row grow" style="gap:6px;${i > act ? 'color:var(--dim)' : ''}"><b style="display:grid;place-items:center;flex:none;width:22px;height:22px;border-radius:50%;font-size:11px;${i < act ? 'background:var(--ok);color:#fff' : i === act ? 'background:var(--pri);color:#fff' : 'border:1px solid var(--bd)'}">${i < act ? ic('check', 13, 2.4) : i + 1}</b><span style="font-size:12px;font-weight:${i === act ? 700 : 500};white-space:nowrap">${s}</span>${i < 6 ? '<i style="flex:1;height:1px;background:var(--bd);min-width:6px"></i>' : ''}</span>`).join('')}</div>`;
const ask = (t = 'Spørges på maskinen') => badge(t, 'var(--warn)', 'message-question');
const auto = (t) => badge(t, 'var(--dim)', 'wand');
const foot = (back, next, dev) => `<div class="row" style="justify-content:space-between;margin-top:${dev === 'm' ? 'auto' : '18px'};padding-top:12px;border-top:1px solid var(--bd)">${btn(back, { k: 'g' })}${btn(next, { k: 'p', i: next === 'Gør klar' ? 'rocket' : 'arrow-right' })}</div>`;
const head = (dev) => `<div class="row" style="margin-bottom:12px">${ic('network', 22)}<h2 class="grow">Ny installation</h2>${btn('', { k: 'g i', i: 'x' })}</div>`;

const FAM = [
  ['brand-windows', 'Windows', '11 · 10', 'Klar', 'ok'], ['server', 'Windows Server', '2025', 'Kommer senere', 'off'], ['brand-ubuntu', 'Ubuntu', '26.04 · 24.04', 'Klar', 'ok'],
  ['brand-debian', 'Debian', '13 · 12', 'Kommer senere', 'off'], ['brand-redhat', 'Fedora', 'seneste', 'Kommer senere', 'off'], ['brand-redhat', 'Rocky Linux', '10 · 9', 'Kommer senere', 'off'],
  ['brand-redhat', 'AlmaLinux', '10 · 9', 'Kommer senere', 'off'], ['server-2', 'Proxmox VE', 'seneste', 'Kommer senere', 'off'], ['leaf', 'Linux Mint', '', 'Udeladt', 'off'],
];
const famCard = ([i, t, v, st, col], on, dev) => `<div class="card col" style="padding:${dev === 'm' ? '10px' : '12px'};gap:6px;${on ? 'border-color:var(--pri);box-shadow:0 0 0 3px var(--priL)' : ''};${col === 'off' ? 'opacity:.62' : ''}"><div class="row">${ic(i, 20)}<b class="grow tr">${t}</b></div><div class="row" style="gap:6px;flex-wrap:wrap"><span class="dim" style="font-size:11.5px">${v}</span><span class="grow"></span>${badge(st, col === 'ok' ? 'var(--ok)' : 'var(--dim)')}</div></div>`;
const seg = (opts, on) => `<div class="row" style="gap:0;border:1px solid var(--bd);border-radius:var(--r);padding:3px;background:var(--chip)">${opts.map(o => `<span class="grow" style="text-align:center;padding:6px 8px;border-radius:calc(var(--r) - 2px);font-weight:600;font-size:12.5px;${o === on ? 'background:var(--card);box-shadow:0 1px 3px rgba(0,0,0,.15)' : 'color:var(--dim)'}">${o}</span>`).join('')}</div>`;

const osStep = (dev) => `${stepper(0, dev)}<div class="col" style="gap:12px">
  <b style="font-size:12.5px">Familie</b>
  <div style="display:grid;grid-template-columns:repeat(${dev === 'm' ? 2 : 3},1fr);gap:8px">${FAM.slice(0, dev === 'm' ? 6 : 9).map((f, i) => famCard(f, i === 2, dev)).join('')}</div>
  <div class="row" style="gap:10px;align-items:flex-start;flex-wrap:${dev === 'm' ? 'wrap' : 'nowrap'}"><div class="col grow" style="gap:5px;min-width:150px"><b style="font-size:12.5px">Version</b>${seg(['26.04 LTS', '24.04 LTS'], '24.04 LTS')}</div><div class="col grow" style="gap:5px;min-width:150px"><b style="font-size:12.5px">Variant</b>${seg(['Skrivebord', 'Server'], 'Server')}</div></div>
  <div class="row" style="padding:10px 12px;border-radius:var(--r);background:color-mix(in srgb,var(--ok) 10%,transparent);border:1px solid color-mix(in srgb,var(--ok) 40%,transparent);font-size:12.5px">${ic('circle-check', 17)}<span class="grow">Hentet: Ubuntu 24.04.3 · 3,1 GB · kontrolleret mod SHA-256</span>${btn('Opdatér', { s: 'height:28px' })}</div>
  ${dev === 'm' ? '' : `<div class="dim row" style="font-size:12px;gap:6px;align-items:flex-start">${ic('info-circle', 13)} "Kommer senere": den automatiske installation er bygget, men ikke afprøvet til ende i en VM endnu.</div>`}
</div>`;

const userStep = (dev) => `${stepper(2, dev)}<div class="col" style="gap:12px">
  <div class="row" style="gap:10px;align-items:flex-start;flex-wrap:${dev === 'm' ? 'wrap' : 'nowrap'}">${field('Brugernavn', 'anna', { s: 'flex:1;min-width:150px', mono: 1, h: 'Små bogstaver, fx anna' })}${field('Fulde navn', 'Anna Eksempel', { s: 'flex:1;min-width:150px' })}</div>
  <label class="field"><b>Adgangskode</b><span class="inp" style="border-style:dashed;border-color:color-mix(in srgb,var(--warn) 60%,transparent)"><em class="dim">tom</em><span class="grow"></span>${ask()}</span><small>Står den tom, skriver du den på maskinen. Ellers gemmes den kun som hash (sha512-crypt).</small></label>
  ${field('SSH-nøgle (valgfri)', 'ssh-ed25519 AAAAC3Nza…Eksempel anna@laptop', { mono: 1, i: 'key', h: 'Offentlig nøgle. Så kan du logge ind uden adgangskode.' })}
  <div class="card row" style="padding:10px 12px">${ic('shield-check', 18)}<div class="grow"><b>Administrator</b><div class="dim" style="font-size:12px">Brugeren får sudo</div></div><span style="width:36px;height:20px;border-radius:12px;background:var(--pri);position:relative"><i style="position:absolute;right:2px;top:2px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span></div>
</div>`;

const diskStep = (dev) => `${stepper(4, dev)}<div class="col" style="gap:10px">
  ${[['Vælg disk på maskinen', 'Installationen stopper ved valg af disk. Intet slettes, før du vælger.', 'hand-click', 1, ask()], ['Hele den første disk', 'Disken slettes og bruges uden at spørge – god til VM\'er.', 'database', 0, '']].map(([t, s, i, on, b]) => `<div class="card row" style="padding:12px 14px;${on ? 'border-color:var(--pri);box-shadow:0 0 0 3px var(--priL)' : ''}">${ic(i, 20)}<div class="grow"><b>${t}</b><div class="dim" style="font-size:12px">${s}</div></div>${b}<span style="width:18px;height:18px;border-radius:50%;border:2px solid ${on ? 'var(--pri)' : 'var(--bd)'};display:grid;place-items:center">${on ? '<i style="width:8px;height:8px;border-radius:50%;background:var(--pri)"></i>' : ''}</span></div>`).join('')}
  <div class="card row" style="padding:10px 12px">${ic('lock', 18)}<div class="grow"><b>Kryptér disken (LUKS)</b><div class="dim" style="font-size:12px">Nøglen skrives på maskinen og gemmes aldrig i Fleetdeck</div></div><span style="width:36px;height:20px;border-radius:12px;background:var(--bd);position:relative"><i style="position:absolute;left:2px;top:2px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span></div>
</div>`;

const SUM = [['Styresystem', 'Ubuntu 24.04 LTS Server'], ['Maskine', 'minpc · 52-54-00-12-34-56'], ['Bruger', 'anna (administrator) · SSH-nøgle'], ['Sprog og region', 'Dansk · dansk tastatur · Europe/Copenhagen'], ['Programmer', 'Profil "Servere" + htop, git']];
const sumStep = (dev) => `${stepper(6, dev)}<div class="col" style="gap:12px">
  <div class="card" style="overflow:hidden">${SUM.map(([k, v], i) => `<div class="row" style="padding:9px 12px;${i ? 'border-top:1px solid var(--bd)' : ''};font-size:12.5px;flex-wrap:${dev === 'm' ? 'wrap' : 'nowrap'};gap:${dev === 'm' ? 2 : 9}px"><span class="dim" style="width:${dev === 'm' ? '100%' : '140px'}">${k}</span><b class="grow">${v}</b></div>`).join('')}</div>
  <div class="warnbox">${ic('message-question', 17)}<span><b>Spørges på maskinen:</b> adgangskode og valg af disk. Du skal være ved maskinen, når den installerer.</span></div>
  <div class="dim row" style="font-size:12px;gap:6px;align-items:flex-start">${ic('wand', 13)} Ubuntu finder selv tidszonen, hvis den står tom.</div>
  <div class="dim row" style="font-size:12px;gap:6px;align-items:flex-start">${ic('player-play', 13)} Boot maskinen fra netværket (F12/F11/Esc) – så starter installationen.</div>
</div>`;

const bg = (dev) => `<div style="padding:${dev === 'm' ? 12 : 22}px;overflow:hidden"><div class="row" style="margin-bottom:14px">${ic('network', 22)}<h1 class="grow">Udrulning</h1>${btn('Ny installation', { k: 'p', i: 'plus' })}</div><div class="card" style="height:300px"></div></div>`;
const screen = (step) => (dev) => wrap(dev, bg(dev), modal(880, `${head(dev)}<div class="col grow" style="overflow:hidden">${step(dev)}</div>${foot(step === osStep ? 'Annullér' : 'Tilbage', step === sumStep ? 'Gør klar' : 'Næste', dev)}`, dev));
const screens = { os: screen(osStep), bruger: screen(userStep), disk: screen(diskStep), opsum: screen(sumStep) };
module.exports = { screens, NAMES: Object.keys(screens), ask, auto };
