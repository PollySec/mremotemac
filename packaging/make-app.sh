#!/bin/zsh
# Builds a self-contained arm64 mRemoteMac.app in ./dist
set -e
ROOT=${0:A:h:h}
DIST=$ROOT/dist
APP=$DIST/mRemoteMac.app
CONF=${1:-Release}

echo "==> native shim"
$ROOT/native/build.sh >/dev/null

echo "==> dotnet publish ($CONF)"
dotnet publish $ROOT/src/MRemoteMac/MRemoteMac.csproj -c $CONF -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o $DIST/publish >/dev/null

echo "==> bundle"
rm -rf $APP
mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cp -R $DIST/publish/. $APP/Contents/MacOS/
cp $ROOT/packaging/Info.plist $APP/Contents/Info.plist

# icon: build .icns from the mRemoteNG icon
ICONSET=$DIST/icon.iconset
rm -rf $ICONSET; mkdir -p $ICONSET
SRC=$ROOT/src/MRemoteMac/Assets/Images/mRemoteNG_Icon.png
for s in 16 32 128 256 512; do
  sips -z $s $s $SRC --out $ICONSET/icon_${s}x${s}.png >/dev/null
  d=$((s*2)); sips -z $d $d $SRC --out $ICONSET/icon_${s}x${s}@2x.png >/dev/null
done
iconutil -c icns $ICONSET -o $APP/Contents/Resources/mRemoteMac.icns 2>/dev/null || echo "(iconutil failed, no icon)"

echo "==> codesign (ad hoc)"
codesign --force --deep --sign - $APP >/dev/null 2>&1 || true
echo "Done: $APP"
