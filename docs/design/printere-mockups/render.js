// Tegner skitserne: FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css } = require('../terminal-mockups/lib'), { screens, NAMES } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser';
const DEV = { desktop: ['d', 1600, 1000], iphone: ['m', 390, 844] };
const LABEL = { liste: 'Printere', tilfoej: 'Tilføj printer', profil: 'På en profil', maskine: 'På en maskine' };
const SKIN = { C: 'Command lys', B: 'Aurora mørk' };
(async () => {
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' }); const problems = [];
  for (const opt of ['A', 'B']) for (const th of ['C', 'B']) {
    const shots = [];
    for (const [dn, [k, w, h]] of Object.entries(DEV)) {
      const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
      for (const s of (dn === 'iphone' ? ['liste', 'tilfoej', 'maskine'] : NAMES)) {
        const tmp = path.join(os.tmpdir(), `fd-pr-${opt}-${th}-${dn}-${s}.html`);
        fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}</style><body>${screens[opt][s](k)}</body></html>`);
        await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
        const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
        if (over) problems.push(`${opt}/${th}/${dn}/${s}: ${over} uden for skærmen`);
        const f = path.join(os.tmpdir(), `fd-pr-${opt}-${th}-${dn}-${s}.png`); await p.screenshot({ path: f }); shots.push([dn, s, f]); fs.unlinkSync(tmp);
      }
      await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2000, height: 1000 }, deviceScaleFactor: 1 });
    const d = shots.filter(x => x[0] === 'desktop'), m = shots.filter(x => x[0] === 'iphone');
    const cell = ([, s, f], w) => `<figure style="margin:0"><img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block"><figcaption style="margin-top:6px;font-size:15px;color:#aeb4c2">${LABEL[s]}</figcaption></figure>`;
    const html = `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:18px Inter,system-ui;padding:30px;width:1940px"><h1 style="margin:0 0 4px;font-size:30px">Forslag ${opt}: ${opt === 'A' ? 'Egen side Printere' : 'Printere som programmer'} · ${SKIN[th]}</h1><p style="margin:0 0 20px;color:#aeb4c2">${opt === 'A' ? 'En side Printere med de tilføjede og de fundne printere. De lægges på profiler eller maskiner og holdes på.' : 'En printer er en pakketype ved siden af programmer: samme liste, samme påkrævet på profilen og samme overholdelse.'}</p><div style="display:grid;grid-template-columns:repeat(2,620px);gap:20px;margin-bottom:24px">${d.map(x => cell(x, 620)).join('')}</div><div style="display:flex;gap:20px">${m.map(x => cell(x, 240)).join('')}</div></body>`;
    const tmp = path.join(os.tmpdir(), `fd-pr-ov-${opt}-${th}.html`); fs.writeFileSync(tmp, html);
    await p.goto('file://' + tmp); await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(OUT, `printere-${opt}-${th === 'C' ? 'command-lys' : 'aurora-moerk'}.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
    for (const [, , f] of shots) fs.unlinkSync(f);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
