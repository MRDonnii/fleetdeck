// Tegner alle skitser: NODE_PATH=<playwright> node render.js [udmappe]
const { chromium } = require('playwright'), path = require('path'), fs = require('fs');
const { THEMES, css } = require('./lib'), { screens } = require('./screens');
const OUT = process.argv[2] || '/mnt/ai-workspace/Fleetdeck/skitser';
const DEV = { desktop: ['d', 1920, 1080], ipad: ['t', 1180, 820], iphone: ['m', 390, 844] };
const NAMES = ['liste', 'session', 'kommandoer', 'boks', 'tilfoej', 'reauth'];
const INFO = { A: ['Arbejdsbord', 'Værtsliste, faner og kommandoer side om side'], B: ['Startplade', 'Store kort først, sessioner i fuld skærm'], C: ['Dok', 'Tabel over værter med terminalen i en skuffe nederst'] };
(async () => {
  const b = await chromium.launch(); const problems = [];
  for (const o of 'ABC') {
    const files = {};
    for (const [dn, [k, w, h]] of Object.entries(DEV)) {
      const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 2 });
      for (const [i, s] of NAMES.entries()) {
        await p.goto('about:blank');
        const tmp = path.join(require('os').tmpdir(), `fd-term-${o}-${dn}-${s}.html`);
        fs.writeFileSync(tmp, `<!doctype html><html lang="da"><meta charset="utf-8"><style>${css(THEMES[o])}</style><body>${screens[o][s](k)}</body></html>`);
        await p.goto('file://' + tmp); await p.evaluate(() => document.fonts.ready);
        const over = await p.evaluate(() => [...document.querySelectorAll('.modal,.btn,.card')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && (r.right > innerWidth + 1 || r.left < -1); }).length);
        if (over) problems.push(`${o}/${dn}/${s}: ${over} uden for skærmen`);
        const f = path.join(OUT, `terminal-${o}-${dn}-${i + 1}-${s}.png`); await p.screenshot({ path: f }); (files[dn] ||= []).push(f); fs.unlinkSync(tmp);
      }
      await p.close();
    }
    const p = await b.newPage({ viewport: { width: 2400, height: 1000 }, deviceScaleFactor: 2 });
    const img = (f, w) => `<img src="file://${f}" style="width:${w}px;border-radius:10px;border:1px solid #333;display:block">`;
    const tmp = path.join(require('os').tmpdir(), `fd-term-ov-${o}.html`);
    fs.writeFileSync(tmp, `<!doctype html><meta charset="utf-8"><body style="margin:0;background:#101218;color:#e5e7eb;font:20px Inter,system-ui;padding:40px;width:2320px"><h1 style="margin:0;font-size:44px">Forslag ${o}: ${INFO[o][0]}</h1><p style="color:#9aa3b5;margin:8px 0 28px;font-size:24px">${INFO[o][1]}</p><div style="display:grid;grid-template-columns:repeat(3,1fr);gap:20px">${files.desktop.map(f => img(f, 760)).join('')}</div><div style="display:grid;grid-template-columns:repeat(3,1fr);gap:20px;margin-top:24px">${files.ipad.map(f => img(f, 760)).join('')}</div><div style="display:grid;grid-template-columns:repeat(6,1fr);gap:20px;margin-top:24px">${files.iphone.map(f => img(f, 370)).join('')}</div></body>`);
    await p.goto('file://' + tmp); await p.waitForLoadState('networkidle');
    await p.screenshot({ path: path.join(OUT, `terminal-${o}-oversigt.png`), fullPage: true }); await p.close(); fs.unlinkSync(tmp);
  }
  await b.close(); console.log(problems.length ? problems.join('\n') : 'ingen elementer uden for skærmen');
})();
