/* macOS keyboard layout detection, kept apart from FreeRDP headers (winpr macros clash with Carbon). */
#include <stdint.h>
#include <string.h>
#include <Carbon/Carbon.h>
#include "kbd_table.h"

__attribute__((visibility("default"))) uint32_t mr_detect_keyboard_layout(void)
{
	uint32_t layout = 0;
	TISInputSourceRef src = TISCopyCurrentKeyboardLayoutInputSource();
	if (!src)
		return 0;
	CFStringRef id = (CFStringRef)TISGetInputSourceProperty(src, kTISPropertyInputSourceID);
	char buf[256] = { 0 };
	if (id && CFStringGetCString(id, buf, sizeof(buf), kCFStringEncodingUTF8))
	{
		for (size_t i = 0; i < sizeof(kbd_table) / sizeof(kbd_table[0]); i++)
		{
			if (strcmp(kbd_table[i].id, buf) == 0)
			{
				layout = kbd_table[i].layout;
				break;
			}
		}
	}
	CFRelease(src);
	return layout;
}
