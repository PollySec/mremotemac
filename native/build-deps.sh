#!/bin/zsh
# Downloads and builds CMake (binary), OpenSSL 3.5 (static) and FreeRDP 3 (shared libs) for arm64 into ../mRemoteMac-deps:
#   freerdp/install         the client libraries the app ships
#   freerdp/install-server  the same FreeRDP with the sample server, for native/nla-test.sh and tools/clipsrv
#
# Nothing in a release may depend on files that only exist on the build machine. OpenSSL is built with no-module, so
# its legacy provider is compiled in instead of being loaded from <prefix>/lib/ossl-modules at run time, and FreeRDP
# uses its own MD4, MD5 and RC4 (NTLM needs them for NLA), exactly like native/build-deps-win.ps1. 1.5.0 shipped
# without either: NLA worked on the build machine, where the module existed, and failed on every other Mac.
set -e
ROOT=${0:A:h:h}
D=${DEPS_ROOT:-$ROOT/../mRemoteMac-deps}
mkdir -p $D/cmake $D/openssl $D/freerdp
CMAKE_VER=4.4.4
OPENSSL_VER=3.5.9
OPENSSL_SHA256=603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a
FREERDP_VER=3.32.1

# A local deps directory built by an older version of this script would silently keep its old options (CI is keyed on
# the hash of this file and always starts clean), so any change here rebuilds OpenSSL and FreeRDP.
SIG=$(shasum -a 256 ${0:A} | cut -c1-16)
if [ -e $D/openssl/install -o -e $D/freerdp/install ] && [ "$(cat $D/.build-deps.sig 2>/dev/null)" != "$SIG" ]; then
  echo "==> build-deps.sh changed since $D was built; rebuilding OpenSSL and FreeRDP"
  rm -rf $D/openssl/install $D/openssl/openssl-$OPENSSL_VER $D/freerdp/build $D/freerdp/install \
         $D/freerdp/build-server $D/freerdp/install-server $D/.build-deps.sig
fi

if [ ! -x $D/cmake/cmake-$CMAKE_VER-macos-universal/CMake.app/Contents/bin/cmake ]; then
  echo "==> cmake $CMAKE_VER"
  curl -sfL -o $D/cmake/cmake.tar.gz https://github.com/Kitware/CMake/releases/download/v$CMAKE_VER/cmake-$CMAKE_VER-macos-universal.tar.gz
  tar xzf $D/cmake/cmake.tar.gz -C $D/cmake
fi
CMAKE=$D/cmake/cmake-$CMAKE_VER-macos-universal/CMake.app/Contents/bin/cmake

if [ ! -f $D/openssl/install/lib/libssl.a ]; then
  echo "==> openssl $OPENSSL_VER"
  curl -sfL -o $D/openssl/openssl.tar.gz https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VER/openssl-$OPENSSL_VER.tar.gz
  echo "$OPENSSL_SHA256  $D/openssl/openssl.tar.gz" | shasum -a 256 -c --quiet
  rm -rf $D/openssl/openssl-$OPENSSL_VER
  tar xzf $D/openssl/openssl.tar.gz -C $D/openssl
  (cd $D/openssl/openssl-$OPENSSL_VER && ./Configure darwin64-arm64-cc no-shared no-module no-tests no-docs --prefix=$D/openssl/install >/dev/null && make -j$(sysctl -n hw.ncpu) >/dev/null && make install_sw >/dev/null)
fi

[ -d $D/freerdp/src ] || git clone --depth 1 --branch $FREERDP_VER https://github.com/FreeRDP/FreeRDP.git $D/freerdp/src
FREERDP_OPTS=(-DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0
  -DOPENSSL_ROOT_DIR=$D/openssl/install -DOPENSSL_USE_STATIC_LIBS=TRUE
  -DWITH_INTERNAL_RC4=ON -DWITH_INTERNAL_MD4=ON -DWITH_INTERNAL_MD5=ON
  -DWITH_CLIENT=OFF -DWITH_CLIENT_SDL=OFF -DWITH_X11=OFF -DWITH_MANPAGES=OFF
  -DBUILD_TESTING=OFF -DWITH_MACAUDIO=ON -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_OPENH264=OFF -DWITH_WEBVIEW=OFF
  -DCHANNEL_URBDRC=OFF -DWITH_PKCS11=OFF -DWITH_CCACHE=OFF -DWITH_CLANG_FORMAT=OFF -DWITH_PLATFORM_SERVER=OFF
  -DWITH_JSON_DISABLED=ON -DCMAKE_VERBOSE_MAKEFILE=OFF -DWITH_VERBOSE_WINPR_ASSERT=OFF)

if [ ! -f $D/freerdp/install/lib/libfreerdp-client3.dylib ]; then
  echo "==> freerdp $FREERDP_VER"
  mkdir -p $D/freerdp/build
  (cd $D/freerdp/build && $CMAKE ../src $FREERDP_OPTS -DCMAKE_INSTALL_PREFIX=$D/freerdp/install \
    -DWITH_SERVER=OFF -DWITH_SAMPLE=OFF >/dev/null && make -j$(sysctl -n hw.ncpu) >/dev/null && make install >/dev/null)
fi

if [ ! -x $D/freerdp/install-server/bin/sfreerdp-server ]; then
  echo "==> freerdp $FREERDP_VER sample server (tests)"
  mkdir -p $D/freerdp/build-server
  (cd $D/freerdp/build-server && $CMAKE ../src $FREERDP_OPTS -DCMAKE_INSTALL_PREFIX=$D/freerdp/install-server \
    -DWITH_SERVER=ON -DWITH_SAMPLE=ON -DWITH_SHADOW=OFF -DWITH_PROXY=OFF >/dev/null && make -j$(sysctl -n hw.ncpu) >/dev/null && make install >/dev/null)
fi
echo $SIG > $D/.build-deps.sig
echo "deps ready in $D"
