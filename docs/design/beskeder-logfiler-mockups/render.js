// Tegner skitserne: FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
// To skitser (Command lys og Aurora mørk), hver med pc (1600×1000) og iPhone (587×1275).
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css } = require('../terminal-mockups/lib'), { screens, DESKTOP, PHONE } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser/beskeder-logfiler';
const DEV = { desktop: ['d', 1600, 1000, 1], iphone: ['m', 390, 847, 587 / 390] };
const LABEL = { maskine: 'Maskinens fane Beskeder: "Hent logfiler", logfiler i samtalen og kopiér-knap', pc: 'Vinduet på pc\'en: svarfelt med fokus, "Send logfiler" og status', traad: 'Samtalen på iPhone' };
const SKIN = { C: ['Command lys', 'command-lys'], B: ['Aurora mørk', 'aurora-moerk'] };
(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' }); const problems = [];
  for (const th of ['C', 'B']) {
    const shots = [];
    for (const [dn, [k, w, h, dsf]] of Object.entries(DEV)) {
      const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: dsf });
      for (const s of (dn === 'iphone' ? PHONE : DESKTOP)) {
        const tmp = path.join(os.tmpdir(), `fd-hdl-${th}-${dn}-${s}.html`);
        fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}</style><body>${screens[s](k)}</body></html>`);
        await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
        const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
        if (over) problems.push(`${th}/${dn}/${s}: ${over} uden for skærmen`);
        const f = path.join(OUT, `beskeder-logfiler-${SKIN[th][1]}-${dn === 'iphone' ? 'iphone' : 'pc'}-${s}.png`); await p.screenshot({ path: f }); shots.push([dn, s, f]); fs.unlinkSync(tmp);
      }
      await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2000, height: 1000 }, deviceScaleFactor: 1 });
    const d = shots.filter(x => x[0] === 'desktop'), m = shots.filter(x => x[0] === 'iphone');
    const cell = ([, s, f], w) => `<figure style="margin:0"><img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block"><figcaption style="margin-top:6px;font-size:15px;color:#aeb4c2">${LABEL[s]}</figcaption></figure>`;
    const html = `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:18px Inter,system-ui;padding:30px;width:1940px"><h1 style="margin:0 0 4px;font-size:30px">Beskeder: svar og logfiler · ${SKIN[th][0]}</h1><p style="margin:0 0 20px;color:#aeb4c2">Brugeren svarer med tekst (Enter sender) og kan sende logfiler med ét klik. Admin kan hente de samme logfiler fra maskinens side, uden at brugeren gør noget. Logfilerne er en zip på højst 5 MB uden hemmeligheder.</p><div style="display:grid;grid-template-columns:repeat(2,620px);gap:20px;margin-bottom:24px">${d.map(x => cell(x, 620)).join('')}</div><div style="display:flex;gap:20px">${m.map(x => cell(x, 293)).join('')}</div></body>`;
    const tmp = path.join(os.tmpdir(), `fd-hdl-ov-${th}.html`); fs.writeFileSync(tmp, html);
    await p.goto('file://' + tmp); await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(OUT, `beskeder-logfiler-${SKIN[th][1]}.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
