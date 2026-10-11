#!/bin/zsh
# Downloads and builds CMake (binary), OpenSSL 3.5 (static) and FreeRDP 3 (shared libs) for arm64 into ../mRemoteMac-deps
set -e
ROOT=${0:A:h:h}
D=${DEPS_ROOT:-$ROOT/../mRemoteMac-deps}
mkdir -p $D/cmake $D/openssl $D/freerdp
CMAKE_VER=4.4.4
OPENSSL_VER=3.5.9
FREERDP_VER=3.32.1

if [ ! -x $D/cmake/cmake-$CMAKE_VER-macos-universal/CMake.app/Contents/bin/cmake ]; then
  echo "==> cmake $CMAKE_VER"
  curl -sL -o $D/cmake/cmake.tar.gz https://github.com/Kitware/CMake/releases/download/v$CMAKE_VER/cmake-$CMAKE_VER-macos-universal.tar.gz
  tar xzf $D/cmake/cmake.tar.gz -C $D/cmake
fi
CMAKE=$D/cmake/cmake-$CMAKE_VER-macos-universal/CMake.app/Contents/bin/cmake

if [ ! -f $D/openssl/install/lib/libssl.a ]; then
  echo "==> openssl $OPENSSL_VER"
  curl -sL -o $D/openssl/openssl.tar.gz https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VER/openssl-$OPENSSL_VER.tar.gz
  tar xzf $D/openssl/openssl.tar.gz -C $D/openssl
  (cd $D/openssl/openssl-$OPENSSL_VER && ./Configure darwin64-arm64-cc no-shared no-tests no-docs --prefix=$D/openssl/install >/dev/null && make -j$(sysctl -n hw.ncpu) >/dev/null && make install_sw >/dev/null)
fi

if [ ! -f $D/freerdp/install/lib/libfreerdp-client3.dylib ]; then
  echo "==> freerdp $FREERDP_VER"
  [ -d $D/freerdp/src ] || git clone --depth 1 --branch $FREERDP_VER https://github.com/FreeRDP/FreeRDP.git $D/freerdp/src
  mkdir -p $D/freerdp/build
  (cd $D/freerdp/build && $CMAKE ../src -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$D/freerdp/install \
    -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
    -DOPENSSL_ROOT_DIR=$D/openssl/install -DOPENSSL_USE_STATIC_LIBS=TRUE \
    -DWITH_SERVER=OFF -DWITH_SAMPLE=OFF -DWITH_CLIENT=OFF -DWITH_CLIENT_SDL=OFF -DWITH_X11=OFF -DWITH_MANPAGES=OFF \
    -DBUILD_TESTING=OFF -DWITH_MACAUDIO=ON -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_OPENH264=OFF -DWITH_WEBVIEW=OFF \
    -DCHANNEL_URBDRC=OFF -DWITH_PKCS11=OFF -DWITH_CCACHE=OFF -DWITH_CLANG_FORMAT=OFF -DWITH_PLATFORM_SERVER=OFF \
    -DCMAKE_VERBOSE_MAKEFILE=OFF -DWITH_VERBOSE_WINPR_ASSERT=OFF >/dev/null && make -j$(sysctl -n hw.ncpu) >/dev/null && make install >/dev/null)
fi
echo "deps ready in $D"
