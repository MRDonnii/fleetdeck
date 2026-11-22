// Tegner BIOS-skitserne:
//   FD_NODE_MODULES=<web/node_modules med @tabler/icons + @fontsource-variable/inter> NODE_PATH=<mappe med playwright-core> node render.js [udmappe]
// pc: 1600×1000, iPhone: 587×1275 (390 punkter bred, skala 1,505). Command = lys, Aurora = mørk.
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const L = require(process.env.FD_LIB || '../terminal-mockups/lib');
const { SCREENS, css } = require('./screens');
const OUT = process.argv[2] || path.resolve(__dirname, '..');
const SKIN = { C: 'command-lys', B: 'aurora-moerk' };
(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome', args: ['--headless=new', '--no-sandbox'] });
  const problems = [], files = [];
  for (const [name, desk, phone] of SCREENS) for (const th of ['C', 'B']) for (const [dn, fn, w, h, sc] of [['pc', desk, 1600, 1000, 1], ['iphone', phone, 390, 847, 587 / 390]]) {
    const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: sc });
    const tmp = path.join(os.tmpdir(), `fd-bios-${name}-${th}-${dn}.html`);
    fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(L.THEMES[th])}</style><body>${fn()}</body></html>`);
    await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
    const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
    const clip = await p.evaluate(() => [...document.querySelectorAll('.modal')].filter((e) => e.scrollHeight > e.clientHeight + 2).length);
    if (over) problems.push(`${name} ${th}/${dn}: ${over} uden for skærmen`);
    if (clip) problems.push(`${name} ${th}/${dn}: vinduet er klippet`);
    const f = path.join(OUT, `bios-${name}-${SKIN[th]}-${dn}.png`);
    await p.screenshot({ path: f }); files.push(f); await p.close(); fs.unlinkSync(tmp);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen'); console.log(files.length + ' filer');
})();
