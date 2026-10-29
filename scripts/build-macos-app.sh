#!/bin/bash
# 將本專案打包成 macOS 單一執行檔 .app
#   用法：./scripts/build-macos-app.sh [osx-arm64|osx-x64] [輸出.zip]
#   產出：dist/訂閱管理.app（若給第二個參數，另外壓成 zip）
set -euo pipefail

RID="${1:-osx-arm64}"
ZIP_OUT="${2:-}"
APP_NAME="訂閱管理"
EXEC_NAME="YoutubeBilibiliSubscription"
BUNDLE_ID="com.feng33.youtubesubscription"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION="$(grep -o '<Version>[^<]*' YoutubeBilibiliSubscription.csproj | head -1 | cut -d'>' -f2)"
[ -n "$VERSION" ] || VERSION="1.0.0"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> 1/4 產生 .icns 圖示"
ICONSET="$STAGE/AppIcon.iconset"
mkdir -p "$ICONSET"
sips -s format png "Assets/avalonia-logo.ico" --out "$STAGE/base.png" >/dev/null
for s in 16 32 64 128 256 512 1024; do
  sips -z "$s" "$s" "$STAGE/base.png" --out "$STAGE/i_$s.png" >/dev/null
done
cp "$STAGE/i_16.png"   "$ICONSET/icon_16x16.png"
cp "$STAGE/i_32.png"   "$ICONSET/icon_16x16@2x.png"
cp "$STAGE/i_32.png"   "$ICONSET/icon_32x32.png"
cp "$STAGE/i_64.png"   "$ICONSET/icon_32x32@2x.png"
cp "$STAGE/i_128.png"  "$ICONSET/icon_128x128.png"
cp "$STAGE/i_256.png"  "$ICONSET/icon_128x128@2x.png"
cp "$STAGE/i_256.png"  "$ICONSET/icon_256x256.png"
cp "$STAGE/i_512.png"  "$ICONSET/icon_256x256@2x.png"
cp "$STAGE/i_512.png"  "$ICONSET/icon_512x512.png"
cp "$STAGE/i_1024.png" "$ICONSET/icon_512x512@2x.png"
iconutil -c icns "$ICONSET" -o "$STAGE/app.icns"

echo "==> 2/4 發佈單一檔案 ($RID, v$VERSION)"
dotnet publish -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=none \
  -o "$STAGE/publish"

echo "==> 3/4 組裝 .app bundle"
APP="dist/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$STAGE/publish/$EXEC_NAME" "$APP/Contents/MacOS/$EXEC_NAME"
chmod +x "$APP/Contents/MacOS/$EXEC_NAME"
cp "$STAGE/app.icns" "$APP/Contents/Resources/app.icns"
printf 'APPL????' > "$APP/Contents/PkgInfo"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleExecutable</key><string>$EXEC_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleIconFile</key><string>app.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleDevelopmentRegion</key><string>zh_TW</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.social-networking</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
</dict>
</plist>
PLIST

echo "==> 4/4 Ad-hoc 簽名"
codesign --force --sign - --identifier "$BUNDLE_ID" --timestamp=none "$APP"
codesign --verify --strict "$APP"

if [ -n "$ZIP_OUT" ]; then
  echo "==> 壓縮 $ZIP_OUT"
  python3 "$ROOT/scripts/pack-app.py" "$APP" "$ZIP_OUT"
fi

echo ""
echo "完成：$ROOT/$APP  ($(du -sh "$APP" | cut -f1))"
