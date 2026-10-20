#!/usr/bin/env bash
# With AGE_PATH set, AgeEncryptorTests fail instead of skip if the CLI is missing
set -euo pipefail

version=v1.3.2
case "${RUNNER_OS}-${RUNNER_ARCH}" in
  Linux-X64)   asset=linux-amd64.tar.gz;  sha256=cbe24006683f8eb669266162894b9a522a1af52f2665fbc63a4bb032ed26ac10 ;;
  Linux-ARM64) asset=linux-arm64.tar.gz;  sha256=6b8dc4333c53a5a57c9e5834e3a48f92605d7154014cd07269ff3327db5d37f4 ;;
  macOS-ARM64) asset=darwin-arm64.tar.gz; sha256=e2020b073c44f692685a24d6abc378817eb81ffaaf49fd0531ef8565f767f2f5 ;;
  Windows-X64) asset=windows-amd64.zip;   sha256=f48d8f8f9ebe903ab5027ed067652f2cc1db94bc206976430133b905dcd8e8c7 ;;
  *) echo "no pinned age build for ${RUNNER_OS}-${RUNNER_ARCH}" >&2; exit 1 ;;
esac

archive="${RUNNER_TEMP}/age-${version}-${asset}"
curl -sSfL -o "$archive" "https://github.com/FiloSottile/age/releases/download/${version}/age-${version}-${asset}"

if command -v sha256sum >/dev/null; then
  echo "${sha256}  ${archive}" | sha256sum -c -
else
  echo "${sha256}  ${archive}" | shasum -a 256 -c -
fi

case "$asset" in
  *.zip) unzip -q -o "$archive" -d "$RUNNER_TEMP" ;;
  *) tar -xzf "$archive" -C "$RUNNER_TEMP" ;;
esac

echo "AGE_PATH=${RUNNER_TEMP}/age" >> "$GITHUB_ENV"
