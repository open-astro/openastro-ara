import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';

import '../support/polar_align_section.dart';
import '../support/polar_preflight.dart';

/// A rig whose pre-flight list follows the profile's polar-align section
/// ([section]): the clearance check reads the connected mount's entry.
class _Rig implements PolarAlignClient {
  final section = FakePolarAlignSection(loaded: true);
  int preflightReads = 0;

  @override
  Future<PolarAlignPreflight?> preflight({String? stage}) async {
    preflightReads++;
    return rigPreflightFor(section.initial);
  }

  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start(PolarAlignStartPlan plan) async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async {}
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _Idle extends PolarAlignLiveNotifier {
  @override
  PolarAlignLive build() => const PolarAlignLive();
}

Future<_Rig> _pump(WidgetTester tester) async {
  final rig = _Rig();
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...polarAlignSectionOverrides(rig.section),
        polarAlignApiProvider.overrideWithValue(rig),
        polarAlignLiveProvider.overrideWith(_Idle.new),
      ],
      child: const MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel())),
      ),
    ),
  );
  await tester.pumpAndSettle();
  return rig;
}

VoidCallback? _onPressed(WidgetTester tester, String key) =>
    tester.widget<ButtonStyleButton>(find.byKey(Key(key))).onPressed;

void main() {
  testWidgets('Start is disabled while a pre-flight check fails', (
    tester,
  ) async {
    final rig = await _pump(tester);

    expect(
      find.byKey(const Key('polar-preflight-check-clearance_confirmed')),
      findsOneWidget,
      reason: 'the failing check is listed on the Start card',
    );
    expect(_onPressed(tester, 'polar-align-start'), isNull);
    expect(
      _onPressed(tester, 'polar-align-start-check'),
      isNull,
      reason: 'Check is a start too',
    );

    // The rig's list passes now (the mount's entry confirmed elsewhere): Start is back.
    rig.section.initial = rigPolarAlign.withMountClearance(
      preflightMountKey,
      const PolarAlignMountClearance(
        maxCwAngleDeg: 60,
        clearanceConfirmed: true,
      ),
    );
    await tester.tap(find.byKey(const Key('polar-preflight-refresh')));
    await tester.pumpAndSettle();
    expect(_onPressed(tester, 'polar-align-start'), isNotNull);
  });

  testWidgets(
    'The clearance dialog confirms the mount and its check turns ok',
    (tester) async {
      final rig = await _pump(tester);

      await tester.tap(find.byKey(const Key('polar-preflight-clearance')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('polar-clearance-dialog')), findsOneWidget);
      expect(
        find.text('Counterweight limit: 60° from counterweight-down'),
        findsOneWidget,
        reason: "pre-filled with the rig's limit for this mount",
      );
      expect(
        _onPressed(tester, 'polar-clearance-confirm'),
        isNull,
        reason: 'Confirm waits for the tick',
      );

      await tester.tap(find.byKey(const Key('polar-clearance-checked')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('polar-clearance-confirm')));
      await tester.pumpAndSettle();

      expect(rig.section.puts, hasLength(1));
      expect(
        rig.section.puts.single.mountClearance[preflightMountKey],
        const PolarAlignMountClearance(
          maxCwAngleDeg: 60,
          clearanceConfirmed: true,
        ),
        reason: "the mount's own entry, confirmed",
      );
      expect(
        find.text('Change clearance…'),
        findsOneWidget,
        reason: 'the clearance check passes now',
      );
      expect(
        find.byKey(const Key('polar-preflight-check-clearance_confirmed')),
        findsNothing,
        reason: 'a passing check folds away with the others',
      );
      expect(_onPressed(tester, 'polar-align-start'), isNotNull);
    },
  );
}
