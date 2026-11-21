import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/optics_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';

import '../../../support/fake_section_rig.dart';
import '../../../support/imaging_sections.dart';
import '../../../support/settings_values.dart';

// ADR 0015: the Optics panel edits the rig's copy of the optics section.

void main() {
  Future<FakeOpticsSection> pumpOnRig(
    WidgetTester tester, {
    bool loaded = true,
  }) async {
    tester.view.physicalSize = const Size(1600, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final section = FakeOpticsSection(
      initial: loaded
          ? const TestOpticsSettings(focalLengthMm: 382, pixelSizeUm: 3.76)
          : null,
    );
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
          opticsSettingsProvider.overrideWith(() => section),
        ],
        child: const MaterialApp(home: Scaffold(body: OpticsPanel())),
      ),
    );
    await tester.pumpAndSettle();
    return section;
  }

  Future<void> save(WidgetTester tester) async {
    final panel =
        tester.state(find.byType(OpticsPanel)) as PanelSaveRegistration;
    await panel.panelSave();
    await tester.pump();
  }

  testWidgets(
    'Optics: Save with the section not loaded from the rig writes nothing and says so (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(tester, loaded: false);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(
        find.text('Focal length (mm)'),
        findsNothing,
        reason: 'no default shows',
      );
      await save(tester);
      expect(section.puts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets('Optics: Save sends the edit made on the rig copy (ADR 0015)', (
    tester,
  ) async {
    final section = await pumpOnRig(tester);
    expect(find.text('382'), findsOneWidget, reason: 'the rig\'s focal length');
    final container = ProviderScope.containerOf(
      tester.element(find.byType(OpticsPanel)),
    );
    container.read(opticsSettingsProvider.notifier).setReducerFactor(0.8);
    await save(tester);
    expect(section.puts.single.reducerFactor, 0.8);
    expect(
      section.puts.single.pixelSizeUm,
      3.76,
      reason: 'the rest of the rig\'s copy goes back unchanged',
    );
    expect(find.text('Saved.'), findsOneWidget);
  });
}
