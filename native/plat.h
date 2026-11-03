/* What mrrdp.c needs from the operating system, for macOS and Windows. Include after the FreeRDP headers. */
#pragma once
#include <errno.h>
#include <stdint.h>
#include <string.h>
#include <sys/stat.h>
#include <fcntl.h>

#ifdef _WIN32

#include <winsock2.h>
#include <windows.h>
#include <io.h>
#include <process.h>

/* the small part of pthreads the shim uses; default pthread mutexes are not recursive and neither is an SRW lock */
typedef SRWLOCK pthread_mutex_t;
#define PTHREAD_MUTEX_INITIALIZER SRWLOCK_INIT
#define pthread_mutex_init(m, attr) InitializeSRWLock(m)
#define pthread_mutex_destroy(m) ((void)(m))
#define pthread_mutex_lock(m) AcquireSRWLockExclusive(m)
#define pthread_mutex_unlock(m) ReleaseSRWLockExclusive(m)
typedef HANDLE pthread_t;

typedef struct
{
	void* (*fn)(void*);
	void* arg;
} plat_thread_start;

static unsigned __stdcall plat_thread_trampoline(void* p)
{
	plat_thread_start start = *(plat_thread_start*)p;
	free(p);
	start.fn(start.arg);
	return 0;
}

static int pthread_create(pthread_t* thread, const void* attr, void* (*fn)(void*), void* arg)
{
	(void)attr;
	plat_thread_start* start = malloc(sizeof(*start));
	if (!start)
		return ENOMEM;
	start->fn = fn;
	start->arg = arg;
	*thread = (HANDLE)_beginthreadex(NULL, 0, plat_thread_trampoline, start, 0, NULL);
	if (!*thread)
	{
		free(start);
		return EAGAIN;
	}
	return 0;
}

static int pthread_join(pthread_t thread, void** result)
{
	(void)result;
	(void)WaitForSingleObject(thread, INFINITE);
	(void)CloseHandle(thread);
	return 0;
}

static uint64_t plat_now_ms(void)
{
	return GetTickCount64();
}

/* FreeRDP leaves Winsock to the application; without this every connection fails name resolution, even for an IP */
static void plat_init_process(void)
{
	static volatile LONG done;
	if (InterlockedExchange(&done, 1))
		return;
	WSADATA data;
	(void)WSAStartup(MAKEWORD(2, 2), &data);
}

/* every path below is UTF-8, as on macOS */
static int plat_create_file(const char* path)
{
	WCHAR* wide = ConvertUtf8ToWCharAlloc(path, NULL);
	if (!wide)
		return -1;
	const int fd = _wopen(wide, _O_WRONLY | _O_CREAT | _O_TRUNC | _O_BINARY | _O_NOINHERIT, _S_IREAD | _S_IWRITE);
	free(wide);
	return fd;
}

static int plat_close(int fd)
{
	return _close(fd);
}

static int64_t plat_pwrite(int fd, const void* data, uint32_t count, uint64_t offset)
{
	/* only the RDP thread writes to a download, so seek and write need no lock */
	if (_lseeki64(fd, (__int64)offset, SEEK_SET) < 0)
		return -1;
	return _write(fd, data, count);
}

/* creates the folder and its parents; drive letters, shares and existing folders along the way simply fail, so only
 * the result counts */
static int plat_mkdirs(const char* path)
{
	WCHAR* wide = ConvertUtf8ToWCharAlloc(path, NULL);
	if (!wide)
		return 0;
	for (WCHAR* q = wide + 1; *q; q++)
	{
		if (*q == L'/' || *q == L'\\')
		{
			const WCHAR sep = *q;
			*q = 0;
			(void)CreateDirectoryW(wide, NULL);
			*q = sep;
		}
	}
	(void)CreateDirectoryW(wide, NULL);
	const DWORD attributes = GetFileAttributesW(wide);
	free(wide);
	return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

static void plat_set_write_time(const char* path, uint64_t filetime)
{
	WCHAR* wide = ConvertUtf8ToWCharAlloc(path, NULL);
	if (!wide)
		return;
	HANDLE file = CreateFileW(wide, FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	free(wide);
	if (file == INVALID_HANDLE_VALUE)
		return;
	FILETIME ft;
	ft.dwLowDateTime = (DWORD)(filetime & 0xFFFFFFFFu);
	ft.dwHighDateTime = (DWORD)(filetime >> 32);
	(void)SetFileTime(file, NULL, NULL, &ft);
	(void)CloseHandle(file);
}

/* A name the server chose, with '/' separators. Windows gives meaning to more than "..": a drive letter or a colon
 * (alternate data stream) escapes the target, device names open devices, and a trailing dot or space is dropped, so
 * "x.." or "CON .txt" are not what they look like. */
static int plat_is_safe_relative_path(const char* name)
{
	static const char* const devices[] = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		                                   "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
	const char* segment = name;
	for (;;)
	{
		const char* end = strchr(segment, '/');
		const size_t len = end ? (size_t)(end - segment) : strlen(segment);
		if (len == 0 || segment[len - 1] == '.' || segment[len - 1] == ' ')
			return 0;
		for (size_t i = 0; i < len; i++)
		{
			const unsigned char c = (unsigned char)segment[i];
			if (c < 0x20 || strchr(":<>\"|?*", c))
				return 0;
		}
		size_t stem = 0;
		while (stem < len && segment[stem] != '.' && segment[stem] != ' ')
			stem++;
		for (size_t i = 0; i < sizeof(devices) / sizeof(devices[0]); i++)
			if (strlen(devices[i]) == stem && _strnicmp(segment, devices[i], stem) == 0)
				return 0;
		/* COM¹ and friends are devices too; superscript digits are the only UTF-8 this can match */
		if (stem == 5 && (_strnicmp(segment, "COM", 3) == 0 || _strnicmp(segment, "LPT", 3) == 0) && (unsigned char)segment[3] == 0xC2)
			return 0;
		if (stem == 6 && (_strnicmp(segment, "COM", 3) == 0 || _strnicmp(segment, "LPT", 3) == 0) && (unsigned char)segment[3] == 0xE2)
			return 0;
		if (!end)
			return 1;
		segment = end + 1;
	}
}

#else

#include <pthread.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>
#include <mach/mach_time.h>

static void plat_init_process(void)
{
}

static uint64_t plat_now_ms(void)
{
	static mach_timebase_info_data_t tb;
	if (tb.denom == 0)
		mach_timebase_info(&tb);
	return mach_absolute_time() * tb.numer / tb.denom / 1000000ULL;
}

static int plat_create_file(const char* path)
{
	return open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
}

static int plat_close(int fd)
{
	return close(fd);
}

static int64_t plat_pwrite(int fd, const void* data, uint32_t count, uint64_t offset)
{
	return pwrite(fd, data, count, (off_t)offset);
}

static int plat_mkdirs(const char* path)
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

/* FILETIME: 100 ns ticks since 1601-01-01 */
static void plat_set_write_time(const char* path, uint64_t filetime)
{
	if (filetime <= 116444736000000000ULL)
		return;
	const uint64_t unix_ns100 = filetime - 116444736000000000ULL;
	struct timespec ts[2];
	ts[0].tv_sec = (time_t)(unix_ns100 / 10000000ULL);
	ts[0].tv_nsec = (long)((unix_ns100 % 10000000ULL) * 100);
	ts[1] = ts[0];
	(void)utimensat(AT_FDCWD, path, ts, 0);
}

static int plat_is_safe_relative_path(const char* name)
{
	(void)name;
	return 1;
}

#endif
