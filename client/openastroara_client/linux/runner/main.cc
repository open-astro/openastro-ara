#include "my_application.h"
#include "planetarium_overlay.h"

int main(int argc, char** argv) {
  // Must stay the first call: it forks and then writes the environment, both
  // of which assume no other thread exists yet. A static initializer that
  // starts a thread (e.g. in a future plugin) would silently break that.
  planetarium_overlay_configure_renderer();
  g_autoptr(MyApplication) app = my_application_new();
  return g_application_run(G_APPLICATION(app), argc, argv);
}
