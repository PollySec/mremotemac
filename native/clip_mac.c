/* macOS pasteboard change detection, kept apart from FreeRDP headers (Cocoa/winpr macros clash).
 * NSPasteboard has no change notification; the documented way is to poll changeCount, which is a cheap
 * integer read. Done through the Objective-C runtime so this stays a plain C file. */
#include <stdint.h>
#include <objc/objc.h>
#include <objc/runtime.h>
#include <objc/message.h>

__attribute__((visibility("default"))) int64_t mr_pasteboard_change_count(void)
{
	Class cls = objc_getClass("NSPasteboard");
	if (!cls)
		return -1;
	id pb = ((id(*)(Class, SEL))objc_msgSend)(cls, sel_registerName("generalPasteboard"));
	if (!pb)
		return -1;
	return (int64_t)((long (*)(id, SEL))objc_msgSend)(pb, sel_registerName("changeCount"));
}
