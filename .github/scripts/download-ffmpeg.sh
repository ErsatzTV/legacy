#!/usr/bin/env bash
# extracts the pinned ErsatzTV-ffmpeg build for <target> (its target names, e.g. macosarm64) into <dir>
set -euo pipefail

target="${1:?usage: download-ffmpeg.sh <target> <dir>}"
dir="${2:?usage: download-ffmpeg.sh <target> <dir>}"
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
pin="$("$root/.github/scripts/ffmpeg-pin.sh")"
read -r tag repo _ <<< "$pin"
sums="$(jq -er .sha256sums "$root/.github/ffmpeg.json")"

version="${tag%%-*}"
if [[ "$tag" =~ -([0-9a-f]{8})-develop$ ]]; then extra="etv.dev.${BASH_REMATCH[1]}"; else extra="etv.${tag#*-}"; fi
base="ffmpeg-n${version}-${extra}-${target}-gpl-${version%.*}"
case "$target" in
  win*) asset="${base}.zip" ;;
  *) asset="${base}.tar.xz" ;;
esac

sha256() { if command -v sha256sum > /dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi; }

# relative: Git Bash on Windows doesn't convert /tmp paths inside native args like 7z -o
tmp="$(mktemp -d ffmpeg-download.XXXXXX)"
trap 'rm -rf "$tmp"' EXIT

# exact name, so a bad pin or target fails instead of matching something else
gh release download "$tag" --repo "$repo" --pattern SHA256SUMS --pattern "$asset" --dir "$tmp"
actual="$(sha256 "$tmp/SHA256SUMS" | cut -d' ' -f1)"
[[ "$actual" == "$sums" ]] || { echo "SHA256SUMS of $tag hashes to $actual, pinned $sums" >&2; exit 1; }
(cd "$tmp" && awk -v f="$asset" '$2 == f' SHA256SUMS | grep . | sha256 -c -)
case "$asset" in
  *.zip) 7z x -bso0 -o"$tmp" "$tmp/$asset" ;;
  *) tar xJf "$tmp/$asset" -C "$tmp" ;;
esac

mkdir -p "$dir"
cp -a "$tmp/$base/." "$dir/"
echo "ffmpeg $tag ($repo) $target -> $dir"
