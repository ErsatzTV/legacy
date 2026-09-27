#!/usr/bin/env bash
# usage: verify-draft.sh <tag>
# Fails unless <tag> is a draft release whose assets are exactly the expected set, all fully uploaded.
set -euo pipefail

tag="${1:?usage: verify-draft.sh <tag>}"
repo="${GITHUB_REPOSITORY:-ErsatzTV/legacy}"

expected="$(printf "ErsatzTV-Legacy-$tag-%s\n" \
  linux-arm64.tar.gz \
  linux-musl-x64.tar.gz \
  linux-x64.tar.gz \
  osx-arm64.dmg \
  osx-x64.dmg \
  win-x64.zip | sort)"

release="$(gh release view "$tag" --repo "$repo" --json isDraft,assets)"

if [[ "$(jq -r .isDraft <<< "$release")" != "true" ]]; then
  echo "$tag is not a draft release" >&2
  exit 1
fi

actual="$(jq -r '.assets[].name' <<< "$release" | sort)"
if [[ "$actual" != "$expected" ]]; then
  echo "asset mismatch for $tag (- expected, + actual):" >&2
  diff <(echo "$expected") <(echo "$actual") | grep '^[<>]' | sed 's/^</-/; s/^>/+/' >&2 || true
  exit 1
fi

incomplete="$(jq -r '.assets[] | select(.state != "uploaded" or .size == 0) | "\(.name) state=\(.state) size=\(.size)"' <<< "$release")"
if [[ -n "$incomplete" ]]; then
  echo "incomplete assets for $tag:" >&2
  echo "$incomplete" >&2
  exit 1
fi

echo "$tag: draft with $(wc -l <<< "$expected") expected assets, all uploaded"
