// Tegner skitserne: FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
// pc: 1600×1000, iPhone: 587×1275 (390 punkter bred, Retina-skala 1,505).
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css } = require('../terminal-mockups/lib'), { desktop, iphone } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser/chipset';
const SKIN = { C: 'command-lys', B: 'aurora-moerk' };
(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' }); const problems = [];
  for (const th of ['C', 'B']) for (const [dn, fn, w, h, sc] of [['pc', desktop, 1600, 1000, 1], ['iphone', iphone, 390, 847, 587 / 390]]) {
    const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: sc });
    const tmp = path.join(os.tmpdir(), `fd-chipset-${th}-${dn}.html`);
    fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}</style><body>${fn()}</body></html>`);
    await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
    const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
    if (over) problems.push(`${th}/${dn}: ${over} uden for skærmen`);
    await p.screenshot({ path: path.join(OUT, `chipset-${SKIN[th]}-${dn}.png`) }); await p.close(); fs.unlinkSync(tmp);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
