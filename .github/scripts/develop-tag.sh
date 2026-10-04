#!/usr/bin/env bash
# usage: develop-tag.sh <sha>   (prints last_release= and tag= lines for $GITHUB_OUTPUT)
# develop builds are versioned as prereleases of the next minor release (vYY.N.0, N restarting at 1
# each year) so they sort after the release they were built on
set -euo pipefail

sha="${1:?usage: develop-tag.sh <sha>}"
last_release=$(git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude '*-develop' "$sha")
IFS=. read -r yy n _ <<< "${last_release#v}"
year=$(date -u +%y)
if (( 10#$year > 10#$yy )); then
  base="v${year}.1.0"
else
  base="v${yy}.$((n + 1)).0"
fi
echo "last_release=${last_release}"
echo "tag=${base}-${sha:0:8}-develop"
