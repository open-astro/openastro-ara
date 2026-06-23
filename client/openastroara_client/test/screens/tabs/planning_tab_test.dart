import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/tabs/planning_tab.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

/// ProfileApi double returning a configured optics section, so the Planning
/// tab's on-mount hydration has something to load.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : super(const AraServer(hostname: 'test', port: 1));
  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings(
        focalLengthMm: 714,
        reducerFactor: 1.0,
        sensorWidthPx: 6248,
        sensorHeightPx: 4176,
        pixelSizeUm: 3.76,
      );
}

// Widget tests for the merged Planning tab (PORT_DECISIONS §36/§25.5). The
// embedded AladinView can't start a webview in the headless test env, so it
// degrades to its "atlas unavailable" panel — these tests exercise the shell
// around it (header modes + the Frame toggle), not the atlas itself. We pump
// fixed frames rather than `pumpAndSettle` because AladinView shows a brief
// animating loading spinner before it settles to the unavailable panel.
void main() {
  // Render at a desktop width — Planning is a desktop-first tab and its header
  // toolbar assumes room to lay out (it scrolls if narrower, but the controls
  // should be on-screen for tap tests).
  setUp(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.physicalSize = const Size(1280, 800);
    view.devicePixelRatio = 1.0;
  });
  tearDown(() {
    final view = TestWidgetsFlutterBinding.ensureInitialized()
        .platformDispatcher
        .views
        .first;
    view.resetPhysicalSize();
    view.resetDevicePixelRatio();
  });

  Future<void> pumpTab(WidgetTester tester) async {
    await tester.pumpWidget(
      const ProviderScope(
        child: MaterialApp(home: Scaffold(body: PlanningTab())),
      ),
    );
    await tester.pump(); // build + let the webview-init future reject
  }

  testWidgets('header shows Planning title + Explore/Tonight\'s + Frame toggle',
      (tester) async {
    await pumpTab(tester);

    expect(find.text('Planning'), findsOneWidget);
    expect(find.text('Explore'), findsOneWidget);
    expect(find.text("Tonight's Sky"), findsOneWidget);
    expect(find.widgetWithText(FilterChip, 'Frame'), findsOneWidget);
    expect(find.text('Data Manager'), findsOneWidget);
  });

  testWidgets('Frame toggle reveals the framing panel; off by default',
      (tester) async {
    await pumpTab(tester);

    // Frame off → no framing controls.
    expect(find.textContaining('Rotation:'), findsNothing);
    expect(find.text('Add to Sequence'), findsNothing);

    // Toggle Frame on.
    await tester.tap(find.widgetWithText(FilterChip, 'Frame'));
    await tester.pump();

    expect(find.textContaining('Rotation:'), findsOneWidget);
    expect(find.text('Mosaic'), findsOneWidget);
    // Sequence output is wired in the FOV slice — disabled for now.
    final addBtn = tester.widget<FilledButton>(
      find.ancestor(
        of: find.text('Add to Sequence'),
        matching: find.byType(FilledButton),
      ),
    );
    expect(addBtn.onPressed, isNull);
  });

  testWidgets('hydrates profile optics on mount so the FOV box has geometry',
      (tester) async {
    final container = ProviderContainer(overrides: [
      profileApiProvider.overrideWithValue(_FakeProfileApi()),
    ]);
    addTearDown(container.dispose);
    // Starts at the zero default → no computable FOV (the bug: camera size never
    // showed when framing).
    expect(container.read(opticsSettingsProvider).fovWidthArcmin, isNull);

    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: PlanningTab())),
    ));
    await tester.pump(); // build + run the post-frame callback (kicks off hydrate)
    await tester.pump(); // flush the getOptics() microtask → state assigned

    final optics = container.read(opticsSettingsProvider);
    expect(optics.focalLengthMm, 714);
    expect(optics.fovWidthArcmin, isNotNull,
        reason: 'optics hydrated on mount → frameFovBoxProvider can draw the FOV');
  });
}
