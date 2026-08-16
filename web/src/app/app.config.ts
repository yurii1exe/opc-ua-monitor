import { provideHttpClient, withFetch } from '@angular/common/http';
import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Zoneless. Every piece of state in this app is a signal, and the update
    // pattern — a hub message arriving on a socket callback several times a
    // second — is exactly the case where zone.js re-checks the whole component
    // tree for a change that the signal graph already located precisely.
    provideZonelessChangeDetection(),
    provideHttpClient(withFetch()),
  ],
};
