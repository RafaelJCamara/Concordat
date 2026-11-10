# Changelog

Notable changes, newest first. The detail behind each line is in [docs/STATUS.md](docs/STATUS.md) (what was
verified, and how) and the [architecture decision records](docs/adr/README.md) (why).

## 1.0.0 — self-hosted

The first release. Self-hosted only; the hosted Cloud edition and SDKs for languages other than .NET follow
([ADR-024](docs/adr/024-v1-ships-dotnet-only.md)).

### The registry

- **Versioned schemas** in JSON Schema (2020-12), Avro and Protobuf, with content-addressed, environment-portable
  ids; schemas are never deleted ([ADR-015](docs/adr/015-content-addressed-ids.md)).
- **Compatibility on two axes** — who breaks (backward, forward, full, and their transitive forms) and what breaks
  (wire, JSON, source) — with the exact JSON Pointer of every finding.
- **An approval gate** that lets a breaking version register for review without moving `latest`. A subject can name
  **who may decide** it ([ADR-043](docs/adr/043-a-subject-names-who-may-decide-its-held-versions.md)), and the
  approval queue is answered by the registry a page at a time.
- **Schema validity is enforced at registration:** a document that is not a valid schema, or whose `pattern` no
  engine accepts, is refused with the pointer to the defect; portability warnings (RE2, ECMA-262) are advice.
- **Subjects** are searched by name prefix, listed a page at a time (newest first on request:
  `sort=newest`, by creation time, ties by name; each subject carries `createdAt`;
  [ADR-049](docs/adr/049-subjects-can-be-listed-newest-first.md)), and can be **retired and, once retired,
  deleted for good** by an administrator, refused while a contract, a service or another schema refers to them
  ([ADR-046](docs/adr/046-a-retired-subject-can-be-deleted-for-good.md)).
- **Contracts** bind subjects to RabbitMQ topology (`vhost`, exchange and routing key on publish; queue on consume),
  with an enforcement mode per contract, and editable bindings.
- **Governance:** impact analysis, environment promotion, an audit trail of every change (as the authenticated
  caller), and notifications by webhook or email, with editable event filters.
- **Identity:** local accounts, roles, scoped API keys, sessions; read as well as write needs a credential.
- **Account lifecycle** ([ADR-048](docs/adr/048-an-account-can-change-its-password-be-reset-and-be-disabled.md)): a person
  changes their own password (`POST /v1/auth/password`; the other sessions end, API keys are untouched); an owner resets a
  member's to a temporary one they must replace, and **disables or enables** a member, which revokes every session and key
  they hold at once. The last owner who can sign in cannot be disabled or demoted, nobody disables or resets themselves, and a
  credential cannot act on an account that holds scopes it does not. Four new audit actions. **A forgotten owner password has a
  documented recovery** ([OPERATING.md](docs/OPERATING.md#accounts-and-a-lost-owner-password)). The refusal for the last owner
  on a demotion is now `409 last_owner` (it was `400 role_invalid`).
- **`GET …/approvals?subject=<exact name>`** narrows the approval queue to one subject's held versions, however far
  down the queue they sit. `scripts/seed-subjects.mjs` seeds an environment through the API for measurement.
  The code catalogue is now 99 codes (`subject_sort_invalid`).
- **A new instance answers nobody until it is claimed** ([ADR-047](docs/adr/047-a-new-instance-is-closed-until-it-is-claimed.md)).
  Set `CONCORDAT_BOOTSTRAP_TOKEN` on any host that is not your own machine.

### The web app

Dashboard, subjects (search, paging, create, retire, delete), versions, diff, impact analysis, approvals, contracts,
audit log and settings (environments, brokers, members, API keys, notifications, and your own account's password),
with members that can be reset, disabled and enabled, a schema editor that validates in a worker and a first-run
setup screen.

- **The subjects page lists the newest subjects first and is paged with page controls**
  ([ADR-052](docs/adr/052-the-subject-list-is-paged-with-page-controls-and-the-url.md), which supersedes the screen
  section of ADR-045): Previous, numbered pages, Next, a page size of 25, 50 or 100, and "Page 7 of 100 · subjects
  301–350 of 5,000 · newest first". The search prefix, page and size are in the address (`?q=&page=&size=`), so a
  reload, a shared link and Back land in the same place. The table shows when each subject was **created**, in place
  of the latest version's date it used to show under "Registered".
- **"N awaiting approval" on the subjects page is a link** to that subject's held version on the Approvals page,
  which can be narrowed to one subject (`?subject=&version=`), says so, opens the version's review and moves focus to it.
- **A held change can be read as a line diff** ([ADR-050](docs/adr/050-a-held-change-can-be-read-as-a-line-diff.md)).
  Opening it offers *Detailed* — the schema as a pull-request-style unified diff, unchanged context folded, the
  registry's findings on the lines they are about, for JSON Schema, Avro and Protobuf — or *Summary*, the findings
  list as before. The choice is remembered in the browser. A large schema is diffed in a worker without blocking the
  page; a change too large to read as lines says so and shows the summary.
- **A guided tour** for first-time visitors ([ADR-051](docs/adr/051-a-first-time-visitor-is-shown-a-guided-tour.md)):
  the first time a browser is signed in, and again after its site data is cleared, it walks through the dashboard,
  subjects, approvals, contracts, settings and API keys. It never opens over the sign-in, setup or forced
  password-change screens, can be skipped at any step (button or Escape), never traps focus or blocks the page,
  becomes a sheet on a phone, and is replayed from **Take the tour** in the sidebar. Built here, no library; it
  works under the Content-Security-Policy unchanged.
- **The app now boots in a browser that refuses `localStorage` outright** (the property getter throws); the theme
  read it unguarded.

### The .NET SDK

NuGet packages: `Concordat.RabbitMq` (enforcement on publish and consume for `RabbitMQ.Client`) and `Concordat.Client`
(the registry client, with fail-open/closed resolution), the format packages `Concordat.Formats.Json`, `.Avro` and
`.Protobuf`, `Concordat.Contracts` (a C# type as the source of truth for a subject, with drift checking at build
time) and `Concordat.Contracts.Testing`.

### The CLI and the CI gate

`concordat check | lint | push | promote | diff | impact | export | infer`, as a native binary per platform, a Docker
image and a GitHub Action, so a team that is not a .NET shop needs no .NET installed.

### A demo to watch

`samples/Demo` ([docs/DEMO.md](docs/DEMO.md)): four services send orders and payments over RabbitMQ under two contracts,
one `ENFORCE` and one `MONITOR`, with some messages wrong on purpose. It fills the web app with contracts, reported
violations and a breaking change held for approval, and shows a running service change behaviour when you approve the
change or switch a contract's mode. Built by CI.

### Operations

Container images for the registry, the web app and the CLI (`linux/amd64` and `linux/arm64`); a compose file for
evaluation; an Azure Container Apps template; health probes; migrations as a separate process.

### Known limits

See [docs/STATUS.md](docs/STATUS.md#holes-in-what-is-already-built). The ones to know first: the registry never
sees message traffic, so "coverage" is what contracts declare; the dashboard's version counts are computed, not
maintained (about 100 ms at 200,000 versions); and Cloud billing, SSO and the other-language SDKs are not part of v1.
