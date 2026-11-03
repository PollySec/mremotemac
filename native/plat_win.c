/* The Windows side of what kbd_mac.c, clip_mac.c and promise_mac.m do on macOS. Secrets go to Windows Credential Manager
 * from C# (Services/WindowsCredentials.cs), so there is no counterpart of keychain_mac.c. */
#include <stdint.h>
#include <stdlib.h>
#include <windows.h>
#include "mrrdp.h"

/* the active layout's KLID, such as 0000041D for Swedish, is what FreeRDP's KeyboardLayout setting holds */
uint32_t mr_detect_keyboard_layout(void)
{
	WCHAR klid[KL_NAMELENGTH] = { 0 };
	if (!GetKeyboardLayoutNameW(klid))
		return 0;
	return (uint32_t)wcstoul(klid, NULL, 16);
}

/* the counterpart of NSPasteboard.changeCount: Windows bumps it on every clipboard change */
MRRDP_API int64_t mr_pasteboard_change_count(void)
{
	return (int64_t)GetClipboardSequenceNumber();
}

/* Windows has no file promises here: a download goes to the clipboard when it is complete. A zero token tells
 * mr_clipboard_fetch_files and the app that nothing was promised. */
MRRDP_API int64_t mr_promise_publish(const char* dest_dir, const char* names, const char* kinds)
{
	(void)dest_dir;
	(void)names;
	(void)kinds;
	return 0;
}

MRRDP_API void mr_promise_complete(int64_t token, const char* error)
{
	(void)token;
	(void)error;
}

MRRDP_API int32_t mr_promise_drag(int64_t token, void* view)
{
	(void)token;
	(void)view;
	return 0;
}
