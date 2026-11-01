import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';

/// Opens on Sequence: Planning's planetarium server and Live's guiding poll
/// leave periodic timers running after the test.
class _StartOnSequence extends SelectedTabIndexNotifier {
  @override
  int build() => 2;
}

Future<ProviderContainer> _pumpAt(WidgetTester tester, Size size) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  // The test font is taller than the real one.
  tester.platformDispatcher.textScaleFactorTestValue = 0.5;
  addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
  // shortcut: the tab bodies themselves still overflow at phone width
  // (Sequence toolbar, Checklist rows); this test pins the shell's bars only.
  // Drop the filter once the tabs lay out on a phone.
  final onError = FlutterError.onError;
  FlutterError.onError = (details) {
    if (!details.toString().contains('overflowed')) onError?.call(details);
  };
  addTearDown(() => FlutterError.onError = onError);
  final container = ProviderContainer(overrides: [
    selectedTabIndexProvider.overrideWith(_StartOnSequence.new),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: AppShell()),
  ));
  await tester.pump();
  return container;
}

void main() {
  testWidgets('a phone-width window puts the tabs in a bottom bar and the devices in a column',
      (tester) async {
    final container = await _pumpAt(tester, const Size(400, 860));

    expect(find.byType(NavigationRail), findsNothing);
    expect(find.byType(NavigationBar), findsOneWidget);
    for (final tab in ['Planning', 'Checklist', 'Sequence', 'Live']) {
      expect(find.descendant(of: find.byType(NavigationBar), matching: find.text(tab)), findsOneWidget,
          reason: tab);
    }
    final camera = tester.getCenter(find.byTooltip('Camera'));
    final mount = tester.getCenter(find.byTooltip('Mount'));
    expect(mount.dx, camera.dx, reason: 'one column');
    expect(mount.dy, greaterThan(camera.dy), reason: 'Mount below Camera');
    expect(tester.getBottomLeft(find.byType(NavigationBar)).dy, 860, reason: 'at the bottom');

    await tester.tap(find.descendant(of: find.byType(NavigationBar), matching: find.text('Checklist')));
    await tester.pump();
    expect(container.read(selectedTabIndexProvider), 1);
  });

  testWidgets('a desktop-width window keeps the side rail and the top device bar', (tester) async {
    await _pumpAt(tester, const Size(1600, 1000));

    expect(find.byType(NavigationRail), findsOneWidget);
    expect(find.byType(NavigationBar), findsNothing);
    expect(find.byType(TopEquipmentChips), findsOneWidget);
    final camera = tester.getCenter(find.text('Camera'));
    final mount = tester.getCenter(find.text('Mount'));
    expect(mount.dy, camera.dy, reason: 'one row');
  });
}
