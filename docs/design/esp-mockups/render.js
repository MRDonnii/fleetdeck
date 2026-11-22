// Tegner skitserne til ESP-flash: FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
// pc: 1600×1000. iPhone: 587×1275 (391×850 punkter ved 1,5×).
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css } = require('../terminal-mockups/lib'), { screens, NAMES, MOBILE } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser/esp';
const LABEL = { katalog: 'Firmwarekatalog', https: 'Ikke sikker side: sådan stoler pc\'en på Fleetdeck', wifi: 'Send Wi-Fi efter flash', detalje: 'Firmware' };
const SKIN = { C: ['Command lys', 'command-lys', 'Liste til venstre, detalje til højre. Ét klik på en firmware viser alt om den.'],
  B: ['Aurora mørk', 'aurora-moerk', 'Kort i et gitter med filtre øverst. Flash og Wi-Fi direkte på kortet.'] };
(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' }); const problems = [];
  for (const th of ['C', 'B']) {
    const shots = [];
    for (const [dn, k, vp, scale] of [['pc', 'd', { width: 1600, height: 1000 }, 1], ['iphone', 'm', { width: 391, height: 850 }, 1.5]]) {
      const p = await b.newPage({ viewport: vp, deviceScaleFactor: scale });
      for (const s of (dn === 'iphone' ? MOBILE : NAMES)) {
        const tmp = path.join(os.tmpdir(), `fd-esp-${th}-${dn}-${s}.html`);
        fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}</style><body>${screens[th][s](k)}</body></html>`);
        await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
        const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
        if (over) problems.push(`${th}/${dn}/${s}: ${over} uden for skærmen`);
        const f = path.join(os.tmpdir(), `fd-esp-${th}-${dn}-${s}.png`); await p.screenshot({ path: f }); shots.push([dn, s, f]); fs.unlinkSync(tmp);
        if (dn === 'iphone') fs.copyFileSync(f, path.join(OUT, `esp-${SKIN[th][1]}-iphone-${s}.png`));
      }
      await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2000, height: 1000 }, deviceScaleFactor: 1 });
    const d = shots.filter(x => x[0] === 'pc'), m = shots.filter(x => x[0] === 'iphone');
    const cell = ([, s, f], w) => `<figure style="margin:0"><img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block"><figcaption style="margin-top:6px;font-size:15px;color:#aeb4c2">${LABEL[s]}</figcaption></figure>`;
    const html = `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:18px Inter,system-ui;padding:30px;width:1940px"><h1 style="margin:0 0 4px;font-size:30px">ESP-flash · ${SKIN[th][0]}</h1><p style="margin:0 0 20px;color:#aeb4c2">${SKIN[th][2]}</p><div style="display:grid;grid-template-columns:repeat(3,620px);gap:20px;margin-bottom:24px">${d.map(x => cell(x, 620)).join('')}</div><div style="display:flex;gap:20px">${m.map(x => cell(x, 293)).join('')}</div></body>`;
    const tmp = path.join(os.tmpdir(), `fd-esp-ov-${th}.html`); fs.writeFileSync(tmp, html);
    await p.goto('file://' + tmp); await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(OUT, `esp-${SKIN[th][1]}.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
    for (const [, , f] of shots) fs.unlinkSync(f);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
