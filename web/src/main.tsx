import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import '@mantine/charts/styles.css';
import '@mantine/spotlight/styles.css';
import '@fontsource-variable/inter';
import './design/fleetdeck.css';
import './mobile.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { DownloadPage } from './pages/Download';
import { MantineProvider } from '@mantine/core';
import { buildTheme, SkinProvider } from './design/skin';
import { Notifications } from '@mantine/notifications';
import { ModalsProvider } from '@mantine/modals';
import { QueryClientProvider } from '@tanstack/react-query';
import { queryClient } from './api';
import { AuthProvider } from './auth';
import { App } from './App';
import { startTableLabels } from './tableLabels';

// Understi (fx /open/fleetdeck/) fra <base href>, som serveren sætter ud fra X-Forwarded-Prefix
const basename = new URL(document.baseURI).pathname.replace(/\/$/, '') || '/';



createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SkinProvider>{(skin) => (
    <MantineProvider theme={buildTheme(skin)} defaultColorScheme="auto">
      <Notifications position={window.matchMedia('(max-width: 48em)').matches ? 'top-center' : 'bottom-right'} />
      <QueryClientProvider client={queryClient}>
        <ModalsProvider labels={{ confirm: 'OK', cancel: 'Annullér' }}>
          <BrowserRouter basename={basename}>
            {/* /hent: downloadsiden, der kan sendes til brugerne – uden login */}
            {/\/hent\/?$/.test(window.location.pathname)
              ? <DownloadPage />
              : <AuthProvider>
                  <App />
                </AuthProvider>}
          </BrowserRouter>
        </ModalsProvider>
      </QueryClientProvider>
    </MantineProvider>
    )}</SkinProvider>
  </StrictMode>,
);
startTableLabels();   // mobil: tabeller vises som kort med kolonnenavne
