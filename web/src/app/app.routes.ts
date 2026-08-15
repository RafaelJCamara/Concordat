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
    path: 'sign-in',
    title: 'Sign in · Concordat',
    loadComponent: () =>
      import('./features/identity/feature/sign-in-page').then((m) => m.SignInPage),
  },
  {
    path: 'subjects',
    title: 'Subjects · Concordat',
    loadComponent: () =>
      import('./features/registry/feature/subject-list-page').then((m) => m.SubjectListPage),
  },
  {
    // `:name` is a dotted subject name, which the router hands over already decoded — so a
    // subject containing a slash arrives here intact rather than as two segments, provided
    // whoever built the link encoded it. `routerLink` with an array does that for us.
    path: 'subjects/:name',
    title: 'Subject · Concordat',
    loadComponent: () =>
      import('./features/registry/feature/subject-detail-page').then((m) => m.SubjectDetailPage),
  },
  {
    // `:ordinal` is a string, not a number, because the API accepts the literal `latest`
    // here and so does this screen. A `latest` link keeps meaning "whatever is current"
    // after the next registration, which is the reason to paste one.
    path: 'subjects/:name/versions/:ordinal',
    title: 'Version · Concordat',
    loadComponent: () =>
      import('./features/registry/feature/version-detail-page').then((m) => m.VersionDetailPage),
  },
  {
    // The landing screen, on `/` rather than `/dashboard`. An overview is what the root of
    // an application is for, and a root that only redirects costs every visitor a
    // navigation to reach the page they were always going to get.
    path: '',
    pathMatch: 'full',
    title: 'Dashboard · Concordat',
    loadComponent: () =>
      import('./features/registry/feature/dashboard-page').then((m) => m.DashboardPage),
  },
  // Unknown paths land on the overview rather than a bespoke 404: until there are enough
  // screens for a wrong URL to be interesting, a dead end costs more than a redirect.
  { path: '**', redirectTo: '' },
];
