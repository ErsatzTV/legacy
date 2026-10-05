#! /bin/bash

SCRIPT_FOLDER=$(dirname ${BASH_SOURCE[0]})
REPO_ROOT=$(realpath "$SCRIPT_FOLDER/../..")

APP_NAME="$REPO_ROOT/ErsatzTV-Legacy.app"
PUBLISH_OUTPUT_DIRECTORY="$REPO_ROOT/publish/."

if [ -d "$APP_NAME" ]
then
    rm -rf "$APP_NAME"
fi

cd "$REPO_ROOT/ErsatzTV-macOS" || exit
xcodebuild build "$@"
cd "$REPO_ROOT" || exit
cp -R "$REPO_ROOT/ErsatzTV-macOS/build/Release/ErsatzTV-macOS.app" "$APP_NAME"

cp -a "$PUBLISH_OUTPUT_DIRECTORY" "$APP_NAME/Contents/MacOS"

# codesign fails on some files in dotted directories
pushd "$APP_NAME"/Contents/MacOS/wwwroot/_content/ || exit
for file in *; do
    if test -d "$file" && test ! -L "$file"; then
        FOLDER=$(basename "$file")
        mv "$file" "$APP_NAME/Contents/Resources/"
        ln -s "../../../Resources/$FOLDER" "$file"
    fi
done
popd || exit

chmod +x "$APP_NAME/Contents/MacOS/ErsatzTV"
chmod +x "$APP_NAME/Contents/MacOS/ErsatzTV.Scanner"
chmod +x "$APP_NAME/Contents/MacOS/ErsatzTV-macOS"
