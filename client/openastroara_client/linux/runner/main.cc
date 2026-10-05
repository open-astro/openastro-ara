#include "my_application.h"
#include "planetarium_overlay.h"

int main(int argc, char** argv) {
  planetarium_overlay_configure_renderer();
  g_autoptr(MyApplication) app = my_application_new();
  return g_application_run(G_APPLICATION(app), argc, argv);
}
