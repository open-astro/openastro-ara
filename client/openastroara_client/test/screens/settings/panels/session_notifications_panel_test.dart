import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/session_notifications_panel.dart';
import 'package:openastroara/settings/registry.dart';
import 'package:openastroara/state/settings/settings_search.dart';

void main() {
  // #1189: nothing shows a banner or posts an OS notification, so both
  // channel toggles are hidden until one exists. The profile fields stay
  // for round-tripping.
  const hidden = {
    'In-app banner': 'session.notifications.in_app_banner',
    'OS desktop notification': 'session.notifications.os_desktop',
  };
  group('Unwired notification channel toggles (#1189)', () {
    testWidgets('are not shown in the Notifications panel', (tester) async {
      tester.view.physicalSize = const Size(1200, 3000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      // The panel's fixed-width number fields overflow under the test font;
      // that layout noise is not what this test is about.
      final prior = FlutterError.onError;
      FlutterError.onError = (d) {
        if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
      };
      addTearDown(() => FlutterError.onError = prior);
      await tester.pumpWidget(const ProviderScope(
        child: MaterialApp(home: Scaffold(body: SessionNotificationsPanel())),
      ));
      await tester.pump();
      for (final label in hidden.keys) {
        expect(find.text(label), findsNothing, reason: label);
      }
      // The live channel is still there.
      expect(find.text('Play a sound for safety alerts'), findsOneWidget);
    });

    test('are not registered as settings', () {
      for (final id in hidden.values) {
        expect(settingsRegistry.where((s) => s.id == id), isEmpty, reason: id);
      }
    });

    test('settings search no longer returns them', () {
      final index = buildSearchIndex();
      for (final q in ['desktop', 'toast', 'banner', ...hidden.keys]) {
        expect(
          searchSettings(index, q)
              .where((e) => hidden.values.contains(e.settingId)),
          isEmpty,
          reason: 'query "$q"',
        );
      }
    });
  });
}
