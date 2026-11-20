import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/safety_policies_panel.dart';
import 'package:openastroara/state/saved_server_state.dart';

import '../../../support/safety_section.dart';

/// #2044: meridian_pause_min is minutes BEFORE the meridian, so the row says so.
void main() {
  testWidgets(
    'the meridian pause row is labelled "Stop before meridian (min)"',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 4000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            activeServerProvider.overrideWithValue(null),
            safetyFromRig(),
          ],
          child: const MaterialApp(home: Scaffold(body: SafetyPoliciesPanel())),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('Stop before meridian (min)'), findsOneWidget);
      expect(find.text('Pause after flip (min)'), findsNothing);
    },
  );
}
