/* mrrdp - thin C shim around libfreerdp-client. See mrrdp.h. */
#include "mrrdp.h"

#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <winpr/wtypes.h>
#include <winpr/synch.h>
#include <winpr/string.h>
#include <freerdp/freerdp.h>
#include <freerdp/client.h>
#include <freerdp/constants.h>
#include <freerdp/settings.h>
#include <freerdp/gdi/gdi.h>
#include <freerdp/graphics.h>
#include <freerdp/codec/color.h>
#include <freerdp/event.h>
#include <freerdp/channels/channels.h>
#include <freerdp/client/channels.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/disp.h>
#include <freerdp/channels/disp.h>
#include <freerdp/client/cliprdr.h>
#include <winpr/clipboard.h>
#include <mach/mach_time.h>

#define MR_RESIZE_MIN_INTERVAL_MS 500 /* like sdl-freerdp / xfreerdp */

struct mr_session
{
	mr_callbacks cb;
	rdpContext* context;
	freerdp* instance;
	pthread_t thread;
	int thread_started;
	volatile int connected;
	DispClientContext* disp;     /* guarded by chan_lock */
	CliprdrClientContext* cliprdr; /* guarded by chan_lock */
	int disp_caps_ready;         /* guarded by chan_lock */
	uint64_t last_layout_ms;
	uint32_t last_layout_w, last_layout_h;
	pthread_mutex_t chan_lock;
	pthread_mutex_t clip_lock;
	char* clip_local_text; /* text we announced to the remote side */
	int clip_ready;        /* MonitorReady handled: format lists may be sent (MS-RDPECLIP 3.1.1.1) */
	UINT32 remote_text_format; /* format id the remote side offered, 0 if none */
};

typedef struct
{
	rdpClientContext common; /* must be first */
	mr_session* session;
} mrContext;

typedef struct
{
	rdpPointer pointer;
	void* handle;
} mrPointer;

static mr_session* S(rdpContext* ctx)
{
	return ((mrContext*)ctx)->session;
}

const char* mr_version(void)
{
	return freerdp_get_version_string();
}

static uint64_t mr_now_ms(void)
{
	static mach_timebase_info_data_t tb;
	if (tb.denom == 0)
		mach_timebase_info(&tb);
	return mach_absolute_time() * tb.numer / tb.denom / 1000000ULL;
}

/* ---------- painting ---------- */

static BOOL mr_begin_paint(rdpContext* context)
{
	rdpGdi* gdi = context->gdi;
	if (gdi && gdi->primary && gdi->primary->hdc && gdi->primary->hdc->hwnd &&
	    gdi->primary->hdc->hwnd->invalid)
		gdi->primary->hdc->hwnd->invalid->null = TRUE;
	return TRUE;
}

static BOOL mr_end_paint(rdpContext* context)
{
	mr_session* s = S(context);
	rdpGdi* gdi = context->gdi;
	if (!gdi || !gdi->primary || !gdi->primary->hdc || !gdi->primary->hdc->hwnd)
		return TRUE;
	HGDI_WND hwnd = gdi->primary->hdc->hwnd;
	if (!hwnd->invalid || hwnd->invalid->null || hwnd->ninvalid < 1)
		return TRUE;
	if (!s->cb.on_frame)
		return TRUE;

	for (INT32 i = 0; i < hwnd->ninvalid; i++)
	{
		GDI_RGN* r = &hwnd->cinvalid[i];
		INT32 x = r->x < 0 ? 0 : r->x;
		INT32 y = r->y < 0 ? 0 : r->y;
		INT32 x2 = r->x + r->w;
		INT32 y2 = r->y + r->h;
		if (x2 > gdi->width)
			x2 = gdi->width;
		if (y2 > gdi->height)
			y2 = gdi->height;
		if (x2 <= x || y2 <= y)
			continue;
		s->cb.on_frame(s->cb.user, x, y, x2 - x, y2 - y);
	}
	if (s->cb.on_present)
		s->cb.on_present(s->cb.user);
	return TRUE;
}

static BOOL mr_desktop_resize(rdpContext* context)
{
	mr_session* s = S(context);
	rdpGdi* gdi = context->gdi;
	rdpSettings* settings = context->settings;
	UINT32 w = freerdp_settings_get_uint32(settings, FreeRDP_DesktopWidth);
	UINT32 h = freerdp_settings_get_uint32(settings, FreeRDP_DesktopHeight);
	if (!gdi_resize(gdi, w, h))
		return FALSE;
	if (s->cb.on_resize)
		s->cb.on_resize(s->cb.user, gdi->width, gdi->height);
	if (s->cb.on_present)
		s->cb.on_present(s->cb.user);
	return TRUE;
}

static BOOL mr_play_sound(rdpContext* context, const PLAY_SOUND_UPDATE* play_sound)
{
	(void)context;
	(void)play_sound;
	return TRUE;
}

static BOOL mr_keyboard_set_indicators(rdpContext* context, UINT16 led_flags)
{
	(void)context;
	(void)led_flags;
	return TRUE;
}

static BOOL mr_keyboard_set_ime_status(rdpContext* context, UINT16 imeId, UINT32 imeState,
                                       UINT32 imeConvMode)
{
	(void)context;
	(void)imeId;
	(void)imeState;
	(void)imeConvMode;
	return TRUE;
}

/* ---------- pointer ---------- */

static BOOL mr_pointer_new(rdpContext* context, rdpPointer* pointer)
{
	mr_session* s = S(context);
	mrPointer* mp = (mrPointer*)pointer;
	rdpGdi* gdi = context->gdi;
	mp->handle = NULL;
	if (!s->cb.on_pointer_new || pointer->width == 0 || pointer->height == 0)
		return TRUE;

	size_t size = (size_t)pointer->width * pointer->height * 4;
	BYTE* buf = calloc(1, size);
	if (!buf)
		return FALSE;
	if (!freerdp_image_copy_from_pointer_data(buf, PIXEL_FORMAT_BGRA32, pointer->width * 4, 0, 0,
	                                          pointer->width, pointer->height,
	                                          pointer->xorMaskData, pointer->lengthXorMask,
	                                          pointer->andMaskData, pointer->lengthAndMask,
	                                          pointer->xorBpp, &gdi->palette))
	{
		free(buf);
		return TRUE; /* keep going with no cursor rather than failing the session */
	}
	mp->handle = s->cb.on_pointer_new(s->cb.user, (int32_t)pointer->width, (int32_t)pointer->height,
	                                  (int32_t)pointer->xPos, (int32_t)pointer->yPos, buf);
	free(buf);
	return TRUE;
}

static void mr_pointer_free(rdpContext* context, rdpPointer* pointer)
{
	mr_session* s = S(context);
	mrPointer* mp = (mrPointer*)pointer;
	if (mp->handle && s->cb.on_pointer_free)
		s->cb.on_pointer_free(s->cb.user, mp->handle);
	mp->handle = NULL;
}

static BOOL mr_pointer_set(rdpContext* context, rdpPointer* pointer)
{
	mr_session* s = S(context);
	mrPointer* mp = (mrPointer*)pointer;
	if (s->cb.on_pointer_set)
		s->cb.on_pointer_set(s->cb.user, mp->handle);
	return TRUE;
}

static BOOL mr_pointer_set_null(rdpContext* context)
{
	mr_session* s = S(context);
	if (s->cb.on_pointer_hidden)
		s->cb.on_pointer_hidden(s->cb.user);
	return TRUE;
}

static BOOL mr_pointer_set_default(rdpContext* context)
{
	mr_session* s = S(context);
	if (s->cb.on_pointer_set)
		s->cb.on_pointer_set(s->cb.user, NULL);
	return TRUE;
}

static BOOL mr_pointer_set_position(rdpContext* context, UINT32 x, UINT32 y)
{
	(void)context;
	(void)x;
	(void)y;
	return TRUE;
}

/* ---------- clipboard (text only) ---------- */

static UINT mr_cliprdr_send_format_list(mr_session* s)
{
	pthread_mutex_lock(&s->chan_lock);
	CliprdrClientContext* clip = s->cliprdr;
	pthread_mutex_unlock(&s->chan_lock);
	if (!clip)
		return CHANNEL_RC_OK;
	pthread_mutex_lock(&s->clip_lock);
	int have = s->clip_local_text != NULL;
	pthread_mutex_unlock(&s->clip_lock);
	/* An empty list is valid and expected: it tells the server "nothing to paste from this side". */
	CLIPRDR_FORMAT formats[2] = { 0 };
	formats[0].formatId = CF_UNICODETEXT;
	formats[1].formatId = CF_TEXT;
	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.common.msgFlags = 0;
	list.numFormats = have ? 2 : 0;
	list.formats = have ? formats : NULL;
	return clip->ClientFormatList(clip, &list);
}

static UINT mr_cliprdr_monitor_ready(CliprdrClientContext* ctx, const CLIPRDR_MONITOR_READY* ready)
{
	(void)ready;
	mr_session* s = (mr_session*)ctx->custom;
	CLIPRDR_CAPABILITIES caps = { 0 };
	CLIPRDR_GENERAL_CAPABILITY_SET gen = { 0 };
	caps.cCapabilitiesSets = 1;
	caps.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&gen;
	gen.capabilitySetType = CB_CAPSTYPE_GENERAL;
	gen.capabilitySetLength = 12;
	gen.version = CB_CAPS_VERSION_2;
	gen.generalFlags = CB_USE_LONG_FORMAT_NAMES;
	UINT rc = ctx->ClientCapabilities(ctx, &caps);
	if (rc != CHANNEL_RC_OK)
		return rc;
	/* The initialisation sequence is Monitor Ready -> Client Capabilities -> Client Format List. Windows does not
	 * consider the channel usable until the format list (possibly empty) has arrived, so always send it here, and
	 * never before: a format list sent ahead of this point is a protocol violation and kills the channel. */
	pthread_mutex_lock(&s->clip_lock);
	s->clip_ready = 1;
	pthread_mutex_unlock(&s->clip_lock);
	return mr_cliprdr_send_format_list(s);
}

static UINT mr_cliprdr_server_capabilities(CliprdrClientContext* ctx,
                                           const CLIPRDR_CAPABILITIES* caps)
{
	(void)ctx;
	(void)caps;
	return CHANNEL_RC_OK;
}

static UINT mr_cliprdr_server_format_list(CliprdrClientContext* ctx,
                                          const CLIPRDR_FORMAT_LIST* list)
{
	mr_session* s = (mr_session*)ctx->custom;
	UINT32 want = 0;
	for (UINT32 i = 0; i < list->numFormats; i++)
	{
		UINT32 id = list->formats[i].formatId;
		if (id == CF_UNICODETEXT)
		{
			want = id;
			break;
		}
		if (id == CF_TEXT && want == 0)
			want = id;
	}
	s->remote_text_format = want;

	CLIPRDR_FORMAT_LIST_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_LIST_RESPONSE;
	resp.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = ctx->ClientFormatListResponse(ctx, &resp);
	if (rc != CHANNEL_RC_OK)
		return rc;

	if (want && s->cb.on_clipboard_text)
	{
		CLIPRDR_FORMAT_DATA_REQUEST req = { 0 };
		req.common.msgType = CB_FORMAT_DATA_REQUEST;
		req.requestedFormatId = want;
		return ctx->ClientFormatDataRequest(ctx, &req);
	}
	return CHANNEL_RC_OK;
}

static UINT mr_cliprdr_server_format_list_response(CliprdrClientContext* ctx,
                                                   const CLIPRDR_FORMAT_LIST_RESPONSE* r)
{
	(void)ctx;
	(void)r;
	return CHANNEL_RC_OK;
}

static UINT mr_cliprdr_server_format_data_request(CliprdrClientContext* ctx,
                                                  const CLIPRDR_FORMAT_DATA_REQUEST* req)
{
	mr_session* s = (mr_session*)ctx->custom;
	CLIPRDR_FORMAT_DATA_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_DATA_RESPONSE;
	BYTE* data = NULL;
	UINT32 len = 0;

	pthread_mutex_lock(&s->clip_lock);
	if (s->clip_local_text)
	{
		if (req->requestedFormatId == CF_UNICODETEXT)
		{
			size_t wlen = 0;
			WCHAR* w = ConvertUtf8ToWCharAlloc(s->clip_local_text, &wlen);
			if (w)
			{
				data = (BYTE*)w;
				len = (UINT32)((wlen + 1) * sizeof(WCHAR));
			}
		}
		else if (req->requestedFormatId == CF_TEXT)
		{
			len = (UINT32)strlen(s->clip_local_text) + 1;
			data = malloc(len);
			if (data)
				memcpy(data, s->clip_local_text, len);
		}
	}
	pthread_mutex_unlock(&s->clip_lock);

	resp.common.msgFlags = data ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
	resp.common.dataLen = len;
	resp.requestedFormatData = data;
	UINT rc = ctx->ClientFormatDataResponse(ctx, &resp);
	free(data);
	return rc;
}

static UINT mr_cliprdr_server_format_data_response(CliprdrClientContext* ctx,
                                                   const CLIPRDR_FORMAT_DATA_RESPONSE* resp)
{
	mr_session* s = (mr_session*)ctx->custom;
	if (!(resp->common.msgFlags & CB_RESPONSE_OK) || !resp->requestedFormatData ||
	    resp->common.dataLen == 0 || !s->cb.on_clipboard_text)
		return CHANNEL_RC_OK;

	char* utf8 = NULL;
	if (s->remote_text_format == CF_UNICODETEXT)
	{
		size_t wchars = resp->common.dataLen / sizeof(WCHAR);
		utf8 = ConvertWCharNToUtf8Alloc((const WCHAR*)resp->requestedFormatData, wchars, NULL);
	}
	else
	{
		utf8 = calloc(resp->common.dataLen + 1, 1);
		if (utf8)
			memcpy(utf8, resp->requestedFormatData, resp->common.dataLen);
	}
	if (utf8)
	{
		s->cb.on_clipboard_text(s->cb.user, utf8);
		free(utf8);
	}
	return CHANNEL_RC_OK;
}

int32_t mr_set_clipboard_text(mr_session* s, const char* utf8)
{
	if (!s)
		return 0;
	pthread_mutex_lock(&s->clip_lock);
	free(s->clip_local_text);
	s->clip_local_text = utf8 ? _strdup(utf8) : NULL;
	int ready = s->clip_ready;
	pthread_mutex_unlock(&s->clip_lock);
	if (!ready || !s->cliprdr || !s->connected)
		return 0; /* kept; announced when MonitorReady completes */
	return mr_cliprdr_send_format_list(s) == CHANNEL_RC_OK ? 1 : 0;
}

/* ---------- channels ---------- */

static UINT mr_disp_caps(DispClientContext* ctx, UINT32 maxMonitors, UINT32 factorA, UINT32 factorB)
{
	(void)maxMonitors;
	(void)factorA;
	(void)factorB;
	mr_session* s = (mr_session*)ctx->custom;
	pthread_mutex_lock(&s->chan_lock);
	s->disp_caps_ready = 1;
	pthread_mutex_unlock(&s->chan_lock);
	return CHANNEL_RC_OK;
}

static void mr_on_channel_connected(void* context, const ChannelConnectedEventArgs* e)
{
	mr_session* s = S((rdpContext*)context);
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		DispClientContext* d = (DispClientContext*)e->pInterface;
		d->custom = s;
		d->DisplayControlCaps = mr_disp_caps;
		pthread_mutex_lock(&s->chan_lock);
		s->disp = d;
		s->disp_caps_ready = 0;
		pthread_mutex_unlock(&s->chan_lock);
	}
	else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		CliprdrClientContext* c = (CliprdrClientContext*)e->pInterface;
		c->custom = s;
		c->MonitorReady = mr_cliprdr_monitor_ready;
		c->ServerCapabilities = mr_cliprdr_server_capabilities;
		c->ServerFormatList = mr_cliprdr_server_format_list;
		c->ServerFormatListResponse = mr_cliprdr_server_format_list_response;
		c->ServerFormatDataRequest = mr_cliprdr_server_format_data_request;
		c->ServerFormatDataResponse = mr_cliprdr_server_format_data_response;
		pthread_mutex_lock(&s->chan_lock);
		s->cliprdr = c;
		pthread_mutex_unlock(&s->chan_lock);
	}
	else
	{
		freerdp_client_OnChannelConnectedEventHandler(context, e);
	}
}

static void mr_on_channel_disconnected(void* context, const ChannelDisconnectedEventArgs* e)
{
	mr_session* s = S((rdpContext*)context);
	if (strcmp(e->name, DISP_DVC_CHANNEL_NAME) == 0)
	{
		pthread_mutex_lock(&s->chan_lock);
		s->disp = NULL;
		s->disp_caps_ready = 0;
		pthread_mutex_unlock(&s->chan_lock);
	}
	else if (strcmp(e->name, CLIPRDR_SVC_CHANNEL_NAME) == 0)
	{
		pthread_mutex_lock(&s->chan_lock);
		s->cliprdr = NULL;
		pthread_mutex_unlock(&s->chan_lock);
		pthread_mutex_lock(&s->clip_lock);
		s->clip_ready = 0; /* a reconnect brings a fresh MonitorReady */
		pthread_mutex_unlock(&s->clip_lock);
	}
	else
		freerdp_client_OnChannelDisconnectedEventHandler(context, e);
}

/* ---------- connection lifecycle ---------- */

static BOOL mr_pre_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	rdpSettings* settings = context->settings;

	if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMajorType, OSMAJORTYPE_MACINTOSH))
		return FALSE;
	if (!freerdp_settings_set_uint32(settings, FreeRDP_OsMinorType, OSMINORTYPE_MACINTOSH))
		return FALSE;

	if (PubSub_SubscribeChannelConnected(context->pubSub, mr_on_channel_connected) < 0)
		return FALSE;
	if (PubSub_SubscribeChannelDisconnected(context->pubSub, mr_on_channel_disconnected) < 0)
		return FALSE;
	/* channels are loaded by FreeRDP itself through instance->LoadChannels after PreConnect (3.x) */
	return TRUE;
}

static BOOL mr_post_connect(freerdp* instance)
{
	rdpContext* context = instance->context;
	mr_session* s = S(context);

	if (!gdi_init(instance, PIXEL_FORMAT_BGRA32))
		return FALSE;

	rdpPointer pointer = { 0 };
	pointer.size = sizeof(mrPointer);
	pointer.New = mr_pointer_new;
	pointer.Free = mr_pointer_free;
	pointer.Set = mr_pointer_set;
	pointer.SetNull = mr_pointer_set_null;
	pointer.SetDefault = mr_pointer_set_default;
	pointer.SetPosition = mr_pointer_set_position;
	graphics_register_pointer(context->graphics, &pointer);

	context->update->BeginPaint = mr_begin_paint;
	context->update->EndPaint = mr_end_paint;
	context->update->DesktopResize = mr_desktop_resize;
	context->update->PlaySound = mr_play_sound;
	context->update->SetKeyboardIndicators = mr_keyboard_set_indicators;
	context->update->SetKeyboardImeStatus = mr_keyboard_set_ime_status;

	s->connected = 1;
	if (s->cb.on_resize)
		s->cb.on_resize(s->cb.user, context->gdi->width, context->gdi->height);
	if (s->cb.on_connected)
		s->cb.on_connected(s->cb.user);
	return TRUE;
}

/* Called before channels are torn down: release what PostConnect allocated. */
static void mr_post_disconnect(freerdp* instance)
{
	rdpContext* context = instance->context;
	mr_session* s = S(context);
	s->connected = 0;
	gdi_free(instance);
}

/* Called after all channel threads have stopped: release what PreConnect allocated. */
static void mr_post_final_disconnect(freerdp* instance)
{
	rdpContext* context = instance->context;
	PubSub_UnsubscribeChannelConnected(context->pubSub, mr_on_channel_connected);
	PubSub_UnsubscribeChannelDisconnected(context->pubSub, mr_on_channel_disconnected);
}

static DWORD mr_verify_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                      const char* common_name, const char* subject,
                                      const char* issuer, const char* fingerprint, DWORD flags)
{
	mr_session* s = S(instance->context);
	if (!s->cb.on_verify_cert)
		return 1;
	return (DWORD)s->cb.on_verify_cert(s->cb.user, host, port, common_name, subject, issuer,
	                                   fingerprint, flags, 0, NULL, NULL, NULL);
}

static DWORD mr_verify_changed_certificate_ex(freerdp* instance, const char* host, UINT16 port,
                                              const char* common_name, const char* subject,
                                              const char* issuer, const char* fingerprint,
                                              const char* old_subject, const char* old_issuer,
                                              const char* old_fingerprint, DWORD flags)
{
	mr_session* s = S(instance->context);
	if (!s->cb.on_verify_cert)
		return 1;
	return (DWORD)s->cb.on_verify_cert(s->cb.user, host, port, common_name, subject, issuer,
	                                   fingerprint, flags, 1, old_subject, old_issuer,
	                                   old_fingerprint);
}

static void mr_copy_in(char* dst, int cap, const char* src)
{
	if (!src)
		return;
	strncpy(dst, src, (size_t)cap - 1);
	dst[cap - 1] = 0;
}

static BOOL mr_authenticate_ex(freerdp* instance, char** username, char** password,
                               char** domain, rdp_auth_reason reason)
{
	mr_session* s = S(instance->context);
	if (!s->cb.on_authenticate)
		return FALSE;
	enum
	{
		CAP = 1024
	};
	char u[CAP] = { 0 }, p[CAP] = { 0 }, d[CAP] = { 0 };
	mr_copy_in(u, CAP, *username);
	mr_copy_in(p, CAP, *password);
	mr_copy_in(d, CAP, *domain);
	if (!s->cb.on_authenticate(s->cb.user, (int32_t)reason, u, CAP, p, CAP, d, CAP))
		return FALSE;
	free(*username);
	free(*password);
	free(*domain);
	*username = _strdup(u);
	*password = _strdup(p);
	*domain = _strdup(d);
	return TRUE;
}

static int mr_logon_error_info(freerdp* instance, UINT32 data, UINT32 type)
{
	(void)instance;
	(void)data;
	(void)type;
	return 1;
}

static BOOL mr_client_new(freerdp* instance, rdpContext* context)
{
	(void)context;
	instance->PreConnect = mr_pre_connect;
	instance->PostConnect = mr_post_connect;
	instance->PostDisconnect = mr_post_disconnect;
	instance->PostFinalDisconnect = mr_post_final_disconnect;
	instance->VerifyCertificateEx = mr_verify_certificate_ex;
	instance->VerifyChangedCertificateEx = mr_verify_changed_certificate_ex;
	instance->AuthenticateEx = mr_authenticate_ex;
	instance->LogonErrorInfo = mr_logon_error_info;
	instance->GetAccessToken = client_failsafe_get_access_token;
	return TRUE;
}

static void mr_client_free(freerdp* instance, rdpContext* context)
{
	(void)instance;
	(void)context;
}

static int mr_client_start(rdpContext* context)
{
	(void)context;
	return 0;
}

static int mr_client_stop(rdpContext* context)
{
	(void)context;
	return 0;
}

static void* mr_thread_proc(void* arg)
{
	mr_session* s = (mr_session*)arg;
	freerdp* instance = s->instance;
	rdpContext* context = s->context;
	UINT32 result = 0;

	if (!freerdp_connect(instance))
	{
		result = freerdp_get_last_error(context);
		goto done;
	}

	while (!freerdp_shall_disconnect_context(context))
	{
		HANDLE handles[MAXIMUM_WAIT_OBJECTS] = { 0 };
		DWORD n = freerdp_get_event_handles(context, handles, ARRAYSIZE(handles));
		if (n == 0)
			break;
		DWORD status = WaitForMultipleObjects(n, handles, FALSE, 100);
		if (status == WAIT_FAILED)
			break;
		if (!freerdp_check_event_handles(context))
		{
			if (freerdp_settings_get_bool(context->settings, FreeRDP_AutoReconnectionEnabled) &&
			    !freerdp_shall_disconnect_context(context))
			{
				s->connected = 0;
				if (s->cb.on_reconnecting)
					s->cb.on_reconnecting(s->cb.user);
				if (client_auto_reconnect(instance))
					continue;
			}
			result = freerdp_get_last_error(context);
			break;
		}
	}

done:
	freerdp_disconnect(instance);
	s->connected = 0;
	if (s->cb.on_disconnected)
	{
		const char* name = result ? freerdp_get_last_error_name(result) : "";
		const char* desc = result ? freerdp_get_last_error_string(result) : "";
		s->cb.on_disconnected(s->cb.user, result, name ? name : "", desc ? desc : "");
	}
	return NULL;
}

/* ---------- public API ---------- */

mr_session* mr_session_create(const mr_callbacks* callbacks)
{
	mr_session* s = calloc(1, sizeof(mr_session));
	if (!s)
		return NULL;
	if (callbacks)
		s->cb = *callbacks;
	pthread_mutex_init(&s->clip_lock, NULL);
	pthread_mutex_init(&s->chan_lock, NULL);

	RDP_CLIENT_ENTRY_POINTS ep = { 0 };
	ep.Version = RDP_CLIENT_INTERFACE_VERSION;
	ep.Size = sizeof(RDP_CLIENT_ENTRY_POINTS_V1);
	ep.ContextSize = sizeof(mrContext);
	ep.ClientNew = mr_client_new;
	ep.ClientFree = mr_client_free;
	ep.ClientStart = mr_client_start;
	ep.ClientStop = mr_client_stop;

	rdpContext* context = freerdp_client_context_new(&ep);
	if (!context)
	{
		pthread_mutex_destroy(&s->clip_lock);
		pthread_mutex_destroy(&s->chan_lock);
		free(s);
		return NULL;
	}
	((mrContext*)context)->session = s;
	s->context = context;
	s->instance = context->instance;
	return s;
}

void mr_session_destroy(mr_session* s)
{
	if (!s)
		return;
	mr_disconnect(s);
	if (s->context)
		freerdp_client_context_free(s->context);
	pthread_mutex_destroy(&s->clip_lock);
	pthread_mutex_destroy(&s->chan_lock);
	free(s->clip_local_text);
	free(s);
}

static SSIZE_T mr_key(const char* key, SSIZE_T expectedType)
{
	char buf[256];
	snprintf(buf, sizeof(buf), "FreeRDP_%s", key);
	SSIZE_T id = freerdp_settings_get_key_for_name(buf);
	if (id < 0)
	{
		fprintf(stderr, "mrrdp: unknown setting %s\n", buf);
		return -1;
	}
	if (freerdp_settings_get_type_for_key(id) != expectedType)
	{
		fprintf(stderr, "mrrdp: setting %s has wrong type\n", buf);
		return -1;
	}
	return id;
}

int32_t mr_set_string(mr_session* s, const char* key, const char* value)
{
	SSIZE_T id = mr_key(key, RDP_SETTINGS_TYPE_STRING);
	if (id < 0)
		return 0;
	return freerdp_settings_set_string(s->context->settings, (FreeRDP_Settings_Keys_String)id,
	                                   value)
	           ? 1
	           : 0;
}

int32_t mr_set_uint32(mr_session* s, const char* key, uint32_t value)
{
	SSIZE_T id = mr_key(key, RDP_SETTINGS_TYPE_UINT32);
	if (id < 0)
		return 0;
	return freerdp_settings_set_uint32(s->context->settings, (FreeRDP_Settings_Keys_UInt32)id,
	                                   value)
	           ? 1
	           : 0;
}

int32_t mr_set_bool(mr_session* s, const char* key, int32_t value)
{
	SSIZE_T id = mr_key(key, RDP_SETTINGS_TYPE_BOOL);
	if (id < 0)
		return 0;
	return freerdp_settings_set_bool(s->context->settings, (FreeRDP_Settings_Keys_Bool)id,
	                                 value ? TRUE : FALSE)
	           ? 1
	           : 0;
}

int32_t mr_parse_args(mr_session* s, int32_t argc, const char** argv)
{
	return freerdp_client_settings_parse_command_line(s->context->settings, argc, (char**)argv,
	                                                  FALSE);
}

int32_t mr_connect(mr_session* s)
{
	if (!s || s->thread_started)
		return -1;
	if (freerdp_client_start(s->context) != 0)
		return -2;
	if (pthread_create(&s->thread, NULL, mr_thread_proc, s) != 0)
		return -3;
	s->thread_started = 1;
	return 0;
}

void mr_disconnect(mr_session* s)
{
	if (!s || !s->thread_started)
		return;
	freerdp_abort_connect_context(s->context);
	pthread_join(s->thread, NULL);
	s->thread_started = 0;
	freerdp_client_stop(s->context);
}

int32_t mr_is_connected(mr_session* s)
{
	return s && s->connected ? 1 : 0;
}

int32_t mr_get_framebuffer(mr_session* s, uint8_t** data, int32_t* width, int32_t* height,
                           int32_t* stride)
{
	rdpGdi* gdi = s && s->context ? s->context->gdi : NULL;
	if (!gdi || !gdi->primary_buffer)
		return 0;
	*data = gdi->primary_buffer;
	*width = gdi->width;
	*height = gdi->height;
	*stride = (int32_t)gdi->stride;
	return 1;
}

void mr_send_mouse(mr_session* s, uint16_t flags, uint16_t x, uint16_t y)
{
	if (s && s->connected)
		freerdp_input_send_mouse_event(s->context->input, flags, x, y);
}

void mr_send_mouse_ext(mr_session* s, uint16_t flags, uint16_t x, uint16_t y)
{
	if (s && s->connected)
		freerdp_input_send_extended_mouse_event(s->context->input, flags, x, y);
}

void mr_send_scancode(mr_session* s, int32_t down, int32_t repeat, uint32_t rdp_scancode)
{
	if (s && s->connected)
		freerdp_input_send_keyboard_event_ex(s->context->input, down ? TRUE : FALSE,
		                                     repeat ? TRUE : FALSE, rdp_scancode);
}

void mr_send_unicode(mr_session* s, int32_t down, uint16_t code)
{
	if (s && s->connected)
		freerdp_input_send_unicode_keyboard_event(s->context->input, down ? 0 : KBD_FLAGS_RELEASE,
		                                          code);
}

void mr_send_sync(mr_session* s, uint32_t toggle_flags)
{
	if (s && s->connected)
		freerdp_input_send_synchronize_event(s->context->input, toggle_flags);
}

void mr_send_focus_in(mr_session* s, uint16_t toggle_flags)
{
	if (s && s->connected)
		freerdp_input_send_focus_in_event(s->context->input, toggle_flags);
}

int32_t mr_resize_desktop(mr_session* s, uint32_t width, uint32_t height)
{
	if (!s || !s->connected)
		return 0;
	width &= ~1u; /* width must be even */
	if (width < 200 || height < 200 || width > 8192 || height > 8192)
		return 0;
	pthread_mutex_lock(&s->chan_lock);
	DispClientContext* disp = s->disp;
	int ready = s->disp_caps_ready;
	pthread_mutex_unlock(&s->chan_lock);
	if (!disp || !ready)
		return 0;
	if (width == s->last_layout_w && height == s->last_layout_h)
		return 1;
	uint64_t now = mr_now_ms();
	if (now - s->last_layout_ms < MR_RESIZE_MIN_INTERVAL_MS)
		return 0;
	DISPLAY_CONTROL_MONITOR_LAYOUT layout = { 0 };
	layout.Flags = DISPLAY_CONTROL_MONITOR_PRIMARY;
	layout.Left = 0;
	layout.Top = 0;
	layout.Width = width;
	layout.Height = height;
	layout.PhysicalWidth = width * 254 / 960; /* ~96 dpi, in mm */
	layout.PhysicalHeight = height * 254 / 960;
	layout.Orientation = ORIENTATION_LANDSCAPE;
	layout.DesktopScaleFactor = 100;
	layout.DeviceScaleFactor = 100;
	if (disp->SendMonitorLayout(disp, 1, &layout) != CHANNEL_RC_OK)
		return 0;
	s->last_layout_ms = now;
	s->last_layout_w = width;
	s->last_layout_h = height;
	return 1;
}
