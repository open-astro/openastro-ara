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
    });
  });

}
