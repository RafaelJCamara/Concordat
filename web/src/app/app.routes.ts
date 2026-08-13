import type { Routes } from '@angular/router';

/**
 * The route table.
 *
 * Lazy per feature, so a screen nobody opened is a chunk nobody downloaded — which is what
 * keeps Monaco (M4.4) off the critical path for someone who only came to read a subject
 * list.
 *
 * **Resource identity goes in the path, never a query parameter** (DESIGN §9). The
 * prototype addressed a subject as `?subject=orders.created`, which cannot be linked to
 * from an incident channel without also carrying whatever other state the page had, and
 * makes `/subjects/:name/versions/:ordinal` — the URL people actually want to paste —
 * impossible. M4.3 adds those children here.
 */
export const routes: Routes = [
  {
    path: 'subjects',
    title: 'Subjects · Concordat',
    loadComponent: () =>
      import('./features/registry/feature/subject-list-page').then((m) => m.SubjectListPage),
  },
  { path: '', pathMatch: 'full', redirectTo: 'subjects' },
  // Unknown paths land on the subject list rather than a bespoke 404: until there are
  // enough screens for a wrong URL to be interesting, a dead end costs more than a redirect.
  { path: '**', redirectTo: 'subjects' },
];
