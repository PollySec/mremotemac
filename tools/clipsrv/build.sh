#!/bin/zsh
# Builds clipsrv: FreeRDP's sample server (from ../mRemoteMac-deps, built by native/build-deps.sh with the server
# enabled) plus sf_clip.c, an rdpclip-like file clipboard. Run it like sfreerdp-server:
#   CLIPSRV_OFFER=<file|dir> CLIPSRV_TEXT=<text> CLIPSRV_PASTE=1 CLIPSRV_DELAY_MS=150 CLIPSRV_MAX_REQUEST=1048576 CLIPSRV_SAVE=<dir> \
#     tools/clipsrv/out/clipsrv --port=3390 --cert=server.crt --key=server.key
# (see the header of sf_clip.c for the switches). Must be started from the sample's directory so it finds its icon.
set -e
HERE=${0:A:h}
DEPS=${DEPS:-${HERE:h:h}/../mRemoteMac-deps/freerdp}
SRC=$DEPS/src/server/Sample
INST=$DEPS/install-server
OUT=$HERE/out
mkdir -p $OUT
cp $SRC/sfreerdp.c $SRC/sfreerdp.h $SRC/sf_audin.c $SRC/sf_audin.h $SRC/sf_rdpsnd.c $SRC/sf_rdpsnd.h \
   $SRC/sf_encomsp.c $SRC/sf_encomsp.h $SRC/sf_ainput.c $SRC/sf_ainput.h $OUT/
cp $HERE/sf_clip.c $HERE/sf_clip.h $OUT/
python3 - $OUT <<'PY'
import sys
out=sys.argv[1]
def patch(p, pairs):
    s=open(p).read()
    for old,new in pairs:
        assert s.count(old)==1, (p, old[:50])
        s=s.replace(old,new)
    open(p,'w').write(s)
patch(f'{out}/sfreerdp.h',[("\tRdpsndServerContext* rdpsnd;","\tRdpsndServerContext* rdpsnd;\n\tstruct s_cliprdr_server_context* clip;")])
patch(f'{out}/sfreerdp.c',[
 ('\tWLog_DBG(TAG, "Client sent a keyboard event (flags:0x%04" PRIX16 " code:0x%04" PRIX8 ")", flags,\n\t         code);',
  '\tWLog_DBG(TAG, "Client sent a keyboard event (flags:0x%04" PRIX16 " code:0x%04" PRIX8 ")", flags,\n\t         code);\n\tsf_peer_clip_key(tcontext, flags, code);'),
 ('\tWLog_DBG(TAG, "Client sent a mouse event (flags:0x%04" PRIX16 " pos:%" PRIu16 ",%" PRIu16 ")",\n\t         flags, x, y);',
  '\tWLog_DBG(TAG, "Client sent a mouse event (flags:0x%04" PRIX16 " pos:%" PRIu16 ",%" PRIu16 ")",\n\t         flags, x, y);\n\tsf_peer_clip_mouse((testPeerContext*)input->context, flags, x, y);'),
 ('#include "sf_encomsp.h"','#include "sf_encomsp.h"\n#include "sf_clip.h"\n#include <freerdp/channels/cliprdr.h>'),
 ("\t\tencomsp_server_context_free(context->encomsp);","\t\tencomsp_server_context_free(context->encomsp);\n\t\tsf_peer_clip_uninit(context);"),
 ("\t/* Dynamic Virtual Channels */","\tif (WTSVirtualChannelManagerIsChannelJoined(context->vcm, CLIPRDR_SVC_CHANNEL_NAME))\n\t{\n\t\tif (!sf_peer_clip_init(context))\n\t\t\treturn FALSE;\n\t}\n\n\t/* Dynamic Virtual Channels */"),
])
PY
cd $OUT && clang -std=gnu23 -O2 -arch arm64 -mmacosx-version-min=14.0 -Wno-c++-keyword -DWINPR_DEFINE_ATTR_NODISCARD \
  -DWITH_OPENSSL -D_DARWIN_C_SOURCE -D_POSIX_C_SOURCE=200809L "-DSAMPLE_RESOURCE_ROOT=\"$INST/share/FreeRDP/images\"" \
  -I$INST/include/freerdp3 -I$INST/include/winpr3 -isystem $DEPS/../openssl/install/include \
  sfreerdp.c sf_audin.c sf_rdpsnd.c sf_encomsp.c sf_ainput.c sf_clip.c \
  -L$INST/lib -lfreerdp-server3 -lfreerdp3 -lwinpr3 -Wl,-rpath,$INST/lib -o clipsrv
echo "built $OUT/clipsrv"
