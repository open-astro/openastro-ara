// Guards for the Linux runner's Wayland-only policy (#1201). No C++ harness
// exists; these pin the source so a revert fails CI.
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  group('Linux runner is Wayland-only (#1201)', () {
    test('main refuses X11 and restricts GDK to the Wayland backend', () {
      final main = File('linux/runner/main.cc').readAsStringSync();
      expect(main, contains('g_getenv("WAYLAND_DISPLAY") == nullptr'));
      expect(main, contains('OpenAstro Ara needs a Wayland session.'));
      expect(main, contains('gdk_set_allowed_backends("wayland");'));
      // The refusal must precede the renderer probe and the application.
      expect(main.indexOf('gdk_set_allowed_backends'),
          lessThan(main.indexOf('planetarium_overlay_configure_renderer();')));
      final app = File('linux/runner/my_application.cc').readAsStringSync();
      expect(app, isNot(contains('GDK_WINDOWING_X11')));
      expect(app, isNot(contains('gtk_window_move(window')));
      expect(app, isNot(contains('gtk_window_set_icon(window')));
      // The size floor sits on the content, not the CSD window (whose request
      // includes the shadow margins and let the content shrink ~90 px short).
      expect(app, isNot(contains('gtk_widget_set_size_request(GTK_WIDGET(window)')));
      expect(app, contains('set_content_min_size(window, 1100, 600);'));
    });
  });

  test('planetarium bounds that arrive while hidden are applied on show', () {
    // A size request set while the webview was hidden read as unchanged on
    // show and GTK kept the old allocation (window grown behind another tab).
    final overlay = File('linux/runner/planetarium_overlay.cc').readAsStringSync();
    final setBounds = overlay.substring(overlay.indexOf('strcmp(method, "setBounds")'),
        overlay.indexOf('strcmp(method, "setVisible")'));
    expect(setBounds, contains('state->webview_widget != nullptr && state->visible'));
    final apply = overlay.substring(overlay.indexOf('void apply_visibility('),
        overlay.indexOf('gtk_widget_hide(state->webview_widget);'));
    expect(apply, contains('if (!was_visible) {'));
    expect(apply.substring(apply.indexOf('if (!was_visible) {')),
        contains('gtk_widget_set_size_request(state->webview_widget, state->rect.width,'));
  });

}
