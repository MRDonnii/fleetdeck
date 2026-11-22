// Skitser til punkt 14: tekstsvar fra pc'en, "Send logfiler" (brugeren) og "Hent logfiler" (admin) i Beskeder.
// Alle navne og adresser er opdigtede (RFC 5737). Afsendernavnet "IT-support" er en indstilling.
const L = require('../terminal-mockups/lib');
const { ic, dot, badge, btn, header, mhead } = L;
const mark = (html, label) => html.split('</a>').map(a => a.endsWith(label) ? a.replace(/class="(nav|)( on)? ?"/, 'class="nav on"') : a.replace(/class="nav on"/, 'class="nav"')).join('</a>');
const NAVX = (label) => mark(L.sidebar().replace('<div class="grp">Drift</div>', `<a class="nav ">${ic('messages', 17)}Beskeder<span class="badge" style="margin-left:auto;background:var(--pri);color:#fff">2</span></a><div class="grp">Drift</div>`), label);
const tab = (label) => L.tabbar().split('</a>').map(a => a.endsWith(label) ? a.replace(/<a class="[^"]*">/, '<a class="on">') : a.replace('<a class="on">', '<a class="">')).join('</a>');
const wrap = (dev, inner, o = {}) => dev === 'm'
  ? `<div class="app"><div class="main">${mhead(o.title || 'Beskeder', o.right || '')}<div class="col grow" style="overflow:hidden">${inner}</div>${tab('Mere')}</div></div>`
  : `<div class="app">${NAVX(o.nav || 'Beskeder')}<div class="main">${header()}<div class="col grow" style="overflow:hidden">${inner}</div></div></div>`;

const copy = `<span style="opacity:.75;display:inline-flex">${ic('copy', 13)}</span>`;
const bubble = (me, who, when, text, extra = '') => `<div style="display:flex;justify-content:${me ? 'flex-end' : 'flex-start'};margin:8px 0"><div style="max-width:78%"><div class="dim" style="font-size:11.5px;margin:0 4px 3px;display:flex;align-items:center;gap:4px;justify-content:${me ? 'flex-end' : 'flex-start'}">${who} · ${when}${copy}</div>${text ? `<div style="padding:9px 12px;border-radius:12px;${me ? 'background:var(--pri);color:#fff;border-bottom-right-radius:4px' : 'background:var(--chip);border:1px solid var(--bd);border-bottom-left-radius:4px'};font-size:13px;line-height:1.45">${text}</div>` : ''}${extra}</div></div>`;
const file = (size, what, right) => `<div style="display:flex;justify-content:${right ? 'flex-end' : 'flex-start'};margin-top:6px"><div class="card" style="display:flex;align-items:center;gap:10px;padding:7px 10px;border-radius:10px">${ic('file-zip', 22)}<div><b style="font-size:13px">Logfiler · ${size}</b><div class="dim" style="font-size:11.5px">${what}</div></div>${btn('Hent', { i: 'download', s: 'height:26px;font-size:12px' })}</div></div>`;
const fetched = (dev) => `<div style="margin:10px 0;text-align:center"><div class="dim" style="font-size:11.5px">Logfiler hentet fra maskinen (admin) · 10.47</div><div style="display:flex;justify-content:center">${file('288 kB', 'agent-log, haendelseslog, systeminfo')}</div></div>`;
const thread = (dev) => `${bubble(false, 'anna', '10.31', 'Programmet fryser, når jeg gemmer.')}
  ${bubble(true, 'IT-support (admin)', '10.38', 'Jeg kigger på det nu. Send gerne logfilerne.', `<div class="dim" style="font-size:11px;text-align:right;margin-top:3px">${ic('checks', 13)} Læst 10.39</div>`)}
  ${bubble(false, 'anna', '10.42', 'Her er de. Det skete lige før kl. 10.30.', file('312 kB', 'agent-log, haendelseslog, systeminfo'))}
  ${fetched(dev)}
  ${bubble(true, 'IT-support (admin)', '10.49', 'Tak. Driveren til printeren er gammel – jeg opdaterer den nu.')}`;
const compose = (dev) => `<div style="border-top:1px solid var(--bd);padding:${dev === 'm' ? 10 : 12}px ${dev === 'm' ? 10 : 16}px;background:var(--card)"><div class="row" style="align-items:flex-end"><span class="inp grow" style="height:${dev === 'm' ? 40 : 44}px"><em class="dim">Skriv en besked til ARBEJDS-PC…</em></span>${btn(dev === 'm' ? '' : 'Send', { k: 'p', i: 'send' })}</div><div class="dim" style="font-size:11.5px;margin-top:6px">Brugeren kan også svare med tekst, et skærmbillede og logfiler.</div></div>`;
const head = (dev) => `<div class="row" style="padding:10px 16px;border-bottom:1px solid var(--bd);gap:8px">${badge('Åben', 'var(--ok)')}<span style="flex:1"></span>${btn(dev === 'm' ? 'Hent logfiler' : 'Hent logfiler', { i: 'file-zip', s: 'height:30px;font-size:12.5px' })}${dev === 'm' ? '' : btn('Luk sagen', { i: 'archive', s: 'height:30px;font-size:12.5px' })}</div>`;

const S = {
  maskine: (dev) => wrap(dev, `<div style="padding:22px;overflow:hidden" class="col grow"><div class="row" style="margin-bottom:12px">${ic('device-desktop', 22)}<h1 class="grow">ARBEJDS-PC</h1>${badge('Online', 'var(--ok)')}</div><div class="row" style="gap:18px;border-bottom:1px solid var(--bd);margin-bottom:16px;padding-bottom:8px;overflow:hidden">${['Overblik', 'Helbred', 'Profil', 'Software og spil', 'Opdateringer', 'Jobs', 'Beskeder'].map(t => `<b style="font-weight:${t === 'Beskeder' ? 700 : 500};color:${t === 'Beskeder' ? 'var(--pri)' : 'var(--dim)'}">${t}</b>`).join('')}</div><div class="card col grow" style="overflow:hidden;max-width:980px">${head(dev)}<div class="grow" style="padding:6px 18px;overflow:hidden">${thread(dev)}</div>${compose(dev)}</div></div>`, { nav: 'Maskiner' }),
  traad: (dev) => wrap(dev, `<div class="col grow" style="overflow:hidden">${head(dev)}<div class="grow" style="padding:4px 12px;overflow:hidden">${thread(dev)}</div>${compose(dev)}</div>`, { title: 'ARBEJDS-PC', right: btn('', { i: 'archive', k: 'g' }) }),
  pc: () => {
    // vinduet på pc'en (Windows 11-agtigt): markøren står i svarfeltet, Enter sender, linjen under viser status
    const T = { bg: '#f3f3f3', card: '#ffffff', tx: '#1b1b1b', mu: '#5e5e5e', bd: '#e3e3e3', ac: '#0f6cbd' };
    const b = (t, p) => `<span style="display:inline-flex;align-items:center;height:32px;padding:0 14px;border-radius:5px;font:600 13px 'Segoe UI',Inter;${p ? `background:${T.ac};color:#fff` : `background:#fbfbfb;border:1px solid ${T.bd};color:${T.tx};font-weight:400`}">${t}</span>`;
    const msg = (me, who, t) => `<div style="margin:6px 0"><div style="font-size:11px;color:${T.mu}">${who}</div><div style="font-size:13px;${me ? '' : 'font-weight:600'}">${t}</div></div>`;
    return `<div class="app" style="background:linear-gradient(160deg,#1e3a8a,#0e7490 60%,#134e4a);font-family:'Segoe UI',Inter">
      <div style="position:absolute;left:50%;top:50px;transform:translateX(-50%);width:470px;background:${T.bg};border-radius:9px;box-shadow:0 30px 80px rgba(0,0,0,.45);color:${T.tx};overflow:hidden">
        <div style="display:flex;align-items:center;gap:8px;height:34px;padding:0 12px;font-size:12px">${L.logo(16)}Fleetdeck<span style="flex:1"></span>✕</div>
        <div style="padding:8px 22px 20px"><div style="font-size:20px;font-weight:600">Besked fra IT-support</div><div style="color:${T.mu};font-size:13px;margin-bottom:12px">Svar herunder. Svaret går direkte til IT-support.</div>
        <div style="background:${T.card};border-radius:8px;padding:8px 12px">${msg(false, 'IT-support · 10.38', 'Jeg kigger på det nu. Send gerne logfilerne.')}${msg(true, 'Dig · 10.42 · logfiler', 'Her er de. Det skete lige før kl. 10.30.')}</div>
        <div style="margin-top:12px;height:84px;border:1px solid ${T.bd};border-bottom:2px solid ${T.ac};border-radius:5px;background:#fff;padding:8px 10px;font-size:13px;color:${T.tx}">Den fryser stadig, når jeg trykker Gem<span style="display:inline-block;width:1px;height:15px;background:${T.tx};vertical-align:-3px;margin-left:1px"></span></div>
        <div style="font-size:11.5px;color:${T.mu};margin:5px 2px 10px">${ic('check', 12)} Sendt kl. 10.42 (med logfiler). Enter sender, Skift+Enter giver ny linje.</div>
        <label style="display:flex;align-items:center;gap:8px;margin:0 0 14px;font-size:13px"><span style="width:18px;height:18px;border-radius:4px;border:1px solid ${T.mu}"></span>Vedhæft et skærmbillede (tages, når du trykker Send)</label>
        <div style="display:flex;gap:8px;justify-content:flex-end;flex-wrap:wrap">${b('Send logfiler')}<span style="flex:1"></span>${b('Luk')}${b('Send svar', 1)}</div></div></div>
      <div style="position:absolute;right:16px;bottom:56px;width:250px;background:#f9f9f9;border-radius:8px;box-shadow:0 12px 40px rgba(0,0,0,.4);padding:6px;font-size:13px;color:${T.tx}">${['Fleetdeck · ARBEJDS-PC · forbundet', 'Åbn status…', 'Skriv til support…', 'Beskeder', 'Opdatér alt nu'].map((t, i) => `<div style="padding:7px 10px;border-radius:5px;${i === 0 ? `color:${T.mu};font-size:12px` : ''}${i === 2 ? 'background:#e8e8e8' : ''}">${t}</div>`).join('')}</div>
      <div style="position:absolute;left:0;right:0;bottom:0;height:48px;background:rgba(32,32,32,.85);display:flex;align-items:center;justify-content:flex-end;padding:0 14px;gap:10px;color:#fff;font-size:12px">${L.logo(20)}<span>10.43</span></div></div>`;
  },
};
module.exports = { screens: S, DESKTOP: ['maskine', 'pc'], PHONE: ['traad'] };
