// Skitser til BIOS i Fleetdeck: sektion på maskinsiden (A), "Opdatér BIOS" med obligatorisk backup,
// forløb og kontrol efter genstart, sammenlign/gendan og BIOS-fanen under Opdateringer (B).
// Alle navne, adresser (RFC 5737), hashes og tidspunkter er eksempler.
const L = require(process.env.FD_LIB || '../terminal-mockups/lib');
const { ic, badge, btn, logo } = L;

const BIOS = '#8b5cf6';
const M = { name: 'reception-pc', os: 'Ubuntu 24.04 LTS', model: 'Lenovo ThinkCentre M720q', ip: '192.0.2.44', cur: 'Q9ZKT75A', curD: '26. jan. 2024', curW: '1.0.0.117', lat: 'Q9ZKT79A', latD: '9. apr. 2026', latW: '1.0.0.121' };
const CHG = [
  ['79A', 'Gendannelse af fabriksnøgler til Secure Boot', 0],
  ['78A', 'Microsoft UEFI CA 2023, KEK 2023 og Windows UEFI CA 2023', 1],
  ['77A', 'Ny indstilling: TFTP Windows Size (netværksopstart)', 0],
  ['76A', 'Sikkerhedsrettelser fra Intel', 0],
];

const extra = `
.side{padding:10px 12px}.nav{padding:6px 10px;font-size:13px}.grp{padding:11px 10px 4px}
.tabs{display:flex;gap:22px;border-bottom:1px solid var(--bd);margin:0 0 16px;overflow:hidden}.tabs b{padding:0 1px 9px;font-weight:500;color:var(--dim);white-space:nowrap;display:flex;gap:6px;align-items:center}.tabs b.on{color:var(--tx);font-weight:650;box-shadow:inset 0 -2px 0 var(--pri)}
.ph{display:flex;align-items:center;gap:9px;padding:12px 16px;border-bottom:1px solid var(--bd)}.pb{padding:14px 16px}
.kv{display:grid;grid-template-columns:118px 1fr;gap:7px 14px;font-size:13px;align-items:baseline}.kv>span:nth-child(odd){color:var(--dim)}
.steps{display:flex;align-items:center;gap:8px;margin:2px 0 16px}.st{display:flex;gap:7px;align-items:center;font-size:12.5px;color:var(--dim);font-weight:550;white-space:nowrap}.st i{width:22px;height:22px;border-radius:50%;display:grid;place-items:center;border:1.5px solid var(--bd);font-style:normal;font-size:11px;font-weight:700}.st.on{color:var(--tx)}.st.on i{background:var(--pri);border-color:var(--pri);color:#fff}.st.done i{background:color-mix(in srgb,var(--ok) 16%,transparent);border-color:transparent;color:var(--ok)}.sl{flex:1;height:1.5px;background:var(--bd);min-width:10px}
.chk{display:flex;gap:9px;align-items:flex-start;padding:7px 0;border-top:1px solid var(--bd);font-size:13px}.chk:first-child{border-top:0}.chk small{display:block;color:var(--dim);font-size:11.5px;margin-top:1px}
.bar{height:8px;border-radius:99px;background:var(--chip);overflow:hidden}.bar i{display:block;height:100%;background:var(--grad);border-radius:99px}
.seg{display:inline-flex;padding:3px;border-radius:var(--r);background:var(--chip);gap:2px;border:1px solid var(--bd)}.seg span{padding:4px 11px;border-radius:calc(var(--r) - 2px);font-size:12.5px;font-weight:550;color:var(--dim);white-space:nowrap}.seg span.on{background:var(--card);color:var(--tx);box-shadow:0 1px 3px rgba(0,0,0,.14)}
.cbx{width:16px;height:16px;border-radius:4px;border:1.5px solid var(--off);display:inline-grid;place-items:center;flex:none}.cbx.on{background:var(--pri);border-color:var(--pri);color:#fff}.cbx.dis{opacity:.35}
.rad{width:16px;height:16px;border-radius:50%;border:1.5px solid var(--off);flex:none;display:inline-block}.rad.on{border:5px solid var(--pri)}
.tli{display:flex;gap:12px;position:relative;padding-bottom:13px}.tli>.tic{width:26px;height:26px;border-radius:50%;display:grid;place-items:center;flex:none;z-index:1;background:var(--chip);color:var(--off)}.tli:not(:last-child)::before{content:'';position:absolute;left:12.5px;top:26px;bottom:0;width:1.5px;background:var(--bd)}.tli.done>.tic{background:color-mix(in srgb,var(--ok) 16%,transparent);color:var(--ok)}.tli.run>.tic{background:var(--pri);color:#fff;box-shadow:0 0 0 4px var(--priL)}.tli.wait{color:var(--dim)}.tli small{display:block;color:var(--dim);font-size:11.5px;margin-top:1px}
.box{display:flex;gap:9px;padding:10px 12px;border-radius:var(--r);font-size:12.5px;align-items:flex-start}.box.w{border:1px solid color-mix(in srgb,var(--warn) 45%,transparent);background:color-mix(in srgb,var(--warn) 10%,transparent)}.box.w>svg{color:var(--warn)}.box.b{border:1px solid color-mix(in srgb,var(--bad) 45%,transparent);background:color-mix(in srgb,var(--bad) 9%,transparent)}.box.b>svg{color:var(--bad)}.box.o{border:1px solid color-mix(in srgb,var(--ok) 45%,transparent);background:color-mix(in srgb,var(--ok) 10%,transparent)}.box.o>svg{color:var(--ok)}.box.i{background:var(--priL)}.box.i>svg{color:var(--pri)}.box.n{background:var(--chip)}.box.n>svg{color:var(--dim)}
.tr2{display:flex;align-items:center;gap:10px;padding:9px 14px;border-top:1px solid var(--bd);font-size:13px}.th{font-size:12px;color:var(--dim);font-weight:600;border-top:0}.tr2.sel{background:var(--priL)}
.sm{height:28px;padding:0 10px;font-size:12px}.full{width:100%}
.mtabs{display:flex;gap:18px;border-bottom:1px solid var(--bd);margin:0 -12px 12px;padding:0 12px;overflow:hidden}.mtabs b{padding-bottom:8px;font-weight:500;color:var(--dim);white-space:nowrap;font-size:13px}.mtabs b.on{color:var(--tx);font-weight:650;box-shadow:inset 0 -2px 0 var(--pri)}
.sheet{max-width:100%!important;border-radius:16px 16px 0 0!important;padding:16px!important;max-height:96%;overflow:hidden;display:flex;flex-direction:column}.sheet>*{flex:none}.sheet>.fill{flex:1 1 auto;min-height:0;overflow:hidden;-webkit-mask-image:linear-gradient(#000 85%,transparent)}.sheet .grab{width:38px;height:5px;border-radius:9px;background:var(--off);opacity:.5;margin:-6px auto 10px}
`;
const css = (t) => L.css(t) + extra;

const ok = (s = 16) => `<span style="color:var(--ok);display:flex">${ic('circle-check', s)}</span>`;
const wn = (s = 16) => `<span style="color:var(--warn);display:flex">${ic('alert-triangle', s)}</span>`;
const nf = (s = 16) => `<span style="color:var(--dim);display:flex">${ic('info-circle', s)}</span>`;
const biosB = () => badge('BIOS', BIOS, 'cpu-2');
const sbtn = (t, o = {}) => btn(t, { ...o, k: `${o.k || ''} sm` });

// ---------- skal ----------
const NAVG = [
  ['Oversigt', [['layout-dashboard', 'Oversigt'], ['activity-heartbeat', 'Helbred'], ['heart-rate-monitor', 'Overvågning']]],
  ['Maskiner og brugere', [['device-desktop', 'Maskiner'], ['message', 'Beskeder'], ['terminal-2', 'Terminal']]],
  ['Programmer og opdateringer', [['download', 'Opdateringer'], ['apps', 'Programmer'], ['shield-check', 'Debloat & privatliv']]],
  ['Netværk og enheder', [['printer', 'Printere'], ['server-2', 'Servere & Docker'], ['cpu', 'ESP-flash']]],
  ['Udrulning og automatik', [['rocket', 'Udrulning'], ['adjustments', 'Profiler'], ['code', 'Scripts'], ['list-check', 'Jobs']]],
  ['System', [['settings', 'Indstillinger'], ['clipboard-list', 'Revisionslog']]],
];
const side = (on) => `<aside class="side"><div class="brand">${logo()}<b>fleetdeck</b></div>${NAVG.map(([g, items]) => `<div class="grp">${g}</div>${items.map(([i, l]) => `<a class="nav ${l === on ? 'on' : ''}">${ic(i, 17)}${l}</a>`).join('')}`).join('')}</aside>`;
const top = () => `<header class="top"><span class="search">${ic('search', 15)}Søg efter maskiner, programmer, sider…<kbd>Ctrl K</kbd></span><span style="flex:1"></span>${btn('', { k: 'g i', i: 'bell' })}<span class="av">AD</span></header>`;
const mtop = (t, back) => `<header class="mh">${back ? `<span class="dim" style="display:flex">${ic('chevron-left', 22)}</span>` : logo(26)}<b class="tr">${t}</b><span style="flex:1"></span>${btn('', { k: 'g i', i: 'dots' })}</header>`;
const mtab = (on) => `<nav class="tabbar">${[['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['download', 'Opdateringer'], ['activity-heartbeat', 'Helbred'], ['menu-2', 'Mere']].map(([i, l]) => `<a class="${l === on ? 'on' : ''}">${ic(i, 20)}${l}</a>`).join('')}</nav>`;
const deskApp = (nav, body, ov = '') => `<div class="app">${side(nav)}<div class="main">${top()}<div class="col grow" style="overflow:hidden;padding:20px 26px">${body}</div></div>${ov}</div>`;
const phoneApp = (title, nav, body, ov = '', back = true) => `<div class="app"><div class="main">${mtop(title, back)}<div class="col grow" style="overflow:hidden;padding:12px">${body}</div>${mtab(nav)}</div>${ov}</div>`;
const modal = (w, inner) => `<div class="ov"><div class="modal" style="max-width:${w}px;padding:20px 22px">${inner}</div></div>`;
const sheet = (inner) => `<div class="ov" style="padding:0;align-items:flex-end"><div class="modal sheet"><div class="grab"></div>${inner}</div></div>`;

// ---------- maskinsiden ----------
const TABS = ['Overblik', 'Helbred', 'Profil', 'Software og spil', 'Opdateringer', 'Hjælper', 'Jobs', 'Beskeder'];
const mhead = (m) => `<div class="row" style="margin-bottom:4px;flex-wrap:wrap"><i class="dot" style="background:var(--ok);width:10px;height:10px"></i><h1 class="grow" style="${m ? 'font-size:20px' : ''}">${M.name}</h1>${m ? '' : btn('Handlinger', { i: 'chevron-down' })}</div>
  <div class="dim" style="margin-bottom:${m ? 10 : 14}px;font-size:12.5px">${M.os} · ${M.model} · ${M.ip} · online</div>
  ${m ? `<div class="mtabs">${TABS.slice(0, 5).map((t) => `<b class="${t === 'Opdateringer' ? 'on' : ''}">${t}</b>`).join('')}</div>`
    : `<div class="tabs">${TABS.map((t) => `<b class="${t === 'Opdateringer' ? 'on' : ''}">${t}${t === 'Opdateringer' ? badge('3', 'var(--warn)') : ''}</b>`).join('')}</div>`}`;

const chg = (m) => `<div style="margin-top:14px"><div class="row" style="margin-bottom:6px"><b class="grow" style="font-size:12.5px">Det er nyt siden din version</b><span class="dim" style="font-size:12px">Vis hele listen</span></div>
  ${CHG.slice(0, m ? 3 : 4).map(([v, t, imp]) => `<div class="row" style="padding:4px 0;font-size:12.5px;align-items:flex-start"><span class="mono" style="width:34px;font-weight:700;color:var(--dim)">${v}</span><span class="grow">${t}${imp ? ` ${badge('Vigtig · Secure Boot', 'var(--bad)', 'shield-lock')}` : ''}</span></div>`).join('')}</div>`;

const biosPanel = (m, state = 'newer') => {
  const st = state === 'running' ? badge('Opdatering i gang', 'var(--pri)', 'loader-2') : badge('Nyere findes', 'var(--warn)', 'arrow-up');
  const kv = m
    ? `<div class="kv" style="grid-template-columns:86px 1fr;font-size:12.5px"><span>Nuværende</span><span class="mono">${M.cur} · ${M.curD}</span><span>Nyeste</span><span><b class="mono">${M.lat}</b> <span class="dim">· ${M.latD}</span></span><span>Vej</span><span>UEFI-kapsel ved genstart</span><span>Indstillinger</span><span>81 · intet BIOS-password</span></div>`
    : `<div class="kv"><span>Model</span><span>${M.model} <span class="dim">· BIOS-familie Q9ZK</span></span>
      <span>Nuværende</span><span class="mono">${M.cur} <span class="dim">· ${M.curD} · ${M.curW}</span></span>
      <span>Nyeste</span><span><b class="mono">${M.lat}</b> <span class="dim mono">· ${M.latD} · ${M.latW}</span> &nbsp;<span style="color:var(--warn);font-weight:600">4 versioner nyere</span></span>
      <span>Kilde</span><span>Lenovo-katalog · tjekket for 3 t siden · modellen passer sikkert</span>
      <span>Opdateringsvej</span><span>UEFI-kapsel, som BIOS'en installerer ved næste genstart. Secure Boot skal ikke slås fra. <span class="dim">Reserve: USB-stik.</span></span>
      <span>Indstillinger</span><span>81 kan læses og skrives (think-lmi) · intet BIOS-password</span></div>`;
  const acts = m
    ? `<div class="col" style="gap:8px;margin-top:14px">${btn('Opdatér BIOS…', { k: 'p full', i: 'cpu-2' })}<div class="row" style="gap:8px">${btn('USB-stik', { s: 'flex:1', i: 'usb' })}${btn('Hent', { s: 'flex:1', i: 'download' })}${btn('Backup', { s: 'flex:1', i: 'database-export' })}</div></div>`
    : `<div class="row" style="margin-top:16px;flex-wrap:wrap;gap:8px">${btn('Opdatér BIOS…', { k: 'p', i: 'cpu-2' })}${btn('Lav USB-stik', { i: 'usb' })}${btn('Hent pakke (7,6 MB)', { i: 'download' })}${btn('Tag backup af indstillinger', { i: 'database-export' })}</div>`;
  return `<div class="card"><div class="ph">${ic('cpu-2', 19)}<h2 class="grow">BIOS og firmware</h2>${st}</div><div class="pb">${kv}
    <div class="box w" style="margin-top:12px">${ic('shield-lock', 16)}<span>Secure Boot-nøglerne fra 2011 udløber i 2026. ${M.lat} har de nye nøgler fra 2023.</span></div>
    ${chg(m)}${acts}
    <div class="row dim" style="margin-top:10px;font-size:12px;gap:6px;align-items:flex-start">${ic('info-circle', 14)}<span>Opdatér BIOS tager altid en backup af indstillingerne først. BIOS kommer aldrig med i “Opdatér alt”.</span></div></div></div>`;
};

const READY = [
  ['Secure Boot', 'Til', 1], ['TPM', '2.0 · aktiv', 1], ['Diskkryptering', 'Ingen', 1], ['Strøm', 'Stationær · lysnet', 1],
  ['EFI-partition', '412 MB fri', 1], ['Ventende BIOS-ændringer', 'Ingen', 1], ['Logget ind', '1 bruger', 0],
];
const readyPanel = () => `<div class="card"><div class="ph">${ic('list-check', 18)}<h3 class="grow">Klar til opdatering</h3>${badge('Ja', 'var(--ok)')}</div><div class="pb" style="padding-top:6px;padding-bottom:6px">
  ${READY.map(([k, v, g]) => `<div class="chk">${g ? ok(15) : nf(15)}<span class="grow">${k}</span><span class="dim">${v}</span></div>`).join('')}</div></div>`;
const SNAPS = [['20. nov. 2026 14:02', M.cur, 81, 'Manuel'], ['3. okt. 2026 09:41', M.cur, 81, 'Manuel']];
const snapPanel = (m) => `<div class="card"><div class="ph">${ic('history', 18)}<h3 class="grow">Backup af BIOS-indstillinger</h3>${m ? '' : sbtn('Sammenlign to…', { k: 'g', i: 'arrows-diff' })}</div>
  ${SNAPS.map(([d, v, n, r], i) => `<div class="tr2" style="${i ? '' : 'border-top:0'};padding:9px 16px"><span class="grow"><b style="font-weight:600">${d}</b><br><span class="dim mono" style="font-size:11.5px">${v} · ${n} felter · ${r}</span></span>${sbtn(m ? '' : 'Sammenlign med nu', { i: 'arrows-diff', k: m ? 'i' : '' })}${m ? '' : sbtn('', { k: 'g i', i: 'download' })}</div>`).join('')}</div>`;
const otherUpd = () => `<div class="card" style="margin-top:14px"><div class="ph"><h3 class="grow">Opdateringer på maskinen</h3>${btn('Opdatér alt', { k: 'sm', i: 'download' })}</div>
  ${[[`BIOS ${M.cur}`, biosB(), M.cur, M.lat, 'Opdatér BIOS…'], ['Mozilla Firefox', badge('APT', 'var(--dim)'), '132.0', '133.0.3', 'Opdatér'], ['linux-firmware', badge('Firmware', '#0d9488'), '20251015', '20251111', 'Opdatér']]
    .map(([n, b, c, a, t], i) => `<div class="tr2" style="${i ? '' : 'border-top:0'}"><span class="row grow" style="gap:7px">${n}${b}</span><span class="mono dim" style="width:90px">${c}</span><span class="mono" style="width:90px;font-weight:700">${a}</span>${sbtn(t)}</div>`).join('')}
  <div class="tr2 dim" style="font-size:12px">${ic('info-circle', 14)}Opdatér alt springer BIOS over.</div></div>`;

const machineDesk = (ov = '', state) => deskApp('Maskiner', `${mhead(0)}<div class="row" style="align-items:flex-start;gap:16px"><div class="col" style="flex:1.65">${biosPanel(0, state)}${otherUpd()}</div><div class="col" style="flex:1;gap:14px">${readyPanel()}${snapPanel(0)}</div></div>`, ov);
const machinePhone = (ov = '') => phoneApp(M.name, 'Maskiner', `${mhead(1)}<div class="col" style="gap:12px">${biosPanel(1)}${snapPanel(1)}</div>`, ov);

// ---------- Opdatér BIOS: trin ----------
const STEPS = ['Backup', 'Kontrol', 'Bekræft', 'Opdatering', 'Efter genstart'];
const stepper = (cur, m) => `<div class="steps">${STEPS.map((s, i) => `${i ? '<span class="sl"></span>' : ''}<span class="st ${i < cur ? 'done' : i === cur ? 'on' : ''}"><i>${i < cur ? ic('check', 13, 2.6) : i + 1}</i>${m && i !== cur ? '' : s}</span>`).join('')}</div>`;
const mtitle = (t, m) => `<div class="row" style="margin-bottom:14px">${ic('cpu-2', 22)}<h2 class="grow">${t}</h2>${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>`;

const SETTINGS = [['After Power Loss', 'Power On'], ['Wake on LAN', 'Automatic'], ['Secure Boot', 'Enabled'], ['Boot Mode', 'UEFI Only'], ['Primary Boot Sequence', 'NVMe0:USB HDD:PCI LAN'], ['Configure SATA as', 'AHCI'], ['Intel Virtualization Technology', 'Enabled'], ['Alarm Date', '01/01/2026'], ['Smart Power On', 'Disabled']];
const backupStep = (m) => {
  const done = 54, n = m ? 5 : 9;
  return `${mtitle(`Opdatér BIOS på ${M.name}`, m)}${stepper(0, m)}
  <div class="col" style="gap:12px">
  <span>${M.cur} → <b class="mono">${M.lat}</b>. Først gemmes alle BIOS-indstillinger, så de kan sammenlignes og sættes tilbage efter opdateringen. <b>Trinnet kan ikke springes over.</b></span>
  <div class="card" style="padding:14px 16px;background:var(--chip)"><div class="row" style="margin-bottom:8px">${ic('database-export', 18)}<b class="grow">Læser indstillinger fra BIOS'en…</b><span class="mono dim">${done} af 81</span></div><div class="bar"><i style="width:${Math.round(done / 81 * 100)}%"></i></div>
   <div style="margin-top:10px">${SETTINGS.slice(0, n).map(([k, v], i) => `<div class="row" style="padding:3px 0;font-size:12.5px;${i >= n - 2 ? 'opacity:.45' : ''}">${i >= n - 2 ? `<span style="color:var(--dim);display:flex">${ic('circle-dashed', 14)}</span>` : ok(14)}<span class="grow tr">${k}</span><span class="mono dim tr" style="max-width:${m ? 130 : 260}px">${i >= n - 2 ? '…' : v}</span></div>`).join('')}</div></div>
  <div class="box n">${ic('lock', 16)}<span>Passwords gemmes aldrig. Backuppen gemmes på serveren og kan hentes som fil bagefter.</span></div>
  <div class="box w">${ic('alert-triangle', 16)}<span>Kan backuppen ikke tages, stopper opdateringen her. Der bliver ikke ændret noget på maskinen.</span></div></div>
  <div class="row" style="justify-content:flex-end;margin-top:16px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Næste: Kontrol', { k: 'p', s: 'opacity:.45', i: 'chevron-right' })}</div>
  <div class="dim" style="font-size:11.5px;margin-top:6px;text-align:${m ? 'center' : 'right'}">Næste bliver mulig, når backuppen er gemt.</div>`;
};

const CHECKS = [
  ['Backup af 81 indstillinger', 'I dag 14:02 · kan sammenlignes og gendannes', 1],
  ['Pakke Q9ZKT79A fra Lenovo', 'SHA-256 kontrolleret · 8f3a…c21e', 1],
  ['Modellen passer sikkert', 'BIOS-familie Q9ZK · Lenovo tillader opgradering fra 75A', 1],
  ['Strøm', 'Stationær pc på lysnettet', 1],
  ['Plads på EFI-partitionen', '412 MB fri · kræver 33 MB', 1],
  ['Ingen ventende BIOS-ændringer', '', 1],
  ['Secure Boot er til og forbliver til', 'Kapslen er signeret af Lenovo', 1],
  ['Diskkryptering', 'Ingen · der skal ikke bruges en genoprettelsesnøgle', 1],
  ['1 bruger er logget ind', 'Får en besked 1 minut før genstart', 0],
];
const confirmStep = (m) => {
  const checks = `<div>${(m ? CHECKS.filter((c, i) => [0, 1, 3, 6, 8].includes(i)) : CHECKS).map(([t, s, g]) => `<div class="chk">${g ? ok(16) : wn(16)}<span class="grow"><span style="font-weight:550">${t}</span>${s ? `<small>${s}</small>` : ''}</span></div>`).join('')}${m ? `<div class="dim" style="font-size:12px;padding:4px 0 0 25px">+ 4 kontroller mere er i orden</div>` : ''}</div>`;
  const how = `<div class="col" style="gap:10px">
    <div class="box i">${ic('info-circle', 16)}<span><b>Sådan foregår det.</b> Fleetdeck lægger Lenovos kapsel på EFI-partitionen og genstarter. BIOS'en installerer den selv, før Ubuntu starter. Det tager typisk 2-5 minutter, og maskinen kan genstarte 2 gange. Bagefter kontrollerer Fleetdeck versionen og tager en ny backup.</span></div>
    <div class="box b">${ic('bolt', 16)}<span><b>Strømmen må ikke gå.</b> Går den midt i opdateringen, kan maskinen ende med ikke at kunne starte. Sluk ikke maskinen, selv om skærmen er sort i flere minutter.</span></div>
    <div><b style="font-size:12.5px">Hvornår</b><div class="row" style="margin-top:6px;gap:16px;flex-wrap:wrap;font-size:13px"><span class="row" style="gap:7px"><i class="rad on"></i>Genstart nu</span><span class="row" style="gap:7px"><i class="rad"></i>Ved næste genstart</span></div></div>
    <label class="row" style="gap:9px;align-items:flex-start;font-size:13px"><i class="cbx on">${ic('check', 12, 3)}</i><span>Jeg ved, at ${M.name} er væk i op til 10 minutter og ikke må slukkes imens.</span></label>
    ${m ? '' : `<div class="row dim" style="font-size:12px;gap:6px;align-items:flex-start">${ic('info-circle', 14)}<span>Afviser BIOS'en kapslen, beholder den den gamle version, og Fleetdeck viser fejlen. Reserven er et USB-stik.</span></div>`}</div>`;
  return `${mtitle(`Opdatér BIOS på ${M.name}`, m)}${stepper(2, m)}
  ${m ? `<div class="col" style="gap:10px">${checks}${how}</div>` : `<div class="row" style="align-items:flex-start;gap:20px"><div style="flex:1"><b style="font-size:12.5px;color:var(--dim)">KONTROL</b>${checks}</div><div style="flex:1.05">${how}</div></div>`}
  <div class="row" style="margin-top:16px;${m ? 'flex-direction:column-reverse;align-items:stretch;gap:8px' : ''}">${m ? '' : btn('Tilbage', { k: 'g', i: 'chevron-left' })}<span style="flex:1"></span>${btn('Annullér')}${btn(`Opdatér BIOS og genstart`, { k: 'd', i: 'cpu-2' })}</div>`;
};

const tl = (items) => `<div style="margin-top:4px">${items.map(([s, t, sub]) => `<div class="tli ${s}"><span class="tic">${s === 'done' ? ic('check', 14, 2.6) : s === 'run' ? ic('loader-2', 15, 2.2) : ic('point', 14)}</span><div class="grow" style="padding-top:3px"><span style="font-weight:${s === 'wait' ? 500 : 600}">${t}</span>${sub ? `<small>${sub}</small>` : ''}</div></div>`).join('')}</div>`;
const progressStep = (m) => `${mtitle(`BIOS-opdatering på ${M.name}`, m)}${stepper(3, m)}
  <div class="row" style="margin-bottom:10px;font-size:12.5px"><span class="mono">${M.cur} → <b>${M.lat}</b></span><span style="flex:1"></span><span class="dim">Startet 14:06 af AD</span></div>
  ${tl([
    ['done', 'Backup af 81 indstillinger', '14:02'],
    ['done', 'Pakke sendt og kontrolleret på maskinen', '14:06 · SHA-256 passer'],
    ['done', 'Kapsel lagt klar på EFI-partitionen', '14:06'],
    ['done', 'Genstarter', '14:07 · brugeren fik besked 14:06'],
    ['run', "BIOS'en installerer opdateringen", `Maskinen er offline. Det er normalt. <b style="color:var(--tx)">2:41</b> · Fleetdeck venter op til 15 min.`],
    ['wait', 'Online igen', ''],
    ['wait', 'Kontrol af version og Secure Boot', ''],
    ['wait', 'Ny backup og sammenligning', ''],
  ])}
  <div class="box b">${ic('bolt', 16)}<span>Sluk ikke ${M.name}, og tag ikke strømmen, selv om skærmen er sort.</span></div>
  <div class="row dim" style="font-size:12px;margin-top:10px;gap:6px;align-items:flex-start">${ic('info-circle', 14)}<span>Du kan godt lukke vinduet. Opdateringen fortsætter, og status står på maskinsiden. Er maskinen ikke online efter 15 min, viser Fleetdeck, hvad du kan gøre.</span></div>
  <div class="row" style="justify-content:flex-end;margin-top:14px;${m ? 'flex-direction:column;align-items:stretch' : ''}">${btn('Luk')}</div>`;

const verifyStep = (m) => `${mtitle(`BIOS-opdatering på ${M.name}`, m)}${stepper(4, m)}
  <div class="box o" style="margin-bottom:12px">${ic('circle-check', 18)}<span style="font-size:13.5px"><b>BIOS'en er opdateret til ${M.lat}.</b> 4 indstillinger er ændret, og 1 er ny. Se dem, og vælg hvad der skal sættes tilbage.</span></div>
  ${tl([
    ['done', 'Backup af 81 indstillinger', '14:02'],
    ['done', 'Pakke kontrolleret og kapsel lagt klar', '14:06'],
    ['done', "Genstart og BIOS'en installerede opdateringen", '14:07-14:10 · offline i 3 min 12 s'],
    ['done', 'Online igen', '14:10 · agenten svarer'],
    ['done', 'Kontrol', `<span class="mono">Version ${M.lat} ✓ · firmware ${M.latW} ✓</span> · Secure Boot til ✓`],
    ['done', 'Ny backup efter genstart', '14:11 · 82 indstillinger · 4 ændret, 1 ny'],
  ])}
  <div class="row" style="justify-content:flex-end;margin-top:6px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Luk')}${btn('Sammenlign og gendan…', { k: 'p', i: 'arrows-diff' })}</div>`;

// ---------- sammenlign og gendan ----------
const DIFF = [
  ['on', 'After Power Loss', 'Power On', 'Power Off', ['Almindelig', 'var(--ok)']],
  ['on', 'Wake on LAN', 'Automatic', 'Disabled', ['Almindelig', 'var(--ok)']],
  ['off', 'Primary Boot Sequence', 'NVMe0:USB HDD:PCI LAN', 'USB HDD:NVMe0:PCI LAN', ['Kræver eget valg', 'var(--warn)', 'alert-triangle'], 'Forkert rækkefølge kan gøre, at maskinen ikke starter. Alle tre enheder findes stadig.'],
  ['dis', 'Alarm Date', '01/01/2026', '01/01/2019', ['Gendannes ikke', 'var(--dim)', 'lock']],
  ['dis', 'TFTP Windows Size', '(fandtes ikke)', '1', ['Ny', BIOS, 'sparkles']],
];
const compare = (m) => {
  const sel = (l, v) => `<div class="field" style="flex:1"><b>${l}</b><span class="inp"><em>${v}</em>${ic('chevron-down', 15)}</span></div>`;
  const rows = m
    ? DIFF.map(([c, n, a, b, k, w]) => `<div class="card" style="padding:10px 12px;${c === 'on' ? 'border-color:color-mix(in srgb,var(--pri) 45%,transparent)' : ''}"><div class="row" style="gap:8px"><i class="cbx ${c === 'on' ? 'on' : ''} ${c === 'dis' ? 'dis' : ''}">${c === 'on' ? ic('check', 12, 3) : ''}</i><b class="grow tr" style="font-size:13px">${n}</b>${badge(k[0], k[1], k[2])}</div><div class="mono" style="font-size:11.5px;margin:5px 0 0 24px"><span class="dim">Før</span> ${a}<br><span class="dim">Nu&nbsp;</span> ${b}</div>${w ? `<div style="font-size:11.5px;color:var(--warn);margin:4px 0 0 24px">${w}</div>` : ''}</div>`).join('')
    : `<div class="card" style="overflow:hidden"><div class="tr2 th"><span style="width:18px"></span><span style="flex:1.4">Indstilling</span><span style="flex:1.3">Før (${M.cur})</span><span style="flex:1.3">Nu (${M.lat})</span><span style="width:150px">Type</span></div>
      ${DIFF.map(([c, n, a, b, k, w]) => `<div class="tr2 ${c === 'on' ? 'sel' : ''}"><i class="cbx ${c === 'on' ? 'on' : ''} ${c === 'dis' ? 'dis' : ''}">${c === 'on' ? ic('check', 12, 3) : ''}</i><span style="flex:1.4;font-weight:550">${n}</span><span class="mono" style="flex:1.3">${a}</span><span class="mono" style="flex:1.3;${c === 'dis' ? '' : 'color:var(--warn)'}">${b}</span><span style="width:150px">${badge(k[0], k[1], k[2])}</span></div>${w ? `<div class="tr2" style="border-top:0;padding-top:0;padding-left:42px;font-size:12px;color:var(--warn)">${ic('alert-triangle', 14)}${w}</div>` : ''}`).join('')}</div>`;
  return `<div class="row" style="margin-bottom:14px">${ic('arrows-diff', 22)}<h2 class="grow">Sammenlign indstillinger · ${M.name}</h2>${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>
  <div class="row" style="gap:12px;margin-bottom:12px;${m ? 'flex-direction:column;align-items:stretch' : ''}">${sel('Før', `20. nov. 14:02 · ${M.cur} · før opdatering`)}${sel('Nu', `20. nov. 14:11 · ${M.lat} · efter genstart`)}</div>
  <div class="row" style="margin-bottom:10px"><span class="seg"><span class="on">Kun forskelle (5)</span><span>Alle 82</span></span><span style="flex:1"></span>${m ? '' : `<span class="dim" style="font-size:12px">Regler: almindelige forvælges, resten kræver dit valg</span>`}</div>
  <div class="col ${m ? 'fill' : ''}" style="gap:8px">${rows}</div>
  <div class="row" style="margin-top:12px;font-size:12.5px;flex-wrap:wrap;gap:6px 14px"><b>2 valgt</b><span style="color:var(--warn)">1 kræver eget valg</span><span class="dim">1 gendannes ikke</span><span class="dim">1 ny</span></div>
  <div class="row dim" style="font-size:12px;margin-top:8px;gap:6px;align-items:flex-start">${ic('info-circle', 14)}<span>Der tages en backup lige før. Ændringerne gemmes på én gang og virker efter næste genstart. Bagefter kontrollerer Fleetdeck, at de blev sat.</span></div>
  <div class="row" style="justify-content:flex-end;margin-top:14px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Gendan 2 indstillinger…', { k: 'p', i: 'restore' })}</div>`;
};

// ---------- B: BIOS-fanen under Opdateringer ----------
const FLEET = [
  ['reception-pc', 'brand-ubuntu', 'Lenovo ThinkCentre M720q', M.cur, M.lat, [['4 versioner bagud', 'var(--warn)'], ['Vigtig', 'var(--bad)', 'shield-lock']], 'UEFI-kapsel', 'I dag 14:02', 1],
  ['arbejds-pc', 'brand-windows', 'Dell OptiPlex 7090', '1.34.0', '1.36.1', [['Nyere findes', 'var(--warn)']], 'Dells program', 'Ingen', 0],
  ['laptop-2', 'brand-windows', 'HP EliteBook 840 G8', '01.36.00', '01.36.00', [['Nyeste', 'var(--ok)', 'check']], '–', '3. nov.', 0],
  ['lager-pc', 'brand-debian', 'Supermicro X11SCL-F', '1.6', '?', [['Leverandør ikke understøttet', 'var(--dim)']], '–', '–', 0],
  ['server-vm', 'server-2', 'QEMU (virtuel)', 'OVMF', '–', [['Virtuel maskine', 'var(--dim)']], '–', '–', 0],
  ['mac-mini', 'brand-apple', 'Mac mini', '–', '–', [['Opdateres med macOS', 'var(--dim)']], '–', '–', 0],
  ['pi-skaerm', 'cpu', 'Raspberry Pi 4', '–', '–', [['Bootloader under Firmware', 'var(--dim)']], '–', '–', 0],
];
const updHead = (m) => `<div class="row" style="margin-bottom:${m ? 8 : 4}px">${m ? '' : `<h1 class="grow">Opdateringer</h1>${btn('Opdatér alt', { k: 'p', i: 'download' })}`}</div>
  ${m ? '' : `<div class="dim" style="margin-bottom:14px;font-size:12.5px">14 opdateringer på 6 maskiner · BIOS på 2 maskiner</div>`}
  <div class="${m ? 'mtabs' : 'tabs'}"><b>Programmer${badge('14', 'var(--warn)')}</b><b class="on">BIOS${badge('2', 'var(--warn)')}</b></div>`;
const fleetDesk = () => {
  const filt = `<div class="row" style="margin-bottom:12px;gap:12px"><span class="seg"><span>Bagud (2)</span><span class="on">Alle (7)</span><span>Ikke tjekket (0)</span></span><span class="inp" style="width:260px">${ic('search', 15)}<em class="dim">Søg maskine eller model</em></span><span style="flex:1"></span><span class="dim" style="font-size:12px">Katalog: Lenovo, Dell, HP · tjekket for 3 t siden</span></div>`;
  const table = `<div class="card" style="overflow:hidden"><div class="tr2 th"><span style="flex:1.1">Maskine</span><span style="flex:1.5">Model</span><span style="flex:.8">Har</span><span style="flex:.8">Nyeste</span><span style="flex:1.7">Status</span><span style="flex:1.1">Opdateringsvej</span><span style="flex:.8">Backup</span><span style="width:16px"></span></div>
    ${FLEET.map(([n, i, mo, c, l, st, way, bk, sel]) => `<div class="tr2 ${sel ? 'sel' : ''}"><span class="row" style="flex:1.1;gap:7px"><span class="dim" style="display:flex">${ic(i, 16)}</span><b style="font-weight:600">${n}</b></span><span class="tr" style="flex:1.5">${mo}</span><span class="mono" style="flex:.8">${c}</span><span class="mono" style="flex:.8;font-weight:${c !== l && l !== '–' && l !== '?' ? 700 : 400}">${l}</span><span class="row" style="flex:1.7;gap:5px;flex-wrap:wrap">${st.map(([t, col, ico]) => badge(t, col, ico)).join('')}</span><span class="dim tr" style="flex:1.1;font-size:12.5px">${way}</span><span class="dim" style="flex:.8;font-size:12.5px">${bk}</span><span class="dim" style="width:16px;display:flex">${ic('chevron-right', 15)}</span></div>`).join('')}</div>
    <div class="row dim" style="font-size:12px;margin-top:10px;gap:6px">${ic('info-circle', 14)}Opdatér alt tager aldrig BIOS med. Hver maskine opdateres for sig, og altid med backup først.</div>`;
  const drawer = `<div class="card col" style="width:360px;flex:none;padding:0"><div class="ph"><span class="dim" style="display:flex">${ic('brand-ubuntu', 18)}</span><h2 class="grow">reception-pc</h2>${btn('', { k: 'g i', i: 'x' })}</div>
    <div class="pb col" style="gap:12px"><div class="mono" style="font-size:14px">${M.cur} → <b>${M.lat}</b></div><div class="dim" style="font-size:12.5px;margin-top:-8px">${M.model} · udgivet ${M.latD}</div>
    <div class="box w">${ic('shield-lock', 16)}<span>${M.lat} har de nye Secure Boot-nøgler fra 2023.</span></div>
    <div>${CHG.map(([v, t]) => `<div class="row" style="padding:3px 0;font-size:12.5px;align-items:flex-start"><span class="mono dim" style="width:32px;font-weight:700">${v}</span><span class="grow">${t}</span></div>`).join('')}</div>
    <div class="kv" style="grid-template-columns:96px 1fr;font-size:12.5px"><span>Vej</span><span>UEFI-kapsel ved genstart</span><span>Backup</span><span>I dag 14:02 · 81 felter</span><span>Klar</span><span style="color:var(--ok);font-weight:600">Ja, alle kontroller er i orden</span></div>
    ${btn('Opdatér BIOS…', { k: 'p full', i: 'cpu-2' })}<div class="row" style="gap:8px">${btn('USB-stik', { s: 'flex:1', i: 'usb' })}${btn('Hent pakke', { s: 'flex:1', i: 'download' })}</div>${btn('Tag backup af indstillinger', { k: 'full', i: 'database-export' })}
    <span style="color:var(--pri);font-size:12.5px;font-weight:600">Åbn maskinen ›</span></div></div>`;
  return deskApp('Opdateringer', `${updHead(0)}${filt}<div class="row" style="align-items:flex-start;gap:16px;flex:1;min-height:0"><div class="col grow">${table}</div>${drawer}</div>`);
};
const fleetPhone = () => phoneApp('Opdateringer', 'Opdateringer', `${updHead(1)}<div class="row" style="margin-bottom:10px"><span class="seg"><span class="on">Bagud (2)</span><span>Alle (7)</span><span>Ikke tjekket</span></span></div>
  <div class="col" style="gap:8px">${FLEET.slice(0, 2).map(([n, i, mo, c, l, st, way, bk]) => `<div class="card" style="padding:12px"><div class="row"><span class="dim" style="display:flex">${ic(i, 17)}</span><b class="grow">${n}</b>${ic('chevron-right', 16)}</div><div class="dim" style="font-size:12px;margin:3px 0 6px">${mo}</div><div class="mono" style="font-size:12.5px">${c} → <b>${l}</b></div><div class="row" style="gap:5px;margin-top:7px;flex-wrap:wrap">${st.map(([t, col, ico]) => badge(t, col, ico)).join('')}</div><div class="row dim" style="font-size:12px;margin-top:8px;gap:12px"><span>${way}</span><span>Backup: ${bk}</span></div><div class="row" style="margin-top:10px;gap:8px">${btn('Opdatér BIOS…', { k: 'p', s: 'flex:1', i: 'cpu-2' })}${btn('', { k: 'i', i: 'usb' })}${btn('', { k: 'i', i: 'database-export' })}</div></div>`).join('')}
  <div class="card" style="padding:10px 12px"><div class="row" style="font-size:12.5px"><span class="dim" style="display:flex">${ic('circle-check', 16)}</span><span class="grow">5 andre maskiner er opdaterede eller understøttes ikke</span><span style="color:var(--pri);font-weight:600">Vis</span></div></div>
  <div class="row dim" style="font-size:11.5px;gap:6px;align-items:flex-start">${ic('info-circle', 14)}<span>Opdatér alt tager aldrig BIOS med.</span></div></div>`, '', false);

const SCREENS = [
  ['1-maskine-sektion', () => machineDesk(), () => machinePhone()],
  ['2-opdater-backup', () => machineDesk(modal(720, backupStep(0))), () => machinePhone(sheet(backupStep(1)))],
  ['3-opdater-kontrol-bekraeft', () => machineDesk(modal(1000, confirmStep(0))), () => machinePhone(sheet(confirmStep(1)))],
  ['4-opdater-forloeb', () => machineDesk(modal(680, progressStep(0)), 'running'), () => machinePhone(sheet(progressStep(1)))],
  ['5-efter-genstart-kontrol', () => machineDesk(modal(680, verifyStep(0))), () => machinePhone(sheet(verifyStep(1)))],
  ['6-sammenlign-gendan', () => machineDesk(modal(1040, compare(0))), () => machinePhone(sheet(compare(1)))],
  ['7-flaade-bios-fane', fleetDesk, fleetPhone],
];
module.exports = { SCREENS, css };
