#!/bin/zsh
# NLA logon test of the native libraries that ship. Logs on to FreeRDP's sample server (tools/clipsrv with CLIPSRV_SAM,
# which requires NLA the way a Windows host does) once with the right password, which must connect, and once with a
# wrong one, which must be refused.
#
# The client runs as if on another Mac: OPENSSL_MODULES points to an empty directory and OPENSSL_CONF to an empty file,
# so nothing under ../mRemoteMac-deps can stand in for something the libraries lack. 1.5.0 loaded MD4 from the deps
# directory at run time; it logged on from the build machine and failed on every other Mac. This test fails for it.
#
#   native/nla-test.sh [directory with libmrrdp.dylib]     (default native/out; make-app.sh passes the app bundle)
set -e
HERE=${0:A:h}
ROOT=${HERE:h}
LIBDIR=${${1:-$HERE/out}:A}
D=${DEPS_ROOT:-$ROOT/../mRemoteMac-deps}
SAMPLE=$D/freerdp/src/server/Sample
[ -f $LIBDIR/libmrrdp.dylib ] || { echo "nla-test: no libmrrdp.dylib in $LIBDIR" >&2; exit 1; }
[ -d $SAMPLE ] || { echo "nla-test: $SAMPLE is missing, run native/build-deps.sh" >&2; exit 1; }

T=$(mktemp -d -t mrm-nla)
SRV_PID=
cleanup() { [ -n "$SRV_PID" ] && kill $SRV_PID 2>/dev/null; wait 2>/dev/null; rm -rf $T }
trap cleanup EXIT

DEPS=$D/freerdp $ROOT/tools/clipsrv/build.sh >$T/build.log 2>&1 || { cat $T/build.log >&2; exit 1; }
clang -arch arm64 -mmacosx-version-min=14.0 -O1 -Wall -I$HERE $HERE/nlatest.c -L$LIBDIR -lmrrdp \
  -Wl,-rpath,$LIBDIR -o $T/nlatest

# throwaway TLS identity for the server
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj /CN=localhost \
  -keyout $T/server.key -out $T/server.crt >/dev/null 2>&1
# NT hash of "password" = MD4 over its UTF-16LE bytes, a published value, so the server side does not depend on the
# libraries under test either
print "nlatest:::8846f7eaee8fb117ad06bdd830b7586c:::" > $T/sam

PORT=$((45000 + RANDOM % 15000))
(cd $SAMPLE && CLIPSRV_SAM=$T/sam CLIPSRV_BIND=127.0.0.1 exec $ROOT/tools/clipsrv/out/clipsrv --port=$PORT \
  --cert=$T/server.crt --key=$T/server.key) >$T/server.log 2>&1 &
SRV_PID=$!
for i in {1..50}; do nc -z 127.0.0.1 $PORT 2>/dev/null && break; sleep 0.1; done
nc -z 127.0.0.1 $PORT 2>/dev/null || { echo "nla-test: server did not start" >&2; cat $T/server.log >&2; exit 1; }

mkdir $T/no-modules; : > $T/empty.cnf
run() { env OPENSSL_MODULES=$T/no-modules OPENSSL_CONF=$T/empty.cnf $T/nlatest 127.0.0.1 $PORT nlatest "$@" }
if ! run password ok >$T/ok.log 2>&1; then
  echo "nla-test: FAIL – NLA logon with the right password did not work with the libraries in $LIBDIR" >&2
  cat $T/ok.log >&2; echo "--- server" >&2; tail -20 $T/server.log >&2; exit 1
fi
if ! run wrong-password fail >$T/fail.log 2>&1; then
  echo "nla-test: FAIL – the wrong password was not refused" >&2; cat $T/fail.log >&2; exit 1
fi
grep -h '^nlatest:' $T/ok.log $T/fail.log
echo "nla-test: passed ($LIBDIR)"
