import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/optics_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';

import '../../../support/fake_section_rig.dart';

// ADR 0015: the Optics panel on the shared panel scaffold (SectionPanel).

const _section = 'optics';

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSectionRig? rig,
) async {
  tester.view.physicalSize = const Size(1400, 2000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig?.api())],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: OpticsPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

FakeSectionRig _rig() => FakeSectionRig({
  _section: {
    ...rigDefaults[_section]!,
    'focal_length_mm': 800,
    'telescope_name': 'Rig scope',
  },
});

void main() {
  testWidgets('unreachable rig: the panel says not loaded, offers Retry and '
      'shows no fields', (tester) async {
    final rig = _rig()..failGet.add(_section);
    await _pump(tester, rig);
    expect(find.textContaining('Not loaded'), findsOneWidget);
    expect(find.text('Focal length (mm)'), findsNothing);
    rig.failGet.clear();
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-retry')), findsNothing);
    expect(find.text('Focal length (mm)'), findsOneWidget);
  });

  testWidgets('no rig connected: the panel says so, with no Retry', (
    tester,
  ) async {
    await _pump(tester, null);
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(find.byKey(const Key('section-retry')), findsNothing);
  });

  testWidgets('a cached copy shows no fields until the rig loads', (
    tester,
  ) async {
    final c = await _pump(tester, null);
    c
        .read(opticsSettingsProvider.notifier)
        .seed(const OpticsSettings(focalLengthMm: 333));
    await tester.pump();
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(find.byType(TextField), findsNothing);
  });

  testWidgets('412 on Save keeps the edits and shows the banner; Reload '
      "restores the rig's copy", (tester) async {
    final rig = _rig();
    final c = await _pump(tester, rig);
    c.read(opticsSettingsProvider.notifier).setFocalLengthMm(1200);
    rig.savedElsewhere(_section);
    await tester.runAsync(c.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsOneWidget);
    expect(c.read(opticsSettingsProvider).value!.focalLengthMm, 1200);
    expect(rig.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsNothing);
    expect(c.read(opticsSettingsProvider).value!.focalLengthMm, 800);
  });
}
