import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

/// Opens a Simple Mode screen the way a person does (#1911): one tap on its
/// tab on a wide layout, the mode button and its grid on a phone. [mode] is
/// the grid tile's name: preview, focus, polarAlign, guide, plan, calibration
/// or preflight.
Future<void> openMode(
  WidgetTester tester,
  String mode, {
  bool warnIfMissed = true,
}) async {
  final slug = switch (mode) {
    'polarAlign' => 'polar',
    'guide' => 'guider',
    _ => mode,
  };
  // Preflight keeps one key in both layouts: its row on the tabs, its tile in
  // the grid.
  final direct = find.byKey(
    Key(
      mode == 'preflight' ? 'simple-mode-preflight' : 'simple-rail-tab-$slug',
    ),
  );
  if (direct.evaluate().isNotEmpty &&
      find.byKey(const Key('simple-rail-tabs')).evaluate().isNotEmpty) {
    await tester.ensureVisible(direct);
    await tester.tap(direct, warnIfMissed: warnIfMissed);
  } else {
    await tester.tap(
      find.byKey(const Key('simple-mode')),
      warnIfMissed: warnIfMissed,
    );
    await tester.pump();
    await tester.tap(
      find.byKey(Key('simple-mode-$mode')),
      warnIfMissed: warnIfMissed,
    );
  }
  await tester.pump();
}
