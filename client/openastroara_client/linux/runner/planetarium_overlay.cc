#include "planetarium_overlay.h"

#include <math.h>
#include <webkit2/webkit2.h>

#include <cstring>

// See planetarium_overlay.h for the why. This file owns the WebKitWebView that
// floats over FlView and the method channel Dart drives it with.

namespace {

// Method channel name — must match lib/services/planetarium_overlay.dart.
const char* kChannelName = "org.openastro.openastroara/planetarium";

struct OverlayState {
  GtkOverlay* overlay = nullptr;
  // Lazily created on the first setUrl so plain registration costs nothing and,
  // crucially, no WebKit GL surface exists until the user actually opens
  // Planning (mirrors webview_all's "GL only inits when a webview is created").
  WebKitWebView* webview = nullptr;
  // The positioned overlay child: a windowed GtkEventBox wrapping the webview.
  // It's promoted to a native X11 subwindow (see on_overlay_child_realize) so
  // the X server stacks it ABOVE the toplevel content Flutter blits its GL frame
  // into — otherwise the windowless webview draws into the same surface Flutter
  // immediately overpaints, and the planetarium stays invisible.
  GtkWidget* webview_widget = nullptr;
  // Target rect in logical (GTK) pixels, relative to the FlView origin. Flutter
  // logical pixels and GTK widget coordinates share the same scale factor on a
  // given display, so the Dart-side global rect maps straight onto the overlay
  // child allocation with no DPI conversion.
  GdkRectangle rect = {0, 0, 0, 0};
  bool has_rect = false;
  bool visible = false;
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
    gtk_widget_show(state->webview_widget);
    // Keep the native subwindow on top of the Flutter surface every time it
    // reappears (e.g. returning to Planning after another tab redrew the view).
    GdkWindow* window = gtk_widget_get_window(state->webview_widget);
    if (window != nullptr) gdk_window_raise(window);
  } else {
    gtk_widget_hide(state->webview_widget);
  }
}

void ensure_webview(OverlayState* state) {
  if (state->webview != nullptr) return;
  state->webview = WEBKIT_WEB_VIEW(webkit_web_view_new());

  // Wrap the webview in a windowed GtkEventBox: the event box owns a GdkWindow
  // we can promote to a native X11 subwindow (the webview itself is windowless
  // and would otherwise draw into the toplevel surface Flutter overpaints).
  GtkWidget* event_box = gtk_event_box_new();
  gtk_event_box_set_visible_window(GTK_EVENT_BOX(event_box), TRUE);
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
  // Force the event box's GdkWindow into existence NOW (synchronously) and
  // promote it to a native X11 subwindow, so the X server composites it above
  // the Flutter GL frame. Relying on the async show→map→realize cycle didn't
  // work: hiding the child before it kept Planning hidden races the realize.
  gtk_widget_realize(event_box);
  GdkWindow* window = gtk_widget_get_window(event_box);
  if (window != nullptr) gdk_window_ensure_native(window);
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
      webkit_web_view_load_uri(state->webview, fl_value_get_string(url));
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
  } else {
    response = FL_METHOD_RESPONSE(fl_method_not_implemented_response_new());
  }

  fl_method_call_respond(method_call, response, nullptr);
}

}  // namespace

void planetarium_overlay_register(GtkOverlay* overlay,
                                  FlView* view,
                                  FlBinaryMessenger* messenger) {
  (void)view;
  OverlayState* state = new OverlayState();
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
