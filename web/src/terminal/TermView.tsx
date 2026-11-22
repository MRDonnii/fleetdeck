import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { FRAME, dataFrame, resizeFrame, wsUrl, type OpenSession } from './client';

export interface TermHandle { send: (text: string) => void; focus: () => void; paste: () => void; setMods: (m: { ctrl: boolean; alt: boolean }) => void }

const THEME = { background: '#0a0c10', foreground: '#d4d8e0', cursor: '#d4d8e0', selectionBackground: '#3270f055' };
const dec = new TextDecoder();

/** Én terminal: xterm i browseren koblet til serveren. Adgangsoplysninger kommer aldrig herud. */
export const TermView = forwardRef<TermHandle, { session: OpenSession; active: boolean; onStatus: (s: string) => void; onExit: () => void; onModsUsed?: () => void; onFocus?: () => void }>(
  function TermView({ session, active, onStatus, onExit, onModsUsed, onFocus }, ref) {
    const box = useRef<HTMLDivElement>(null);
    const ws = useRef<WebSocket | null>(null);
    const xterm = useRef<Terminal | null>(null);
    const fit = useRef<FitAddon | null>(null);
    const cb = useRef({ onStatus, onExit, onModsUsed, onFocus }); cb.current = { onStatus, onExit, onModsUsed, onFocus };
    const mods = useRef({ ctrl: false, alt: false });

    useImperativeHandle(ref, () => ({
      send: (t) => { if (ws.current?.readyState === WebSocket.OPEN) ws.current.send(dataFrame(t)); },
      focus: () => xterm.current?.focus(),
      paste: () => { void navigator.clipboard?.readText().then((t) => xterm.current?.paste(t)); },
      setMods: (m) => { mods.current = m; },
    }), []);

    useEffect(() => {
      const t = new Terminal({ fontFamily: '"JetBrains Mono","Cascadia Code",Consolas,monospace', fontSize: 13, cursorBlink: true, theme: THEME, scrollback: 5000, allowProposedApi: false });
      const f = new FitAddon(); t.loadAddon(f); t.open(box.current!); f.fit();
      xterm.current = t; fit.current = f;
      const sock = new WebSocket(wsUrl(session)); sock.binaryType = 'arraybuffer'; ws.current = sock;
      sock.onopen = () => sock.send(resizeFrame(t.cols, t.rows));
      sock.onmessage = (e) => {
        const b = new Uint8Array(e.data as ArrayBuffer);
        if (b[0] === FRAME.data) t.write(b.subarray(1));
        else if (b[0] === FRAME.status) cb.current.onStatus(dec.decode(b.subarray(1)));
        else if (b[0] === FRAME.exit) cb.current.onStatus('Sessionen er afsluttet.');
      };
      sock.onclose = () => cb.current.onExit();
      // Ctrl og Alt fra tastaturrækken på mobil gælder for næste tegn
      const input = t.onData((d) => {
        if (sock.readyState !== WebSocket.OPEN) return;
        let out = d; const m = mods.current;
        if ((m.ctrl || m.alt) && d.length === 1) {
          if (m.ctrl) out = String.fromCharCode(d.toUpperCase().charCodeAt(0) & 0x1f);
          if (m.alt) out = `\x1b${out}`;
          mods.current = { ctrl: false, alt: false }; cb.current.onModsUsed?.();
        }
        sock.send(dataFrame(out));
      });
      // Ctrl+Shift+C og Ctrl+Shift+V: Ctrl+C alene skal stadig afbryde i skallen
      t.attachCustomKeyEventHandler((e) => {
        if (e.type !== 'keydown' || !e.ctrlKey || !e.shiftKey) return true;
        if (e.code === 'KeyC' && t.hasSelection()) { void navigator.clipboard?.writeText(t.getSelection()); return false; }
        if (e.code === 'KeyV') { void navigator.clipboard?.readText().then((x) => t.paste(x)); return false; }
        return true;
      });
      const onDown = () => cb.current.onFocus?.();
      box.current!.addEventListener('pointerdown', onDown);
      const size = t.onResize(({ cols, rows }) => { if (sock.readyState === WebSocket.OPEN) sock.send(resizeFrame(cols, rows)); });
      const ro = new ResizeObserver(() => { if (box.current?.offsetWidth) f.fit(); });
      ro.observe(box.current!);
      const el = box.current!;
      return () => { el.removeEventListener('pointerdown', onDown); ro.disconnect(); input.dispose(); size.dispose(); sock.close(); t.dispose(); };
    }, [session]);

    useEffect(() => { if (active) { fit.current?.fit(); xterm.current?.focus(); } }, [active]);
    return <div ref={box} style={{ position: 'absolute', inset: 0, padding: 6, background: THEME.background }} />;
  });
