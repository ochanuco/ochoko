#!/usr/bin/env bash
# Dispatch the deployer workflow for app ochoko, wait for the run and exit 0
# only when it concluded success.
set -euo pipefail

repo=$DEPLOYER_REPO
workflow=gpu-box-app.yml

body=$(jq -n --arg action "$ACTION" --arg version "$VERSION" --arg request_id "$REQUEST_ID" '{
  ref: "production",
  return_run_details: true,
  inputs: ({app: "ochoko", action: $action, request_id: $request_id}
    + (if $version != "" then {version: $version} else {} end))
}')
dispatched_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
for attempt in 1 2 3; do
  if run_id=$(gh api -X POST "repos/$repo/actions/workflows/$workflow/dispatches" --input - \
      --jq '.workflow_run_id // empty' <<<"$body" 2>"$RUNNER_TEMP/dispatch.err"); then
    break
  fi
  cat "$RUNNER_TEMP/dispatch.err" >&2
  if ! grep -q "HTTP 422" "$RUNNER_TEMP/dispatch.err" || (( attempt == 3 )); then
    exit 1
  fi
  sleep 10
done

if [[ -z "$run_id" ]]; then
  for _ in $(seq 12); do
    run_id=$(gh api "repos/$repo/actions/workflows/$workflow/runs?event=workflow_dispatch&created=>=$dispatched_at" 2>/dev/null |
      jq -r --arg id "$REQUEST_ID" '[.workflow_runs[] | select(.display_title | contains($id))][0].id // empty' || true)
    [[ -n "$run_id" ]] && break
    sleep 10
  done
fi
if [[ -z "$run_id" ]]; then
  echo "::error::dispatched, but no run of $workflow carries request_id $REQUEST_ID"
  exit 1
fi

url="https://github.com/$repo/actions/runs/$run_id"
echo "deployer run: $url"

deadline=$(( $(date +%s) + WAIT_MINUTES * 60 ))
while :; do
  # 一時的な API 失敗でジョブを落とさない。取れなければ次の周期まで待つ
  status=""
  conclusion="-"
  for _ in 1 2 3; do
    if line=$(gh api "repos/$repo/actions/runs/$run_id" --jq '"\(.status) \(.conclusion // "-")"'); then
      read -r status conclusion <<<"$line"
      break
    fi
    sleep 5
  done
  [[ "$status" == completed ]] && break
  if (( $(date +%s) >= deadline )); then
    conclusion="unknown"
    break
  fi
  sleep 30
done

failed_jobs=$(gh api "repos/$repo/actions/runs/$run_id/jobs" --paginate \
  --jq '.jobs[] | select(.conclusion == "failure") | .name' || true)

case "$conclusion" in
  success) result="success" ;;
  cancelled) result=$([[ -z "$failed_jobs" ]] && echo "superseded by a later call, did not run" || echo "cancelled after a failed job") ;;
  unknown) result="unknown: still $status after $WAIT_MINUTES min" ;;
  *) result="$conclusion" ;;
esac

{
  echo "### gpu-box-app: $ACTION $VERSION"
  echo
  echo "- run: $url"
  echo "- result: $result"
  [[ -n "$failed_jobs" ]] && echo "- failed jobs: $(paste -sd, - <<<"$failed_jobs")"
} >> "$GITHUB_STEP_SUMMARY"

if [[ "$conclusion" != success ]]; then
  echo "::error::gpu-box-app $ACTION $VERSION: $result ($url)"
  exit 1
fi
