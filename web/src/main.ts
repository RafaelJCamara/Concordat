import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { applyStoredAppearance } from './app/core/config/theme';

// Before bootstrap, deliberately. `ThemeStore` keeps the class in step from here on, but its
// first effect does not run until Angular has rendered — long enough for someone whose
// stored choice disagrees with their OS to watch the page start in the wrong theme.
applyStoredAppearance(document);

bootstrapApplication(App, appConfig).catch((error: unknown) => console.error(error));
