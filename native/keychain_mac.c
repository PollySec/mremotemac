/* macOS Keychain access for the connection database key (generic password items), kept apart from FreeRDP headers.
 * Returns OSStatus (0 = errSecSuccess, -25300 = errSecItemNotFound, -128 = errSecUserCanceled). */
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <Security/Security.h>
#include <CoreFoundation/CoreFoundation.h>

static CFMutableDictionaryRef mr_kc_query(const char* service, const char* account)
{
	CFMutableDictionaryRef q = CFDictionaryCreateMutable(NULL, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
	CFStringRef s = CFStringCreateWithCString(NULL, service, kCFStringEncodingUTF8);
	CFStringRef a = CFStringCreateWithCString(NULL, account, kCFStringEncodingUTF8);
	CFDictionarySetValue(q, kSecClass, kSecClassGenericPassword);
	CFDictionarySetValue(q, kSecAttrService, s);
	CFDictionarySetValue(q, kSecAttrAccount, a);
	CFRelease(s);
	CFRelease(a);
	return q;
}

__attribute__((visibility("default"))) int32_t mr_keychain_get(const char* service, const char* account, uint8_t** data, int32_t* len)
{
	*data = NULL;
	*len = 0;
	CFMutableDictionaryRef q = mr_kc_query(service, account);
	CFDictionarySetValue(q, kSecReturnData, kCFBooleanTrue);
	CFDictionarySetValue(q, kSecMatchLimit, kSecMatchLimitOne);
	CFTypeRef result = NULL;
	OSStatus st = SecItemCopyMatching(q, &result);
	CFRelease(q);
	if (st == errSecSuccess && result)
	{
		CFDataRef d = (CFDataRef)result;
		CFIndex n = CFDataGetLength(d);
		*data = malloc(n > 0 ? (size_t)n : 1);
		if (*data)
		{
			memcpy(*data, CFDataGetBytePtr(d), (size_t)n);
			*len = (int32_t)n;
		}
		else
			st = errSecAllocate;
	}
	if (result)
		CFRelease(result);
	return (int32_t)st;
}

__attribute__((visibility("default"))) int32_t mr_keychain_set(const char* service, const char* account, const uint8_t* data, int32_t len, const char* label)
{
	CFMutableDictionaryRef q = mr_kc_query(service, account);
	SecItemDelete(q); /* replace any previous value; a missing item is fine */
	CFDataRef d = CFDataCreate(NULL, data, len);
	CFStringRef l = CFStringCreateWithCString(NULL, label ? label : "mRemoteMac", kCFStringEncodingUTF8);
	CFDictionarySetValue(q, kSecValueData, d);
	CFDictionarySetValue(q, kSecAttrLabel, l);
	CFDictionarySetValue(q, kSecAttrAccessible, kSecAttrAccessibleWhenUnlockedThisDeviceOnly);
	OSStatus st = SecItemAdd(q, NULL);
	CFRelease(l);
	CFRelease(d);
	CFRelease(q);
	return (int32_t)st;
}

__attribute__((visibility("default"))) int32_t mr_keychain_delete(const char* service, const char* account)
{
	CFMutableDictionaryRef q = mr_kc_query(service, account);
	OSStatus st = SecItemDelete(q);
	CFRelease(q);
	return (int32_t)st;
}

__attribute__((visibility("default"))) void mr_keychain_free(uint8_t* data)
{
	if (data)
	{
		free(data);
	}
}
