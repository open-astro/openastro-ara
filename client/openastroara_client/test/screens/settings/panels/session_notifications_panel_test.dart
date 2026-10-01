import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/session_notifications_panel.dart';
import 'package:openastroara/settings/registry.dart';
import 'package:openastroara/state/settings/settings_search.dart';

void main() {
  // #1189: nothing posts an OS notification, so the toggle is hidden until a
  // channel exists. The profile field stays for round-tripping.
  group('OS desktop notification toggle (#1189)', () {
    testWidgets('is not shown in the Notifications panel', (tester) async {
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
      expect(find.text('OS desktop notification'), findsNothing);
      // The live channels are still there.
      expect(find.text('In-app banner'), findsOneWidget);
      expect(find.text('Play a sound for safety alerts'), findsOneWidget);
    });

    test('is not registered as a setting', () {
      expect(
        settingsRegistry.where((s) => s.id == 'session.notifications.os_desktop'),
        isEmpty,
      );
    });

    test('settings search no longer returns it', () {
      final index = buildSearchIndex();
      for (final q in ['desktop', 'toast', 'OS desktop notification']) {
        expect(
          searchSettings(index, q)
              .where((e) => e.settingId == 'session.notifications.os_desktop'),
          isEmpty,
          reason: 'query "$q"',
        );
      }
    });
  });
}
