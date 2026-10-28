#!/usr/bin/env bash
# usage: ffmpeg-pin.sh [check]
# prints "<tag> <repo> <image>" for the ErsatzTV-ffmpeg build pinned in .github/ffmpeg.json
# check: also verifies the pin against GitHub and GHCR, and that every Dockerfile defaults to it
set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
pin="$root/.github/ffmpeg.json"
tag="$(jq -er .tag "$pin")"
digest="$(jq -er .digest "$pin")"
sums="$(jq -er .sha256sums "$pin")"
last_release="$(jq -r '.last_release // empty' "$pin")"

if [[ "$tag" =~ ^[0-9]+\.[0-9]+\.[0-9]+-[0-9]+$ ]]; then
  repo=ErsatzTV/ErsatzTV-ffmpeg image=ghcr.io/ersatztv/ersatztv-ffmpeg
  [[ -z "$last_release" ]] || { echo "last_release is only for develop pins in .github/ffmpeg.json" >&2; exit 1; }
elif [[ "$tag" =~ ^([0-9]+\.[0-9]+\.[0-9]+)-[0-9a-f]{8}-develop$ ]]; then
  repo=ErsatzTV/ffmpeg-develop-builds image=ghcr.io/ersatztv/ersatztv-ffmpeg-develop
  # the health check's version floor; the develop build's release notes name it
  [[ "$last_release" =~ ^${BASH_REMATCH[1]//./\\.}-[0-9]+$ ]] ||
    { echo "develop pin needs last_release ${BASH_REMATCH[1]}-N in .github/ffmpeg.json, got '$last_release'" >&2; exit 1; }
else
  echo "unrecognized ffmpeg tag '$tag' in .github/ffmpeg.json; expected X.Y.Z-N or X.Y.Z-<sha8>-develop" >&2
  exit 1
fi
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "bad digest '$digest' in .github/ffmpeg.json" >&2; exit 1; }
[[ "$sums" =~ ^[0-9a-f]{64}$ ]] || { echo "bad sha256sums '$sums' in .github/ffmpeg.json" >&2; exit 1; }
ref="$image:$tag@$digest"

if [[ "${1:-}" == check ]]; then
  fail() { echo "ffmpeg pin $tag: $1" >&2; exit 1; }
  dockerfiles="$(git -C "$root" ls-files '*Dockerfile')"
  [[ -n "$dockerfiles" ]] || fail "no Dockerfiles found"
  while read -r f; do
    others="$(grep -n 'ersatztv-ffmpeg' "$root/$f" | grep -v '^[0-9]*:ARG FFMPEG_IMAGE=' || true)"
    [[ -z "$others" ]] || fail "$f names ersatztv-ffmpeg outside ARG FFMPEG_IMAGE: $others"
    default="$(sed -n 's/^ARG FFMPEG_IMAGE=//p' "$root/$f")"
    [[ -z "$default" || "$default" == "$ref" ]] || fail "$f ARG FFMPEG_IMAGE is '$default', expected '$ref'"
  done <<< "$dockerfiles"

  is_draft="$(gh release view "$tag" --repo "$repo" --json isDraft --jq .isDraft)" || fail "no release in $repo"
  [[ "$is_draft" == false ]] || fail "release in $repo is a draft"
  if [[ -n "$last_release" ]]; then
    gh release view "$last_release" --repo ErsatzTV/ErsatzTV-ffmpeg --json tagName > /dev/null ||
      fail "last_release $last_release has no release in ErsatzTV/ErsatzTV-ffmpeg"
    gh release view "$tag" --repo "$repo" --json body --jq .body | grep -qF "\`$last_release\`" ||
      fail "the $tag release notes don't name last_release $last_release"
  fi
  actual="$(gh release download "$tag" --repo "$repo" --pattern SHA256SUMS --output - | sha256sum | cut -d' ' -f1)"
  [[ "$actual" == "$sums" ]] || fail "SHA256SUMS hashes to $actual, pinned $sums"
  actual="$(docker buildx imagetools inspect "$image:$tag" --format '{{json .Manifest}}' | jq -er .digest)"
  [[ "$actual" == "$digest" ]] || fail "$image:$tag is $actual, pinned $digest"
fi

echo "$tag $repo $ref"
