import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/safety_policies_panel.dart';
import 'package:openastroara/screens/settings/panels/session_calibration_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../../../support/fake_section_rig.dart';
import '../../../support/safety_section.dart';

// ADR 0015: Unattended running and Calibration edit the rig's copy of the
// safety section.

void main() {
  Future<FakeSafetySection> pumpOnRig(
    WidgetTester tester,
    Widget panel, {
    bool loaded = true,
  }) async {
    tester.view.physicalSize = const Size(1600, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final section = FakeSafetySection(
      initial: loaded
          ? const SafetyPolicies(meridianPauseMin: 12, flatFramesPerFilter: 40)
          : null,
    );
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
          safetyPoliciesProvider.overrideWith(() => section),
        ],
        child: MaterialApp(home: Scaffold(body: panel)),
      ),
    );
    await tester.pumpAndSettle();
    return section;
  }

  Future<void> save(WidgetTester tester, Type panel) async {
    final state = tester.state(find.byType(panel)) as PanelSaveRegistration;
    await state.panelSave();
    await tester.pump();
  }

  ProviderContainer containerOf(WidgetTester tester, Type panel) =>
      ProviderScope.containerOf(tester.element(find.byType(panel)));

  testWidgets(
    'Unattended running: Save with the section not loaded from the rig writes nothing and says so (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(
        tester,
        const SafetyPoliciesPanel(),
        loaded: false,
      );
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(find.text('Auto flip'), findsNothing, reason: 'no default shows');
      await save(tester, SafetyPoliciesPanel);
      expect(section.puts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets(
    'Unattended running: Save sends the edit made on the rig copy (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(tester, const SafetyPoliciesPanel());
      expect(find.text('12'), findsOneWidget, reason: 'the rig\'s pause');
      containerOf(tester, SafetyPoliciesPanel)
          .read(safetyPoliciesProvider.notifier)
          .setMeridianFlipAuto(false);
      await save(tester, SafetyPoliciesPanel);
      expect(section.puts.single.meridianFlipAuto, isFalse);
      expect(
        section.puts.single.meridianPauseMin,
        12,
        reason: 'the rest of the rig\'s copy goes back unchanged',
      );
      expect(find.text('Saved.'), findsOneWidget);
    },
  );

  testWidgets(
    'Calibration: Save with the section not loaded from the rig writes nothing and says so (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(
        tester,
        const SessionCalibrationPanel(),
        loaded: false,
      );
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(
        find.text('Frames per filter'),
        findsNothing,
        reason: 'no default shows',
      );
      await save(tester, SessionCalibrationPanel);
      expect(section.puts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets(
    'Calibration: Save sends the edit made on the rig copy (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(tester, const SessionCalibrationPanel());
      expect(find.text('40'), findsOneWidget, reason: 'the rig\'s frames');
      containerOf(tester, SessionCalibrationPanel)
          .read(safetyPoliciesProvider.notifier)
          .setPostFlatParkMount(true);
      await save(tester, SessionCalibrationPanel);
      expect(section.puts.single.postFlatParkMount, isTrue);
      expect(
        section.puts.single.meridianPauseMin,
        12,
        reason: 'the rest of the rig\'s copy goes back unchanged',
      );
      expect(find.text('Saved.'), findsOneWidget);
    },
  );
}
