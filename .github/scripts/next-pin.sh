#!/usr/bin/env bash
# prints "<tag> <repo>" for the next build pinned in .github/next-tag
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
tag="$(tr -d '[:space:]' < "$root/.github/next-tag")"

if [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "$tag ErsatzTV/next"
elif [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+-[0-9a-f]{8}-develop$ ]]; then
  echo "$tag ErsatzTV/next-develop-builds"
else
  echo "unrecognized next tag '$tag' in .github/next-tag; expected vX.Y.Z or vX.Y.Z-<sha8>-develop" >&2
  exit 1
fi
