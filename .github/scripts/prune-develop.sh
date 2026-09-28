#!/usr/bin/env bash
# usage: prune-develop.sh <keep>
# Deletes every develop draft, and every published develop release (with its tag) beyond the
# <keep> most recently published, in $RELEASE_REPO (default: this repository). Callers must hold
# the "develop" concurrency group, otherwise an in-flight run's draft could be deleted.
# DRY_RUN=1 lists what would be deleted.
set -euo pipefail

keep="${1:?usage: prune-develop.sh <keep>}"
repo="${RELEASE_REPO:-${GITHUB_REPOSITORY:-ErsatzTV/legacy}}"
[[ "$keep" =~ ^[0-9]+$ ]] || { echo "keep must be a non-negative integer" >&2; exit 1; }

releases="$(gh release list --repo "$repo" --limit 1000 --json tagName,isDraft,publishedAt)"

develop='[.[] | select(.tagName | test("^v[0-9]+\\.[0-9]+\\.[0-9]+-[0-9a-f]{8}-develop$"))]'
drafts="$(jq -r "$develop | map(select(.isDraft)) | .[].tagName" <<< "$releases")"
expired="$(jq -r --argjson keep "$keep" "$develop | map(select(.isDraft | not)) | sort_by(.publishedAt) | reverse | .[\$keep:] | .[].tagName" <<< "$releases")"

run() {
  if [[ -n "${DRY_RUN:-}" ]]; then echo "would run: $*"; else "$@"; fi
}

# drafts have no tag yet; it is only created on publish
for tag in $drafts; do
  echo "deleting draft $tag"
  run gh release delete "$tag" --repo "$repo" --yes
done

for tag in $expired; do
  echo "deleting $tag"
  run gh release delete "$tag" --repo "$repo" --cleanup-tag --yes
done
