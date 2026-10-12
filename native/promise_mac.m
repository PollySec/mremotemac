/* File promises for files fetched from a session (NSFilePromiseProvider, macOS 10.12+).
 *
 * The moment a fetch starts, one promise per top-level item goes on the general pasteboard. Finder honours promises
 * only for drag and drop (its Paste command stays disabled while the pasteboard holds nothing else, verified on
 * macOS 26), so their job here is to stop a stale clipboard from being pasted during the download; the real file URLs
 * replace them when the transfer is done. A receiver that does accept promises (drop targets) gets the item written
 * once the transfer has finished. Kept apart from FreeRDP headers (Cocoa/winpr macros clash). */
#import <AppKit/AppKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <copyfile.h>
#include <stdint.h>

@interface MRPromiseTransfer : NSObject
@property(nonatomic, strong) NSString* destDir;
@property(nonatomic, strong) NSCondition* cond;
@property(nonatomic) BOOL done;
@property(nonatomic, strong) NSString* error;
@end
@implementation MRPromiseTransfer
@end

@interface MRPromiseDelegate : NSObject <NSFilePromiseProviderDelegate>
@property(nonatomic, strong) MRPromiseTransfer* transfer;
@property(nonatomic, strong) NSString* name;
@property(nonatomic) BOOL isDirectory;
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
	return self.name;
}
- (NSOperationQueue*)operationQueueForFilePromiseProvider:(NSFilePromiseProvider*)p
{
	return mr_promise_queue();
}
- (void)filePromiseProvider:(NSFilePromiseProvider*)p writePromiseToURL:(NSURL*)url completionHandler:(void (^)(NSError*))done
{
	MRPromiseTransfer* t = self.transfer;
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
	NSString* src = [t.destDir stringByAppendingPathComponent:self.name];
	copyfile_flags_t flags = COPYFILE_CLONE | (self.isDirectory ? COPYFILE_RECURSIVE : 0);
	if (copyfile(src.fileSystemRepresentation, url.path.fileSystemRepresentation, NULL, flags) != 0)
	{
		done([NSError errorWithDomain:NSPOSIXErrorDomain code:errno userInfo:@{ NSLocalizedDescriptionKey : [NSString stringWithFormat:@"could not copy %@ to %@", self.name, url.path] }]);
		return;
	}
	done(nil);
}
@end

static NSMutableDictionary<NSNumber*, MRPromiseTransfer*>* mr_transfers(void)
{
	static NSMutableDictionary* d;
	static dispatch_once_t once;
	dispatch_once(&once, ^{ d = [NSMutableDictionary dictionary]; });
	return d;
}
static int64_t g_next_token = 1;

static NSString* mr_uti_for(NSString* name, BOOL isDirectory)
{
	if (isDirectory)
		return UTTypeFolder.identifier;
	NSString* ext = name.pathExtension;
	UTType* t = ext.length ? [UTType typeWithFilenameExtension:ext] : nil;
	return t ? t.identifier : UTTypeData.identifier;
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
		NSMutableArray* providers = [NSMutableArray array];
		NSUInteger k = 0;
		for (NSString* name in list)
		{
			if (name.length == 0)
				continue;
			BOOL dir = kinds[k] == 'd';
			if (kinds[k])
				k++;
			MRPromiseDelegate* d = [[MRPromiseDelegate alloc] init];
			d.transfer = t;
			d.name = name;
			d.isDirectory = dir;
			NSFilePromiseProvider* p = [[NSFilePromiseProvider alloc] initWithFileType:mr_uti_for(name, dir) delegate:d];
			[providers addObject:p];
		}
		if (providers.count == 0)
			return 0;
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

/* Marks the transfer finished (error NULL = success). Waiting and future paste operations proceed. Any thread. */
__attribute__((visibility("default"))) void mr_promise_complete(int64_t token, const char* error)
{
	@autoreleasepool
	{
		MRPromiseTransfer* t;
		@synchronized(mr_transfers())
		{
			t = mr_transfers()[@(token)];
			[mr_transfers() removeObjectForKey:@(token)]; /* the delegates keep it alive while promises exist */
		}
		if (!t)
			return;
		[t.cond lock];
		t.done = YES;
		t.error = error ? [NSString stringWithUTF8String:error] : nil;
		[t.cond broadcast];
		[t.cond unlock];
	}
}
