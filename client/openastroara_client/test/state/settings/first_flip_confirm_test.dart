import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_policies_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

/// #1589 — a meridian flip is confirmed by the user, after a flip completed.
/// The daemon owns the confirmation and the completed-flip record; the client
/// reads them, offers Confirm only when a completed flip is on record, and
/// patches only those fields from the daemon's answer.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi(this.echo) : super(const AraServer(hostname: 'test', port: 1));
  final SafetyPolicies echo;
  int confirms = 0;
  int rearms = 0;

  @override
  Future<SafetyPolicies> confirmFirstFlip() async {
    confirms++;
    return echo;
  }

  @override
  Future<SafetyPolicies> rearmFirstFlip() async {
    rearms++;
    return echo;
  }
}

class _Seeded extends SafetyPoliciesNotifier {
  _Seeded(this.seed);
  final SafetyPolicies seed;
  @override
  SafetyPolicies build() => seed;
}

final _completed = DateTime.utc(2026, 11, 20, 3, 0);

void main() {
  test('confirmFirstFlip patches only the daemon-owned fields', () async {
    final c = ProviderContainer(
      overrides: [
        safetyPoliciesProvider.overrideWith(
          () => _Seeded(
            SafetyPolicies(
              resumeDelayMin: 42,
              firstFlipCompletedUtc: _completed,
              firstFlipPierSideChanged: true,
            ),
          ),
        ),
      ],
    );
    addTearDown(c.dispose);
    // The daemon's echo is the PERSISTED policies: resume delay 10, not the staged 42.
    final api = _FakeProfileApi(
      SafetyPolicies(
        firstFlipConfirmed: true,
        firstFlipCompletedUtc: _completed,
        firstFlipPierSideChanged: true,
      ),
    );

    await c.read(safetyPoliciesProvider.notifier).confirmFirstFlip(api);

    final s = c.read(safetyPoliciesProvider);
    expect(api.confirms, 1);
    expect(s.firstFlipConfirmed, isTrue);
    expect(s.firstFlipCompletedUtc, _completed);
    expect(s.resumeDelayMin, 42, reason: 'unsaved edits survive the echo');
  });

  test('rearmFirstFlip clears the completed-flip record too', () async {
    final c = ProviderContainer(
      overrides: [
        safetyPoliciesProvider.overrideWith(
          () => _Seeded(
            SafetyPolicies(
              firstFlipConfirmed: true,
              firstFlipCompletedUtc: _completed,
              firstFlipPierSideChanged: true,
            ),
          ),
        ),
      ],
    );
    addTearDown(c.dispose);

    await c
        .read(safetyPoliciesProvider.notifier)
        .rearmFirstFlip(_FakeProfileApi(const SafetyPolicies()));

    final s = c.read(safetyPoliciesProvider);
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
    expect(old.firstFlipCompletedUtc, isNull, reason: 'an older daemon has no record');
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
          safetyPoliciesProvider.overrideWith(() => _Seeded(seed)),
        ],
        child: const MaterialApp(home: Scaffold(body: SafetyPoliciesPanel())),
      ),
    );
    await tester.pumpAndSettle();
  }

  bool confirmEnabled(WidgetTester tester) => tester
      .widget<FilledButton>(find.byKey(const Key('confirm_first_flip')))
      .enabled;

  testWidgets('Confirm is enabled only after a completed flip', (tester) async {
    await pumpPanel(tester, const SafetyPolicies());
    expect(confirmEnabled(tester), isFalse, reason: 'no completed flip yet');
    expect(find.textContaining('every flip alerts and waits 60 s'), findsOneWidget);

    await pumpPanel(
      tester,
      SafetyPolicies(firstFlipCompletedUtc: _completed, firstFlipPierSideChanged: true),
    );
    expect(confirmEnabled(tester), isTrue);
    expect(find.textContaining('pier side changed'), findsOneWidget);

    await pumpPanel(tester, SafetyPolicies(firstFlipCompletedUtc: _completed));
    expect(confirmEnabled(tester), isTrue);
    expect(find.textContaining('check by eye'), findsOneWidget);

    await pumpPanel(
      tester,
      SafetyPolicies(firstFlipConfirmed: true, firstFlipCompletedUtc: _completed),
    );
    expect(confirmEnabled(tester), isFalse, reason: 'already confirmed');
  });
}
