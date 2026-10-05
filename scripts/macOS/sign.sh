#! /bin/bash

SCRIPT_FOLDER=$(dirname ${BASH_SOURCE[0]})
REPO_ROOT=$(realpath "$SCRIPT_FOLDER/../..")

APP_NAME="$REPO_ROOT/ErsatzTV-Legacy.app"
ENTITLEMENTS="$SCRIPT_FOLDER/ErsatzTV.entitlements"
SIGNING_IDENTITY="C3BBCFB2D6851FF0DCA6CAC06A3EF1ECE71F9FFF"
FFMPEG_BIN="$1"

codesign --force --verbose --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" --deep "$APP_NAME" || exit 1

# --deep would replace ffmpeg's own signature and entitlements
if [ -n "$FFMPEG_BIN" ]; then
    cp -p "$FFMPEG_BIN/ffmpeg" "$FFMPEG_BIN/ffprobe" "$APP_NAME/Contents/MacOS/" || exit 1
    codesign --force --verbose --timestamp --options=runtime --entitlements "$ENTITLEMENTS" --sign "$SIGNING_IDENTITY" "$APP_NAME" || exit 1
    codesign --verify --deep --strict --verbose=2 "$APP_NAME" || exit 1
fi
