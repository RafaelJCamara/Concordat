import type { Routes } from '@angular/router';
import {
  accountGuard,
  orgAdminGuard,
  schemaWriteGuard,
  signedInGuard,
} from './core/auth/scope-guard';

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
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/subject-list-page').then((m) => m.SubjectListPage),
  },
  {
    /*
     * **This must stay above `subjects/:name`, and its last segment is not `new`.** The router
     * matches in order, so below it `/subjects/new-subject` would open the detail screen of a
     * subject called `new-subject`. And it is not `/subjects/new`, because `new` is a perfectly
     * good subject name — the registry accepts it — so that route would hide one. A hyphen is
     * outside the name grammar (`subject-name.spec.ts` pins that), so no subject can ever be named
     * this, and the create screen cannot shadow one.
     *
     * A write route, so it carries the guard `NewVersionPage` does: the same constant the
     * "New subject" buttons read, so the route and the affordances that point at it cannot drift.
     */
    path: 'subjects/new-subject',
    title: 'New subject · Concordat',
    canActivate: [schemaWriteGuard],
    loadComponent: () =>
      import('./features/registry/feature/new-subject-page').then((m) => m.NewSubjectPage),
  },
  {
    // `:name` is a dotted subject name, which the router hands over already decoded — so a
    // subject containing a slash arrives here intact rather than as two segments, provided
    // whoever built the link encoded it. `routerLink` with an array does that for us.
    path: 'subjects/:name',
    title: 'Subject · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/subject-detail-page').then((m) => m.SubjectDetailPage),
  },
  {
    /*
     * **This must stay above `versions/:ordinal`.** The router matches in order, so with the
     * two swapped, `/subjects/x/versions/new` matches the detail screen with `ordinal: 'new'`
     * — which resolves to nothing and renders "no version 'new'", a 404 for the one route
     * that is not missing.
     *
     * The app's first write route, and therefore the first `scopeGuard` is attached to. The
     * guard was built and unit-tested in M4.2 and referenced by nothing until now.
     */
    path: 'subjects/:name/versions/new',
    title: 'Register a version · Concordat',
    canActivate: [schemaWriteGuard],
    loadComponent: () =>
      import('./features/registry/feature/new-version-page').then((m) => m.NewVersionPage),
  },
  {
    // `:ordinal` is a string, not a number, because the API accepts the literal `latest`
    // here and so does this screen. A `latest` link keeps meaning "whatever is current"
    // after the next registration, which is the reason to paste one.
    path: 'subjects/:name/versions/:ordinal',
    title: 'Version · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/version-detail-page').then((m) => m.VersionDetailPage),
  },
  {
    // The API's own path with the resource prefix dropped, so a link read out of a registry
    // log and one pasted from the address bar are the same shape. Six segments against the
    // version screen's four, so neither can shadow the other and order does not matter here.
    path: 'subjects/:name/versions/:from/diff/:to',
    title: 'Compare versions · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/compatibility-diff-page').then(
        (m) => m.CompatibilityDiffPage,
      ),
  },
  {
    // `:ordinal` takes `latest` here too; the screen names the version it resolved to.
    path: 'subjects/:name/versions/:ordinal/impact',
    title: 'Impact · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/impact-analysis-page').then((m) => m.ImpactAnalysisPage),
  },
  {
    // Readable by any signed-in caller: DESIGN §9 has non-admins see the pending diff and impact
    // without the decision controls, which are `*cdIfScope` inside the page. Guarding the route
    // by scope would hide the queue from the people who most need to see what is waiting.
    path: 'approvals',
    title: 'Approvals · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/approvals-page').then((m) => m.ApprovalsPage),
  },
  {
    // Any read scope admits a caller to the trail, so this is a plain read route: the audit log
    // spans every resource type and a reader should be able to browse all of it.
    path: 'audit',
    title: 'Audit log · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/governance/feature/audit-log-page').then((m) => m.AuditLogPage),
  },
  {
    path: 'contracts',
    title: 'Contracts · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/contracts/feature/contracts-page').then((m) => m.ContractsPage),
  },
  {
    // Resource identity in the path, like subjects. The write forms are on this screen and are
    // `*cdIfScope` inside it, so there is no separate write route to guard.
    path: 'contracts/:name',
    title: 'Contract · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/contracts/feature/contract-detail-page').then((m) => m.ContractDetailPage),
  },
  {
    // The settings area opens on its first tab. A redirect rather than a screen of its own: an
    // index with nothing on it is a click every visitor pays to reach the thing they came for.
    path: 'settings',
    pathMatch: 'full',
    redirectTo: 'settings/environment',
  },
  {
    // Readable by any signed-in caller; the edit form is `*cdIfScope` inside the page.
    path: 'settings/environment',
    title: 'Environment settings · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/settings/feature/environment-settings-page').then(
        (m) => m.EnvironmentSettingsPage,
      ),
  },
  {
    path: 'settings/brokers',
    title: 'Brokers · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/settings/feature/brokers-page').then((m) => m.BrokersPage),
  },
  {
    // The whole members endpoint group is `org:admin`, reads included, so there is no read-only
    // version to fall back to: a route guard redirects anyone else rather than rendering a screen
    // that would fail every request it makes.
    path: 'settings/members',
    title: 'Members · Concordat',
    canActivate: [signedInGuard, orgAdminGuard],
    loadComponent: () =>
      import('./features/settings/feature/members-page').then((m) => m.MembersPage),
  },
  {
    path: 'settings/api-keys',
    title: 'API keys · Concordat',
    canActivate: [signedInGuard, orgAdminGuard],
    loadComponent: () =>
      import('./features/settings/feature/api-keys-page').then((m) => m.ApiKeysPage),
  },
  {
    path: 'settings/notifications',
    title: 'Notifications · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/settings/feature/notifications-page').then((m) => m.NotificationsPage),
  },
  {
    // Anybody who is signed in, with no scope: everybody owns their own password. The one route that
    // also admits a session that must replace a password an administrator set (ADR-048), which is why
    // it has its own guard and not `signedInGuard`, which sends that session here.
    path: 'settings/account',
    title: 'Account · Concordat',
    canActivate: [accountGuard],
    loadComponent: () =>
      import('./features/settings/feature/account-page').then((m) => m.AccountPage),
  },
  {
    // The landing screen, on `/` rather than `/dashboard`. An overview is what the root of
    // an application is for, and a root that only redirects costs every visitor a
    // navigation to reach the page they were always going to get.
    path: '',
    pathMatch: 'full',
    title: 'Dashboard · Concordat',
    canActivate: [signedInGuard],
    loadComponent: () =>
      import('./features/registry/feature/dashboard-page').then((m) => m.DashboardPage),
  },
  // Unknown paths land on the overview rather than a bespoke 404: until there are enough
  // screens for a wrong URL to be interesting, a dead end costs more than a redirect.
  { path: '**', redirectTo: '' },
];
