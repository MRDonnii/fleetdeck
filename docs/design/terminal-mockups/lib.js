// Fælles byggesten til Terminal-skitserne: tema, ikoner, eksempeldata og små komponenter.
// Alle værter, adresser (RFC 5737) og fingeraftryk er opdigtede.
const fs = require('fs'), path = require('path');
const NM = process.env.FD_NODE_MODULES || require('path').join(__dirname, '../../../web/node_modules');
const ic = (n, s = 16, sw = 1.8) => {
  const base = path.join(NM, '@tabler/icons/icons');
  const f = n.endsWith('-filled') ? path.join(base, 'filled', n.replace(/-filled$/, '') + '.svg') : path.join(base, 'outline', n + '.svg');
  const b = fs.readFileSync(f, 'utf8').replace(/<!--[\s\S]*?-->/g, '').replace(/\n\s*/g, ' ');
  return b.replace(/width="24"/, `width="${s}"`).replace(/height="24"/, `height="${s}"`).replace(/stroke-width="2"/, `stroke-width="${sw}"`);
};
const THEMES = {
  A: { name: 'command-dark', body: '#0b0b0d', side: '#0f0f12', card: '#141417', border: '#232328', chip: '#1c1c21', text: '#e4e4e7', dim: '#8b8b95', pri: '#3270f0', priL: 'rgba(50,112,240,.16)', ok: '#22c55e', warn: '#f59e0b', bad: '#ef4444', off: '#52525b', r: 8, bg: '#0b0b0d', grad: 'linear-gradient(135deg,#3270f0,#1854d1)', la: '#3b82f6', lb: '#1d4ed8' },
  B: { name: 'aurora-dark', body: '#070b16', side: 'rgba(15,20,36,.72)', card: 'rgba(22,28,48,.74)', border: 'rgba(255,255,255,.08)', chip: 'rgba(255,255,255,.06)', text: '#e6e9f5', dim: '#8f97b5', pri: '#5f63ee', priL: 'rgba(95,99,238,.2)', ok: '#22c55e', warn: '#f59e0b', bad: '#ef4444', off: '#565d7a', r: 14, bg: 'radial-gradient(900px 520px at 88% -8%,rgba(56,189,248,.16),transparent 60%),radial-gradient(820px 520px at 8% 108%,rgba(129,140,248,.18),transparent 60%),#070b16', grad: 'linear-gradient(135deg,#22d3ee,#6366f1)', la: '#22d3ee', lb: '#6366f1' },
  C: { name: 'command-light', body: '#f7f7f8', side: '#fbfbfc', card: '#ffffff', border: '#e4e4e7', chip: '#f4f4f5', text: '#18181b', dim: '#71717a', pri: '#2563eb', priL: 'rgba(37,99,235,.1)', ok: '#16a34a', warn: '#d97706', bad: '#dc2626', off: '#a1a1aa', r: 8, bg: '#f7f7f8', grad: 'linear-gradient(135deg,#2563eb,#1854d1)', la: '#3b82f6', lb: '#1d4ed8' },
};
const HOSTS = [
  { n: 'mediecenter', os: 'Ubuntu 24.04', i: 'brand-ubuntu', via: 'Agent', g: 'Servere', on: 1, fav: 1, who: 'root', ip: '192.0.2.21' },
  { n: 'byggeserver', os: 'Debian 12', i: 'brand-debian', via: 'Agent', g: 'Servere', on: 1, fav: 1, who: 'root', ip: '192.0.2.22' },
  { n: 'varme-pi', os: 'Raspberry Pi OS', i: 'cpu', via: 'Agent', g: 'Servere', on: 1, prot: 1, who: 'root', ip: '192.0.2.23' },
  { n: 'STUE-PC', os: 'Windows 11', i: 'brand-windows', via: 'Agent', g: 'Computere', on: 1, fav: 1, who: 'SYSTEM', ip: '192.0.2.31' },
  { n: 'KONTOR-LAPTOP', os: 'Windows 11', i: 'brand-windows', via: 'Agent', g: 'Computere', on: 1, who: 'SYSTEM', ip: '192.0.2.32' },
  { n: 'macbook-air', os: 'macOS 26', i: 'brand-apple', via: 'Agent', g: 'Computere', on: 0, who: 'root', ip: '192.0.2.33' },
  { n: 'router-hoved', os: 'OpenWrt', i: 'router', via: 'SSH', g: 'Netværk', on: 1, fav: 1, who: 'root', ip: '198.51.100.1', cred: 'Netværk-nøgle' },
  { n: 'switch-kaelder', os: 'Switch', i: 'network', via: 'SSH', g: 'Netværk', on: 1, who: 'admin', ip: '198.51.100.2', cred: 'Switch-login' },
  { n: 'nas-01', os: 'NAS', i: 'server-2', via: 'SSH', g: 'Netværk', on: 0, who: 'admin', ip: '198.51.100.5', cred: 'Netværk-nøgle' },
];
const SNIPS = [
  { n: 'Diskplads', c: 'df -h', s: 'Linux, Mac', i: 'database' },
  { n: 'Seneste fejl i loggen', c: 'journalctl -p err -n {{antal}} --no-pager', s: 'Linux', i: 'file-alert', v: 1 },
  { n: 'Genstart tjeneste', c: 'systemctl restart {{tjeneste}}', s: 'Linux', i: 'refresh', v: 1, risk: 1 },
  { n: 'Docker: kørende containere', c: 'docker ps --format "table {{.Names}}\\t{{.Status}}"', s: 'Gruppe: Servere', i: 'brand-docker' },
  { n: 'Tjenester der er stoppet', c: 'Get-Service | Where Status -eq Stopped', s: 'Windows', i: 'brand-windows' },
  { n: 'Genstart maskinen', c: 'shutdown -r +1', s: 'Alle', i: 'power', risk: 1 },
];
const CREDS = [
  { n: 'Netværk-nøgle', t: 'SSH-nøgle · ed25519', u: 'root', h: 2, last: 'I dag 09.14', i: 'key', fp: 'SHA256:eksempelFingeraftryk1aB' },
  { n: 'Switch-login', t: 'Adgangskode', u: 'admin', h: 1, last: 'I går 18.02', i: 'password', warn: 1 },
  { n: 'Backup-nøgle', t: 'SSH-nøgle · ed25519 · med løsen', u: 'backup', h: 0, last: 'Aldrig brugt', i: 'key', fp: 'SHA256:eksempelFingeraftryk2cD' },
];
const c = { p: '#7dd3fc', g: '#86efac', y: '#fcd34d', r: '#fca5a5', d: '#7c8496', w: '#e5e7eb', b: '#93c5fd' };
const sp = (col, t) => `<span style="color:${col}">${t}</span>`;
const prompt = (u, h, d = '~') => sp(c.g, `${u}@${h}`) + sp(c.d, ':') + sp(c.b, d) + sp(c.w, '# ');
const TERM = {
  linux: [prompt('root', 'mediecenter') + sp(c.w, 'df -h'), sp(c.d, 'Filsystem      Str.  Brugt  Fri  Brug%  Monteret på'), '/dev/nvme0n1p2  468G   212G 232G    48% /', '/dev/sda1      3,6T   2,9T 540G    ' + sp(c.y, '85%') + ' /srv/medier', 'tmpfs          7,8G   1,1M 7,8G     1% /run', '', prompt('root', 'mediecenter') + sp(c.w, 'systemctl status nginx --no-pager'), sp(c.g, '●') + ' nginx.service - A high performance web server', '     Loaded: loaded (/usr/lib/systemd/system/nginx.service; enabled)', '     Active: ' + sp(c.g, 'active (running)') + ' siden man 09.02; 3 dage siden', '   Main PID: 1184 (nginx)', '      Tasks: 5 (limit: 18944)', '     Memory: 12.4M', '', prompt('root', 'mediecenter') + '<span class="cur"></span>'],
  router: [sp(c.d, 'BusyBox v1.36 built-in shell (ash)'), '', prompt('root', 'router-hoved') + sp(c.w, 'uptime'), ' 09:14:22 up 41 days,  3:07,  load average: 0.08, 0.11, 0.09', prompt('root', 'router-hoved') + sp(c.w, 'ip -br addr'), 'lo      UNKNOWN  127.0.0.1/8', 'br-lan  UP       198.51.100.1/24', 'wan     UP       203.0.113.40/24', '', prompt('root', 'router-hoved') + '<span class="cur"></span>'],
  win: [sp(c.d, 'PowerShell 7.5'), sp(c.b, 'PS C:\\Windows\\system32> ') + sp(c.w, 'Get-Volume C | ft -a'), '', sp(c.d, 'DriveLetter FileSystem SizeRemaining   Size'), 'C           NTFS            212 GB  476 GB', '', sp(c.b, 'PS C:\\Windows\\system32> ') + '<span class="cur"></span>'],
};
const term = (k = 'linux', fs = 13) => `<div class="term" style="font-size:${fs}px">${TERM[k].map(l => `<div>${l || '&nbsp;'}</div>`).join('')}</div>`;
const dot = (on) => `<i class="dot" style="background:${on ? 'var(--ok)' : 'var(--off)'}"></i>`;
const badge = (t, col = 'var(--dim)', ico) => `<span class="badge" style="color:${col};background:color-mix(in srgb,${col} 14%,transparent)">${ico ? ic(ico, 12) : ''}${t}</span>`;
const via = (h) => h.via === 'Agent' ? badge('Agent', 'var(--pri)', 'plug-connected') : badge('SSH', '#14b8a6', 'key');
const prot = badge('Beskyttet', 'var(--warn)', 'shield-lock');
const btn = (t, o = {}) => `<button class="btn ${o.k || ''}" style="${o.s || ''}">${o.i ? ic(o.i, o.z || 15) : ''}${t ? `<span>${t}</span>` : ''}</button>`;
const field = (l, v, o = {}) => `<label class="field" style="${o.s || ''}"><b>${l}</b><span class="inp ${o.mono ? 'mono' : ''}">${o.i ? ic(o.i, 15) : ''}<em>${v}</em>${o.r || ''}</span>${o.h ? `<small>${o.h}</small>` : ''}</label>`;
const logo = (s = 30) => `<svg width="${s}" height="${s}" viewBox="0 0 32 32"><defs><linearGradient id="lg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="var(--la)"/><stop offset="1" stop-color="var(--lb)"/></linearGradient></defs><rect width="32" height="32" rx="9" fill="url(#lg)"/><g stroke="#fff" stroke-width="2.6" stroke-linecap="round"><path d="M8 10h16M8 16h11M8 22h6"/></g><circle cx="23.5" cy="21.5" r="2.6" fill="#fff"/></svg>`;
const NAV = [['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['terminal-2', 'Terminal'], ['download', 'Opdateringer'], ['activity-heartbeat', 'Helbred'], ['apps', 'Programmer'], ['adjustments', 'Profiler']];
const NAV2 = [['heart-rate-monitor', 'Overvågning'], ['list-check', 'Jobs'], ['server-2', 'Servere & Docker'], ['code', 'Scripts'], ['clipboard-list', 'Revisionslog'], ['settings', 'Indstillinger']];
const sidebar = () => `<aside class="side"><div class="brand">${logo()}<b>fleetdeck</b></div>${NAV.map(([i, l]) => `<a class="nav ${l === 'Terminal' ? 'on' : ''}">${ic(i, 17)}${l}</a>`).join('')}<div class="grp">Drift</div>${NAV2.map(([i, l]) => `<a class="nav">${ic(i, 17)}${l}</a>`).join('')}</aside>`;
const rail = () => `<aside class="rail">${logo(30)}${[...NAV, ...NAV2.slice(1, 2), ...NAV2.slice(-1)].map(([i, l]) => `<a class="ri ${l === 'Terminal' ? 'on' : ''}">${ic(i, 19)}</a>`).join('')}</aside>`;
const header = () => `<header class="top"><span class="search">${ic('search', 15)}Søg efter maskiner, programmer, sider…<kbd>Ctrl K</kbd></span><span style="flex:1"></span>${badge('2 sessioner', 'var(--pri)', 'terminal-2')}<span class="av">AD</span></header>`;
const mhead = (t, r = '') => `<header class="mh">${logo(26)}<b>${t}</b><span style="flex:1"></span>${r}</header>`;
const tabbar = () => `<nav class="tabbar">${[['layout-dashboard', 'Oversigt'], ['device-desktop', 'Maskiner'], ['terminal-2', 'Terminal'], ['activity-heartbeat', 'Helbred'], ['menu-2', 'Mere']].map(([i, l]) => `<a class="${l === 'Terminal' ? 'on' : ''}">${ic(i, 20)}${l}</a>`).join('')}</nav>`;
const keys = ['Esc', 'Tab', 'Ctrl', 'Alt', '↑', '↓', '←', '→', '|', '/', '~', '-'];
const keyrow = (extra = '') => `<div class="keyrow">${extra}${keys.map(k => `<span class="${k === 'Ctrl' ? 'on' : ''}">${k}</span>`).join('')}</div>`;
const iosKb = () => `<div class="ioskb">${['qwertyuiopå', 'asdfghjklæø', 'zxcvbnm'].map(r => `<div>${[...r].map(k => `<span>${k}</span>`).join('')}</div>`).join('')}<div><span class="w">123</span><span class="sp">mellemrum</span><span class="w">retur</span></div></div>`;
const css = (t) => `
@font-face{font-family:Inter;src:url(file://${NM}/@fontsource-variable/inter/files/inter-latin-wght-normal.woff2);font-weight:100 900}
@font-face{font-family:Inter;src:url(file://${NM}/@fontsource-variable/inter/files/inter-latin-ext-wght-normal.woff2);font-weight:100 900;unicode-range:U+0100-02AF}
:root{--body:${t.body};--side:${t.side};--card:${t.card};--bd:${t.border};--chip:${t.chip};--tx:${t.text};--dim:${t.dim};--pri:${t.pri};--priL:${t.priL};--ok:${t.ok};--warn:${t.warn};--bad:${t.bad};--off:${t.off};--r:${t.r}px;--grad:${t.grad};--la:${t.la};--lb:${t.lb}}
*{box-sizing:border-box}html,body{margin:0;height:100%;overflow:hidden}body{font:13.5px/1.4 Inter,system-ui,sans-serif;color:var(--tx);background:${t.bg};-webkit-font-smoothing:antialiased}
svg{flex:none;display:block}.app{display:flex;height:100vh;width:100vw;position:relative}.main{flex:1;min-width:0;display:flex;flex-direction:column;min-height:0}
.side{width:236px;flex:none;background:var(--side);border-right:1px solid var(--bd);padding:12px}.brand{display:flex;gap:10px;align-items:center;padding:4px 8px 14px;font-size:17px}.brand b{font-weight:800;letter-spacing:-.02em}
.nav{display:flex;gap:10px;align-items:center;padding:8px 10px;border-radius:8px;color:var(--dim);font-weight:500}.nav.on{background:var(--priL);color:var(--tx);font-weight:600}.nav.on svg{color:var(--pri)}.grp{font-size:11px;font-weight:600;color:var(--dim);text-transform:uppercase;letter-spacing:.06em;padding:14px 10px 6px}
.rail{width:60px;flex:none;background:var(--side);border-right:1px solid var(--bd);display:flex;flex-direction:column;align-items:center;gap:6px;padding:12px 0}.ri{width:40px;height:38px;display:grid;place-items:center;border-radius:8px;color:var(--dim)}.ri.on{background:var(--priL);color:var(--pri)}.rail svg:first-child{margin-bottom:4px}
.top{height:60px;flex:none;display:flex;align-items:center;gap:12px;padding:0 20px;border-bottom:1px solid var(--bd)}.search{display:flex;gap:8px;align-items:center;width:380px;height:36px;padding:0 10px;border:1px solid var(--bd);border-radius:var(--r);color:var(--dim);background:var(--card)}.search kbd{margin-left:auto;font:600 10.5px Inter;border:1px solid var(--bd);padding:1px 6px;border-radius:5px}.av{width:34px;height:34px;border-radius:50%;display:grid;place-items:center;background:var(--priL);color:var(--pri);font-weight:700;font-size:12px}
.mh{height:52px;flex:none;display:flex;gap:9px;align-items:center;padding:0 14px;border-bottom:1px solid var(--bd);font-size:16px}.tabbar{flex:none;display:grid;grid-template-columns:repeat(5,1fr);padding:6px 4px 18px;border-top:1px solid var(--bd);background:var(--side)}.tabbar a{display:flex;flex-direction:column;align-items:center;gap:2px;font-size:10.5px;color:var(--dim);font-weight:500}.tabbar a.on{color:var(--pri);font-weight:600}
.card{background:var(--card);border:1px solid var(--bd);border-radius:var(--r)}.dot{width:8px;height:8px;border-radius:50%;flex:none;display:inline-block}.badge{display:inline-flex;align-items:center;gap:4px;font-size:10.5px;font-weight:650;padding:2px 7px;border-radius:5px;white-space:nowrap}
.btn{display:inline-flex;align-items:center;justify-content:center;gap:7px;height:34px;padding:0 13px;border-radius:var(--r);border:1px solid var(--bd);background:var(--card);color:var(--tx);font:600 13px Inter;white-space:nowrap}.btn.p{background:var(--grad);border:0;color:#fff}.btn.d{background:var(--bad);border:0;color:#fff}.btn.g{background:transparent;border-color:transparent;color:var(--dim)}.btn.i{width:34px;padding:0}
.field{display:flex;flex-direction:column;gap:5px;min-width:0}.field b{font-size:12.5px;font-weight:600}.field small{color:var(--dim);font-size:11.5px}.inp{display:flex;align-items:center;gap:8px;height:36px;padding:0 10px;border:1px solid var(--bd);border-radius:var(--r);background:color-mix(in srgb,var(--chip) 60%,transparent);color:var(--tx);min-width:0}.inp em{font-style:normal;flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.inp svg{color:var(--dim)}.mono{font-family:"JetBrains Mono","DejaVu Sans Mono",monospace;font-size:12px}
.term{background:#0a0c10;color:#d4d8e0;font-family:"JetBrains Mono","DejaVu Sans Mono",monospace;line-height:1.5;padding:12px 14px;height:100%;overflow:hidden;white-space:pre}.cur{display:inline-block;width:.6em;height:1.1em;background:#d4d8e0;vertical-align:text-bottom}
.keyrow{display:flex;gap:5px;padding:6px;background:#1a1d24;overflow:hidden;flex:none}.keyrow span{flex:none;min-width:36px;height:34px;padding:0 8px;display:grid;place-items:center;border-radius:6px;background:#2b2f39;color:#e5e7eb;font:600 12.5px Inter}.keyrow span.on{background:var(--pri);color:#fff}
.ioskb{background:#23262e;padding:7px 3px 22px;flex:none}.ioskb div{display:flex;justify-content:center;gap:4px;margin-bottom:8px}.ioskb span{flex:1;max-width:33px;height:38px;border-radius:5px;background:#4a4e59;color:#fff;display:grid;place-items:center;font-size:15px}.ioskb span.w{max-width:76px;background:#343842;font-size:12.5px}.ioskb span.sp{max-width:none;font-size:12.5px}
.ov{position:absolute;inset:0;background:rgba(5,7,12,.62);display:flex;align-items:center;justify-content:center;z-index:20;padding:16px}.modal{background:${t.name === 'aurora-dark' ? '#141a30' : t.card};border:1px solid var(--bd);border-radius:calc(var(--r) + 4px);box-shadow:0 24px 70px rgba(0,0,0,.45);padding:22px;width:100%}
h1,h2,h3{margin:0;letter-spacing:-.01em}h1{font-size:22px}h2{font-size:16px}h3{font-size:13.5px}.dim{color:var(--dim)}.row{display:flex;align-items:center;gap:9px;min-width:0}.tr{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}.grow{flex:1;min-width:0}.col{display:flex;flex-direction:column;min-height:0;min-width:0}
.code{display:flex;gap:8px;justify-content:center}.code span{width:44px;height:52px;border:1px solid var(--bd);border-radius:var(--r);display:grid;place-items:center;font-size:22px;font-weight:700;background:var(--chip)}.code span.a{border-color:var(--pri);box-shadow:0 0 0 3px var(--priL)}
.warnbox{display:flex;gap:9px;padding:10px 12px;border-radius:var(--r);border:1px solid color-mix(in srgb,var(--warn) 45%,transparent);background:color-mix(in srgb,var(--warn) 10%,transparent);font-size:12.5px}.warnbox svg{color:var(--warn)}
`;
module.exports = { ic, THEMES, HOSTS, SNIPS, CREDS, term, dot, badge, via, prot, btn, field, sidebar, rail, header, mhead, tabbar, keyrow, iosKb, css, logo };
