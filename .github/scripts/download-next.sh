#!/usr/bin/env bash
# extracts the pinned next build for <target> (next's target names, e.g. linux-x64) into <dir>
set -euo pipefail

target="${1:?usage: download-next.sh <target> <dir>}"
dir="${2:?usage: download-next.sh <target> <dir>}"
pin="$("$(dirname "$0")/next-pin.sh")"
read -r tag repo <<< "$pin"

base="ersatztv-next-${tag}-${target}"
case "$target" in
  windows-*) asset="${base}.zip" ;;
  *) asset="${base}.tar.gz" ;;
esac

# relative: Git Bash on Windows doesn't convert /tmp paths inside native args like 7z -o
tmp="$(mktemp -d next-download.XXXXXX)"
trap 'rm -rf "$tmp"' EXIT

# exact name, so a bad pin or target fails instead of matching something else
gh release download "$tag" --repo "$repo" --pattern "$asset" --dir "$tmp"
case "$asset" in
  *.zip) 7z x -bso0 -o"$tmp" "$tmp/$asset" ;;
  *) tar xzf "$tmp/$asset" -C "$tmp" ;;
esac

mkdir -p "$dir"
cp -a "$tmp/$base/." "$dir/"
echo "next $tag ($repo) $target -> $dir"
