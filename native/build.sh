#!/bin/zsh
# Builds libmrrdp.dylib and bundles FreeRDP libraries into native/out
set -e
HERE=${0:A:h}
DEPS=${DEPS:-$HOME/RiderProjects/mRemoteMac-deps/freerdp/install}
OUT=$HERE/out
mkdir -p $OUT
clang -dynamiclib -arch arm64 -mmacosx-version-min=14.0 -O2 -fvisibility=hidden \
  -Wall -Wno-unused-parameter \
  -I$DEPS/include/freerdp3 -I$DEPS/include/winpr3 \
  $HERE/mrrdp.c $HERE/kbd_mac.c \
  -L$DEPS/lib -lfreerdp-client3 -lfreerdp3 -lwinpr3 -framework Carbon \
  -Wl,-rpath,@loader_path \
  -install_name @rpath/libmrrdp.dylib \
  -o $OUT/libmrrdp.dylib
for f in libfreerdp-client3.3.dylib libfreerdp3.3.dylib libwinpr3.3.dylib; do
  cp -f $DEPS/lib/$f $OUT/$f
  chmod u+w $OUT/$f
  install_name_tool -add_rpath @loader_path $OUT/$f 2>/dev/null || true
done
codesign -f -s - $OUT/*.dylib 2>/dev/null || true
echo "built:"; ls -la $OUT
