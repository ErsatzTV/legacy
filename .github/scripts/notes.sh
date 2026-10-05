#!/usr/bin/env bash
set -euo pipefail

tag="${1:?usage: notes.sh <tag> [<last release>]}"
repo="${GITHUB_REPOSITORY:-ErsatzTV/legacy}"
changelog="${CHANGELOG:-$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/CHANGELOG.md}"

# the last section ends at the link references
section() {
  awk -v heading="## [$1]" '
    { sub(/^\xef\xbb\xbf/, ""); sub(/\r$/, "") }
    found && (/^## \[/ || /^\[[^]]+\]: /) { exit }
    found && (started || NF) { started = 1; print }
    index($0, heading) == 1 { found = 1 }
    END { if (!found) exit 3 }
  ' "$changelog"
}

is_blank() { [[ -z "${1//[[:space:]]/}" ]]; }

if [[ "$tag" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
  version="${BASH_REMATCH[1]}"
  body="$(section "$version")" || { echo "CHANGELOG.md has no '## [$version]' section" >&2; exit 1; }
  is_blank "$body" && { echo "CHANGELOG.md section '## [$version]' is empty" >&2; exit 1; }
  printf '## Release Notes\n%s\n' "$body"
elif [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+-([0-9a-f]{8})-develop$ ]]; then
  sha="${BASH_REMATCH[1]}"
  # vX.Y.Z is the next release, not a tag yet
  base="${2:?develop tags need the last release tag as the second argument}"
  body="$(section Unreleased)" || { echo "CHANGELOG.md has no '## [Unreleased]' section" >&2; exit 1; }
  # normal right after a release
  is_blank "$body" && body="No changelog entries since $base."
  printf '## Release Notes\n%s\n\nFull changes: https://github.com/%s/compare/%s...%s\n' "$body" "$repo" "$base" "$sha"
else
  echo "unrecognized tag '$tag'; expected vX.Y.Z or vX.Y.Z-<sha8>-develop" >&2
  exit 1
fi
