import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_guiding.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
import 'package:openastroara/state/wizard_state.dart';

/// §76 S3 — records the pixel-size lookup and answers with a scripted value.
class _FakeGuiderEquipment implements GuiderEquipmentClient {
  _FakeGuiderEquipment({this.pixelSize});
  final double? pixelSize;
  String? lastHost;
  int? lastPort;
  int? lastDevice;

  @override
  Future<double?> getAlpacaCameraPixelSize(
      {String? host, int? port, int? device}) async {
    lastHost = host;
    lastPort = port;
    lastDevice = device;
    return pixelSize;
  }

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

Future<ProviderContainer> _pump(WidgetTester tester,
    {GuiderEquipmentClient? equipment,
    void Function(ProviderContainer)? seedDraft}) async {
  final container = ProviderContainer(overrides: [
    guiderEquipmentApiProvider.overrideWithValue(equipment),
  ]);
  addTearDown(container.dispose);
  container.listen(wizardStepValidProvider, (_, _) {});
  seedDraft?.call(container);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ScreenGuider())),
  ));
  await tester.pump();
  return container;
}

void main() {
  testWidgets('the exposure range defaults to the guider\'s own 1.0–6.0 s '
      'and darks-on-finish defaults ON', (tester) async {
    final container = await _pump(tester);
    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.guider.darkMinExposureMs, 1000);
    expect(draft.guider.darkMaxExposureMs, 6000);
    expect(draft.guider.buildDarksOnFinish, isTrue);
    expect(find.text('Build dark library now'), findsOneWidget);
    expect(container.read(wizardStepValidProvider), isTrue);
  });

  testWidgets('an inverted range blocks Next and shows the error hint; '
      'fixing it re-validates', (tester) async {
    final container = await _pump(tester);

    // Shortest 2.5 s > Longest 1.0 s. Drive the DropdownMenu selection
    // callbacks directly — the exposure items sit inside the menu overlay's
    // own scrollable, where a coordinate tap silently misses; the range-gate
    // logic is what's under test, not Material's menu scrolling. (Two int
    // dropdowns on screen: index 0 = Shortest, 1 = Longest — the Advanced
    // frame-count dropdown only exists once the disclosure is expanded.)
    final shortest = find.byType(DropdownMenu<int>).at(0);
    final longest = find.byType(DropdownMenu<int>).at(1);
    tester.widget<DropdownMenu<int>>(shortest).onSelected!(2500);
    tester.widget<DropdownMenu<int>>(longest).onSelected!(1000);
    await tester.pumpAndSettle();

    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.text('Shortest exposure must not exceed the longest.'),
        findsOneWidget);

    tester.widget<DropdownMenu<int>>(longest).onSelected!(4000);
    await tester.pumpAndSettle();
    expect(container.read(wizardStepValidProvider), isTrue);

    final g = container.read(wizardControllerProvider).draft.guider;
    expect(g.darkMinExposureMs, 2500);
    expect(g.darkMaxExposureMs, 4000);
  });

  testWidgets('a chosen Alpaca guide camera fills the pixel size from the '
      'driver and shows it as a read fact', (tester) async {
    final fake = _FakeGuiderEquipment(pixelSize: 2.9);
    final container = await _pump(tester, equipment: fake, seedDraft: (c) {
      c.read(wizardControllerProvider).draft.guider.guiderCamera =
          'Alpaca Camera [10.0.0.9:11111/1]';
    });
    await tester.pumpAndSettle();

    expect(fake.lastHost, '10.0.0.9');
    expect(fake.lastPort, 11111);
    expect(fake.lastDevice, 1);
    final g = container.read(wizardControllerProvider).draft.guider;
    expect(g.guidePixelSizeUm, 2.9);
    expect(find.textContaining('read from the camera driver'), findsOneWidget);
  });

  testWidgets('a non-Alpaca camera choice leaves the manual pixel-size field '
      'in Advanced', (tester) async {
    final container = await _pump(tester, seedDraft: (c) {
      c.read(wizardControllerProvider).draft.guider.guiderCamera = 'QHY5-II';
    });
    await tester.pumpAndSettle();
    expect(find.textContaining('read from the camera driver'), findsNothing);

    await tester.ensureVisible(find.text('Advanced'));
    await tester.tap(find.text('Advanced'));
    await tester.pumpAndSettle();
    expect(find.text('Guide pixel size (µm)'), findsOneWidget);
    expect(container.read(wizardControllerProvider).draft.guider.guidePixelSizeUm,
        isNull);
  });
}
