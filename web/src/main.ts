import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { createAppConfig } from './app/app.config';
import { RuntimeConfigError, fetchRuntimeConfig } from './app/core/config/runtime-config';
import { showStartupFailure } from './app/core/config/startup-failure';
import { applyStoredAppearance } from './app/core/config/theme';

// Before bootstrap, deliberately. `ThemeStore` keeps the class in step from here on, but its
// first effect does not run until Angular has rendered — long enough for someone whose
// stored choice disagrees with their OS to watch the page start in the wrong theme.
applyStoredAppearance(document);

async function start(): Promise<void> {
  // Before anything is built, because where the API lives is an input to building it: the same
  // bundle is served by every deployment, and each says where its registry is in a file beside
  // the page. Absent means the defaults; present and wrong means the app does not start and says
  // so — see `runtime-config.ts` for why those are different.
  const runtime = await fetchRuntimeConfig();

  await bootstrapApplication(App, createAppConfig(runtime));
}

start().catch((error: unknown) => {
  const root = document.querySelector('cd-root');

  if (error instanceof RuntimeConfigError && root !== null) {
    showStartupFailure(root, error.message);
    return;
  }

  console.error(error);
});
