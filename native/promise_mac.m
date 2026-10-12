/* File promises for files fetched from a session (NSFilePromiseProvider, macOS 10.12+).
 *
 * The moment a fetch starts, one promise per top-level item goes on the general pasteboard, and the same promises back
 * the file tray the user can drag into Finder. Finder honours promises only for drag and drop (its Paste command stays
 * disabled while the pasteboard holds nothing else, verified on macOS 26), so on the pasteboard their job is to stop a
 * stale clipboard from being pasted during the download; the real file URLs replace them when the transfer is done.
 * A drop target asks us to write each item to the destination it chose; that call waits until the transfer has
 * finished and then clones the cached copy there, so a drag started mid-transfer lands by itself when the data is in.
 * Kept apart from FreeRDP headers (Cocoa/winpr macros clash). */
#import <AppKit/AppKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <copyfile.h>
#include <stdint.h>

@interface MRPromiseItem : NSObject
@property(nonatomic, strong) NSString* name;
@property(nonatomic) BOOL isDirectory;
@end
@implementation MRPromiseItem
@end

@interface MRPromiseTransfer : NSObject
@property(nonatomic, strong) NSString* destDir;
@property(nonatomic, strong) NSArray<MRPromiseItem*>* items;
@property(nonatomic, strong) NSMutableArray* delegates; /* NSFilePromiseProvider.delegate is weak: keep ours alive */
@property(nonatomic, strong) NSCondition* cond;
@property(nonatomic) BOOL done;
@property(nonatomic, strong) NSString* error;
@end
@implementation MRPromiseTransfer
@end

@interface MRPromiseDelegate : NSObject <NSFilePromiseProviderDelegate>
@property(nonatomic, weak) MRPromiseTransfer* transfer;
@property(nonatomic, strong) MRPromiseItem* item;
@end

static NSOperationQueue* mr_promise_queue(void)
{
	static NSOperationQueue* q;
	static dispatch_once_t once;
	dispatch_once(&once, ^{
		q = [[NSOperationQueue alloc] init];
		q.name = @"mRemoteMac file promises";
		q.maxConcurrentOperationCount = 2;
	});
	return q;
}

@implementation MRPromiseDelegate
- (NSString*)filePromiseProvider:(NSFilePromiseProvider*)p fileNameForType:(NSString*)type
{
	return self.item.name;
}
- (NSOperationQueue*)operationQueueForFilePromiseProvider:(NSFilePromiseProvider*)p
{
	return mr_promise_queue();
}
- (void)filePromiseProvider:(NSFilePromiseProvider*)p writePromiseToURL:(NSURL*)url completionHandler:(void (^)(NSError*))done
{
	MRPromiseTransfer* t = self.transfer;
	if (!t)
	{
		done([NSError errorWithDomain:@"com.pollysec.mremotemac" code:2 userInfo:@{ NSLocalizedDescriptionKey : @"the transfer no longer exists" }]);
		return;
	}
	[t.cond lock];
	while (!t.done)
		[t.cond wait];
	NSString* err = t.error;
	[t.cond unlock];
	if (err)
	{
		done([NSError errorWithDomain:@"com.pollysec.mremotemac" code:1 userInfo:@{ NSLocalizedDescriptionKey : err }]);
		return;
	}
	NSString* src = [t.destDir stringByAppendingPathComponent:self.item.name];
	copyfile_flags_t flags = COPYFILE_CLONE | (self.item.isDirectory ? COPYFILE_RECURSIVE : 0);
	if (copyfile(src.fileSystemRepresentation, url.path.fileSystemRepresentation, NULL, flags) != 0)
	{
		done([NSError errorWithDomain:NSPOSIXErrorDomain code:errno userInfo:@{ NSLocalizedDescriptionKey : [NSString stringWithFormat:@"could not copy %@ to %@", self.item.name, url.path] }]);
		return;
	}
	done(nil);
}
@end

/* Drag source for the file tray: a plain copy, modifier keys ignored. */
@interface MRDragSource : NSObject <NSDraggingSource>
@end
@implementation MRDragSource
- (NSDragOperation)draggingSession:(NSDraggingSession*)session sourceOperationMaskForDraggingContext:(NSDraggingContext)context
{
	return NSDragOperationCopy;
}
- (BOOL)ignoreModifierKeysForDraggingSession:(NSDraggingSession*)session
{
	return YES;
}
@end

/* Transfers stay registered for the life of the process: the pasteboard and any drop target may still hold promises
 * on them, and they only carry a few strings. */
static NSMutableDictionary<NSNumber*, MRPromiseTransfer*>* mr_transfers(void)
{
	static NSMutableDictionary* d;
	static dispatch_once_t once;
	dispatch_once(&once, ^{ d = [NSMutableDictionary dictionary]; });
	return d;
}
static int64_t g_next_token = 1;

static MRPromiseTransfer* mr_transfer(int64_t token)
{
	@synchronized(mr_transfers())
	{
		return mr_transfers()[@(token)];
	}
}

static UTType* mr_type_for(MRPromiseItem* item)
{
	if (item.isDirectory)
		return UTTypeFolder;
	NSString* ext = item.name.pathExtension;
	UTType* t = ext.length ? [UTType typeWithFilenameExtension:ext] : nil;
	return t ? t : UTTypeData;
}

static NSFilePromiseProvider* mr_provider(MRPromiseTransfer* t, MRPromiseItem* item)
{
	MRPromiseDelegate* d = [[MRPromiseDelegate alloc] init];
	d.transfer = t;
	d.item = item;
	[t.delegates addObject:d];
	return [[NSFilePromiseProvider alloc] initWithFileType:mr_type_for(item).identifier delegate:d];
}

/* names: newline-separated top-level entries in dest_dir; kinds: one char per entry, 'd' for a folder. Returns a
 * token (> 0) once the promises are on the pasteboard, 0 if nothing could be published. Call on the main thread. */
__attribute__((visibility("default"))) int64_t mr_promise_publish(const char* dest_dir, const char* names, const char* kinds)
{
	@autoreleasepool
	{
		if (!dest_dir || !names || !kinds)
			return 0;
		NSArray<NSString*>* list = [[NSString stringWithUTF8String:names] componentsSeparatedByString:@"\n"];
		MRPromiseTransfer* t = [[MRPromiseTransfer alloc] init];
		t.destDir = [NSString stringWithUTF8String:dest_dir];
		t.cond = [[NSCondition alloc] init];
		t.delegates = [NSMutableArray array];
		NSMutableArray<MRPromiseItem*>* items = [NSMutableArray array];
		NSUInteger k = 0;
		for (NSString* name in list)
		{
			if (name.length == 0)
				continue;
			MRPromiseItem* item = [[MRPromiseItem alloc] init];
			item.name = name;
			item.isDirectory = kinds[k] == 'd';
			if (kinds[k])
				k++;
			[items addObject:item];
		}
		if (items.count == 0)
			return 0;
		t.items = items;
		NSMutableArray* providers = [NSMutableArray array];
		for (MRPromiseItem* item in items)
			[providers addObject:mr_provider(t, item)];
		NSPasteboard* pb = [NSPasteboard generalPasteboard];
		[pb clearContents];
		if (![pb writeObjects:providers])
			return 0;
		int64_t token;
		@synchronized(mr_transfers())
		{
			token = g_next_token++;
			mr_transfers()[@(token)] = t;
		}
		return token;
	}
}

/* Marks the transfer finished (error NULL = success). Waiting and future drop operations proceed. Any thread. */
__attribute__((visibility("default"))) void mr_promise_complete(int64_t token, const char* error)
{
	@autoreleasepool
	{
		MRPromiseTransfer* t = mr_transfer(token);
		if (!t)
			return;
		[t.cond lock];
		if (!t.done)
		{
			t.done = YES;
			t.error = error ? [NSString stringWithUTF8String:error] : nil;
		}
		[t.cond broadcast];
		[t.cond unlock];
	}
}

/* Starts a drag of the transfer's items from the window's content view (an NSView*), using the mouse event being
 * handled right now. Finder-style file icons, stacked for several items. Main thread; returns 1 when the session
 * began. The drop completes once the transfer is done. */
__attribute__((visibility("default"))) int32_t mr_promise_drag(int64_t token, void* nsview)
{
	@autoreleasepool
	{
		MRPromiseTransfer* t = mr_transfer(token);
		NSView* view = (__bridge NSView*)nsview;
		if (!t || !view || !view.window)
			return 0;
		NSEvent* ev = [NSApp currentEvent];
		const NSEventType type = ev ? ev.type : 0;
		if (!ev || !(type == NSEventTypeLeftMouseDragged || type == NSEventTypeLeftMouseDown))
		{
			NSPoint p = [view.window mouseLocationOutsideOfEventStream];
			ev = [NSEvent mouseEventWithType:NSEventTypeLeftMouseDragged location:p modifierFlags:0 timestamp:[NSProcessInfo processInfo].systemUptime
			                     windowNumber:view.window.windowNumber context:nil eventNumber:0 clickCount:1 pressure:1];
		}
		const NSPoint at = [view convertPoint:ev.locationInWindow fromView:nil];
		static MRDragSource* source;
		if (!source)
			source = [[MRDragSource alloc] init];
		NSMutableArray<NSDraggingItem*>* dragItems = [NSMutableArray array];
		const CGFloat size = 64;
		NSUInteger i = 0;
		for (MRPromiseItem* item in t.items)
		{
			NSDraggingItem* di = [[NSDraggingItem alloc] initWithPasteboardWriter:mr_provider(t, item)];
			NSImage* icon = [[NSWorkspace sharedWorkspace] iconForContentType:mr_type_for(item)];
			icon.size = NSMakeSize(size, size);
			const CGFloat off = (CGFloat)(i < 4 ? i : 4) * 4; /* a little fan for several items */
			const CGFloat y = view.isFlipped ? at.y - size / 2 + off : at.y - size / 2 - off;
			[di setDraggingFrame:NSMakeRect(at.x - size / 2 + off, y, size, size) contents:icon];
			[dragItems addObject:di];
			i++;
		}
		NSDraggingSession* session = [view beginDraggingSessionWithItems:dragItems event:ev source:source];
		if (!session)
			return 0;
		session.animatesToStartingPositionsOnCancelOrFail = YES;
		session.draggingFormation = dragItems.count > 1 ? NSDraggingFormationStack : NSDraggingFormationNone;
		return 1;
	}
}
