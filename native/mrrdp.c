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
#include <stdarg.h>
#include <ctype.h>
#include <stdio.h>
#include <freerdp/client/cliprdr.h>
#include <freerdp/client/client_cliprdr_file.h>
#include <freerdp/utils/cliprdr_utils.h>
#include <winpr/clipboard.h>
#include <winpr/shell.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <errno.h>
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
	char* clip_local_files; /* text/uri-list of local files announced to the remote side, NULL = none */
	int clip_ready;        /* MonitorReady handled: format lists may be sent (MS-RDPECLIP 3.1.1.1) */
	UINT32 remote_text_format; /* format id the remote side offered, 0 if none */
	UINT32 remote_fgd_format;  /* remote FileGroupDescriptorW format id, 0 if none */
	int pending_request;       /* which format data we asked the server for: 0 none, 1 text, 2 files */
	UINT32 remote_caps_flags;
	wClipboard* clip_sys;             /* winpr clipboard: synthesizes FileGroupDescriptorW from text/uri-list */
	CliprdrFileContext* clip_file;    /* FreeRDP helper that serves our local files to the server */
	FILEDESCRIPTORW* remote_files;    /* what the server last offered (owned) */
	UINT32 remote_file_count;
	UINT32 stream_counter, clip_data_counter;
	struct
	{
		int active;
		char* dest;
		UINT32 index;       /* current descriptor */
		UINT64 size;        /* of the current file */
		UINT64 offset;
		UINT64 total, done; /* bytes over all files */
		UINT32 files;       /* non-directory entries */
		FILE* fp;
		char* cur_path;
		UINT32 stream_id;
		int waiting_size;
		int locked;
		UINT32 clip_data_id;
	} dl;
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

/* ---------- diagnostics ---------- */

static void mr_logf(mr_session* s, const char* fmt, ...)
{
	if (!s || !s->cb.on_log)
		return;
	char buf[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof(buf), fmt, ap);
	va_end(ap);
	s->cb.on_log(s->cb.user, buf);
}

/* ---------- clipboard: text and files (MS-RDPECLIP) ---------- */

#define MR_CLIP_CHUNK (1024u * 1024u)  /* bytes per File Contents Request when downloading (FreeRDP caps at 8 MiB) */
#define MR_CLIP_PROGRESS_STEP (1u << 20)

static const char type_FileGroupDescriptorW[] = "FileGroupDescriptorW";
static const char mime_uri_list[] = "text/uri-list";

static mr_session* clip_session(CliprdrClientContext* ctx)
{
	/* cliprdr_file_context_init owns ctx->custom; our session hangs off the file context */
	return (mr_session*)cliprdr_file_context_get_context((CliprdrFileContext*)ctx->custom);
}

static CliprdrClientContext* clip_ctx(mr_session* s)
{
	pthread_mutex_lock(&s->chan_lock);
	CliprdrClientContext* c = s->cliprdr;
	pthread_mutex_unlock(&s->chan_lock);
	return c;
}

static UINT mr_cliprdr_send_format_list(mr_session* s)
{
	CliprdrClientContext* clip = clip_ctx(s);
	if (!clip)
		return CHANNEL_RC_OK;
	pthread_mutex_lock(&s->clip_lock);
	int have_text = s->clip_local_text != NULL;
	int have_files = s->clip_local_files != NULL;
	pthread_mutex_unlock(&s->clip_lock);
	/* An empty list is valid and expected: it tells the server "nothing to paste from this side". */
	CLIPRDR_FORMAT formats[3] = { 0 };
	UINT32 n = 0;
	if (have_files)
	{
		formats[n].formatId = ClipboardGetFormatId(s->clip_sys, type_FileGroupDescriptorW);
		formats[n].formatName = (char*)type_FileGroupDescriptorW;
		n++;
	}
	if (have_text)
	{
		formats[n++].formatId = CF_UNICODETEXT;
		formats[n++].formatId = CF_TEXT;
	}
	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.common.msgFlags = 0;
	list.numFormats = n;
	list.formats = n ? formats : NULL;
	UINT rc = clip->ClientFormatList(clip, &list);
	mr_logf(s, "Clipboard: announced %s to the session (rc=%u)", have_files ? "files" : have_text ? "text" : "nothing", (unsigned)rc);
	return rc;
}

static UINT mr_cliprdr_monitor_ready(CliprdrClientContext* ctx, const CLIPRDR_MONITOR_READY* ready)
{
	(void)ready;
	mr_session* s = clip_session(ctx);
	CLIPRDR_CAPABILITIES caps = { 0 };
	CLIPRDR_GENERAL_CAPABILITY_SET gen = { 0 };
	caps.cCapabilitiesSets = 1;
	caps.capabilitySets = (CLIPRDR_CAPABILITY_SET*)&gen;
	gen.capabilitySetType = CB_CAPSTYPE_GENERAL;
	gen.capabilitySetLength = 12;
	gen.version = CB_CAPS_VERSION_2;
	/* file flags are only offered when the server announced stream file clip support (caps come before ready) */
	gen.generalFlags = CB_USE_LONG_FORMAT_NAMES | cliprdr_file_context_current_flags(s->clip_file);
	/* lock support is negotiated as (server & client): announce it so our downloads may lock the remote selection */
	if (s->remote_caps_flags & CB_CAN_LOCK_CLIPDATA)
		gen.generalFlags |= CB_CAN_LOCK_CLIPDATA;
	UINT rc = ctx->ClientCapabilities(ctx, &caps);
	mr_logf(s, "Clipboard: channel ready, capabilities sent (files %s, rc=%u)",
	        (gen.generalFlags & CB_STREAM_FILECLIP_ENABLED) ? "on" : "off", (unsigned)rc);
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
	mr_session* s = clip_session(ctx);
	const BYTE* p = (const BYTE*)caps->capabilitySets;
	for (UINT32 i = 0; i < caps->cCapabilitiesSets; i++)
	{
		const CLIPRDR_CAPABILITY_SET* set = (const CLIPRDR_CAPABILITY_SET*)p;
		if (set->capabilitySetType == CB_CAPSTYPE_GENERAL)
		{
			const CLIPRDR_GENERAL_CAPABILITY_SET* gen = (const CLIPRDR_GENERAL_CAPABILITY_SET*)set;
			s->remote_caps_flags = gen->generalFlags;
			(void)cliprdr_file_context_remote_set_flags(s->clip_file, gen->generalFlags);
		}
		p += set->capabilitySetLength;
	}
	mr_logf(s, "Clipboard: server capabilities 0x%x (files %s, lock %s)", (unsigned)s->remote_caps_flags,
	        (s->remote_caps_flags & CB_STREAM_FILECLIP_ENABLED) ? "yes" : "no",
	        (s->remote_caps_flags & CB_CAN_LOCK_CLIPDATA) ? "yes" : "no");
	return CHANNEL_RC_OK;
}

/* ----- download from the session ----- */

static void dl_reset(mr_session* s)
{
	if (s->dl.fp)
		fclose(s->dl.fp);
	free(s->dl.cur_path);
	free(s->dl.dest);
	memset(&s->dl, 0, sizeof(s->dl));
}

static void dl_finish(mr_session* s, const char* error)
{
	CliprdrClientContext* clip = clip_ctx(s);
	if (s->dl.locked && clip)
	{
		CLIPRDR_UNLOCK_CLIPBOARD_DATA u = { 0 };
		u.common.msgType = CB_UNLOCK_CLIPDATA;
		u.clipDataId = s->dl.clip_data_id;
		(void)clip->ClientUnlockClipboardData(clip, &u);
	}
	char* dest = s->dl.dest ? _strdup(s->dl.dest) : NULL;
	UINT32 files = s->dl.files;
	dl_reset(s);
	if (s->cb.on_clipboard_files_done)
		s->cb.on_clipboard_files_done(s->cb.user, dest ? dest : "", (int32_t)files, error);
	free(dest);
}

/* relative path from a descriptor: backslashes become slashes; refuses absolute paths and ".." */
static char* dl_relative_path(const FILEDESCRIPTORW* fd)
{
	char* name = ConvertWCharNToUtf8Alloc(fd->cFileName, ARRAYSIZE(fd->cFileName), NULL);
	if (!name)
		return NULL;
	for (char* q = name; *q; q++)
		if (*q == '\\')
			*q = '/';
	const size_t nlen = strlen(name);
	if (name[0] == '/' || strstr(name, "../") || strcmp(name, "..") == 0 || (nlen >= 3 && strcmp(name + nlen - 3, "/..") == 0))
	{
		free(name);
		return NULL;
	}
	return name;
}

static int dl_mkdirs(const char* path)
{
	char* copy = _strdup(path);
	if (!copy)
		return 0;
	for (char* q = copy + 1; *q; q++)
	{
		if (*q == '/')
		{
			*q = 0;
			if (mkdir(copy, 0700) != 0 && errno != EEXIST)
			{
				free(copy);
				return 0;
			}
			*q = '/';
		}
	}
	int ok = mkdir(copy, 0700) == 0 || errno == EEXIST;
	free(copy);
	return ok;
}

static void dl_request(mr_session* s, UINT32 flags, UINT32 cb)
{
	CliprdrClientContext* clip = clip_ctx(s);
	if (!clip)
	{
		dl_finish(s, "clipboard channel is gone");
		return;
	}
	CLIPRDR_FILE_CONTENTS_REQUEST req = { 0 };
	req.common.msgType = CB_FILECONTENTS_REQUEST;
	req.streamId = ++s->stream_counter;
	req.listIndex = s->dl.index;
	req.dwFlags = flags;
	req.nPositionLow = (UINT32)(s->dl.offset & 0xFFFFFFFFu);
	req.nPositionHigh = (UINT32)(s->dl.offset >> 32);
	req.cbRequested = cb;
	req.haveClipDataId = s->dl.locked ? TRUE : FALSE;
	req.clipDataId = s->dl.clip_data_id;
	s->dl.stream_id = req.streamId;
	s->dl.waiting_size = flags == FILECONTENTS_SIZE;
	UINT rc = clip->ClientFileContentsRequest(clip, &req);
	if (rc != CHANNEL_RC_OK)
		dl_finish(s, "could not request file contents");
}

static void dl_next_file(mr_session* s)
{
	while (s->dl.index < s->remote_file_count)
	{
		const FILEDESCRIPTORW* fd = &s->remote_files[s->dl.index];
		if ((fd->dwFlags & FD_ATTRIBUTES) && (fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
		{
			s->dl.index++;
			continue;
		}
		char* rel = dl_relative_path(fd);
		if (!rel)
		{
			dl_finish(s, "the session offered an invalid file name");
			return;
		}
		size_t len = strlen(s->dl.dest) + 1 + strlen(rel) + 1;
		char* path = malloc(len);
		if (!path)
		{
			free(rel);
			dl_finish(s, "out of memory");
			return;
		}
		snprintf(path, len, "%s/%s", s->dl.dest, rel);
		free(rel);
		char* slash = strrchr(path, '/');
		if (slash)
		{
			*slash = 0;
			if (!dl_mkdirs(path))
			{
				free(path);
				dl_finish(s, "could not create the target folder");
				return;
			}
			*slash = '/';
		}
		s->dl.fp = fopen(path, "wb");
		if (!s->dl.fp)
		{
			free(path);
			dl_finish(s, "could not create the target file");
			return;
		}
		s->dl.cur_path = path;
		s->dl.offset = 0;
		s->dl.files++;
		if (fd->dwFlags & FD_FILESIZE)
		{
			s->dl.size = ((UINT64)fd->nFileSizeHigh << 32) | fd->nFileSizeLow;
			if (s->dl.size == 0)
			{
				dl_close_current(s);
				s->dl.index++;
				continue;
			}
			dl_request(s, FILECONTENTS_RANGE, (UINT32)(s->dl.size - s->dl.offset < MR_CLIP_CHUNK ? s->dl.size - s->dl.offset : MR_CLIP_CHUNK));
		}
		else
			dl_request(s, FILECONTENTS_SIZE, 8);
		return;
	}
	dl_finish(s, NULL);
}

static void dl_close_current(mr_session* s)
{
	if (s->dl.fp)
	{
		fclose(s->dl.fp);
		s->dl.fp = NULL;
	}
	const FILEDESCRIPTORW* fd = s->dl.index < s->remote_file_count ? &s->remote_files[s->dl.index] : NULL;
	if (fd && s->dl.cur_path && (fd->dwFlags & FD_WRITESTIME))
	{
		/* FILETIME: 100 ns ticks since 1601-01-01 -> Unix seconds */
		const UINT64 ft = ((UINT64)fd->ftLastWriteTime.dwHighDateTime << 32) | fd->ftLastWriteTime.dwLowDateTime;
		if (ft > 116444736000000000ULL)
		{
			const UINT64 unix_ns100 = ft - 116444736000000000ULL;
			struct timespec ts[2];
			ts[0].tv_sec = (time_t)(unix_ns100 / 10000000ULL);
			ts[0].tv_nsec = (long)((unix_ns100 % 10000000ULL) * 100);
			ts[1] = ts[0];
			(void)utimensat(AT_FDCWD, s->dl.cur_path, ts, 0);
		}
	}
	free(s->dl.cur_path);
	s->dl.cur_path = NULL;
}

static UINT mr_cliprdr_server_file_contents_response(CliprdrClientContext* ctx,
                                                     const CLIPRDR_FILE_CONTENTS_RESPONSE* resp)
{
	mr_session* s = clip_session(ctx);
	if (!s->dl.active || resp->streamId != s->dl.stream_id)
		return CHANNEL_RC_OK; /* late or foreign stream */
	if (!(resp->common.msgFlags & CB_RESPONSE_OK))
	{
		dl_finish(s, "the session refused to send the file contents");
		return CHANNEL_RC_OK;
	}
	if (s->dl.waiting_size)
	{
		if (resp->cbRequested < 8)
		{
			dl_finish(s, "malformed file size response");
			return CHANNEL_RC_OK;
		}
		UINT64 size = 0;
		memcpy(&size, resp->requestedData, 8);
		s->dl.size = size;
		s->dl.total += size;
		if (size == 0)
		{
			dl_close_current(s);
			s->dl.index++;
			dl_next_file(s);
			return CHANNEL_RC_OK;
		}
		dl_request(s, FILECONTENTS_RANGE, (UINT32)(size < MR_CLIP_CHUNK ? size : MR_CLIP_CHUNK));
		return CHANNEL_RC_OK;
	}
	UINT32 n = resp->cbRequested;
	if (n > 0 && fwrite(resp->requestedData, 1, n, s->dl.fp) != n)
	{
		dl_finish(s, "could not write the file (disk full?)");
		return CHANNEL_RC_OK;
	}
	UINT64 before = s->dl.done;
	s->dl.offset += n;
	s->dl.done += n;
	if (s->cb.on_clipboard_progress && (s->dl.done / MR_CLIP_PROGRESS_STEP != before / MR_CLIP_PROGRESS_STEP || s->dl.offset >= s->dl.size || n == 0))
	{
		const char* name = strrchr(s->dl.cur_path, '/');
		s->cb.on_clipboard_progress(s->cb.user, s->dl.done, s->dl.total, name ? name + 1 : s->dl.cur_path);
	}
	if (n == 0 || s->dl.offset >= s->dl.size)
	{
		dl_close_current(s);
		s->dl.index++;
		dl_next_file(s);
		return CHANNEL_RC_OK;
	}
	UINT64 left = s->dl.size - s->dl.offset;
	dl_request(s, FILECONTENTS_RANGE, (UINT32)(left < MR_CLIP_CHUNK ? left : MR_CLIP_CHUNK));
	return CHANNEL_RC_OK;
}

int32_t mr_clipboard_fetch_files(mr_session* s, const char* dest_dir)
{
	if (!s || !dest_dir || !s->remote_files || s->remote_file_count == 0 || s->dl.active)
		return 0;
	CliprdrClientContext* clip = clip_ctx(s);
	if (!clip)
		return 0;
	if (!dl_mkdirs(dest_dir))
		return 0;
	memset(&s->dl, 0, sizeof(s->dl));
	s->dl.active = 1;
	s->dl.dest = _strdup(dest_dir);
	/* directories first, so empty folders exist even when nothing is downloaded into them */
	for (UINT32 i = 0; i < s->remote_file_count; i++)
	{
		const FILEDESCRIPTORW* fd = &s->remote_files[i];
		if ((fd->dwFlags & FD_ATTRIBUTES) && (fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
		{
			char* rel = dl_relative_path(fd);
			if (rel)
			{
				size_t len = strlen(dest_dir) + 1 + strlen(rel) + 1;
				char* path = malloc(len);
				if (path)
				{
					snprintf(path, len, "%s/%s", dest_dir, rel);
					(void)dl_mkdirs(path);
					free(path);
				}
				free(rel);
			}
		}
		else if (fd->dwFlags & FD_FILESIZE)
			s->dl.total += ((UINT64)fd->nFileSizeHigh << 32) | fd->nFileSizeLow;
	}
	if (s->remote_caps_flags & CB_CAN_LOCK_CLIPDATA)
	{
		CLIPRDR_LOCK_CLIPBOARD_DATA l = { 0 };
		l.common.msgType = CB_LOCK_CLIPDATA;
		l.clipDataId = ++s->clip_data_counter;
		if (clip->ClientLockClipboardData(clip, &l) == CHANNEL_RC_OK)
		{
			s->dl.locked = 1;
			s->dl.clip_data_id = l.clipDataId;
		}
	}
	mr_logf(s, "Clipboard: fetching %u item(s) from the session into %s", (unsigned)s->remote_file_count, dest_dir);
	dl_next_file(s);
	return 1;
}

static void clip_forget_remote_files(mr_session* s)
{
	free(s->remote_files);
	s->remote_files = NULL;
	s->remote_file_count = 0;
}

static UINT mr_cliprdr_server_format_list(CliprdrClientContext* ctx,
                                          const CLIPRDR_FORMAT_LIST* list)
{
	mr_session* s = clip_session(ctx);
	UINT32 want = 0;
	UINT32 fgd = 0;
	for (UINT32 i = 0; i < list->numFormats; i++)
	{
		const CLIPRDR_FORMAT* f = &list->formats[i];
		if (f->formatName && strcmp(f->formatName, type_FileGroupDescriptorW) == 0)
			fgd = f->formatId;
		else if (f->formatId == CF_UNICODETEXT)
			want = f->formatId;
		else if (f->formatId == CF_TEXT && want == 0)
			want = f->formatId;
	}
	s->remote_text_format = want;
	s->remote_fgd_format = fgd;
	/* the remote selection changed: a download in flight refers to the old one */
	if (s->dl.active)
		dl_finish(s, "the session clipboard changed during the transfer");
	clip_forget_remote_files(s);
	(void)cliprdr_file_context_notify_new_server_format_list(s->clip_file);
	mr_logf(s, "Clipboard: session offers %u format(s): text %s, files %s", (unsigned)list->numFormats,
	        want == CF_UNICODETEXT ? "unicode" : want == CF_TEXT ? "ansi" : "none", fgd ? "yes" : "no");

	CLIPRDR_FORMAT_LIST_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_LIST_RESPONSE;
	resp.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = ctx->ClientFormatListResponse(ctx, &resp);
	if (rc != CHANNEL_RC_OK)
		return rc;

	UINT32 request = fgd ? fgd : want;
	s->pending_request = fgd ? 2 : want ? 1 : 0;
	if (request && (fgd ? s->cb.on_clipboard_files != NULL : s->cb.on_clipboard_text != NULL))
	{
		CLIPRDR_FORMAT_DATA_REQUEST req = { 0 };
		req.common.msgType = CB_FORMAT_DATA_REQUEST;
		req.requestedFormatId = request;
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
	mr_session* s = clip_session(ctx);
	CLIPRDR_FORMAT_DATA_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_DATA_RESPONSE;
	BYTE* data = NULL;
	UINT32 len = 0;
	const UINT32 fgd_id = ClipboardGetFormatId(s->clip_sys, type_FileGroupDescriptorW);

	pthread_mutex_lock(&s->clip_lock);
	if (req->requestedFormatId == fgd_id && s->clip_local_files)
	{
		/* text/uri-list -> FILEDESCRIPTORW[] (winpr synthesizer) -> CLIPRDR_FILELIST on the wire */
		ClipboardLock(s->clip_sys);
		const UINT32 uri_id = ClipboardGetFormatId(s->clip_sys, mime_uri_list);
		if (ClipboardSetData(s->clip_sys, uri_id, s->clip_local_files, (UINT32)strlen(s->clip_local_files) + 1))
		{
			UINT32 dsize = 0;
			FILEDESCRIPTORW* arr = ClipboardGetData(s->clip_sys, fgd_id, &dsize);
			if (arr)
			{
				const UINT32 count = dsize / sizeof(FILEDESCRIPTORW);
				const UINT serr = cliprdr_serialize_file_list_ex(cliprdr_file_context_remote_get_flags(s->clip_file), arr, count, &data, &len);
				if (serr != 0)
				{
					data = NULL;
					mr_logf(s, serr == ERROR_FILE_TOO_LARGE
					               ? "Clipboard: a file is 2 GiB or larger and the session did not negotiate huge file support"
					               : "Clipboard: could not build the file list (error %u)", (unsigned)serr);
				}
				free(arr);
			}
		}
		ClipboardUnlock(s->clip_sys);
	}
	else if (s->clip_local_text)
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
	mr_logf(s, "Clipboard: session asked for format %u, sent %u bytes (rc=%u)", (unsigned)req->requestedFormatId, (unsigned)len, (unsigned)rc);
	free(data);
	return rc;
}

static UINT mr_cliprdr_server_format_data_response(CliprdrClientContext* ctx,
                                                   const CLIPRDR_FORMAT_DATA_RESPONSE* resp)
{
	mr_session* s = clip_session(ctx);
	const int kind = s->pending_request;
	s->pending_request = 0;
	mr_logf(s, "Clipboard: data from session, %u bytes, %s", (unsigned)resp->common.dataLen,
	        (resp->common.msgFlags & CB_RESPONSE_OK) ? "ok" : "refused");
	if (!(resp->common.msgFlags & CB_RESPONSE_OK) || !resp->requestedFormatData || resp->common.dataLen == 0)
		return CHANNEL_RC_OK;

	if (kind == 2)
	{
		FILEDESCRIPTORW* arr = NULL;
		UINT32 count = 0;
		if (cliprdr_parse_file_list(resp->requestedFormatData, resp->common.dataLen, &arr, &count) != 0 || !arr)
		{
			mr_logf(s, "Clipboard: could not parse the session's file list");
			return CHANNEL_RC_OK;
		}
		clip_forget_remote_files(s);
		s->remote_files = arr;
		s->remote_file_count = count;
		UINT64 total = 0;
		UINT32 dirs = 0;
		char preview[256] = { 0 };
		for (UINT32 i = 0; i < count; i++)
		{
			const FILEDESCRIPTORW* fd = &arr[i];
			if ((fd->dwFlags & FD_ATTRIBUTES) && (fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
				dirs++;
			else if (fd->dwFlags & FD_FILESIZE)
				total += ((UINT64)fd->nFileSizeHigh << 32) | fd->nFileSizeLow;
			if (i < 3)
			{
				char* name = ConvertWCharNToUtf8Alloc(fd->cFileName, ARRAYSIZE(fd->cFileName), NULL);
				if (name)
				{
					size_t used = strlen(preview);
					snprintf(preview + used, sizeof(preview) - used, "%s%s", used ? ", " : "", name);
					free(name);
				}
			}
		}
		if (s->cb.on_clipboard_files)
			s->cb.on_clipboard_files(s->cb.user, (int32_t)count, (int32_t)dirs, total, preview);
		return CHANNEL_RC_OK;
	}

	if (!s->cb.on_clipboard_text)
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

static int32_t clip_announce(mr_session* s)
{
	pthread_mutex_lock(&s->clip_lock);
	int ready = s->clip_ready;
	pthread_mutex_unlock(&s->clip_lock);
	if (!ready || !clip_ctx(s) || !s->connected)
	{
		mr_logf(s, "Clipboard: local content kept until the channel is ready (ready=%d channel=%d connected=%d)", ready, clip_ctx(s) != NULL, s->connected);
		return 0; /* kept; announced when MonitorReady completes */
	}
	return mr_cliprdr_send_format_list(s) == CHANNEL_RC_OK ? 1 : 0;
}

int32_t mr_set_clipboard_text(mr_session* s, const char* utf8)
{
	if (!s)
		return 0;
	pthread_mutex_lock(&s->clip_lock);
	free(s->clip_local_text);
	s->clip_local_text = utf8 ? _strdup(utf8) : NULL;
	int had_files = s->clip_local_files != NULL;
	free(s->clip_local_files);
	s->clip_local_files = NULL;
	pthread_mutex_unlock(&s->clip_lock);
	if (had_files)
		(void)cliprdr_file_context_clear(s->clip_file);
	return clip_announce(s);
}

/* file:// URI per RFC 8089 with percent-encoding; winpr's uri-list parser decodes it again */
static int uri_append(char** buf, size_t* cap, size_t* len, const char* text)
{
	size_t add = strlen(text);
	if (*len + add + 1 > *cap)
	{
		size_t ncap = (*len + add + 1) * 2;
		char* n = realloc(*buf, ncap);
		if (!n)
			return 0;
		*buf = n;
		*cap = ncap;
	}
	memcpy(*buf + *len, text, add + 1);
	*len += add;
	return 1;
}

static char* uri_list_from_paths(const char* paths)
{
	size_t cap = 256, len = 0;
	char* out = malloc(cap);
	if (!out)
		return NULL;
	out[0] = 0;
	const char* p = paths;
	while (*p)
	{
		const char* nl = strchr(p, '\n');
		size_t plen = nl ? (size_t)(nl - p) : strlen(p);
		if (plen > 0)
		{
			if (!uri_append(&out, &cap, &len, "file://"))
				goto fail;
			for (size_t i = 0; i < plen; i++)
			{
				unsigned char c = (unsigned char)p[i];
				char enc[4];
				if (isalnum(c) || c == '/' || c == '-' || c == '_' || c == '.' || c == '~')
					snprintf(enc, sizeof(enc), "%c", c);
				else
					snprintf(enc, sizeof(enc), "%%%02X", c);
				if (!uri_append(&out, &cap, &len, enc))
					goto fail;
			}
			if (!uri_append(&out, &cap, &len, "\r\n"))
				goto fail;
		}
		if (!nl)
			break;
		p = nl + 1;
	}
	return out;
fail:
	free(out);
	return NULL;
}

int32_t mr_set_clipboard_files(mr_session* s, const char* paths_newline_separated)
{
	if (!s)
		return 0;
	char* uris = paths_newline_separated && *paths_newline_separated ? uri_list_from_paths(paths_newline_separated) : NULL;
	pthread_mutex_lock(&s->clip_lock);
	free(s->clip_local_files);
	s->clip_local_files = uris;
	free(s->clip_local_text);
	s->clip_local_text = NULL;
	pthread_mutex_unlock(&s->clip_lock);
	if (uris)
		(void)cliprdr_file_context_update_client_data(s->clip_file, uris, strlen(uris));
	return clip_announce(s);
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
		(void)cliprdr_file_context_init(s->clip_file, c); /* sets c->custom and serves our local files */
		c->ServerFileContentsResponse = mr_cliprdr_server_file_contents_response;
		c->MonitorReady = mr_cliprdr_monitor_ready;
		c->ServerCapabilities = mr_cliprdr_server_capabilities;
		c->ServerFormatList = mr_cliprdr_server_format_list;
		c->ServerFormatListResponse = mr_cliprdr_server_format_list_response;
		c->ServerFormatDataRequest = mr_cliprdr_server_format_data_request;
		c->ServerFormatDataResponse = mr_cliprdr_server_format_data_response;
		pthread_mutex_lock(&s->chan_lock);
		s->cliprdr = c;
		pthread_mutex_unlock(&s->chan_lock);
		mr_logf(s, "Clipboard: channel connected");
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
		CliprdrClientContext* c = (CliprdrClientContext*)e->pInterface;
		pthread_mutex_lock(&s->chan_lock);
		s->cliprdr = NULL;
		pthread_mutex_unlock(&s->chan_lock);
		pthread_mutex_lock(&s->clip_lock);
		s->clip_ready = 0; /* a reconnect brings a fresh MonitorReady */
		pthread_mutex_unlock(&s->clip_lock);
		if (s->dl.active)
			dl_finish(s, "the clipboard channel closed during the transfer");
		clip_forget_remote_files(s);
		(void)cliprdr_file_context_uninit(s->clip_file, c);
		mr_logf(s, "Clipboard: channel disconnected");
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

void mr_session_destroy(mr_session* s);

mr_session* mr_session_create(const mr_callbacks* callbacks)
{
	mr_session* s = calloc(1, sizeof(mr_session));
	if (!s)
		return NULL;
	if (callbacks)
		s->cb = *callbacks;
	pthread_mutex_init(&s->clip_lock, NULL);
	pthread_mutex_init(&s->chan_lock, NULL);
	s->clip_sys = ClipboardCreate();
	s->clip_file = cliprdr_file_context_new(s);
	if (!s->clip_sys || !s->clip_file)
	{
		mr_session_destroy(s);
		return NULL;
	}
	(void)cliprdr_file_context_set_locally_available(s->clip_file, TRUE);

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
	if (s->dl.active)
		dl_reset(s);
	clip_forget_remote_files(s);
	if (s->clip_file)
		cliprdr_file_context_free(s->clip_file);
	if (s->clip_sys)
		ClipboardDestroy(s->clip_sys);
	pthread_mutex_destroy(&s->clip_lock);
	pthread_mutex_destroy(&s->chan_lock);
	free(s->clip_local_text);
	free(s->clip_local_files);
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
