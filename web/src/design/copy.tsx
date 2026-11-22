import { useState, type ReactNode } from 'react';

/**
 * Kopiér tekst – også når siden åbnes over http (fx http://192.168.1.29:8585), hvor browseren blokerer navigator.clipboard.
 * Så bruges den gamle metode med et skjult tekstfelt.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (window.isSecureContext && navigator.clipboard) { await navigator.clipboard.writeText(text); return true; }
  } catch { /* falder tilbage nedenfor */ }
  const ta = document.createElement('textarea');
  ta.value = text; ta.setAttribute('readonly', ''); ta.style.position = 'fixed'; ta.style.top = '-1000px'; ta.style.opacity = '0';
  document.body.appendChild(ta); ta.select(); ta.setSelectionRange(0, text.length);
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  document.body.removeChild(ta);
  return ok;
}

/** Samme brug som Mantines CopyButton, men virker også over http. */
export function CopyButton({ value, timeout = 1500, children }: { value: string; timeout?: number; children: (p: { copied: boolean; copy: () => void }) => ReactNode }) {
  const [copied, setCopied] = useState(false);
  const copy = () => { void copyText(value).then((ok) => { if (ok) { setCopied(true); setTimeout(() => setCopied(false), timeout); } else window.prompt('Kopiér teksten (Ctrl+C):', value); }); };
  return <>{children({ copied, copy })}</>;
}
