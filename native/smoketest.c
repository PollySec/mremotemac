#include "mrrdp.h"
#include <stdio.h>
#include <unistd.h>
static volatile int done = 0;
static void on_disc(void* u, uint32_t err, const char* n, const char* d) { printf("disconnected: 0x%08x %s - %s\n", err, n, d); done = 1; }
static void on_conn(void* u) { printf("connected\n"); }
int main(void) {
  printf("freerdp %s\n", mr_version());
  mr_callbacks cb = {0}; cb.on_disconnected = on_disc; cb.on_connected = on_conn;
  mr_session* s = mr_session_create(&cb);
  if (!s) { printf("create failed\n"); return 1; }
  printf("set host: %d port: %d bogus: %d\n", mr_set_string(s, "ServerHostname", "192.0.2.1"), mr_set_uint32(s, "ServerPort", 3389), mr_set_string(s, "NoSuchKey", "x"));
  const char* argv[] = {"mrrdp", "/sound", "/clipboard", "/dynamic-resolution", "/timeout:3000", "/cert:ignore"};
  printf("parse: %d\n", mr_parse_args(s, 6, argv));
  printf("connect: %d\n", mr_connect(s));
  for (int i = 0; i < 100 && !done; i++) usleep(100000);
  if (!done) { printf("timeout, forcing disconnect\n"); }
  mr_disconnect(s);
  mr_session_destroy(s);
  printf("destroyed ok\n");
  return 0;
}
