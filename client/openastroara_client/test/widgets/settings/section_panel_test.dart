import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/camera_electronics_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';

import '../../support/fake_section_rig.dart';

// The shared panel scaffold (SectionPanel), exercised through one panel: the
// camera electronics panel on a fake rig.

const _section = 'camera-electronics';

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSectionRig? rig,
) async {
  tester.view.physicalSize = const Size(1400, 4000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig?.api())],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: CameraElectronicsPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

FakeSectionRig _rig() => FakeSectionRig({
  _section: {'sensor_name': 'IMX-rig', 'gain': 100},
});

void main() {
  testWidgets('unreachable rig: the panel says not loaded, offers Retry and '
      'shows no fields', (tester) async {
    final rig = _rig()..failGet.add(_section);
    await _pump(tester, rig);
    expect(find.textContaining('Not loaded'), findsOneWidget);
    expect(find.text('Sensor name'), findsNothing);
    rig.failGet.clear();
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-retry')), findsNothing);
    expect(find.text('Sensor name'), findsOneWidget);
  });

  testWidgets('no rig connected: the panel says so, with no Retry', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(find.byKey(const Key('section-retry')), findsNothing);
  });

  testWidgets('412 on Save keeps the edits and shows the banner; Reload '
      "restores the rig's copy", (tester) async {
    final rig = _rig();
    final container = await _pump(tester, rig);
    container.read(cameraElectronicsProvider.notifier).setSensorName('mine');
    rig.savedElsewhere(_section);
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsOneWidget);
    expect(container.read(cameraElectronicsProvider).value!.sensorName, 'mine');
    expect(rig.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsNothing);
    expect(
      container.read(cameraElectronicsProvider).value!.sensorName,
      'IMX-rig',
    );
  });

  testWidgets('Save sends the tag the panel loaded', (tester) async {
    final rig = _rig();
    final container = await _pump(tester, rig);
    container.read(cameraElectronicsProvider.notifier).setSensorName('mine');
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(rig.ifMatches, ['"$_section-v1"']);
    expect(rig.puts, hasLength(1));
  });
}
