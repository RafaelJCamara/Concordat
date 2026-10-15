# Cutting a release

A release is a tag. `vX.Y.Z` runs three workflows, each of which needs nothing beyond what is below, and none of
which runs on a pull request (they hold write permissions and CI must not).

| Workflow | Produces | Needs |
|---|---|---|
| [`publish-images.yml`](../.github/workflows/publish-images.yml) | `ghcr.io/<owner>/concordat-api`, `-web` and `-cli`, `linux/amd64` and `arm64`, tagged with the commit and the version (and `latest` unless the tag is a pre-release) | nothing: it uses the run's own token |
| [`release-cli.yml`](../.github/workflows/release-cli.yml) | native `concordat` binaries for linux-x64, linux-arm64, win-x64 and osx-arm64, with `SHA256SUMS`, attached to a GitHub release with generated notes | nothing |
| [`release-nuget.yml`](../.github/workflows/release-nuget.yml) | the nine SDK packages on nuget.org | the `NUGET_API_KEY` secret |

## Once, before the first release

1. **Decide the repository name.** The packages' metadata (`Directory.Build.props`) and the compose file's default
   image names say `Concordat`/`concordat-*`. Renaming the GitHub repository from `Signet` to `Concordat` before the
   first tag means the package pages, the release page and the `uses:` line for the GitHub Action all agree. Do it
   first: GitHub redirects the old name, but a package's project URL is fixed when it is published.
2. **Add the secret `NUGET_API_KEY`** (Settings → Secrets and variables → Actions): an API key from nuget.org with
   permission to push, scoped to the glob `Concordat.*`. Reserve the `Concordat` prefix on nuget.org if you want the
   packages marked as yours.
3. **After the first image push, make the three GHCR packages public** (your profile → Packages → each one →
   Package settings → Change visibility). They are private by default, and the compose file's defaults pull them
   anonymously.
4. **Set the Azure parameters** if you will use `deploy/azure`: `bootstrapToken` (`openssl rand -hex 32`) is required.

## Every release

1. **CI is green on the commit you will tag.** The `browser end-to-end` job is the one that runs the real stack.
2. **Cut a release candidate first**: `git tag v1.0.0-rc.1 && git push origin v1.0.0-rc.1`. A pre-release tag
   publishes the images under its version and does *not* take `latest`, and publishes the packages as a pre-release
   that nobody gets without asking. This is the dress rehearsal of everything below, with nothing to take back.
3. **Check what it published**, from a machine that is not yours if you can:

   ```bash
   docker pull ghcr.io/<owner>/concordat-api:1.0.0-rc.1
   dotnet new console -o try && cd try && dotnet add package Concordat.RabbitMq --version 1.0.0-rc.1 && dotnet build
   curl -L -o concordat https://github.com/<owner>/<repo>/releases/download/v1.0.0-rc.1/concordat-linux-x64
   ```

   and then follow [RUNNING.md §7](RUNNING.md#7-or-run-all-of-it-from-containers) from a fresh clone with the three
   image variables unset, which is the first run a stranger has: open the web app, create the owner, make an API key,
   run [the quickstart](QUICKSTART.md).
4. **Tag the release**: `git tag v1.0.0 && git push origin v1.0.0`. The same three workflows run; this time the
   images also take `latest`.
5. **Write the release notes**: the GitHub release is created with generated notes; paste in the matching section of
   [CHANGELOG.md](../CHANGELOG.md) at the top.

## If something goes wrong

- **A package pushed with a mistake cannot be replaced** on nuget.org: unlist it and release the next patch version.
  `--skip-duplicate` means re-running a tag run never overwrites anything.
- **A bad image** is superseded by a new tag; `latest` moves on promotion, and the `sha` tag of every build stays.
- **The workflow failed at the NuGet push** with "NUGET_API_KEY is not set": add the secret and re-run the failed
  job. The packages were built and are in the run's artifacts.
- **`gh run download` failed in the CLI release job**: the binaries are still in the run's artifacts, and the release
  can be created by hand with `gh release create`.

## What a release does not do

It does not publish the documentation anywhere, tag the Azure template, or announce itself. The Cloud edition and
the SDKs for other languages are not part of 1.0 ([ADR-024](adr/024-v1-ships-dotnet-only.md)).
