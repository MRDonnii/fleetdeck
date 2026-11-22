/**
 * Mobil: tabeller vises som kort (se mobile.css). Her får hver celle kolonnens overskrift som data-label,
 * og den første tekstcelle markeres som "main" (navnet på rækken). Kører løbende, når siden ændrer sig.
 */
/** Cellens tekst uden det, der står i badges. */
function ownText(td: HTMLElement) {
  let s = '';
  const w = document.createTreeWalker(td, NodeFilter.SHOW_TEXT);
  for (let n = w.nextNode(); n; n = w.nextNode()) if (!n.parentElement?.closest('.mantine-Badge-root')) s += n.textContent ?? '';
  return s.trim();
}

function label(table: HTMLTableElement) {
  const heads = [...table.querySelectorAll(':scope > thead th')].map((th) => (th.textContent ?? '').trim());
  for (const tr of table.querySelectorAll<HTMLTableRowElement>(':scope > tbody > tr')) {
    let mainSet = false;
    [...tr.cells].forEach((td, i) => {
      const h = heads[i] ?? '';
      if (h && h !== '#') { if (td.dataset.label !== h) td.dataset.label = h; } else if (td.dataset.label) delete td.dataset.label;
      // navnet: første celle med almindelig tekst uden for badges (ikke kun et badge, en knap eller et tal)
      const txt = ownText(td);
      const isMain = !mainSet && txt.length > 2 && !/^[\d\s.,%:-]+$/.test(txt) && !td.querySelector('button, input');
      if (isMain) { mainSet = true; if (!('main' in td.dataset)) td.dataset.main = ''; } else if ('main' in td.dataset) delete td.dataset.main;
    });
  }
}

export function startTableLabels() {
  let queued = false;
  const run = () => { queued = false; document.querySelectorAll<HTMLTableElement>('table.mantine-Table-table').forEach(label); };
  const obs = new MutationObserver(() => { if (!queued) { queued = true; requestAnimationFrame(run); } });
  obs.observe(document.body, { childList: true, subtree: true, characterData: true });
  run();
}
