/* Test clipboard server: behaves like Windows' rdpclip for file transfers.
 *   CLIPSRV_OFFER=<file|dir>     offer this to the client once its first format list arrives
 *   CLIPSRV_MAX_REQUEST=<bytes>  refuse range requests larger than this (CB_RESPONSE_FAIL)
 *   CLIPSRV_DELAY_MS=<ms>        sleep before answering each file contents request (simulated latency)
 *   CLIPSRV_SAVE=<dir>           when the client announces FileGroupDescriptorW, fetch everything into this dir
 *   CLIPSRV_CHUNK=<bytes>        request size used when fetching (default 262144, like rdpclip)
 */
#include <freerdp/config.h>
#include <winpr/assert.h>
#include <winpr/string.h>
#include <freerdp/server/cliprdr.h>
#include <freerdp/channels/cliprdr.h>
#include <freerdp/utils/cliprdr_utils.h>
#include <sys/stat.h>
#include <dirent.h>
#include <unistd.h>
#include <time.h>
#include "sf_clip.h"

typedef struct
{
	char* local;
	char* rel; /* backslash separated */
	int is_dir;
	UINT64 size;
	UINT64 mtime_ft;
} clip_entry;

typedef struct
{
	CliprdrServerContext* ctx;
	clip_entry* offer;
	UINT32 offer_count;
	UINT32 max_request, delay_ms, chunk;
	const char* save_dir;
	int offered;
	UINT32 my_fgd_id;
	UINT32 lock_counter;
	/* fetching from the client */
	FILEDESCRIPTORW* dl;
	UINT32 dl_count, dl_index;
	UINT64 dl_offset, dl_size, dl_bytes;
	FILE* dl_fp;
	UINT32 dl_stream, dl_lock;
	int dl_locked;
	struct timespec dl_t0;
	UINT32 served_requests;
	UINT32 max_inflight, inflight;
} clip_state;

static double now_s(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return ts.tv_sec + ts.tv_nsec / 1e9;
}

static void add_entry(clip_state* st, const char* local, const char* rel)
{
	struct stat sb;
	if (stat(local, &sb) != 0)
		return;
	st->offer = realloc(st->offer, (st->offer_count + 1) * sizeof(clip_entry));
	clip_entry* e = &st->offer[st->offer_count++];
	e->local = _strdup(local);
	e->rel = _strdup(rel);
	for (char* p = e->rel; *p; p++)
		if (*p == '/')
			*p = '\\';
	e->is_dir = S_ISDIR(sb.st_mode);
	e->size = e->is_dir ? 0 : (UINT64)sb.st_size;
	e->mtime_ft = ((UINT64)sb.st_mtime + 11644473600ULL) * 10000000ULL;
	if (e->is_dir)
	{
		DIR* d = opendir(local);
		if (!d)
			return;
		struct dirent* de;
		while ((de = readdir(d)))
		{
			if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0)
				continue;
			char l[4096], r[4096];
			snprintf(l, sizeof(l), "%s/%s", local, de->d_name);
			snprintf(r, sizeof(r), "%s/%s", rel, de->d_name);
			add_entry(st, l, r);
		}
		closedir(d);
	}
}

static UINT send_offer(clip_state* st)
{
	CLIPRDR_FORMAT fmt = { 0 };
	fmt.formatId = st->my_fgd_id;
	fmt.formatName = "FileGroupDescriptorW";
	CLIPRDR_FORMAT_LIST list = { 0 };
	list.common.msgType = CB_FORMAT_LIST;
	list.numFormats = 1;
	list.formats = &fmt;
	fprintf(stderr, "[clipsrv] offering %u entries\n", st->offer_count);
	return st->ctx->ServerFormatList(st->ctx, &list);
}

static UINT on_client_caps(CliprdrServerContext* ctx, const CLIPRDR_CAPABILITIES* caps)
{
	(void)caps;
	fprintf(stderr, "[clipsrv] client caps: long names %d, files %d, lock %d, huge %d\n", ctx->useLongFormatNames,
	        ctx->streamFileClipEnabled, ctx->canLockClipData, ctx->hasHugeFileSupport);
	return CHANNEL_RC_OK;
}

static void dl_next(clip_state* st);

static UINT on_client_format_list(CliprdrServerContext* ctx, const CLIPRDR_FORMAT_LIST* list)
{
	clip_state* st = ctx->custom;
	CLIPRDR_FORMAT_LIST_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_LIST_RESPONSE;
	resp.common.msgFlags = CB_RESPONSE_OK;
	UINT rc = ctx->ServerFormatListResponse(ctx, &resp);
	UINT32 fgd = 0;
	for (UINT32 i = 0; i < list->numFormats; i++)
		if (list->formats[i].formatName && strcmp(list->formats[i].formatName, "FileGroupDescriptorW") == 0)
			fgd = list->formats[i].formatId;
	fprintf(stderr, "[clipsrv] client format list: %u formats, FileGroupDescriptorW id %u\n", list->numFormats, fgd);
	if (fgd && st->save_dir && !st->dl)
	{
		if (ctx->canLockClipData)
		{
			CLIPRDR_LOCK_CLIPBOARD_DATA l = { 0 };
			l.common.msgType = CB_LOCK_CLIPDATA;
			l.clipDataId = ++st->lock_counter;
			st->dl_lock = l.clipDataId;
			st->dl_locked = ctx->ServerLockClipboardData(ctx, &l) == CHANNEL_RC_OK;
		}
		CLIPRDR_FORMAT_DATA_REQUEST req = { 0 };
		req.common.msgType = CB_FORMAT_DATA_REQUEST;
		req.requestedFormatId = fgd;
		(void)ctx->ServerFormatDataRequest(ctx, &req);
	}
	if (st->offer_count && !st->offered)
	{
		st->offered = 1;
		(void)send_offer(st);
	}
	return rc;
}

static UINT on_client_format_data_request(CliprdrServerContext* ctx, const CLIPRDR_FORMAT_DATA_REQUEST* req)
{
	clip_state* st = ctx->custom;
	CLIPRDR_FORMAT_DATA_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FORMAT_DATA_RESPONSE;
	BYTE* data = NULL;
	UINT32 len = 0;
	if (req->requestedFormatId == st->my_fgd_id && st->offer_count)
	{
		FILEDESCRIPTORW* arr = calloc(st->offer_count, sizeof(FILEDESCRIPTORW));
		for (UINT32 i = 0; i < st->offer_count; i++)
		{
			const clip_entry* e = &st->offer[i];
			FILEDESCRIPTORW* fd = &arr[i];
			fd->dwFlags = FD_ATTRIBUTES | FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI;
			fd->dwFileAttributes = e->is_dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
			fd->nFileSizeLow = (UINT32)(e->size & 0xFFFFFFFFu);
			fd->nFileSizeHigh = (UINT32)(e->size >> 32);
			fd->ftLastWriteTime.dwLowDateTime = (UINT32)(e->mtime_ft & 0xFFFFFFFFu);
			fd->ftLastWriteTime.dwHighDateTime = (UINT32)(e->mtime_ft >> 32);
			(void)ConvertUtf8ToWChar(e->rel, fd->cFileName, ARRAYSIZE(fd->cFileName));
		}
		UINT32 flags = CB_STREAM_FILECLIP_ENABLED | (ctx->hasHugeFileSupport ? CB_HUGE_FILE_SUPPORT_ENABLED : 0);
		const UINT serr = cliprdr_serialize_file_list_ex(flags, arr, st->offer_count, &data, &len);
		if (serr != 0)
		{
			fprintf(stderr, "[clipsrv] serialize failed: %u (flags 0x%x)\n", serr, flags);
			data = NULL;
		}
		free(arr);
	}
	resp.common.msgFlags = data ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
	resp.common.dataLen = len;
	resp.requestedFormatData = data;
	UINT rc = ctx->ServerFormatDataResponse(ctx, &resp);
	fprintf(stderr, "[clipsrv] descriptor requested (format %u): sent %u bytes\n", req->requestedFormatId, len);
	free(data);
	return rc;
}

static UINT on_client_file_contents_request(CliprdrServerContext* ctx, const CLIPRDR_FILE_CONTENTS_REQUEST* req)
{
	clip_state* st = ctx->custom;
	CLIPRDR_FILE_CONTENTS_RESPONSE resp = { 0 };
	resp.common.msgType = CB_FILECONTENTS_RESPONSE;
	resp.streamId = req->streamId;
	BYTE* buf = NULL;
	UINT32 n = 0;
	int ok = 0;
	st->served_requests++;
	if (st->delay_ms)
		usleep(st->delay_ms * 1000);
	if (req->listIndex < st->offer_count)
	{
		const clip_entry* e = &st->offer[req->listIndex];
		if (req->dwFlags & FILECONTENTS_SIZE)
		{
			buf = malloc(8);
			memcpy(buf, &e->size, 8);
			n = 8;
			ok = 1;
		}
		else if (req->dwFlags & FILECONTENTS_RANGE)
		{
			if (st->max_request && req->cbRequested > st->max_request)
				fprintf(stderr, "[clipsrv] REFUSING range request of %u bytes (max %u)\n", req->cbRequested, st->max_request);
			else
			{
				const UINT64 off = ((UINT64)req->nPositionHigh << 32) | req->nPositionLow;
				FILE* f = fopen(e->local, "rb");
				if (f && fseeko(f, (off_t)off, SEEK_SET) == 0)
				{
					buf = malloc(req->cbRequested ? req->cbRequested : 1);
					n = (UINT32)fread(buf, 1, req->cbRequested, f);
					ok = 1;
				}
				if (f)
					fclose(f);
				fprintf(stderr, "[clipsrv] range #%u off=%llu len=%u -> %u\n", req->listIndex, (unsigned long long)off, req->cbRequested, n);
			}
		}
	}
	resp.common.msgFlags = ok ? CB_RESPONSE_OK : CB_RESPONSE_FAIL;
	resp.cbRequested = n;
	resp.requestedData = buf;
	UINT rc = ctx->ServerFileContentsResponse(ctx, &resp);
	free(buf);
	return rc;
}

static UINT on_lock(CliprdrServerContext* ctx, const CLIPRDR_LOCK_CLIPBOARD_DATA* l)
{
	(void)ctx;
	fprintf(stderr, "[clipsrv] client locked id %u\n", l->clipDataId);
	return CHANNEL_RC_OK;
}
static UINT on_unlock(CliprdrServerContext* ctx, const CLIPRDR_UNLOCK_CLIPBOARD_DATA* u)
{
	(void)ctx;
	fprintf(stderr, "[clipsrv] client unlocked id %u\n", u->clipDataId);
	return CHANNEL_RC_OK;
}

/* ----- fetching what the client announced ----- */

static void dl_request(clip_state* st, UINT32 flags, UINT32 cb)
{
	CLIPRDR_FILE_CONTENTS_REQUEST req = { 0 };
	req.common.msgType = CB_FILECONTENTS_REQUEST;
	req.streamId = ++st->dl_stream;
	req.listIndex = st->dl_index;
	req.dwFlags = flags;
	req.nPositionLow = (UINT32)(st->dl_offset & 0xFFFFFFFFu);
	req.nPositionHigh = (UINT32)(st->dl_offset >> 32);
	req.cbRequested = cb;
	req.haveClipDataId = st->dl_locked ? TRUE : FALSE;
	req.clipDataId = st->dl_lock;
	(void)st->ctx->ServerFileContentsRequest(st->ctx, &req);
}

static void dl_finish(clip_state* st, const char* err)
{
	const double secs = now_s() - (st->dl_t0.tv_sec + st->dl_t0.tv_nsec / 1e9);
	fprintf(stderr, "[clipsrv] FETCH %s: %u entries, %llu bytes in %.2f s (%.1f MB/s)\n", err ? err : "DONE", st->dl_count,
	        (unsigned long long)st->dl_bytes, secs, secs > 0 ? st->dl_bytes / 1048576.0 / secs : 0);
	if (st->dl_locked)
	{
		CLIPRDR_UNLOCK_CLIPBOARD_DATA u = { 0 };
		u.common.msgType = CB_UNLOCK_CLIPDATA;
		u.clipDataId = st->dl_lock;
		(void)st->ctx->ServerUnlockClipboardData(st->ctx, &u);
		st->dl_locked = 0;
	}
	if (st->dl_fp)
		fclose(st->dl_fp);
	st->dl_fp = NULL;
	free(st->dl);
	st->dl = NULL;
}

static void dl_next(clip_state* st)
{
	while (st->dl_index < st->dl_count)
	{
		const FILEDESCRIPTORW* fd = &st->dl[st->dl_index];
		char* name = ConvertWCharNToUtf8Alloc(fd->cFileName, ARRAYSIZE(fd->cFileName), NULL);
		for (char* p = name; p && *p; p++)
			if (*p == '\\')
				*p = '/';
		char path[4096];
		snprintf(path, sizeof(path), "%s/%s", st->save_dir, name ? name : "unnamed");
		free(name);
		if ((fd->dwFlags & FD_ATTRIBUTES) && (fd->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
		{
			mkdir(path, 0700);
			st->dl_index++;
			continue;
		}
		st->dl_fp = fopen(path, "wb");
		st->dl_offset = 0;
		st->dl_size = ((UINT64)fd->nFileSizeHigh << 32) | fd->nFileSizeLow;
		if (st->dl_size == 0)
		{
			fclose(st->dl_fp);
			st->dl_fp = NULL;
			st->dl_index++;
			continue;
		}
		dl_request(st, FILECONTENTS_RANGE, (UINT32)(st->dl_size < st->chunk ? st->dl_size : st->chunk));
		return;
	}
	dl_finish(st, NULL);
}

static UINT on_client_format_data_response(CliprdrServerContext* ctx, const CLIPRDR_FORMAT_DATA_RESPONSE* resp)
{
	clip_state* st = ctx->custom;
	if (!(resp->common.msgFlags & CB_RESPONSE_OK) || !st->save_dir)
		return CHANNEL_RC_OK;
	if (cliprdr_parse_file_list(resp->requestedFormatData, resp->common.dataLen, &st->dl, &st->dl_count) != 0)
	{
		fprintf(stderr, "[clipsrv] could not parse the client's file list\n");
		return CHANNEL_RC_OK;
	}
	fprintf(stderr, "[clipsrv] client's list has %u entries; fetching with %u-byte requests\n", st->dl_count, st->chunk);
	st->dl_index = 0;
	st->dl_bytes = 0;
	clock_gettime(CLOCK_MONOTONIC, &st->dl_t0);
	dl_next(st);
	return CHANNEL_RC_OK;
}

static UINT on_client_file_contents_response(CliprdrServerContext* ctx, const CLIPRDR_FILE_CONTENTS_RESPONSE* resp)
{
	clip_state* st = ctx->custom;
	if (!st->dl || resp->streamId != st->dl_stream)
		return CHANNEL_RC_OK;
	if (!(resp->common.msgFlags & CB_RESPONSE_OK))
	{
		dl_finish(st, "FAILED (client refused)");
		return CHANNEL_RC_OK;
	}
	if (resp->cbRequested)
		fwrite(resp->requestedData, 1, resp->cbRequested, st->dl_fp);
	st->dl_offset += resp->cbRequested;
	st->dl_bytes += resp->cbRequested;
	if (resp->cbRequested == 0 || st->dl_offset >= st->dl_size)
	{
		fclose(st->dl_fp);
		st->dl_fp = NULL;
		st->dl_index++;
		dl_next(st);
		return CHANNEL_RC_OK;
	}
	const UINT64 left = st->dl_size - st->dl_offset;
	dl_request(st, FILECONTENTS_RANGE, (UINT32)(left < st->chunk ? left : st->chunk));
	return CHANNEL_RC_OK;
}

BOOL sf_peer_clip_init(testPeerContext* context)
{
	clip_state* st = calloc(1, sizeof(clip_state));
	CliprdrServerContext* ctx = cliprdr_server_context_new(context->vcm);
	if (!ctx)
		return FALSE;
	st->ctx = ctx;
	st->my_fgd_id = 0xC0FF;
	st->chunk = 262144;
	const char* v;
	if ((v = getenv("CLIPSRV_OFFER")) && *v)
	{
		const char* base = strrchr(v, '/');
		add_entry(st, v, base ? base + 1 : v);
	}
	if ((v = getenv("CLIPSRV_MAX_REQUEST")) && *v)
		st->max_request = (UINT32)strtoul(v, NULL, 10);
	if ((v = getenv("CLIPSRV_DELAY_MS")) && *v)
		st->delay_ms = (UINT32)strtoul(v, NULL, 10);
	if ((v = getenv("CLIPSRV_CHUNK")) && *v)
		st->chunk = (UINT32)strtoul(v, NULL, 10);
	if ((v = getenv("CLIPSRV_SAVE")) && *v)
		st->save_dir = v;
	ctx->custom = st;
	ctx->rdpcontext = &context->_p;
	ctx->useLongFormatNames = TRUE;
	ctx->streamFileClipEnabled = TRUE;
	ctx->fileClipNoFilePaths = TRUE;
	ctx->canLockClipData = TRUE;
	ctx->hasHugeFileSupport = TRUE;
	ctx->ClientCapabilities = on_client_caps;
	ctx->ClientFormatList = on_client_format_list;
	ctx->ClientFormatDataRequest = on_client_format_data_request;
	ctx->ClientFormatDataResponse = on_client_format_data_response;
	ctx->ClientFileContentsRequest = on_client_file_contents_request;
	ctx->ClientFileContentsResponse = on_client_file_contents_response;
	ctx->ClientLockClipboardData = on_lock;
	ctx->ClientUnlockClipboardData = on_unlock;
	context->clip = ctx;
	fprintf(stderr, "[clipsrv] clipboard channel up (offer %u entries, max request %u, delay %u ms, save %s)\n",
	        st->offer_count, st->max_request, st->delay_ms, st->save_dir ? st->save_dir : "-");
	return ctx->Start(ctx) == CHANNEL_RC_OK;
}

void sf_peer_clip_uninit(testPeerContext* context)
{
	if (!context->clip)
		return;
	clip_state* st = context->clip->custom;
	context->clip->Stop(context->clip);
	cliprdr_server_context_free(context->clip);
	context->clip = NULL;
	if (st)
	{
		fprintf(stderr, "[clipsrv] served %u file contents requests\n", st->served_requests);
		for (UINT32 i = 0; i < st->offer_count; i++)
		{
			free(st->offer[i].local);
			free(st->offer[i].rel);
		}
		free(st->offer);
		free(st->dl);
		free(st);
	}
}
