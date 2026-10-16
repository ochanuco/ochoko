# Deployment pipeline

`.github/workflows/deploy.yml` builds a release and hands it to a deployer
workflow in another repository, which installs it on the Windows machine. The
deployer is not part of this repository. A fork without one can still use the
Releases this pipeline publishes and install them as in [setup.md](setup.md).

## What the pipeline does

A push to `main` (or `workflow_dispatch` with a `ref` on `main`, which is how a
rollback is done):

1. `build` tests, publishes, and zips `ochoko-win-x64.zip` (exe, dependencies,
   `deploy/`, `release.json` with `version` and `commit`).
2. `deploy` waits for approval in the `production` Environment, then publishes
   a GitHub Release `r<run_number>-<sha7>` with the zip, reusing an existing
   Release of the same commit.
3. It dispatches the deployer workflow and waits up to 230 minutes for it to
   succeed (`.github/actions/gpu-box-app`).
4. Releases older than the newest 3 are deleted with their tags.

## Repository settings

| Setting | Kind | Value |
|---|---|---|
| `production` | Environment | Add required reviewers to gate deploys |
| `DEPLOYER_REPO` | Environment or repository secret | `owner/name` of the deployer repository. A secret rather than a variable so that public run logs mask it |
| `OP_SA_DEPLOY` | Environment or repository secret | 1Password service account token |

The service account must read the item `op://gh-deploy/gpu-box-dispatch` with
fields `client_id` and `private_key`: the Client ID and private key of a GitHub
App installed on the deployer repository with the Actions: write permission.
Release publishing uses the workflow's own `GITHUB_TOKEN`.

## The deployer contract

ochoko dispatches `gpu-box-app.yml` on the `production` branch of
`DEPLOYER_REPO` with these `workflow_dispatch` inputs:

| Input | Value |
|---|---|
| `app` | `ochoko` |
| `action` | `deploy` (the pipeline only sends this) or `restart` |
| `version` | the Release tag, for `deploy` |
| `request_id` | `<this repository>#<run id>` |

The dispatch asks for the run id in the response. If the API does not return
one, ochoko looks for a run whose title contains `request_id`, so the
deployer's `run-name` must include it. ochoko succeeds only when the run
concludes `success`; a `cancelled` run fails the deploy.

For `deploy`, the deployer is expected to do what
[setup.md § Upgrading](setup.md#upgrading-and-rolling-back) does by hand:

1. Download `ochoko-win-x64.zip` from the Release and extract it to
   `C:\apps\ochoko\releases\<version>`.
2. Run the current version's `deploy\stop.ps1` with that version's folder as
   the working directory and no arguments (`APP_NAME` may override the task
   and process name). Exit code 1 means a process survived; fail the deploy.
3. Switch the `C:\apps\ochoko\current` junction to the new folder.
4. Start the `ochoko` task.
5. Run the new version's `deploy\check.ps1`; exit code 1 means `/healthz` did
   not report the new version within 60 s.

`register.ps1` must have been run once on the machine before the first deploy.
