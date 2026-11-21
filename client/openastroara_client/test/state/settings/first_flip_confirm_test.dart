import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/safety_policies_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/safety_section.dart';

/// #1589 — a meridian flip is confirmed by the user, after a flip completed.
/// The daemon owns the confirmation and the completed-flip record; the client
/// reads them, offers Confirm only when a completed flip is on record, and
/// patches only those fields from the daemon's answer.

final _completed = DateTime.utc(2026, 11, 20, 3, 0);
const _completedWire = '2026-11-20T03:00:00+00:00';

/// A rig whose safety copy holds [safety] over the daemon's defaults, with
/// the confirm and re-arm routes the daemon serves.
FakeSectionRig _rig(Map<String, Object?> safety) =>
    FakeSectionRig({
        'safety': {...rigDefaults['safety']!, ...safety},
      })
      ..actions['safety/first-flip/confirm'] = (
        section: 'safety',
        change: (copy) => {
          ...copy! as Map<String, Object?>,
          'first_flip_confirmed': true,
        },
      )
      ..actions['safety/first-flip/rearm'] = (
        section: 'safety',
        change: (copy) => {
          ...copy! as Map<String, Object?>,
          'first_flip_confirmed': false,
          'first_flip_completed_utc': null,
          'first_flip_pier_side_changed': false,
        },
      );

Future<ProviderContainer> _loaded(FakeSectionRig rig) async {
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  c.listen(safetyPoliciesProvider, (_, _) {});
  await settle(() => c.read(safetyPoliciesProvider).canSave);
  return c;
}

void main() {
  test('confirmFirstFlip patches only the daemon-owned fields', () async {
    final rig = _rig({
      'first_flip_completed_utc': _completedWire,
      'first_flip_pier_side_changed': true,
    });
    final c = await _loaded(rig);
    final loaded = c.read(safetyPoliciesProvider).value!;
    expect(
      loaded.firstFlipCompletedUtc,
      _completed,
      reason: 'the rig\'s completed-flip record is read',
    );
    expect(loaded.firstFlipPierSideChanged, isTrue);
    final n = c.read(safetyPoliciesProvider.notifier);
    n.setResumeDelayMin(42); // typed in the panel; Save not pressed yet

    await n.confirmFirstFlip();

    final shown = c.read(safetyPoliciesProvider);
    final s = shown.value!;
    expect(s.firstFlipConfirmed, isTrue);
    expect(s.firstFlipCompletedUtc, _completed);
    expect(s.resumeDelayMin, 42, reason: 'unsaved edits survive the echo');
    expect(
      shown.etag,
      rig.etagOf('safety'),
      reason: 'the confirm\'s tag is kept, so the next Save is not stale',
    );
    expect(shown.conflict, isFalse);
  });

  test('rearmFirstFlip clears the completed-flip record too', () async {
    final rig = _rig({
      'first_flip_confirmed': true,
      'first_flip_completed_utc': _completedWire,
      'first_flip_pier_side_changed': true,
    });
    final c = await _loaded(rig);

    await c.read(safetyPoliciesProvider.notifier).rearmFirstFlip();

    final s = c.read(safetyPoliciesProvider).value!;
    expect(s.firstFlipConfirmed, isFalse);
    expect(s.firstFlipCompletedUtc, isNull);
    expect(s.firstFlipPierSideChanged, isFalse);
  });

  test('first_flip_completed_utc parses tolerantly', () {
    final read = ProfileApi.safetyPoliciesFromJson({
      'first_flip_completed_utc': '2026-11-20T03:00:00+00:00',
      'first_flip_pier_side_changed': true,
    });
    expect(read.firstFlipCompletedUtc, _completed);
    expect(read.firstFlipPierSideChanged, isTrue);
    expect(
      ProfileApi.safetyPoliciesFromJson({'first_flip_completed_utc': 'garbage'})
          .firstFlipCompletedUtc,
      isNull,
    );
    expect(
      ProfileApi.safetyPoliciesFromJson({'first_flip_completed_utc': 42})
          .firstFlipCompletedUtc,
      isNull,
    );
    final old = ProfileApi.safetyPoliciesFromJson(const {});
    expect(
      old.firstFlipCompletedUtc,
      isNull,
      reason: 'an older daemon has no record',
    );
    expect(old.firstFlipPierSideChanged, isFalse);
  });

  Future<void> pumpPanel(WidgetTester tester, SafetyPolicies seed) async {
    tester.view.physicalSize = const Size(1600, 4000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ProviderScope(
        // A fresh scope per seed: an existing scope keeps its first state.
        key: UniqueKey(),
        overrides: [
          activeServerProvider.overrideWithValue(null),
          safetyFromRig(seed),
        ],
        child: const MaterialApp(home: Scaffold(body: SafetyPoliciesPanel())),
      ),
    );
    await tester.pumpAndSettle();
  }

  bool confirmEnabled(WidgetTester tester) => tester
      .widget<FilledButton>(find.byKey(const Key('confirm_first_flip')))
      .enabled;

  bool rearmEnabled(WidgetTester tester) => tester
      .widget<OutlinedButton>(find.byKey(const Key('rearm_first_flip')))
      .enabled;

  testWidgets('Re-arm can drop a completed flip that is not confirmed yet', (
    tester,
  ) async {
    await pumpPanel(tester, const SafetyPolicies());
    expect(rearmEnabled(tester), isFalse, reason: 'nothing to re-arm');

    await pumpPanel(tester, SafetyPolicies(firstFlipCompletedUtc: _completed));
    expect(
      rearmEnabled(tester),
      isTrue,
      reason: 'a flip recorded before a rig change must not stay confirmable',
    );

    await pumpPanel(
      tester,
      SafetyPolicies(
        firstFlipConfirmed: true,
        firstFlipCompletedUtc: _completed,
      ),
    );
    expect(rearmEnabled(tester), isTrue);
  });

  testWidgets('Confirm is enabled only after a completed flip', (tester) async {
    await pumpPanel(tester, const SafetyPolicies());
    expect(confirmEnabled(tester), isFalse, reason: 'no completed flip yet');
    expect(
      find.textContaining('every flip alerts and waits 60 s'),
      findsOneWidget,
    );

    await pumpPanel(
      tester,
      SafetyPolicies(
        firstFlipCompletedUtc: _completed,
        firstFlipPierSideChanged: true,
      ),
    );
    expect(confirmEnabled(tester), isTrue);
    expect(find.textContaining('pier side changed'), findsOneWidget);

    await pumpPanel(tester, SafetyPolicies(firstFlipCompletedUtc: _completed));
    expect(confirmEnabled(tester), isTrue);
    expect(find.textContaining('check by eye'), findsOneWidget);

    await pumpPanel(
      tester,
      SafetyPolicies(
        firstFlipConfirmed: true,
        firstFlipCompletedUtc: _completed,
      ),
    );
    expect(confirmEnabled(tester), isFalse, reason: 'already confirmed');
  });
}
