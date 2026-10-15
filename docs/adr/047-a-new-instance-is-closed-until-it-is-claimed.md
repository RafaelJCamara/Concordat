# ADR-047: A new instance is closed until it is claimed

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** Rafael Camara

## Context

[ADR-008](008-built-in-identity.md) promised that `docker compose up` produces something usable with no account, and
M8.2 built that as `AllowAnonymousUntilClaimed`, **on by default**: an instance with no accounts answered every request as an
owner, and stopped the moment the first account existed. Decision 27 made it audible (a warning on a loop, a banner) and
added a bootstrap token for the deploy-to-claim race. It stayed on, recorded as "still on" in STATUS, because the alternative
broke the five-minute evaluation.

That was the right trade for a project being built and the wrong one for a product being shipped. A v1 that a stranger
deploys to a reachable host would start as a registry where **anyone can read every contract and write any of them**, for as
long as it takes the operator to find the warning, and the only defence (the bootstrap token) is optional and
unset by default. The registry decides what reaches production.

## Decision

**Off by default.** `AuthenticationOptions.AllowAnonymousUntilClaimed` is `false`. A fresh instance answers `401` to every
request except the ones that claim it: `GET /v1/auth/status` (so a client can tell "nobody has set this up" from "you are
signed out"), `POST /v1/auth/bootstrap` and sign-in, plus the health probes an orchestrator needs.

- **The first-run path is the web app's setup screen**, which already existed and now has a **Setup token** box for a
  deployment that set `CONCORDAT_BOOTSTRAP_TOKEN`. Or `curl -X POST /v1/auth/bootstrap`. Claiming still happens exactly once.
- **The opt-in stays**, for the evaluation it was built for: `Concordat__Authentication__AllowAnonymousUntilClaimed=true`
  (compose: `CONCORDAT_ALLOW_ANONYMOUS_UNTIL_CLAIMED=true`). Behaviour is unchanged when it is on, including that it turns
  itself off the moment an account exists and that the warning repeats while it applies.
- **The quickstart sample takes `CONCORDAT_API_KEY`**, and without one it stops at `401` with a message that says why.
- **A credential that was presented and failed is still `401`**, as before; nothing here changes the claimed-instance rules
  (ADR-027).

## What it costs

- **The first run is one step longer.** Open the page, create an owner, make an API key for the sample. That is the cost of
  not handing out an owner to whoever finds the port, and it is recorded in the quickstart and in RUNNING.md step 4.
- **The claim itself is still open until it happens.** Without a bootstrap token, whoever reaches `/v1/auth/bootstrap` first
  owns the instance. Closing the default narrows this from "everything, indefinitely" to "one request, once", and the docs say
  to set a token on any host that is not your own machine; the Azure template already requires one.
- **A script that relied on the open default stops working** on a fresh install and needs a key. Nothing stored changes; an
  instance that already has an account is unaffected, because the option only ever applied before the first one.
- **The test suites opt in explicitly**: `ApiFactory` and the end-to-end `StackFixture` set the option, and one class
  (`FreshInstanceTests`) runs a host with it absent and asserts what a fresh install does.

## If you overturn it

Change the property's initial value back to `true` and delete `FreshInstanceTests`' first two tests; the docs, the compose
variable and the sample's key handling are all additive and stay correct.
