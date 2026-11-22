// Tegner skitserne: FD_NODE_MODULES=<web/node_modules> NODE_PATH=<playwright-core> node render.js [udmappe]
const { chromium } = require('playwright-core'), path = require('path'), fs = require('fs'), os = require('os');
const { THEMES, css } = require('../terminal-mockups/lib'), { screens, NAMES } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser/genstart';
const LABEL = { nedtaelling: 'Nedtælling med "Genstart nu" + menuen ved ikonet', haenger: 'Genstarten hænger', bekraeft: 'Sluk og tænd strømmen (bekræft)', wol: 'Genstarter… + Gør klar til WoL' };
const SKIN = { C: ['Command lys', 'command-lys'], B: ['Aurora mørk', 'aurora-moerk'] };
(async () => {
  const b = await chromium.launch({ executablePath: '/usr/bin/google-chrome' }); const problems = [];
  for (const th of ['C', 'B']) {
    const shots = [];
    for (const [dn, k, w, h, scale, list] of [['desktop', 'd', 1440, 900, 1, NAMES], ['iphone', 'm', 391, 850, 1.5, ['haenger', 'bekraeft', 'wol']]]) {
      const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: scale });
      for (const s of list) {
        const tmp = path.join(os.tmpdir(), `fd-gs-${th}-${dn}-${s}.html`);
        fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[th])}</style><body>${screens[s](k)}</body></html>`);
        await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
        const out = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.bottom > innerHeight + 1); }).length);
        if (out) problems.push(`${th}/${dn}/${s}: ${out} uden for skærmen`);
        const f = dn === 'iphone' ? path.join(OUT, `genstart-${SKIN[th][1]}-iphone-${s}.png`) : path.join(os.tmpdir(), `fd-gs-${th}-${dn}-${s}.png`);
        await p.screenshot({ path: f }); shots.push([dn, s, f]); fs.unlinkSync(tmp);
      }
      await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2000, height: 1000 }, deviceScaleFactor: 1 });
    const d = shots.filter(x => x[0] === 'desktop'), m = shots.filter(x => x[0] === 'iphone');
    const cell = ([, s, f], w) => `<figure style="margin:0"><img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block"><figcaption style="margin-top:6px;font-size:15px;color:#aeb4c2">${LABEL[s]}</figcaption></figure>`;
    const html = `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:18px Inter,system-ui;padding:30px;width:1940px"><h1 style="margin:0 0 4px;font-size:30px">Genstart nu · genstart der hænger · Wake-on-LAN · ${SKIN[th][0]}</h1><p style="margin:0 0 20px;color:#aeb4c2">Nedtællingen får "Genstart nu". Jobbet står som "Genstarter…", til pc'en er tilbage med ny opstartstid; ellers "Hænger" med besked, og strømmen kan slås fra og til via Home Assistant (kun med bekræftelse og når måleren viser strøm). Strøm-fanen samler kontakt, måler og Wake-on-LAN.</p><div style="display:grid;grid-template-columns:repeat(2,950px);gap:20px;margin-bottom:24px">${d.map(x => cell(x, 950)).join('')}</div><div style="display:flex;gap:20px">${m.map(x => cell(x, 300)).join('')}</div></body>`;
    const tmp = path.join(os.tmpdir(), `fd-gs-ov-${th}.html`); fs.writeFileSync(tmp, html);
    await p.goto('file://' + tmp); await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(OUT, `genstart-${SKIN[th][1]}.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
    for (const [dn, , f] of shots) if (dn === 'desktop') fs.unlinkSync(f);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
