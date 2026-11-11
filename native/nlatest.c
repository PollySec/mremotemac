/*
 * nlatest – logs on to an RDP server with NLA through libmrrdp, the way the app does (NlaSecurity + TlsSecurity, no
 * RDP security, credentials set up front). Built and run by native/nla-test.sh against the libraries that ship.
 *
 *   nlatest <host> <port> <user> <password> ok|fail
 *
 * "ok" expects the logon to succeed; "fail" expects it to be refused without connecting, which shows the server
 * really checks the password (a server that let anyone in would make the "ok" run prove nothing).
 * Exit code 0 = the expectation held.
 */
#include "mrrdp.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static volatile int g_connected, g_done, g_asked;
static volatile uint32_t g_error;
static char g_error_name[128];

static void on_connected(void* user)
{
	(void)user;
	g_connected = 1;
}

static void on_disconnected(void* user, uint32_t error, const char* name, const char* description)
{
	(void)user;
	g_error = error;
	(void)snprintf(g_error_name, sizeof(g_error_name), "%s", name ? name : "");
	if (error && !g_connected) /* after a successful logon this is the test's own disconnect */
		fprintf(stderr, "nlatest: disconnected 0x%08x %s - %s\n", error, name ? name : "", description ? description : "");
	g_done = 1;
}

/* the credentials are set before connecting, as the app does when the connection has a password; being asked
 * anyway means they were not used */
static int32_t on_authenticate(void* user, int32_t reason, char* u, int32_t uc, char* p, int32_t pc, char* d, int32_t dc)
{
	(void)user, (void)reason, (void)u, (void)uc, (void)p, (void)pc, (void)d, (void)dc;
	g_asked = 1;
	return 0;
}

static void on_log(void* user, int32_t level, const char* message)
{
	(void)user, (void)level;
	fprintf(stderr, "  | %s\n", message);
}

int main(int argc, char** argv)
{
	if (argc != 6 || (strcmp(argv[5], "ok") != 0 && strcmp(argv[5], "fail") != 0))
	{
		fprintf(stderr, "usage: %s <host> <port> <user> <password> ok|fail\n", argv[0]);
		return 2;
	}
	const int expect_ok = strcmp(argv[5], "ok") == 0;

	char st[256];
	const int crypto_ok = mr_selftest(st, sizeof(st));
	printf("nlatest: FreeRDP %s, self-test: %s\n", mr_version(), st);
	if (!crypto_ok)
		return 1;

	mr_callbacks cb = { 0 };
	cb.on_connected = on_connected;
	cb.on_disconnected = on_disconnected;
	cb.on_authenticate = on_authenticate;
	cb.on_log = on_log;
	mr_session* s = mr_session_create(&cb);
	if (!s)
	{
		fprintf(stderr, "nlatest: mr_session_create failed\n");
		return 1;
	}
	/* settings first, then the command line, then connect – the order RdpSettingsBuilder.Apply uses. mr_set_* return
	 * 1 on success, mr_parse_args and mr_connect 0. */
	const char* args[] = { "nlatest", "/cert:ignore", "/timeout:10000" };
	if (!mr_set_string(s, "ServerHostname", argv[1]) || !mr_set_uint32(s, "ServerPort", (uint32_t)atoi(argv[2])) ||
	    !mr_set_string(s, "Username", argv[3]) || !mr_set_string(s, "Password", argv[4]) ||
	    !mr_set_bool(s, "NlaSecurity", 1) || !mr_set_bool(s, "TlsSecurity", 1) || !mr_set_bool(s, "RdpSecurity", 0) ||
	    mr_parse_args(s, 3, args) != 0 || mr_connect(s) != 0)
	{
		fprintf(stderr, "nlatest: could not set up the session\n");
		return 1;
	}
	for (int i = 0; i < 300 && !g_connected && !g_done; i++)
		usleep(100000);
	const int connected = g_connected;
	mr_disconnect(s);
	mr_session_destroy(s);

	if (expect_ok)
	{
		if (connected)
			printf("nlatest: NLA logon accepted, as expected\n");
		else
			fprintf(stderr, "nlatest: FAIL – NLA logon with the right password did not connect (%s)%s\n",
			        g_error_name[0] ? g_error_name : "timeout", g_asked ? ", credentials were requested" : "");
		return connected && !g_asked ? 0 : 1;
	}
	if (connected)
	{
		fprintf(stderr, "nlatest: FAIL – the server accepted a wrong password, so it does not check NLA credentials\n");
		return 1;
	}
	printf("nlatest: wrong password refused (%s), as expected\n", g_error_name[0] ? g_error_name : "timeout");
	return g_done ? 0 : 1;
}
