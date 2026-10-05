#include "planetarium_overlay.h"

#include <math.h>
#include <webkit2/webkit2.h>
#ifdef GDK_WINDOWING_WAYLAND
#include <gdk/gdkwayland.h>
#endif

#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <glob.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

#include <cstring>

// See planetarium_overlay.h for the why. This file owns the WebKitWebView that
// floats over FlView and the method channel Dart drives it with.

namespace {

// Set when every planetarium frame still goes through a CPU copy in this
// process (the shm renderer, or NVIDIA where GTK3 copies the DMABUF frame on
// the UI thread); the page then caps its backing resolution on large views.
bool g_heavy_compositing = false;

// Method channel name — must match lib/services/planetarium_overlay.dart.
const char* kChannelName = "org.openastro.openastroara/planetarium";

struct OverlayState {
  GtkOverlay* overlay = nullptr;
  // Web-process crashes seen this session (see web_process_terminated_cb).
  int web_process_crashes = 0;
  // Lazily created on the first setUrl so plain registration costs nothing and,
  // crucially, no WebKit GL surface exists until the user actually opens
  // Planning (mirrors webview_all's "GL only inits when a webview is created").
  WebKitWebView* webview = nullptr;
  // The positioned overlay child: a windowed GtkEventBox wrapping the webview.
  // On X11 it's promoted to a native subwindow (see ensure_webview) so the X
  // server stacks it ABOVE the toplevel content Flutter blits its GL frame
  // into. On Wayland it stays client-side and GtkOverlay draw order does the
  // stacking (#1200).
  GtkWidget* webview_widget = nullptr;
  // Target rect in logical (GTK) pixels, relative to the FlView origin. Flutter
  // logical pixels and GTK widget coordinates share the same scale factor on a
  // given display, so the Dart-side global rect maps straight onto the overlay
  // child allocation with no DPI conversion.
  GdkRectangle rect = {0, 0, 0, 0};
  bool has_rect = false;
  bool visible = false;
  // The exact origin (`http://127.0.0.1:<port>/`) of the loopback asset server,
  // captured from the first setUrl. Navigations are locked to this — not merely
  // "any 127.0.0.1 port" — so the page can't be steered onto another local service.
  gchar* allowed_origin = nullptr;
  // Night mode is a page-level tint; remembered so a page (re)load can
  // re-apply it, and so a toggle before the first setUrl isn't lost.
  bool night = false;
};

// Read a numeric arg that Dart may encode as float or int.
double lookup_number(FlValue* args, const char* key, double fallback) {
  if (args == nullptr || fl_value_get_type(args) != FL_VALUE_TYPE_MAP) {
    return fallback;
  }
  FlValue* value = fl_value_lookup_string(args, key);
  if (value == nullptr) return fallback;
  switch (fl_value_get_type(value)) {
    case FL_VALUE_TYPE_FLOAT:
      return fl_value_get_float(value);
    case FL_VALUE_TYPE_INT:
      return static_cast<double>(fl_value_get_int(value));
    default:
      return fallback;
  }
}

// GtkOverlay::get-child-position — place our webview child at the stored rect.
// Returning TRUE means "I positioned it"; for any other overlay child we defer
// to GTK's default alignment handling.
gboolean on_get_child_position(GtkOverlay* overlay,
                               GtkWidget* widget,
                               GdkRectangle* allocation,
                               gpointer user_data) {
  (void)overlay;
  OverlayState* state = static_cast<OverlayState*>(user_data);
  if (widget != state->webview_widget || !state->has_rect) return FALSE;
  *allocation = state->rect;
  return TRUE;
}

void apply_visibility(OverlayState* state) {
  if (state->webview_widget == nullptr) return;
  if (state->visible && state->has_rect) {
    // Only raise on a genuine hidden→visible transition. setBounds() calls this
    // on every resize/DPI tick; raising the subwindow each time flickers the
    // overlay on compositing WMs. Raise once when it reappears (e.g. returning to
    // Planning after another tab redrew the view), not on every geometry update.
    gboolean was_visible = gtk_widget_get_visible(state->webview_widget);
    gtk_widget_show(state->webview_widget);
    if (!was_visible) {
      // X11: reorders the native subwindow above FlView's. Wayland: the window
      // is client-side, so this only reorders GDK's child list; stacking there
      // comes from GtkOverlay draw order (#1200). Harmless on both.
      GdkWindow* window = gtk_widget_get_window(state->webview_widget);
      if (window != nullptr) gdk_window_raise(window);
    }
  } else {
    gtk_widget_hide(state->webview_widget);
  }
}

// Only ever let the webview navigate to our own loopback page. The setUrl method
// guard covers Dart-originated loads; this covers navigations the page itself
// initiates (window.location, a link, a redirect) so a page-side bug or XSS can't
// pull the unsandboxed native WebKit process onto external content.
gboolean decide_policy_cb(WebKitWebView* web_view,
                          WebKitPolicyDecision* decision,
                          WebKitPolicyDecisionType type,
                          gpointer user_data) {
  (void)web_view;
  OverlayState* state = static_cast<OverlayState*>(user_data);
  if (type != WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION &&
      type != WEBKIT_POLICY_DECISION_TYPE_NEW_WINDOW_ACTION) {
    return FALSE;  // resource-load decisions etc. — use the default policy.
  }
  WebKitNavigationPolicyDecision* nav =
      WEBKIT_NAVIGATION_POLICY_DECISION(decision);
  WebKitNavigationAction* action =
      webkit_navigation_policy_decision_get_navigation_action(nav);
  WebKitURIRequest* request = webkit_navigation_action_get_request(action);
  const gchar* uri = webkit_uri_request_get_uri(request);
  // Lock to the asset server's exact origin (scheme+host+port), not any loopback
  // port. allowed_origin is set on the first setUrl, before any page can navigate.
  if (uri != nullptr && state->allowed_origin != nullptr &&
      g_str_has_prefix(uri, state->allowed_origin)) {
    webkit_policy_decision_use(decision);
  } else {
    g_warning("planetarium_overlay: blocking navigation to '%s'",
              uri != nullptr ? uri : "(null)");
    webkit_policy_decision_ignore(decision);
  }
  return TRUE;  // handled.
}

// Night mode (§ red display): Flutter can't paint over this native surface, so
// the red tint is injected into the page itself — the same fixed-position
// multiply layer the embedded webviews get from stellarium_view.dart. The
// script is a constant here rather than passed over the channel: this webview
// runs JS unsandboxed, so the channel never accepts caller-supplied code.
const char* kNightOnJs =
    ";(function(){"
    "var el=document.getElementById('ara-night');"
    "if(!el){el=document.createElement('div');el.id='ara-night';"
    "el.style.cssText='position:fixed;inset:0;pointer-events:none;"
    "z-index:2147483647;mix-blend-mode:multiply;background:rgba(255,0,0,0.35);';"
    "document.body.appendChild(el);}"
    "})();";
const char* kNightOffJs =
    ";(function(){var el=document.getElementById('ara-night');"
    "if(el)el.remove();})();";

void run_js(WebKitWebView* view, const char* script) {
  if (view == nullptr) return;
#if WEBKIT_CHECK_VERSION(2, 40, 0)
  webkit_web_view_evaluate_javascript(view, script, -1, nullptr, nullptr,
                                      nullptr, nullptr, nullptr);
#else
  webkit_web_view_run_javascript(view, script, nullptr, nullptr, nullptr);
#endif
}

// Re-apply the page tint after every load: the injected <div> dies with the
// document, so a reload (or the very first page, if night mode was already on
// at startup) would otherwise come back white.
void load_changed_cb(WebKitWebView* view,
                     WebKitLoadEvent event,
                     gpointer user_data) {
  OverlayState* state = static_cast<OverlayState*>(user_data);
  if (event == WEBKIT_LOAD_FINISHED && state->night) {
    run_js(view, kNightOnJs);
  }
  // Where every frame still costs a CPU copy (shm renderer, NVIDIA), let the
  // page cap its backing resolution and interactive rate on very large views.
  if (event == WEBKIT_LOAD_FINISHED && g_heavy_compositing) {
    run_js(view, "araViewCaps.enable()");
  }
}

// WebKitGTK's DMABUF renderer allocates its frames through GBM on a DRM render
// node. Where that can't work (GPU-less VMs and headless containers whose Mesa
// has no 3D so GBM falls back to dumb buffers, which render nodes refuse) the
// WebProcess logs "Failed to create GBM buffer ... Permission denied" and
// never produces a frame, so the planetarium is blank with no other symptom.
// An openable render node is not enough to tell (#1200 spike: the node opened
// fine, the allocation failed), so try a tiny allocation the way WebKit will.
// libgbm is loaded at runtime: it ships with Mesa, which WebKitGTK already
// needs, and this keeps it out of the build and package dependency lists.
struct GbmProbe {
  void* lib = nullptr;
  void* (*create_device)(int) = nullptr;
  void (*destroy_device)(void*) = nullptr;
  void* (*create_bo)(void*, uint32_t, uint32_t, uint32_t, uint32_t) = nullptr;
  void (*destroy_bo)(void*) = nullptr;

  // Loads libgbm once for the whole probe; a multi-GPU box has several render
  // nodes and should not pay a dlopen per node.
  bool load() {
    lib = dlopen("libgbm.so.1", RTLD_NOW | RTLD_LOCAL);
    if (lib == nullptr) return false;
    create_device = reinterpret_cast<decltype(create_device)>(
        dlsym(lib, "gbm_create_device"));
    destroy_device = reinterpret_cast<decltype(destroy_device)>(
        dlsym(lib, "gbm_device_destroy"));
    create_bo =
        reinterpret_cast<decltype(create_bo)>(dlsym(lib, "gbm_bo_create"));
    destroy_bo =
        reinterpret_cast<decltype(destroy_bo)>(dlsym(lib, "gbm_bo_destroy"));
    return create_device && destroy_device && create_bo && destroy_bo;
  }

  // Tries the allocation WebKit's DMABUF renderer will make on this node.
  bool allocation_works(int fd) const {
    void* dev = create_device(fd);
    if (dev == nullptr) return false;
    // GBM_FORMAT_ARGB8888 ('AR24'), GBM_BO_USE_RENDERING (1 << 2).
    const uint32_t kArgb8888 = 0x34325241;
    const uint32_t kUseRendering = 1u << 2;
    void* bo = create_bo(dev, 64, 64, kArgb8888, kUseRendering);
    const bool ok = bo != nullptr;
    if (ok) destroy_bo(bo);
    destroy_device(dev);
    return ok;
  }

  ~GbmProbe() {
    if (lib != nullptr) dlclose(lib);
  }
};


// Per-user marker written when WebKit's web process crashed while the DMABUF
// renderer was active (web_process_terminated_cb). Its presence makes every
// later launch start on the shm renderer: the failure keys on itself, not on
// a proxy. Delete the file to retry DMABUF after a driver update.
gchar* no_dmabuf_marker_path() {
  return g_build_filename(g_get_user_config_dir(), "openastroara",
                          "webkit-no-dmabuf", nullptr);
}

// True when a render node is driven by the proprietary NVIDIA module. On that
// stack GBM allocates fine but WebKitGTK's DMABUF renderer segfaults inside
// libnvidia-eglcore (Kubuntu 26.04, webkit2gtk 2.52, driver 595.84,
// 2026-10-05), so the allocation probe cannot detect it.
bool render_node_is_nvidia(const char* node_path) {
  g_autofree gchar* base = g_path_get_basename(node_path);
  g_autofree gchar* link = g_build_filename("/sys/class/drm", base, "device",
                                            "driver", nullptr);
  g_autofree gchar* target = g_file_read_link(link, nullptr);
  if (target == nullptr) return false;
  g_autofree gchar* driver = g_path_get_basename(target);
  return g_strcmp0(driver, "nvidia") == 0;
}

// Runs in a forked child: returns 0 when some render node can back a GBM
// buffer, 1 when none can, 2 when libgbm is not loadable. "Some" is a
// deliberate choice: on a hybrid laptop WebKit allocates on the display's
// device, which may not be the node that passed, so a working iGPU can mask a
// failing dGPU; requiring every node to pass would instead disable DMABUF on
// any box with one dead secondary node. RUNNING.md covers the manual override.
int probe_render_nodes(const glob_t& g) {
  GbmProbe probe;
  if (!probe.load()) return 2;
  for (size_t i = 0; i < g.gl_pathc; i++) {
    int fd = open(g.gl_pathv[i], O_RDWR | O_CLOEXEC);
    if (fd < 0) continue;
    const bool ok = probe.allocation_works(fd);
    close(fd);
    if (ok) return 0;
  }
  return 1;
}

}  // namespace

// Called from main() before GTK and the Flutter engine start, so the g_setenv
// below happens while the process is still single-threaded (setenv is not
// thread-safe against a concurrent getenv). The GBM probe itself runs in a
// forked child: gbm_create_device loads the Mesa DRI driver, and some drivers
// (radeonsi) start compiler threads that may outlive gbm_device_destroy, so
// the parent never loads a driver and stays single-threaded for the setenv.
// The user's own setting wins. Applies on X11 too: where GBM can't allocate
// there either (e.g. proprietary NVIDIA drivers without a GBM backend), the
// shm renderer is the right fallback.
void planetarium_overlay_configure_renderer() {
  if (g_getenv("WEBKIT_DISABLE_DMABUF_RENDERER") != nullptr) return;
  const char* reason = nullptr;
  gint64 probe_ms = -1;  // set when the probe had to be killed
  glob_t g = {};
  g_autofree gchar* marker = no_dmabuf_marker_path();
  if (glob("/dev/dri/renderD*", 0, nullptr, &g) != 0 || g.gl_pathc == 0) {
    // Cheap first check: no node at all means nothing to probe and no driver
    // to load (headless containers, GPU-less VMs without a DRM device).
    reason = "no DRM render node is present";
  } else if (g_file_test(marker, G_FILE_TEST_EXISTS)) {
    reason = "a previous run's WebKit process crashed in the DMABUF renderer";
  } else if ([&] {
               for (size_t i = 0; i < g.gl_pathc; i++) {
                 if (render_node_is_nvidia(g.gl_pathv[i])) return true;
               }
               return false;
             }()) {
    // Keep the DMABUF renderer (31 fps vs 8 on the shm path at 4K) but turn
    // off NVIDIA's Wayland explicit sync: the driver arms
    // wp_linux_drm_syncobj on GTK's toplevel surface, GTK3 then commits a
    // buffer with no acquire point, KWin disconnects the client
    // ("explicit sync is used, but no acquire point is set") and the web
    // process dies inside libnvidia-eglcore. Found with WebKitGTK's
    // MiniBrowser on a plain WebGL page (Kubuntu 26.04, 595.84, 2026-10-05).
    // The user's own setting wins; the crash marker above remains the
    // backstop if a driver still fails.
    if (g_getenv("__NV_DISABLE_EXPLICIT_SYNC") == nullptr) {
      g_setenv("__NV_DISABLE_EXPLICIT_SYNC", "1", TRUE);
    }
    // WebKitGTK's hardware-acceleration policy reads "never" on this driver
    // (webkit://gpu), which lands the WebGL page on a software compositor at
    // ~90 % of a core. Forcing the DMABUF renderer and compositing mode gives
    // the GPU path (WebKit ~5 %). Only applied where the user set neither.
    if (g_getenv("WEBKIT_FORCE_DMABUF_RENDERER") == nullptr &&
        g_getenv("WEBKIT_FORCE_COMPOSITING_MODE") == nullptr) {
      g_setenv("WEBKIT_FORCE_DMABUF_RENDERER", "1", TRUE);
      g_setenv("WEBKIT_FORCE_COMPOSITING_MODE", "1", TRUE);
    }
    g_message("planetarium_overlay: the proprietary NVIDIA driver is in use, "
              "setting __NV_DISABLE_EXPLICIT_SYNC=1 (DMABUF renderer kept, "
              "compositing forced)");
    g_heavy_compositing = true;
  } else {
    pid_t pid = fork();
    if (pid == 0) {
      _exit(probe_render_nodes(g));
    }
    int status = 0;
    int result = -1;
    if (pid > 0) {
      // Bounded wait: a wedged GPU can hang a driver inside gbm_create_device,
      // and that must not hold the window back forever. 2 s is far above a
      // healthy probe (milliseconds) and short enough to go unnoticed.
      const gint64 started = g_get_monotonic_time();
      const gint64 deadline = started + 2 * G_USEC_PER_SEC;
      pid_t waited = 0;
      for (;;) {
        waited = waitpid(pid, &status, WNOHANG);
        if (waited == pid) break;
        if (waited < 0 && errno == EINTR) continue;
        if (waited < 0 || g_get_monotonic_time() >= deadline) break;
        g_usleep(10 * 1000);
      }
      if (waited != pid) {
        // Timed out, or waitpid failed for a reason other than EINTR: never
        // leave the child running or unreaped.
        kill(pid, SIGKILL);
        while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {}
        probe_ms = (g_get_monotonic_time() - started) / 1000;
      } else if (WIFEXITED(status)) {
        result = WEXITSTATUS(status);
      }
    }
    switch (result) {
      case 0:
        break;
      case 2:
        // Distinct from "allocation failed": the library is missing or too
        // old, which a minimal install can hit even with a working GPU.
        reason = "libgbm.so.1 is not loadable";
        break;
      case 1:
        reason = "no DRM render node can back a GBM buffer";
        break;
      default:
        // fork failed, or the probe crashed or hung inside the driver: assume
        // the DMABUF renderer would fail the same way.
        reason = "the GBM probe did not complete";
        break;
    }
  }
  globfree(&g);
  if (reason != nullptr) g_heavy_compositing = true;
  if (reason != nullptr) {
    if (probe_ms >= 0) {
      g_message("planetarium_overlay: %s after %" G_GINT64_FORMAT
                " ms, setting WEBKIT_DISABLE_DMABUF_RENDERER=1",
                reason, probe_ms);
    } else {
      g_message("planetarium_overlay: %s, setting WEBKIT_DISABLE_DMABUF_RENDERER=1",
                reason);
    }
    g_setenv("WEBKIT_DISABLE_DMABUF_RENDERER", "1", TRUE);
  }
}

namespace {

void webview_widget_destroyed_cb(GtkWidget* widget, gpointer user_data) {
  (void)widget;
  OverlayState* state = static_cast<OverlayState*>(user_data);
  state->webview_widget = nullptr;
  state->webview = nullptr;
}

// Desktop WebKitGTK answers a touchpad pinch with page magnification (it
// ignores the viewport meta), which blew up the whole planetarium UI instead
// of zooming the sky (Fedora KDE Wayland, 2026-10-05). The handler runs
// before WebKit's class handler: it swallows GDK_TOUCHPAD_PINCH and forwards
// the cumulative scale to the page's pinchUpdate(), which maps it onto the
// field of view. Mouse wheel and touch pinch are unaffected (the page already
// handles those itself).
gboolean pinch_event_cb(GtkWidget* widget, GdkEvent* event, gpointer user_data) {
  (void)widget;
  if (gdk_event_get_event_type(event) != GDK_TOUCHPAD_PINCH) return FALSE;
  // Swallowed unconditionally, page loaded or not: letting a pinch through to
  // WebKit before `stel` exists would still magnify the page. The JS hooks
  // guard on `stel` themselves, so an early pinch is simply a no-op.
  OverlayState* state = static_cast<OverlayState*>(user_data);
  const GdkEventTouchpadPinch* pinch = &event->touchpad_pinch;
  switch (pinch->phase) {
    case GDK_TOUCHPAD_GESTURE_PHASE_BEGIN:
      run_js(state->webview, "pinchBegin()");
      break;
    case GDK_TOUCHPAD_GESTURE_PHASE_UPDATE: {
      // g_ascii_dtostr, not printf: gtk_init applies the user's locale and a
      // decimal-comma locale (de_DE, fr_FR, ...) would emit "1,5", which JS
      // parses as two arguments.
      gchar buf[G_ASCII_DTOSTR_BUF_SIZE];
      g_ascii_dtostr(buf, sizeof buf, pinch->scale);
      gchar* js = g_strconcat("pinchUpdate(", buf, ")", nullptr);
      run_js(state->webview, js);
      g_free(js);
      break;
    }
    case GDK_TOUCHPAD_GESTURE_PHASE_END:
    case GDK_TOUCHPAD_GESTURE_PHASE_CANCEL:
      run_js(state->webview, "pinchEnd()");
      break;
  }
  return TRUE;
}

// WebKit's web process died. A crash while the DMABUF renderer is active is
// the NVIDIA failure above (or a cousin of it): record it so the next launch
// starts on the shm renderer, tell the user plainly, and reload the page once
// so the current session isn't left blank. The env var can't be flipped here
// (threads exist now), so a second crash in the same session just logs.
void web_process_terminated_cb(WebKitWebView* view,
                               WebKitWebProcessTerminationReason reason,
                               gpointer user_data) {
  OverlayState* state = static_cast<OverlayState*>(user_data);
  if (reason != WEBKIT_WEB_PROCESS_CRASHED) return;
  state->web_process_crashes++;
  const bool dmabuf_active =
      g_getenv("WEBKIT_DISABLE_DMABUF_RENDERER") == nullptr;
  if (dmabuf_active) {
    g_autofree gchar* marker = no_dmabuf_marker_path();
    g_autofree gchar* dir = g_path_get_dirname(marker);
    g_mkdir_with_parents(dir, 0700);
    g_file_set_contents(marker, "", 0, nullptr);
  }
  if (state->web_process_crashes == 1) {
    g_warning("planetarium_overlay: WebKit web process crashed%s; reloading "
              "the planetarium once. %s",
              dmabuf_active ? " with the DMABUF renderer active" : "",
              dmabuf_active ? "The next launch will use WebKit's shm renderer."
                            : "If it recurs, report your WebKitGTK version.");
    webkit_web_view_reload(view);
  } else {
    g_warning("planetarium_overlay: WebKit web process crashed again "
              "(%d this session); not reloading. Relaunch Ara%s.",
              state->web_process_crashes,
              dmabuf_active ? " to switch to the shm renderer" : "");
  }
}

void ensure_webview(OverlayState* state) {
  if (state->webview != nullptr) return;
  state->webview = WEBKIT_WEB_VIEW(webkit_web_view_new());
  g_signal_connect(state->webview, "decide-policy",
                   G_CALLBACK(decide_policy_cb), state);
  g_signal_connect(state->webview, "load-changed",
                   G_CALLBACK(load_changed_cb), state);
  g_signal_connect(state->webview, "web-process-terminated",
                   G_CALLBACK(web_process_terminated_cb), state);
  // Keyboard focus stays with FlView. WebKit's button-press handler grabs GTK
  // focus on click, which is a no-op for a widget that can't focus; without
  // this, one click on the sky and the Planning search field stops receiving
  // keys (Fedora KDE Wayland, 2026-10-05). The page needs no keyboard.
  gtk_widget_set_can_focus(GTK_WIDGET(state->webview), FALSE);
  // Touchpad pinch: see pinch_event_cb.
  g_signal_connect(state->webview, "event", G_CALLBACK(pinch_event_cb), state);

  // Wrap the webview in a windowed GtkEventBox: the event box owns a GdkWindow
  // that X11 promotes to a native subwindow and Wayland keeps client-side under
  // GtkOverlay draw order (the webview itself is windowless and would otherwise
  // draw into the toplevel surface Flutter overpaints).
  GtkWidget* event_box = gtk_event_box_new();
  gtk_event_box_set_visible_window(GTK_EVENT_BOX(event_box), TRUE);
  gtk_widget_set_can_focus(event_box, FALSE);
  // The overlay owns the event box: a normal window close destroys it before
  // GApplication::shutdown runs, so drop our pointers then and
  // planetarium_overlay_shutdown becomes a no-op instead of a use-after-free.
  g_signal_connect(event_box, "destroy", G_CALLBACK(webview_widget_destroyed_cb),
                   state);
  gtk_container_add(GTK_CONTAINER(event_box), GTK_WIDGET(state->webview));
  state->webview_widget = event_box;

  // get-child-position drives the geometry; alignment just keeps GTK from
  // stretching the child before our handler runs.
  gtk_widget_set_halign(event_box, GTK_ALIGN_START);
  gtk_widget_set_valign(event_box, GTK_ALIGN_START);
  gtk_overlay_add_overlay(state->overlay, event_box);
  // The webview must receive clicks/scroll (clickable stars, pan/zoom), so it
  // is NOT pass-through; events inside its rect go to WebKit, everything outside
  // falls through to Flutter.
  gtk_overlay_set_overlay_pass_through(state->overlay, event_box, FALSE);

  // The webview child must be visible so it maps when the event box maps.
  gtk_widget_show(GTK_WIDGET(state->webview));
  // Force the event box's GdkWindow into existence NOW (synchronously). On
  // X11 it is then promoted to a native subwindow so the X server composites
  // it above the Flutter GL frame; on Wayland it stays client-side (below).
  // Relying on the async show→map→realize cycle didn't work: hiding the
  // child before it kept Planning hidden races the realize.
  gtk_widget_realize(event_box);
  GdkWindow* window = gtk_widget_get_window(event_box);
  bool wayland = false;
#ifdef GDK_WINDOWING_WAYLAND
  wayland = GDK_IS_WAYLAND_DISPLAY(gtk_widget_get_display(event_box));
#endif
  if (wayland) {
    // #1200: on GDK3's Wayland backend a "native" child window is not a
    // subsurface — it becomes a parentless xdg_toplevel that never receives a
    // buffer, so the overlay is silently invisible (verified with
    // WAYLAND_DEBUG=client on KDE Plasma). Leave the event box client-side and
    // rely on GtkOverlay's draw order: overlay children paint after FlView.
    g_message("planetarium_overlay: Wayland display, using client-side overlay");
  } else if (window != nullptr && !gdk_window_ensure_native(window)) {
    // If the GdkWindow can't be promoted to a native X11 subwindow, the webview
    // renders into the same client-side surface as Flutter, which overpaints
    // it — the overlay goes permanently invisible with no other symptom. Warn
    // so that failure mode is at least diagnosable in logs.
    g_warning("planetarium_overlay: gdk_window_ensure_native failed — "
              "overlay may be invisible on this display backend");
  }
  // Keep it unmapped until Dart pushes bounds and Planning is the active tab.
  apply_visibility(state);
}

void method_call_cb(FlMethodChannel* channel,
                    FlMethodCall* method_call,
                    gpointer user_data) {
  (void)channel;
  OverlayState* state = static_cast<OverlayState*>(user_data);
  const gchar* method = fl_method_call_get_name(method_call);
  FlValue* args = fl_method_call_get_args(method_call);
  g_autoptr(FlMethodResponse) response = nullptr;

  if (strcmp(method, "setUrl") == 0) {
    ensure_webview(state);
    FlValue* url = (args != nullptr &&
                    fl_value_get_type(args) == FL_VALUE_TYPE_MAP)
                       ? fl_value_lookup_string(args, "url")
                       : nullptr;
    if (url != nullptr && fl_value_get_type(url) == FL_VALUE_TYPE_STRING) {
      const gchar* url_str = fl_value_get_string(url);
      // Defense-in-depth: this native WebKit process runs JS with no sandbox, so
      // only ever load our own loopback page. The Dart side always hands a
      // http://127.0.0.1:<port>/… URL; reject anything else rather than trust the
      // channel, so a future bug or compromised caller can't render external content.
      if (g_str_has_prefix(url_str, "http://127.0.0.1:")) {
        // Capture this server's exact origin (up to and including the path's
        // leading '/') so decide_policy_cb can lock navigations to it. The path
        // always starts at the first '/' after the "http://" scheme marker.
        const gchar* path = strchr(url_str + strlen("http://"), '/');
        g_free(state->allowed_origin);
        state->allowed_origin =
            path != nullptr ? g_strndup(url_str, (path - url_str) + 1)
                            : g_strconcat(url_str, "/", nullptr);
        webkit_web_view_load_uri(state->webview, url_str);
      } else {
        g_warning("planetarium_overlay: refusing non-loopback URL '%s'", url_str);
      }
    }
    response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
  } else if (strcmp(method, "setBounds") == 0) {
    state->rect.x = static_cast<int>(lround(lookup_number(args, "x", 0)));
    state->rect.y = static_cast<int>(lround(lookup_number(args, "y", 0)));
    state->rect.width =
        static_cast<int>(lround(lookup_number(args, "width", 0)));
    state->rect.height =
        static_cast<int>(lround(lookup_number(args, "height", 0)));
    state->has_rect = state->rect.width > 0 && state->rect.height > 0;
    if (state->webview_widget != nullptr) {
      // Pin the natural size to the rect so GtkOverlay's alignment path can't
      // clamp the child down to the (empty) webview's 0×0 request, then re-run
      // get-child-position with the new rect.
      gtk_widget_set_size_request(state->webview_widget, state->rect.width,
                                  state->rect.height);
      gtk_widget_queue_resize(GTK_WIDGET(state->overlay));
    }
    apply_visibility(state);
    response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
  } else if (strcmp(method, "setVisible") == 0) {
    FlValue* v = (args != nullptr &&
                  fl_value_get_type(args) == FL_VALUE_TYPE_MAP)
                     ? fl_value_lookup_string(args, "visible")
                     : nullptr;
    state->visible =
        v != nullptr && fl_value_get_type(v) == FL_VALUE_TYPE_BOOL &&
        fl_value_get_bool(v);
    apply_visibility(state);
    response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
  } else if (strcmp(method, "setNightMode") == 0) {
    FlValue* v = (args != nullptr &&
                  fl_value_get_type(args) == FL_VALUE_TYPE_MAP)
                     ? fl_value_lookup_string(args, "night")
                     : nullptr;
    state->night =
        v != nullptr && fl_value_get_type(v) == FL_VALUE_TYPE_BOOL &&
        fl_value_get_bool(v);
    run_js(state->webview, state->night ? kNightOnJs : kNightOffJs);
    response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
  } else {
    response = FL_METHOD_RESPONSE(fl_method_not_implemented_response_new());
  }

  fl_method_call_respond(method_call, response, nullptr);
}

}  // namespace

namespace {
OverlayState* g_registered_state = nullptr;
}  // namespace

// Destroys the WebKit view so its web process is told to exit. Without this a
// SIGTERM to the app orphans WebKitWebProcess, which then segfaults inside
// NVIDIA's EGL during its own teardown (Kubuntu, 2026-10-05) and leaves a
// coredump behind. Safe to call more than once.
void planetarium_overlay_shutdown() {
  OverlayState* state = g_registered_state;
  if (state == nullptr || state->webview_widget == nullptr) return;
  // Destroys the child webview too; webview_widget_destroyed_cb clears the
  // pointers. After a normal window close they are already null.
  gtk_widget_destroy(state->webview_widget);
}

void planetarium_overlay_register(GtkOverlay* overlay,
                                  FlView* view,
                                  FlBinaryMessenger* messenger) {
  (void)view;
  OverlayState* state = new OverlayState();
  g_registered_state = state;
  state->overlay = overlay;
  g_signal_connect(overlay, "get-child-position",
                   G_CALLBACK(on_get_child_position), state);

  g_autoptr(FlStandardMethodCodec) codec = fl_standard_method_codec_new();
  // The channel lives for the whole process (one window, one planetarium); it is
  // deliberately not unref'd. `state` likewise outlives every call.
  FlMethodChannel* channel = fl_method_channel_new(messenger, kChannelName,
                                                   FL_METHOD_CODEC(codec));
  fl_method_channel_set_method_call_handler(channel, method_call_cb, state,
                                            nullptr);
}
