#include "my_application.h"
#include "planetarium_overlay.h"

#include <gdk/gdk.h>

int main(int argc, char** argv) {
  // The Linux client is Wayland-only (#1201, #1204): the planetarium overlay
  // is stacked by GtkOverlay draw order on a Wayland surface and the X11
  // paths are gone. Refuse X11 and XWayland up front with a plain message
  // instead of letting GTK fail with "cannot open display".
  if (g_getenv("WAYLAND_DISPLAY") == nullptr) {
    g_printerr(
        "OpenAstro Ara needs a Wayland session. Log in to a Wayland session "
        "(GNOME, KDE Plasma) and run it there; X11 is not supported.\n");
    return 1;
  }
  gdk_set_allowed_backends("wayland");
  // Must stay the first GTK-relevant call: it forks and then writes the
  // environment, both of which assume no other thread exists yet. A static
  // initializer that starts a thread (e.g. in a future plugin) would silently
  // break that.
  planetarium_overlay_configure_renderer();
  g_autoptr(MyApplication) app = my_application_new();
  return g_application_run(G_APPLICATION(app), argc, argv);
}
