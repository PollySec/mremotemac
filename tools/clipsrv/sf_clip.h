#ifndef SF_CLIP_H
#define SF_CLIP_H
#include "sfreerdp.h"
BOOL sf_peer_clip_init(testPeerContext* context);
void sf_peer_clip_uninit(testPeerContext* context);
void sf_peer_clip_key(testPeerContext* context, UINT16 flags, UINT8 code);
void sf_peer_clip_mouse(testPeerContext* context, UINT16 flags, UINT16 x, UINT16 y);
#endif
