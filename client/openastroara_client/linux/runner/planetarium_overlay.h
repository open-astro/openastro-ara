#ifndef RUNNER_PLANETARIUM_OVERLAY_H_
#define RUNNER_PLANETARIUM_OVERLAY_H_

#include <flutter_linux/flutter_linux.h>
#include <gtk/gtk.h>

// §36 Planetarium — native GTK in-window embed for Linux.
//
// Flutter's GTK embedder can't host a texture-based webview platform view
// (webview_all_linux / WebKitGTK) without poisoning Skia's shared GL context —
// "Could not make the context current to set up the Gr context" — which blanks
// the whole app window (flutter/flutter#88168, #70811). So instead of routing
// the WebKitGTK surface through Flutter's platform-view/GL path, we add a real
// `WebKitWebView` GtkWidget as a child of a `GtkOverlay` wrapped around `FlView`.
// It renders in its OWN GTK/GL surface, composited by GTK on top of the Flutter
// view, and never touches Flutter's GL context — so the blank-screen bug can't
// trigger. Dart drives its rect / visibility / URL over a method channel
// (see lib/services/planetarium_overlay.dart).
//
// `overlay` must be the GtkOverlay whose main child is `view`; `messenger` is
// the engine's binary messenger (fl_engine_get_binary_messenger).
void planetarium_overlay_register(GtkOverlay* overlay,
                                  FlView* view,
                                  FlBinaryMessenger* messenger);

#endif  // RUNNER_PLANETARIUM_OVERLAY_H_
