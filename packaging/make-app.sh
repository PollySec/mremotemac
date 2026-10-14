#!/bin/zsh
# Builds a NativeAOT arm64 mRemoteMac.app (plus .zip and .dmg) in ./dist. Needs Xcode command line tools (clang).
set -e
ROOT=${0:A:h:h}
DIST=$ROOT/dist
APP=$DIST/mRemoteMac.app
CONF=${1:-Release}
PROJ=$ROOT/src/MRemoteMac/MRemoteMac.csproj

echo "==> native shim"
$ROOT/native/build.sh >/dev/null

VERSION=$(dotnet msbuild $PROJ -t:MinVer -getProperty:MinVerVersion -p:Configuration=$CONF 2>/dev/null | tail -1)
BUILD=$(git -C $ROOT rev-list --count HEAD)
echo "==> version $VERSION (build $BUILD)"

echo "==> dotnet publish ($CONF, NativeAOT)"
# NativeAOT: one native executable plus the native libraries, no .NET runtime or IL in the bundle.
rm -rf $DIST/publish
dotnet publish $PROJ -c $CONF -r osx-arm64 --self-contained true \
  -p:PublishAot=true -p:PublishReadyToRun=false -p:DebugType=none -o $DIST/publish >/dev/null

echo "==> bundle"
rm -rf $APP
mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cp -R $DIST/publish/. $APP/Contents/MacOS/
rm -rf $APP/Contents/MacOS/*.dSYM   # debug symbols stay in dist/publish, not in the app
# drop local symbols from the native libraries (exported symbols stay, so dlsym/LibraryImport are unaffected)
for f in $APP/Contents/MacOS/*.dylib; do strip -x "$f"; done
# Every library must come from the bundle or from macOS. A Homebrew path (json-c on the CI runner once) loads on the
# build machine and nowhere else, so the app would start without its connections on every other Mac.
LEAKS=$(for f in $APP/Contents/MacOS/mRemoteMac $APP/Contents/MacOS/*.dylib; do
  self=$(otool -arch arm64 -D "$f" | tail -n +2)
  otool -arch arm64 -L "$f" | tail -n +2 | awk '{print $1}' | while read -r dep; do
    case $dep in /usr/lib/*|/System/*|@rpath/*|@loader_path/*|@executable_path/*|"$self") ;; *) echo "  ${f:t} -> $dep" ;; esac
  done
done)
if [ -n "$LEAKS" ]; then
  echo "error: libraries outside the app bundle and macOS:\n$LEAKS" >&2
  exit 1
fi
sed -e "s/__VERSION__/${VERSION%%[+-]*}/; s/__BUILD__/$BUILD/" $ROOT/packaging/Info.plist > $APP/Contents/Info.plist

# icon: .icns from the 1024px app icon
ICONSET=$DIST/icon.iconset
rm -rf $ICONSET; mkdir -p $ICONSET
SRC=$ROOT/src/MRemoteMac/Assets/AppIcon.png
for s in 16 32 128 256 512; do
  sips -z $s $s $SRC --out $ICONSET/icon_${s}x${s}.png >/dev/null
  d=$((s*2)); sips -z $d $d $SRC --out $ICONSET/icon_${s}x${s}@2x.png >/dev/null
done
iconutil -c icns $ICONSET -o $APP/Contents/Resources/mRemoteMac.icns

echo "==> codesign"
if [ -n "$CODESIGN_IDENTITY" ]; then
  codesign --force --deep --options runtime --timestamp --sign "$CODESIGN_IDENTITY" $APP
else
  codesign --force --deep --sign - $APP
fi

echo "==> archives"
cd $DIST
rm -f mRemoteMac-$VERSION-macos-arm64.zip mRemoteMac-$VERSION-macos-arm64.dmg
ditto -c -k --keepParent mRemoteMac.app mRemoteMac-$VERSION-macos-arm64.zip
STAGE=$DIST/dmg-stage; rm -rf $STAGE; mkdir -p $STAGE
cp -R mRemoteMac.app $STAGE/
ln -s /Applications $STAGE/Applications
hdiutil create -quiet -volname "mRemoteMac $VERSION" -srcfolder $STAGE -ov -format UDZO mRemoteMac-$VERSION-macos-arm64.dmg
rm -rf $STAGE $ICONSET
echo "Done:"; ls -la $DIST/*.zip $DIST/*.dmg
