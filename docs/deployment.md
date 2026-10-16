# Releases and deployment

`.github/workflows/release.yml` builds a release zip and publishes it as a
GitHub Release. It does not deploy: a deployer outside this repository picks
up Releases and installs them on the Windows machine. Without a deployer,
install a Release by hand as in [setup.md](setup.md).

## What the pipeline does

A push to `main` (or `workflow_dispatch` with a `ref` on `main`, to republish
an older commit):

1. `build` tests, publishes, and zips `ochoko-win-x64.zip` (exe, dependencies,
   `deploy/`, `release.json` with `version` and `commit`).
2. `publish` waits for approval in the `release` Environment, then publishes a
   GitHub Release `r<run_number>-<sha7>` with the zip, reusing an existing
   Release of the same commit. Add required reviewers to the Environment to
   gate releases; without them `publish` runs at once.
3. Releases older than the newest 3 are deleted with their tags.
4. `notify` starts the deployer's workflow, if the secrets below are set, and
   does not wait for it.

Release publishing uses the workflow's own `GITHUB_TOKEN`. Without the
secrets below, `notify` does nothing and the deployer has to find new Releases
itself.

| Secret | Value |
|---|---|
| `DEPLOYER_REPO` | `owner/name` of the deployer repository |
| `DEPLOYER_WORKFLOW` | file name of the deployer workflow; it is started on `main` by `workflow_dispatch` with no inputs, so it must look up the latest Release itself |
| `DEPLOYER_APP_CLIENT_ID` | Client ID of a GitHub App installed on the deployer repository with the Actions: write permission |
| `DEPLOYER_APP_PRIVATE_KEY` | private key of that App |

## The deployer contract

A deployer is expected to do what
[setup.md § Upgrading](setup.md#upgrading-and-rolling-back) does by hand:

1. Download `ochoko-win-x64.zip` from the Release and extract it to its own
   folder, for example `C:\apps\ochoko\releases\<version>`.
2. Run the current version's `deploy\stop.ps1` with that version's folder as
   the working directory and no arguments (`APP_NAME` may override the task
   and process name). Exit code 1 means a process survived; fail the deploy.
3. Switch the `C:\apps\ochoko\current` junction to the new folder.
4. Start the `ochoko` task.
5. Run the new version's `deploy\check.ps1`; exit code 1 means `/healthz` did
   not report the new version within 60 s.

`register.ps1` must have been run once on the machine, with `-Gateway` set to
the caller's address, before the first deploy.
