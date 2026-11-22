import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const backend = process.env.WS_BACKEND ?? 'http://localhost:8585';

export default defineConfig({
  // Relative adresser, så siden også virker under en understi (Kontrolrum: /open/fleetdeck/)
  base: './',
  plugins: [react()],
  build: {
    outDir: '../src/Fleetdeck.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        manualChunks: (id: string) => {
          if (!id.includes('node_modules')) return undefined;
          if (id.includes('@xterm')) return 'xterm';   // kun terminalsiden
          if (/recharts|d3-|@mantine[\\/]charts/.test(id)) return 'charts';
          if (id.includes('@mantine')) return 'mantine';
          if (/[\\/](react|react-dom|react-router|scheduler)[\\/]/.test(id)) return 'react';
          return 'vendor';
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/install.ps1': backend,
      '/hubs': { target: backend, ws: true },
    },
  },
});
