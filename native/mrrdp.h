/*
 * mrrdp - thin C shim around libfreerdp-client for mRemoteMac.
 * All callbacks are invoked on the session's RDP thread.
 * Framebuffer pointers are only valid inside on_frame / on_resize callbacks.
 */
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MRRDP_API __attribute__((visibility("default")))

typedef struct mr_session mr_session;

/* Dirty rectangle in the framebuffer has been updated. */
typedef void (*mr_on_frame)(void* user, int32_t x, int32_t y, int32_t w, int32_t h);
/* diagnostic text, UTF-8, valid only during the call; level 0 = trace (log file only), 1 = event worth showing */
typedef void (*mr_on_log)(void* user, int32_t level, const char* message);
/* the session offered files: count of entries, how many are folders, total bytes of the files, first names */
typedef void (*mr_on_clipboard_files)(void* user, int32_t count, int32_t folders, uint64_t total_bytes, const char* preview);
typedef void (*mr_on_clipboard_progress)(void* user, uint64_t done, uint64_t total, const char* current_name);
/* error is NULL on success */
typedef void (*mr_on_clipboard_files_done)(void* user, const char* dest_dir, int32_t files, const char* error);
/* All dirty rectangles of one paint batch have been delivered; show the frame. */
typedef void (*mr_on_present)(void* user);
/* Framebuffer has been (re)allocated to a new size. */
typedef void (*mr_on_resize)(void* user, int32_t w, int32_t h);
typedef void (*mr_on_connected)(void* user);
/* error == 0 means clean disconnect. */
typedef void (*mr_on_disconnected)(void* user, uint32_t error, const char* name, const char* description);
/* Return 0 = reject, 1 = accept permanently, 2 = accept for this session only. */
typedef int32_t (*mr_on_verify_cert)(void* user, const char* host, uint16_t port,
                                     const char* common_name, const char* subject,
                                     const char* issuer, const char* fingerprint, uint32_t flags,
                                     int32_t changed, const char* old_subject,
                                     const char* old_issuer, const char* old_fingerprint);
/* Fill the three buffers (UTF-8, NUL terminated). Return 1 = ok, 0 = cancel. */
typedef int32_t (*mr_on_authenticate)(void* user, int32_t reason, char* username, int32_t username_cap,
                                      char* password, int32_t password_cap, char* domain,
                                      int32_t domain_cap);
/* Pointer (mouse cursor) management. bgra is w*h*4 bytes, premultiplied = no. Return an opaque handle. */
typedef void* (*mr_on_pointer_new)(void* user, int32_t w, int32_t h, int32_t hot_x, int32_t hot_y,
                                   const uint8_t* bgra);
typedef void (*mr_on_pointer_free)(void* user, void* handle);
/* handle == NULL means "default system cursor". */
typedef void (*mr_on_pointer_set)(void* user, void* handle);
typedef void (*mr_on_pointer_hidden)(void* user);
/* The connection dropped and automatic reconnection is being attempted (requires AutoReconnectionEnabled). */
typedef void (*mr_on_reconnecting)(void* user);
/* Remote side announced new clipboard text (UTF-8). */
typedef void (*mr_on_clipboard_text)(void* user, const char* utf8);

typedef struct {
	void* user;
	mr_on_frame on_frame;
	mr_on_present on_present;
	mr_on_resize on_resize;
	mr_on_connected on_connected;
	mr_on_disconnected on_disconnected;
	mr_on_verify_cert on_verify_cert;
	mr_on_authenticate on_authenticate;
	mr_on_pointer_new on_pointer_new;
	mr_on_pointer_free on_pointer_free;
	mr_on_pointer_set on_pointer_set;
	mr_on_pointer_hidden on_pointer_hidden;
	mr_on_clipboard_text on_clipboard_text;
	mr_on_reconnecting on_reconnecting;
	mr_on_log on_log;
	mr_on_clipboard_files on_clipboard_files;
	mr_on_clipboard_progress on_clipboard_progress;
	mr_on_clipboard_files_done on_clipboard_files_done;
} mr_callbacks;

MRRDP_API const char* mr_version(void);

/* Windows keyboard layout id (e.g. 0x041D Swedish) for the current macOS input source, 0 if unknown. */
MRRDP_API uint32_t mr_detect_keyboard_layout(void);

MRRDP_API mr_session* mr_session_create(const mr_callbacks* callbacks);
MRRDP_API void mr_session_destroy(mr_session* s);

/* Settings. key is the FreeRDP settings name without prefix, e.g. "ServerHostname". Return 1 ok / 0 fail. */
MRRDP_API int32_t mr_set_string(mr_session* s, const char* key, const char* value);
MRRDP_API int32_t mr_set_uint32(mr_session* s, const char* key, uint32_t value);
MRRDP_API int32_t mr_set_bool(mr_session* s, const char* key, int32_t value);
/* Parse xfreerdp-style arguments, e.g. {"mrrdp", "/sound", "/clipboard", "/dynamic-resolution"}. Return 0 ok. */
MRRDP_API int32_t mr_parse_args(mr_session* s, int32_t argc, const char** argv);

/* Start connecting on a background thread. Return 0 ok. */
MRRDP_API int32_t mr_connect(mr_session* s);
/* Abort/disconnect and join the thread. Safe to call multiple times. */
MRRDP_API void mr_disconnect(mr_session* s);
MRRDP_API int32_t mr_is_connected(mr_session* s);

/* Only valid on the RDP thread (inside callbacks). Format is 32bpp BGRA. */
MRRDP_API int32_t mr_get_framebuffer(mr_session* s, uint8_t** data, int32_t* width, int32_t* height,
                                     int32_t* stride);

/* Input. flags use the PTR_FLAGS_* / KBD_FLAGS_* values from FreeRDP. */
MRRDP_API void mr_send_mouse(mr_session* s, uint16_t flags, uint16_t x, uint16_t y);
MRRDP_API void mr_send_mouse_ext(mr_session* s, uint16_t flags, uint16_t x, uint16_t y);
MRRDP_API void mr_send_scancode(mr_session* s, int32_t down, int32_t repeat, uint32_t rdp_scancode);
MRRDP_API void mr_send_unicode(mr_session* s, int32_t down, uint16_t code);
MRRDP_API void mr_send_sync(mr_session* s, uint32_t toggle_flags);
MRRDP_API void mr_send_focus_in(mr_session* s, uint16_t toggle_flags);

/* Dynamic resolution (requires /dynamic-resolution). Return 1 if sent, 0 if not possible yet (no caps, rate limit, unchanged). */
MRRDP_API int32_t mr_resize_desktop(mr_session* s, uint32_t width, uint32_t height);

/* Clipboard: announce local text to remote. */
MRRDP_API int32_t mr_set_clipboard_text(mr_session* s, const char* utf8);
/* announces local files (absolute paths, newline separated) to the session; replaces any announced text */
MRRDP_API int32_t mr_set_clipboard_files(mr_session* s, const char* paths_newline_separated);
/* number of format lists the session has accepted so far: it has taken ownership of the announced content once this
 * grows (a paste typed before that may reach Windows while it still holds the old clipboard) */
MRRDP_API int32_t mr_clipboard_list_acks(mr_session* s);
/* downloads the files the session last offered into dest_dir (created); progress via callbacks. 0 = not started,
 * 1 = started, 2 = started and file promises for the items are on the Mac pasteboard; *promise_token then identifies
 * them for mr_promise_drag. items receives the top-level entries as "<f|d>\t<name>\n" lines (may be truncated).
 * Call on the main thread (the pasteboard is written here). */
MRRDP_API int32_t mr_clipboard_fetch_files(mr_session* s, const char* dest_dir, int64_t* promise_token, char* items, int32_t items_cap);
/* starts a Finder-style drag of the promised items from the window's content view (NSView*), main thread, during a
 * mouse event; the drop completes when the transfer is done. 1 = drag session began */
MRRDP_API int32_t mr_promise_drag(int64_t promise_token, void* nsview);

#ifdef __cplusplus
}
#endif
