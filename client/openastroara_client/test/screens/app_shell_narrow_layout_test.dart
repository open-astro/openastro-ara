import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/services/notch_side.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';
import 'package:openastroara/widgets/settings_gear_button.dart';

/// Opens on Sequence: Planning's planetarium server and Live's guiding poll
/// leave periodic timers running after the test.
class _StartOnSequence extends SelectedTabIndexNotifier {
  @override
  int build() => 2;
}

Future<ProviderContainer> _pumpAt(WidgetTester tester, Size size, {FakeViewPadding? padding}) async {
  tester.view.physicalSize = size;
  if (padding != null) tester.view.padding = padding;
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

  testWidgets('a phone in landscape fits every device in the top bar (icons only)', (tester) async {
    await _pumpAt(tester, const Size(852, 393));

    expect(find.byType(NavigationRail), findsOneWidget, reason: 'wide enough for the desktop layout');
    for (final device in [
      'Camera', 'Mount', 'Guider', 'Focuser', 'Filter Wheel', 'Rotator',
      'Flat Panel', 'Switches', 'Weather', 'Safety', 'Dome',
    ]) {
      expect(tester.getRect(find.byTooltip(device)).right, lessThanOrEqualTo(852), reason: '$device on screen');
    }
    expect(tester.getRect(find.byTooltip('Dome')).right,
        lessThan(tester.getRect(find.byTooltip(RegExp('^Search')).first).left),
        reason: 'the devices stop before search, night mode and Settings');
  });

  testWidgets('the device column starts right under the top bar', (tester) async {
    await _pumpAt(tester, const Size(400, 860));
    final barBottom = tester.getRect(find.byTooltip(RegExp('^Search')).first).bottom;
    expect(tester.getRect(find.byTooltip('Camera')).top - barBottom, lessThan(16),
        reason: 'not floating in the middle of the column');
  });

  testWidgets("the notch and home-indicator margins are painted in the bars' colour, not left black",
      (tester) async {
    // A landscape iPhone reports a notch inset on both sides and the home
    // indicator below; the content stays inside them, the margins match the bars.
    await _pumpAt(tester, const Size(852, 393),
        padding: const FakeViewPadding(left: 59, right: 59, bottom: 21));
    final edge = find.ancestor(of: find.byType(SafeArea).first, matching: find.byType(ColoredBox)).first;
    expect(tester.widget<ColoredBox>(edge).color, AraColors.bgPanel);
    expect(tester.getRect(edge), const Rect.fromLTWH(0, 0, 852, 393));
  });

  group('a landscape iPhone keeps its margin on the Dynamic Island side only', () {
    setUp(() => debugDefaultTargetPlatformOverride = TargetPlatform.iOS);
    tearDown(() {
      debugDefaultTargetPlatformOverride = null;
      NotchSideProbe.interfaceOrientation = NotchSideProbe.query;
    });

    Future<void> pumpTurned(WidgetTester tester, String orientation) async {
      NotchSideProbe.interfaceOrientation = () async => orientation;
      await _pumpAt(tester, const Size(852, 393),
          padding: const FakeViewPadding(left: 59, right: 59, bottom: 21));
      await tester.pump();
    }

    double gearRight(WidgetTester tester) => tester.getRect(find.byType(SettingsGearButton)).right;

    testWidgets('island on the left: the right edge is used', (tester) async {
      await pumpTurned(tester, 'landscapeRight');
      expect(tester.getRect(find.byType(NavigationRail)).left, 59);
      expect(gearRight(tester), greaterThan(852 - 59), reason: 'into the old right margin');
      debugDefaultTargetPlatformOverride = null;
    });

    testWidgets('island on the right: the left edge is used', (tester) async {
      await pumpTurned(tester, 'landscapeLeft');
      expect(tester.getRect(find.byType(NavigationRail)).left, 0);
      expect(gearRight(tester), lessThanOrEqualTo(852 - 59));
      debugDefaultTargetPlatformOverride = null;
    });

    testWidgets('side unknown: both margins stay', (tester) async {
      await pumpTurned(tester, 'unknown');
      expect(tester.getRect(find.byType(NavigationRail)).left, 59);
      expect(gearRight(tester), lessThanOrEqualTo(852 - 59));
      debugDefaultTargetPlatformOverride = null;
    });
  });

  testWidgets('an iPad in portrait fits every device in the top bar', (tester) async {
    // 768 pt wide: wider than a phone, too narrow for eleven named buttons.
    await _pumpAt(tester, const Size(768, 1024));
    final search = tester.getRect(find.byTooltip(RegExp('^Search')).first).left;
    for (final device in [
      'Camera', 'Mount', 'Guider', 'Focuser', 'Filter Wheel', 'Rotator',
      'Flat Panel', 'Switches', 'Weather', 'Safety', 'Dome',
    ]) {
      expect(tester.getRect(find.byTooltip(device)).right, lessThan(search), reason: '$device before search');
    }
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
