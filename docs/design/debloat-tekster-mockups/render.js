// Skitser til gennemgangen af debloat-teksterne (punkt 11).
// FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css, ic, badge, btn } = require('../terminal-mockups/lib');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser/debloat';
const SKIN = { C: ['Command lys', 'command-lys'], B: ['Aurora mørk', 'aurora-moerk'] };
const extra = `.seg{display:inline-flex;background:var(--chip);border:1px solid var(--bd);border-radius:var(--r);padding:3px;gap:2px}.seg span{padding:4px 11px;border-radius:calc(var(--r) - 2px);font-weight:600;font-size:12.5px;color:var(--dim)}.seg span.on{background:var(--card);color:var(--tx);box-shadow:0 1px 3px rgba(0,0,0,.15)}
.sw{width:30px;height:17px;border-radius:9px;background:var(--off);position:relative;flex:none}.sw.on{background:var(--pri)}.sw::after{content:"";position:absolute;top:2px;left:2px;width:13px;height:13px;border-radius:50%;background:#fff}.sw.on::after{left:15px}
.it{border-top:1px solid var(--bd);padding:8px 0}.pad{padding:14px}.hint{display:flex;gap:8px;align-items:center;font-size:12px;color:var(--dim)}.stat{padding:6px 14px}.stat+.stat{border-left:1px solid var(--bd)}.box{border:1px solid var(--bd);border-radius:8px;padding:8px;font-size:12px}`;
const G = 'var(--ok)', O = 'var(--warn)', D = 'var(--dim)';
const ITEMS = [
  ['Slå reklame-id fra', 'Reklame-id', 'fra', G, 'sikker', 'green'],
  ['Slå telemetritjenesten fra (DiagTrack)', 'Telemetritjenesten', 'deaktiveret', G, 'moderat', 'orange'],
  ['Vis filtypenavne', 'Filtypenavne', 'vises', G, 'sikker', 'green'],
  ['Udsæt nye Windows-versioner i et år', 'Opdateringer', 'standard', D, 'moderat', 'orange'],
  ['Fjern Clipchamp', 'Clipchamp', 'installeret igen', O, 'sikker', 'green'],
];
const riskCol = { green: 'var(--ok)', orange: 'var(--warn)' };
const row = (opt, [t, s, st, col, r, rc], i, open, narrow) => {
  const state = opt === 'A' ? `<span class="badge" style="color:${col};background:color-mix(in srgb,${col} 14%,transparent)">${s}: ${st}</span>` : badge(st, col);
  return `<div class="it"><div class="row"><span class="sw ${i === 3 ? '' : 'on'}"></span><div class="grow"><div class="row" style="gap:5px">${ic(open ? 'chevron-down' : 'chevron-right', 13)}<span class="${narrow ? '' : 'tr'}" style="font-size:13px">${t}</span></div>
  ${opt === 'B' ? `<div style="font-size:11.5px;color:${col};margin:1px 0 0 18px">${s}: ${st}</div>` : ''}</div>${narrow ? '' : badge(r, riskCol[rc])}${opt === 'A' ? state : ''}</div>
  ${open ? `<div style="margin:6px 0 0 40px;display:flex;flex-direction:column;gap:6px;font-size:12px"><div><b>Når du trykker Anvend:</b> <span class="dim">Apps kan ikke længere bruge et fælles reklame-id til at målrette reklamer på tværs af apps.</span></div>
  <div><b>Fortryd:</b> <span class="dim">Det, der stod før, sættes tilbage. Har nogen ændret det siden, får det lov at blive.</span></div>
  <div class="dim">Gælder: Windows 10 og 11 · alle udgaver · sidst tjekket: Reklame-id: fra (for 2 t siden)</div>
  <div class="row">${btn('Anvend dette punkt', { s: 'height:26px;font-size:12px' })}${btn('Fortryd dette punkt', { s: 'height:26px;font-size:12px' })}</div></div>` : ''}</div>`;
};
const levels = (narrow) => `<div class="col" style="gap:5px"><span class="dim" style="font-size:12px">Niveau</span><div class="seg"><span>Intet</span><span>Let</span><span class="on">Anbefalet</span><span>Maks</span></div>
<span class="dim" style="font-size:12px;max-width:${narrow ? 340 : 560}px">Anbefalet: alt fra Let plus telemetri, Copilot/Recall, websøgning og de typiske medfølgende apps. Skifter du niveau, nulstilles dine egne til- og fravalg. Intet ændres på pc'en, før du trykker Anvend.</span></div>`;
const stats = `<div class="card row" style="gap:0">${[['Anvendt', 23, G], ['Vendt tilbage', 1, O], ['Valgt, ikke anvendt', 4, 'var(--tx)']].map(([l, v, c]) => `<div class="stat"><div class="dim" style="font-size:11px">${l}</div><b style="font-size:20px;color:${c}">${v}</b></div>`).join('')}</div>`;
const hint = `<div class="hint">${ic('info-circle', 15)}Kontakten vælger et punkt. Pc'en ændres først, når du trykker Anvend – enten for alle valgte nederst eller for ét punkt i dets detaljer.</div>`;
const cat = (opt, narrow) => `<div class="card pad"><div class="row" style="justify-content:space-between"><b>Telemetri og sporing</b><span class="dim" style="font-size:11px">9/11 valgt</span></div>
<div class="dim" style="font-size:11.5px;margin:2px 0 8px">Hvad Windows sender til Microsoft, og hvad der huskes om brugeren.</div>${ITEMS.map((x, i) => row(opt, x, i, i === 0, narrow)).join('')}</div>`;
const actions = (narrow) => `<div class="row" style="justify-content:space-between;flex-wrap:wrap;gap:8px"><div class="row">${btn('Anvend valgte (27)', { k: 'p' })}${btn('Tjek nu', { i: 'refresh' })}</div>${btn('Fortryd alt', { s: 'color:var(--bad)' })}</div>
<div class="dim" style="font-size:11.5px">Tjek nu læser kun tilstanden. Fortryd alt sætter alt, Fleetdeck har anvendt, tilbage – valget på pc'en bliver stående.</div>`;
const enforce = `<div class="card pad row" style="flex-wrap:wrap;gap:12px"><span class="sw on"></span><div><b style="font-size:13px">Hold håndhævet</b><div class="dim" style="font-size:11.5px">Fleetdeck tjekker dagligt og efter nye Windows-builds, om anvendte punkter er vendt tilbage.</div></div><div class="seg"><span class="on">Giv besked</span><span>Anvend igen</span></div></div>`;
const page = (opt, narrow) => `<div class="col" style="gap:12px;padding:${narrow ? 12 : 20}px;height:100%;overflow:hidden">
<div class="row"><h1 style="font-size:${narrow ? 18 : 22}px">ARBEJDS-PC</h1><span class="dim">· Debloat & privatliv</span></div>
<div class="row" style="justify-content:space-between;align-items:flex-end;flex-wrap:wrap;gap:10px">${levels(narrow)}${stats}</div>${enforce}${hint}
<div style="display:grid;grid-template-columns:${narrow ? '1fr' : '1fr 1fr'};gap:12px">${cat(opt, narrow)}${narrow ? '' : `<div class="col" style="gap:12px">${confirm()}</div>`}</div>${actions(narrow)}</div>`;
const confirm = () => `<div class="modal" style="padding:16px"><h2 style="margin-bottom:8px">Fortryd på ARBEJDS-PC?</h2>
<div style="font-size:12.5px;display:flex;flex-direction:column;gap:8px"><div><b>Telemetri og sporing · 2</b><div class="dim">Reklame-id · Telemetritjenesten</div></div><div><b>Medfølgende apps · 1</b><div class="dim">Clipchamp</div></div>
<div>Henter igen fra Microsoft Store: Clipchamp (for den bruger, der er logget ind)</div>
<div class="dim" style="font-size:11.5px">Det, der stod før, sættes tilbage. Har nogen ændret det siden, får det lov at blive. Punkterne bliver stående som valgt – vil du ikke have dem anvendt igen, så slå dem fra.</div>
<div class="row" style="justify-content:flex-end">${btn('Annullér')}${btn('Fortryd 3 punkter', { k: 'd' })}</div></div></div>`;
(async () => {
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' });
  fs.mkdirSync(OUT, { recursive: true });
  for (const th of ['C', 'B']) {
    const shots = [];
    for (const opt of ['A', 'B']) for (const [dn, w, h] of [['pc', 1440, 980], ['iphone', 390, 844]]) {
      const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 2 });
      const tmp = path.join(os.tmpdir(), `fd-dbt-${th}-${opt}-${dn}.html`);
      fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}${extra}</style><body>${page(opt, dn === 'iphone')}</body></html>`);
      await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
      const f = tmp.replace('.html', '.png'); await p.screenshot({ path: f }); shots.push([opt, dn, f]); fs.unlinkSync(tmp); await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2200, height: 1000 }, deviceScaleFactor: 2 });
    const img = (f, w) => `<img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block">`;
    const DESC = { A: 'Forslag A: tilstanden som mærke til højre, "Reklame-id: fra" (klemmer punktets tekst på iPhone).', B: 'Forslag B (anbefalet): tilstanden som linje under punktets tekst – plads til hele teksten, også på iPhone.' };
    const html = `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:18px Inter,system-ui;padding:30px;width:2140px">
<h1 style="margin:0 0 6px;font-size:30px">Debloat: teksterne passer til knapperne · ${SKIN[th][0]}</h1>
<p style="margin:0 0 20px;color:#aeb4c2">Punktets tekst siger, hvad Anvend gør. Tilstanden står med punktets egne ord. Detaljerne siger, hvad Anvend og Fortryd gør. Knapperne hedder Anvend og Fortryd overalt.</p>
${['A', 'B'].map((o) => `<h2 style="font-size:22px;margin:16px 0 10px">${DESC[o]}</h2><div style="display:flex;gap:20px;align-items:flex-start">${img(shots.find((s) => s[0] === o && s[1] === 'pc')[2], 1560)}${img(shots.find((s) => s[0] === o && s[1] === 'iphone')[2], 420)}</div>`).join('')}</body>`;
    const tmp = path.join(os.tmpdir(), `fd-dbt-ov-${th}.html`); fs.writeFileSync(tmp, html);
    await p.goto('file://' + tmp); await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(OUT, `debloat-tekster-${SKIN[th][1]}.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
    for (const [, , f] of shots) fs.unlinkSync(f);
  }
  await b.close(); console.log('ok');
})();
