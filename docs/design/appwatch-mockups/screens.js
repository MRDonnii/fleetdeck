// Skitser til siden "Apps": Fleetdeck holder øje med opdateringer til egne apps (GitHub-udgivelser).
// Command = liste med noter i siden. Aurora = kort med noter i et vindue. Alle navne, repoer og adresser er eksempler.
const L = require('../terminal-mockups/lib');
const { ic, badge, btn, dot } = L;
const APPS = [
  { n: 'Ventilation', r: 'ejer/ventilation', on: 'varme-pi', w: 'tjeneste ventilation', how: ['world', 'HTTP · version'], cur: '1.7.2', lat: '1.8.0', ch: 'Stabil', st: 'new', never: 1, i: 'wind' },
  { n: 'Mailfilter', r: 'ejer/mailfilter', on: 'byggeserver', w: 'container mailfilter', how: ['brand-docker', 'Docker-tag'], cur: '2.3.1', lat: '2.4.0-beta.3', ch: 'Beta', st: 'new', i: 'mail' },
  { n: 'Infoskærm', r: 'ejer/infoskaerm', on: 'minpi', w: 'tjeneste infoskaerm', how: ['file-text', 'Fil · /opt/infoskaerm/VERSION'], cur: '1.24.0', lat: '1.24.0', ch: 'Stabil', st: 'ok', i: 'device-tv' },
  { n: 'Kontrolpanel', r: 'ejer/kontrolpanel', on: 'mediecenter', w: 'container kontrolpanel', how: ['brand-docker', 'Docker-tag'], cur: '3.3.0', lat: '3.2.1', ch: 'Stabil', st: 'ahead', i: 'layout-dashboard' },
  { n: 'Kontrolpanel (pc)', r: 'ejer/kontrolpanel', on: 'ARBEJDS-PC', w: 'program', how: ['terminal', 'Kommando'], cur: '3.2.1', lat: '3.2.1', ch: 'Stabil', st: 'ok', i: 'device-desktop' },
  { n: 'Flasher', r: 'ejer/flasher', on: 'macbook-air', w: 'program', how: ['terminal', 'Kommando'], cur: null, lat: '0.9.4', ch: 'Stabil', st: 'err', err: 'Maskinen er offline', i: 'cpu' },
];
const ST = {
  new: ['Ny version', 'var(--warn)', 'arrow-up-circle'], ok: ['Opdateret', 'var(--ok)', 'circle-check'],
  ahead: ['Nyere end udgivelsen', 'var(--pri)', 'flask'], err: ['Kan ikke læse version', 'var(--bad)', 'alert-triangle'],
};
const NOTES = ['<b>Nyt</b>', '• Ugeplan for nattesænkning', '• Viser filtertid tilbage i dage', '<b>Rettet</b>', '• Bypass-spjæld blev stående efter genstart', '• Dansk tekst på fejlkoder E12–E15'];
// sidebaren med "Apps" som valgt punkt lige under Opdateringer
const side = () => L.sidebar().replace('class="nav on"', 'class="nav "').replace('Opdateringer</a>', 'Opdateringer</a><a class="nav on">' + ic('package', 17) + 'Apps</a>');
const stb = (a) => badge(ST[a.st][0], ST[a.st][1], ST[a.st][2]);
const chb = (a) => a.ch === 'Beta' ? badge('Beta', '#a855f7') : badge('Stabil', 'var(--dim)');
const ver = (a) => a.cur ? `<span class="mono">${a.cur}</span>` : '<span class="dim">–</span>';
const act = (a) => a.st === 'new' ? btn(a.never ? 'Opdatér…' : 'Opdatér', { k: a.never ? '' : 'p', i: 'player-play', z: 13, s: 'height:28px;padding:0 10px;font-size:12px' }) : '';
const never = badge('Aldrig automatisk', 'var(--warn)', 'lock');
const head = (m, sub) => `<div class="row" style="margin-bottom:${m ? 10 : 16}px;flex-wrap:wrap;gap:10px"><div class="grow"><h1>${m ? 'Apps' : 'Apps'}</h1>${sub ? `<div class="dim" style="margin-top:3px">${sub}</div>` : ''}</div>
  ${btn(m ? '' : 'Tjek nu', { i: 'refresh', k: m ? 'i' : '' })}${btn(m ? '' : 'Tilføj app', { k: 'p' + (m ? ' i' : ''), i: 'plus' })}</div>`;
const summary = () => `<div class="row" style="gap:10px;margin-bottom:14px">${[['2', 'nye versioner', 'var(--warn)'], ['2', 'opdateret', 'var(--ok)'], ['1', 'nyere end udgivelsen', 'var(--pri)'], ['1', 'kan ikke læses', 'var(--bad)']].map(([v, l, c]) => `<div class="card row" style="padding:10px 14px;gap:8px"><b style="font-size:20px;color:${c}">${v}</b><span class="dim">${l}</span></div>`).join('')}<span class="grow"></span><span class="dim" style="font-size:12px">${ic('brand-github', 14)}</span><span class="dim" style="font-size:12px">GitHub tjekket i dag 09.12 · 4.812 forespørgsler tilbage</span></div>`;
// ---------------------------------------------------------------- Command: liste + noter i siden
const table = () => `<div class="card" style="overflow:hidden"><div class="row" style="padding:9px 14px;font-size:12px;color:var(--dim);font-weight:600"><span style="flex:2.2">App</span><span style="flex:1.8">Kører på</span><span style="width:90px">Nu</span><span style="width:110px">Seneste</span><span style="width:170px">Status</span><span style="width:96px"></span></div>
  ${APPS.map((a, i) => `<div class="row" style="padding:10px 14px;border-top:1px solid var(--bd);font-size:13px;${i === 0 ? 'background:var(--priL)' : ''}">
    <span class="row" style="flex:2.2;gap:10px">${ic(a.i, 18)}<span class="col"><b class="tr">${a.n}</b><span class="mono dim tr" style="font-size:11.5px">${a.r}</span></span></span>
    <span class="col" style="flex:1.8"><span class="row" style="gap:6px">${dot(a.st !== 'err')}<span class="tr">${a.on}</span></span><span class="dim tr" style="font-size:11.5px">${a.w} · ${a.how[1].split(' ·')[0]}</span></span>
    <span style="width:90px">${ver(a)}</span><span class="row" style="width:110px;gap:5px"><span class="mono" style="font-weight:700">${a.lat}</span></span>
    <span class="col" style="width:170px;gap:3px;align-items:flex-start">${stb(a)}${a.never ? never : a.err ? `<span style="font-size:11px;color:var(--bad)">${a.err}</span>` : ''}</span>
    <span style="width:96px;display:flex;justify-content:flex-end">${act(a)}</span></div>`).join('')}</div>`;
const notes = (m) => `<div class="card col" style="padding:16px;gap:10px;${m ? '' : 'width:380px;flex:none'}">
  <div class="row">${ic('wind', 20)}<h2 class="grow">Ventilation 1.8.0</h2>${badge('Stabil', 'var(--dim)')}</div>
  <div class="dim" style="font-size:12.5px">Udgivet 17. nov. · <span class="mono">ejer/ventilation</span> · kører 1.7.2 på varme-pi</div>
  <div class="col" style="gap:3px;font-size:13px;line-height:1.5;padding:10px 12px;background:var(--chip);border-radius:var(--r)">${NOTES.map(l => `<span>${l}</span>`).join('')}</div>
  <div class="warnbox">${ic('lock', 17)}<span><b>Aldrig automatisk.</b> Appen styrer et anlæg. Fleetdeck opdaterer den kun, når du selv trykker og bekræfter.</span></div>
  <div class="col" style="gap:4px"><b style="font-size:12.5px">Opdatér-kommando (kører på varme-pi)</b><span class="inp mono" style="height:auto;padding:8px 10px;white-space:pre;line-height:1.5">cd /opt/ventilation &amp;&amp; ./opdater.sh {version}</span></div>
  <div class="row" style="justify-content:flex-end;gap:8px">${btn('Se på GitHub', { i: 'external-link', k: 'g' })}${btn('Opdatér til 1.8.0…', { k: 'p', i: 'player-play' })}</div></div>`;
const cmdDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0, 'Dine egne apps: Fleetdeck spørger GitHub om nye udgivelser og sammenligner med den version, der kører.')}${summary()}
  <div class="row" style="align-items:flex-start;gap:16px"><div class="grow">${table()}</div>${notes(0)}</div></div></div></div>`;
const mcard = (a) => `<div class="card" style="padding:11px 12px"><div class="row" style="gap:9px">${ic(a.i, 18)}<span class="col grow"><b class="tr">${a.n}</b><span class="dim tr" style="font-size:11.5px">${a.on} · ${a.how[1].split(' ·')[0]}</span></span>${stb(a)}</div>
  <div class="row" style="margin-top:8px;gap:8px"><span class="mono grow" style="font-size:12.5px">${a.cur ?? '–'} ${a.st === 'new' ? `→ <b>${a.lat}</b>` : ''}</span>${a.never ? ic('lock', 14) : ''}${act(a)}</div>${a.err ? `<div style="font-size:11.5px;color:var(--bad);margin-top:4px">${a.err}</div>` : ''}</div>`;
const sheet = () => `<div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;padding:18px">
  <div class="row" style="margin-bottom:10px">${ic('wind', 20)}<h2 class="grow">Opdatér Ventilation?</h2></div>
  <div class="col" style="gap:10px;font-size:13.5px"><span>1.7.2 → <b>1.8.0</b> på varme-pi. Fleetdeck kører din kommando som et job:</span>
  <span class="inp mono" style="height:auto;padding:8px 10px;white-space:pre-wrap;line-height:1.5">cd /opt/ventilation &amp;&amp; ./opdater.sh 1.8.0</span>
  <div class="warnbox">${ic('lock', 17)}<span><b>Aldrig automatisk.</b> Appen styrer et anlæg – tjek, at det er et godt tidspunkt.</span></div>
  <div class="col" style="gap:3px;font-size:12.5px;padding:9px 11px;background:var(--chip);border-radius:var(--r)">${NOTES.slice(0, 3).map(l => `<span>${l}</span>`).join('')}</div></div>
  <div class="col" style="gap:8px;margin-top:14px">${btn('Opdatér til 1.8.0', { k: 'p', i: 'player-play', s: 'height:42px' })}${btn('Annullér', { s: 'height:42px' })}</div></div></div>`;
const tabs = () => L.tabbar().replace(/<a class="on">/g, '<a class="">').replace(/<a class="">(<svg[^]*?<\/svg>)Mere<\/a>/, '<a class="on">$1Mere</a>');
const cmdIphone = () => `<div class="app"><div class="main">${L.mhead('Apps', '')}<div class="col grow" style="overflow:hidden;padding:12px;gap:8px">${head(1)}
  <div class="row" style="gap:6px;flex-wrap:wrap">${badge('2 nye', 'var(--warn)')}${badge('2 opdateret', 'var(--ok)')}${badge('1 nyere', 'var(--pri)')}${badge('1 fejl', 'var(--bad)')}</div>${APPS.map(mcard).join('')}</div>${tabs()}</div>${sheet()}</div>`;
// ---------------------------------------------------------------- Aurora: kort i et gitter + noter i et vindue
const acard = (a) => `<div class="card col" style="padding:16px;gap:10px;${a.st === 'new' ? 'border-color:color-mix(in srgb,var(--warn) 50%,transparent)' : ''}">
  <div class="row" style="gap:10px"><span style="width:38px;height:38px;border-radius:11px;display:grid;place-items:center;background:var(--priL);color:var(--pri)">${ic(a.i, 20)}</span><span class="col grow"><b class="tr" style="font-size:14.5px">${a.n}</b><span class="mono dim tr" style="font-size:11.5px">${a.r}</span></span>${chb(a)}</div>
  <div class="row" style="gap:16px"><span class="col"><span class="dim" style="font-size:11px">Kører</span><b class="mono" style="font-size:18px">${a.cur ?? '–'}</b></span>${ic('arrow-right', 16)}<span class="col"><span class="dim" style="font-size:11px">Seneste</span><b class="mono" style="font-size:18px;color:${a.st === 'new' ? 'var(--warn)' : 'var(--tx)'}">${a.lat}</b></span><span class="grow"></span>${stb(a)}</div>
  <div class="row dim" style="font-size:12px;gap:6px">${dot(a.st !== 'err')}<span class="tr grow">${a.on} · ${a.w}</span>${ic(a.how[0], 13)}<span class="tr">${a.how[1].split(' ·')[0]}</span></div>
  <div class="row" style="gap:8px;min-height:28px">${a.never ? never : a.err ? `<span style="font-size:12px;color:var(--bad)">${a.err}</span>` : ''}<span class="grow"></span>${a.st === 'new' ? btn('Noter', { k: 'g', i: 'notes', s: 'height:28px;font-size:12px' }) : ''}${act(a)}</div></div>`;
const aDialog = (m) => `<div class="row" style="margin-bottom:12px">${ic('brand-docker', 22)}<h2 class="grow">Mailfilter 2.4.0-beta.3</h2>${badge('Beta', '#a855f7')}${m ? '' : btn('', { k: 'g i', i: 'x' })}</div>
  <div class="col" style="gap:10px;font-size:13.5px"><span class="dim" style="font-size:12.5px">Udgivet i går · kører 2.3.1 i containeren mailfilter på byggeserver</span>
  <div class="col" style="gap:3px;line-height:1.5;padding:10px 12px;background:var(--chip);border-radius:var(--r)"><b>Nyt</b><span>• Lærer af mapper, du flytter mails til</span><span>• Ugentlig rapport som besked</span><b>Rettet</b><span>• Store vedhæftninger fik tidsfrist</span></div>
  <div class="col" style="gap:4px"><b style="font-size:12.5px">Opdatér-kommando</b><span class="inp mono" style="height:auto;padding:8px 10px;white-space:pre-wrap;line-height:1.5">docker pull ghcr.io/ejer/mailfilter:2.4.0-beta.3 &amp;&amp; docker compose -f /srv/mailfilter/compose.yml up -d</span></div>
  <label class="row" style="gap:8px;font-size:13px"><span style="width:34px;height:20px;border-radius:99px;background:var(--pri);position:relative;display:inline-block"><i style="position:absolute;right:2px;top:2px;width:16px;height:16px;border-radius:50%;background:#fff"></i></span>Opdatér automatisk om natten (kl. 3)</label></div>
  <div class="row" style="justify-content:flex-end;margin-top:16px;gap:8px;${m ? 'flex-direction:column-reverse;align-items:stretch' : ''}">${btn('Annullér')}${btn('Opdatér nu', { k: 'p', i: 'player-play' })}</div>`;
const auroraDesktop = () => `<div class="app">${side()}<div class="main">${L.header()}<div class="col grow" style="overflow:hidden;padding:22px">${head(0, 'Dine egne apps fra GitHub – nye udgivelser, den kørende version og en knap til at opdatere.')}${summary()}
  <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:14px">${APPS.map(acard).join('')}</div></div></div>
  <div class="ov"><div class="modal" style="max-width:620px">${aDialog(0)}</div></div></div>`;
const auroraIphone = () => `<div class="app"><div class="main">${L.mhead('Apps', btn('', { k: 'p i', i: 'plus' }))}<div class="col grow" style="overflow:hidden;padding:12px;gap:10px">${APPS.slice(0, 3).map(acard).join('')}</div>${tabs()}</div>
  <div class="ov" style="padding:0;align-items:flex-end"><div class="modal" style="max-width:100%;border-radius:16px 16px 0 0;max-height:94%;overflow:hidden">${aDialog(1)}</div></div></div>`;
module.exports = { command: { desktop: cmdDesktop, iphone: cmdIphone }, aurora: { desktop: auroraDesktop, iphone: auroraIphone } };
