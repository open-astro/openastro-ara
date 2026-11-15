import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/diagnostics_mode_panel.dart';
import 'package:openastroara/screens/settings/panels/session_notifications_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';

import '../../support/fake_section_rig.dart';

// The shared panel scaffold (SectionPanel) on a fake rig, exercised through
// the notifications panel; the last case checks the diagnostics panel's radios.

const _section = 'notifications';

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSectionRig? rig, [
  Widget panel = const SessionNotificationsPanel(),
]) async {
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
      child: MaterialApp(home: Scaffold(body: panel)),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

FakeSectionRig _rig() => FakeSectionRig({
  _section: {'pushover_token': 'rig-token'},
});

void main() {
  testWidgets('unreachable rig: the panel says not loaded, offers Retry and '
      'shows no fields', (tester) async {
    final rig = _rig()..failGet.add(_section);
    await _pump(tester, rig);
    expect(find.textContaining('Not loaded'), findsOneWidget);
    expect(find.text('Pushover app token'), findsNothing);
    rig.failGet.clear();
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-retry')), findsNothing);
    expect(find.text('Pushover app token'), findsOneWidget);
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
    container
        .read(notificationsSettingsProvider.notifier)
        .setPushoverToken('mine');
    rig.savedElsewhere(_section);
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsOneWidget);
    expect(
      container.read(notificationsSettingsProvider).value!.pushoverToken,
      'mine',
    );
    expect(rig.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsNothing);
    expect(
      container.read(notificationsSettingsProvider).value!.pushoverToken,
      'rig-token',
    );
  });

  testWidgets('Save sends the tag the panel loaded', (tester) async {
    final rig = _rig();
    final container = await _pump(tester, rig);
    container
        .read(notificationsSettingsProvider.notifier)
        .setPushoverToken('mine');
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(rig.ifMatches, ['"$_section-v1"']);
    expect(rig.puts, hasLength(1));
  });

  testWidgets('diagnostics modes read as one radio group, the loaded one '
      'checked', (tester) async {
    await _pump(
      tester,
      FakeSectionRig({'diagnostics-mode': <String, Object?>{}}),
      const DiagnosticsModePanel(),
    );
    expect(
      tester.getSemantics(find.text('Notify only (default)')),
      isSemantics(isInMutuallyExclusiveGroup: true, isChecked: true),
    );
  });
}
