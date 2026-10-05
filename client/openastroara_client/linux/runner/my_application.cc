#include "my_application.h"

#include <flutter_linux/flutter_linux.h>

#include "flutter/generated_plugin_registrant.h"
#include "planetarium_overlay.h"

#include <glib-unix.h>
#include <signal.h>

struct _MyApplication {
  GtkApplication parent_instance;
  char** dart_entrypoint_arguments;
};

G_DEFINE_TYPE(MyApplication, my_application, GTK_TYPE_APPLICATION)

// Called when first Flutter frame received.
static void first_frame_cb(MyApplication* self, FlView* view) {
  gtk_widget_show(gtk_widget_get_toplevel(GTK_WIDGET(view)));
}

// openastroara/window — launchpad (compact, centered) vs workstation
// (maximized with a layout floor). Mirrors the macOS MainFlutterWindow
// channel; the Dart router drives it as the §30 launch flow hands off to the
// §25 shell and back.
static void window_mode_method_cb(FlMethodChannel* channel,
                                  FlMethodCall* method_call,
                                  gpointer user_data) {
  GtkWindow* window = GTK_WINDOW(user_data);
  const gchar* method = fl_method_call_get_name(method_call);
  if (g_strcmp0(method, "workstation") == 0) {
    gtk_widget_set_size_request(GTK_WIDGET(window), 1100, 700);
    gtk_window_maximize(window);
    fl_method_call_respond_success(method_call, nullptr, nullptr);
  } else if (g_strcmp0(method, "launchpad") == 0) {
    gtk_window_unmaximize(window);
    gtk_widget_set_size_request(GTK_WIDGET(window), 760, 560);
    gtk_window_resize(window, 960, 680);
    // No re-centre: Wayland gives clients no window positioning, so the
    // gtk_window_move() the X11 build used here was a no-op (#1201). The
    // compositor places the unmaximised window.
    fl_method_call_respond_success(method_call, nullptr, nullptr);
  } else if (g_strcmp0(method, "title") == 0) {
    // "OpenAstro Ara <version>" — composed Dart-side so pubspec.yaml stays
    // the single source of the version string. Set both the window title and
    // the header bar (when GNOME-style chrome is in use, the header bar's own
    // title is what actually renders).
    FlValue* args = fl_method_call_get_args(method_call);
    if (args != nullptr && fl_value_get_type(args) == FL_VALUE_TYPE_STRING) {
      const gchar* title = fl_value_get_string(args);
      gtk_window_set_title(window, title);
      GtkWidget* titlebar = gtk_window_get_titlebar(window);
      if (titlebar != nullptr && GTK_IS_HEADER_BAR(titlebar)) {
        gtk_header_bar_set_title(GTK_HEADER_BAR(titlebar), title);
      }
    }
    fl_method_call_respond_success(method_call, nullptr, nullptr);
  } else {
    fl_method_call_respond_not_implemented(method_call, nullptr);
  }
}

// Implements GApplication::activate.
static void my_application_activate(GApplication* application) {
  MyApplication* self = MY_APPLICATION(application);
  GtkWindow* window =
      GTK_WINDOW(gtk_application_window_new(GTK_APPLICATION(application)));

  // Use a header bar when running in GNOME as this is the common style used
  // by applications and is the setup most users will be using (e.g. Ubuntu
  // desktop).
  // Wayland only (#1201): always the GTK header bar. The X11 branch that
  // fell back to a WM title bar outside GNOME Shell is gone with X11.
  // Build-time title; Dart re-stamps it with the version ("OpenAstro Ara
  // 0.0.1a") over the window channel as soon as PackageInfo resolves.
  GtkHeaderBar* header_bar = GTK_HEADER_BAR(gtk_header_bar_new());
  gtk_widget_show(GTK_WIDGET(header_bar));
  gtk_header_bar_set_title(header_bar, "OpenAstro Ara");
  gtk_header_bar_set_show_close_button(header_bar, TRUE);
  gtk_window_set_titlebar(window, GTK_WIDGET(header_bar));

  // Window/taskbar icon: Wayland compositors ignore per-window icons and
  // resolve the app_id (org.openastro.openastroara) through the installed
  // desktop entry, so the X11-era gtk_window_set_icon() is gone (#1201). See
  // linux/CMakeLists.txt and linux/install-desktop-entry.sh.

  // Launchpad-first sizing: open compact (server connect + profile box); the
  // Dart router flips to the maximized "workstation" mode when the §25 shell
  // mounts, via the openastroara/window channel registered below.
  gtk_window_set_default_size(window, 960, 680);
  gtk_widget_set_size_request(GTK_WIDGET(window), 760, 560);

  g_autoptr(FlDartProject) project = fl_dart_project_new();
  fl_dart_project_set_dart_entrypoint_arguments(
      project, self->dart_entrypoint_arguments);

  FlView* view = fl_view_new(project);
  GdkRGBA background_color;
  // Fully transparent: the embedder's paint_background() cairo_paints the
  // whole view in software before every GL frame unless the colour is
  // (0,0,0,0), and with the Wayland planetarium overlay every WebKit frame
  // redraws the view. perf on a 4090 at 6144x3348 put 50 % of the UI thread
  // in that paint. Flutter's frame covers the view and the window is only
  // shown after the first frame, so nothing is visible through it.
  gdk_rgba_parse(&background_color, "#00000000");
  fl_view_set_background_color(view, &background_color);
  gtk_widget_show(GTK_WIDGET(view));

  // §36 Planetarium (Linux): wrap FlView in a GtkOverlay so the native
  // WebKitWebView can be composited on top of the Flutter view in its own GTK
  // surface (see planetarium_overlay.h — this sidesteps Flutter's broken
  // texture-based webview GL path). The overlay is transparent over the whole
  // window; its only overlay child is the planetarium webview, positioned by
  // Dart over a method channel.
  GtkOverlay* overlay = GTK_OVERLAY(gtk_overlay_new());
  gtk_widget_show(GTK_WIDGET(overlay));
  gtk_container_add(GTK_CONTAINER(overlay), GTK_WIDGET(view));
  gtk_container_add(GTK_CONTAINER(window), GTK_WIDGET(overlay));
  // FlView paints every pixel of the window through GL, so GTK's own
  // background fill of the toplevel is wasted work. On Wayland with the
  // client-side planetarium overlay every WebKit frame repaints the window,
  // and a perf profile (Kubuntu, RTX 4090, 6144x3348, 2026-10-05) put 37 % of
  // the UI thread in pixman_fill under gtk_main_do_event: GTK filling the
  // full toplevel in software before the GL blit. app-paintable skips it.
  gtk_widget_set_app_paintable(GTK_WIDGET(window), TRUE);

  // Show the window when Flutter renders.
  // Requires the view to be realized so we can start rendering.
  g_signal_connect_swapped(view, "first-frame", G_CALLBACK(first_frame_cb),
                           self);
  gtk_widget_realize(GTK_WIDGET(view));

  fl_register_plugins(FL_PLUGIN_REGISTRY(view));

  // Wire the native planetarium overlay to its Dart method channel.
  planetarium_overlay_register(
      overlay, view,
      fl_engine_get_binary_messenger(fl_view_get_engine(view)));

  // Wire the launchpad/workstation window-mode channel. The channel ref is
  // deliberately leaked for the window's lifetime (same as the app runs).
  FlMethodChannel* window_channel = fl_method_channel_new(
      fl_engine_get_binary_messenger(fl_view_get_engine(view)),
      "openastroara/window", FL_METHOD_CODEC(fl_standard_method_codec_new()));
  fl_method_channel_set_method_call_handler(
      window_channel, window_mode_method_cb, g_object_ref(window),
      g_object_unref);

  gtk_widget_grab_focus(GTK_WIDGET(view));
}

// Implements GApplication::local_command_line.
static gboolean my_application_local_command_line(GApplication* application,
                                                  gchar*** arguments,
                                                  int* exit_status) {
  MyApplication* self = MY_APPLICATION(application);
  // Strip out the first argument as it is the binary name.
  self->dart_entrypoint_arguments = g_strdupv(*arguments + 1);

  g_autoptr(GError) error = nullptr;
  if (!g_application_register(application, nullptr, &error)) {
    g_warning("Failed to register: %s", error->message);
    *exit_status = 1;
    return TRUE;
  }

  g_application_activate(application);
  *exit_status = 0;

  return TRUE;
}

static gboolean on_terminate_signal(gpointer user_data);

// Implements GApplication::startup.
static void my_application_startup(GApplication* application) {
  // MyApplication* self = MY_APPLICATION(object);

  // Perform any actions required at application startup.

  G_APPLICATION_CLASS(my_application_parent_class)->startup(application);

  g_unix_signal_add(SIGTERM, on_terminate_signal, application);
  g_unix_signal_add(SIGINT, on_terminate_signal, application);
}

// Implements GApplication::shutdown.
static void my_application_shutdown(GApplication* application) {
  // Tear the WebKit view down before the process exits so WebKitWebProcess
  // is told to quit instead of being orphaned (see planetarium_overlay.cc).
  planetarium_overlay_shutdown();

  G_APPLICATION_CLASS(my_application_parent_class)->shutdown(application);
}

// SIGTERM/SIGINT (pkill, Ctrl-C, session logout) default to an immediate
// exit, which skips GApplication::shutdown and orphans the web process. Route
// them through the main loop so the normal teardown runs.
static gboolean on_terminate_signal(gpointer user_data) {
  g_application_quit(G_APPLICATION(user_data));
  return G_SOURCE_REMOVE;
}

// Implements GObject::dispose.
static void my_application_dispose(GObject* object) {
  MyApplication* self = MY_APPLICATION(object);
  g_clear_pointer(&self->dart_entrypoint_arguments, g_strfreev);
  G_OBJECT_CLASS(my_application_parent_class)->dispose(object);
}

static void my_application_class_init(MyApplicationClass* klass) {
  G_APPLICATION_CLASS(klass)->activate = my_application_activate;
  G_APPLICATION_CLASS(klass)->local_command_line =
      my_application_local_command_line;
  G_APPLICATION_CLASS(klass)->startup = my_application_startup;
  G_APPLICATION_CLASS(klass)->shutdown = my_application_shutdown;
  G_OBJECT_CLASS(klass)->dispose = my_application_dispose;
}

static void my_application_init(MyApplication* self) {}

MyApplication* my_application_new() {
  // Set the program name to the application ID, which helps various systems
  // like GTK and desktop environments map this running application to its
  // corresponding .desktop file. This ensures better integration by allowing
  // the application to be recognized beyond its binary name.
  g_set_prgname(APPLICATION_ID);

  return MY_APPLICATION(g_object_new(my_application_get_type(),
                                     "application-id", APPLICATION_ID, "flags",
                                     G_APPLICATION_NON_UNIQUE, nullptr));
}
