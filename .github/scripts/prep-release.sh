#!/usr/bin/env bash
# moves [Unreleased] into a dated [X.Y.Z] section and updates the compare links
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
changelog="${CHANGELOG:-$root/CHANGELOG.md}"
repo_url="https://github.com/${GITHUB_REPOSITORY:-ErsatzTV/legacy}"

if [[ $# -gt 0 ]]; then
  version="${1#v}"
else
  # the version develop builds have been announcing
  tag="$("$root/.github/scripts/develop-tag.sh" "$(git -C "$root" rev-parse HEAD)" | sed -n 's/^tag=//p')"
  version="${tag%%-*}"
  version="${version#v}"
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "usage: prep-release.sh [X.Y.Z]" >&2; exit 1; }

if grep -q "^## \[$version\]" "$changelog"; then
  echo "CHANGELOG.md already has a '## [$version]' section" >&2
  exit 1
fi

previous="$(sed -n "s#^\[Unreleased\]: $repo_url/compare/\(v[0-9.]*\)\.\.\.HEAD\$#\1#p" "$changelog")"
[[ -n "$previous" ]] || { echo "CHANGELOG.md has no '[Unreleased]: $repo_url/compare/vX.Y.Z...HEAD' link" >&2; exit 1; }
last_release="$(git -C "$root" describe --tags --abbrev=0 --match 'v[0-9]*' --exclude '*-develop' HEAD)"
if [[ "$previous" != "$last_release" ]]; then
  echo "[Unreleased] compares from $previous but the last release tag is $last_release" >&2
  exit 1
fi

out="$(mktemp)"
trap 'rm -f "$out"' EXIT
awk -v version="$version" -v date="$(date +%F)" -v previous="$previous" -v url="$repo_url" '
  { line = $0; sub(/^\xef\xbb\xbf/, "", line) }
  line == "## [Unreleased]" { print; print ""; print "## [" version "] - " date; next }
  line ~ /^\[Unreleased\]: / {
    print "[Unreleased]: " url "/compare/v" version "...HEAD"
    print "[" version "]: " url "/compare/" previous "...v" version
    next
  }
  { print }
' "$changelog" > "$out"
cp "$out" "$changelog"

# same check release.yml preflight runs
CHANGELOG="$changelog" "$root/.github/scripts/notes.sh" "v$version" > /dev/null

echo "CHANGELOG.md prepared for v$version (compare from $previous). Next:"
echo "  git commit -m 'prep for release v$version' CHANGELOG.md && git push origin main"
echo "  git tag v$version && git push origin v$version"
