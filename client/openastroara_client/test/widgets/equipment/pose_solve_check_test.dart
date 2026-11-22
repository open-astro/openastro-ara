import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/pose_solve_check.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/pose_solve_check_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/equipment/pose_solve_check_state.dart';
import 'package:openastroara/widgets/equipment/pose_solve_check.dart';

/// #2173 — the "Check position with a plate solve" button and its result texts.

/// A mount whose position is unverified (or not), with a refresh that counts.
class _Mount extends MountNotifier {
  _Mount({required this.unverified});
  final bool unverified;

  @override
  Future<MountStatus?> build() async => MountStatus.fromJson({
    'device_id': 'm',
    'name': 'HAE29C',
    'state': 'connected',
    'capabilities': {'can_find_home': true, 'can_slew': true},
    'runtime': {
      'state': 'idle',
      'tracking': false,
      'parked': false,
      'at_home': false,
      'position_unverified': unverified,
      'position_unverified_reason': unverified ? 'at_home_on_connect' : null,
    },
  });

  @override
  Future<void> refresh([EquipmentDeviceClient<MountStatus>? client]) async {}
}

class _Client implements PoseSolveCheckClient {
  _Client(this.available, {this.results = const []});
  PoseSolveAvailability available;
  final List<PoseSolveCheckResult> results;
  final calls = <String>[];

  @override
  Future<PoseSolveAvailability> availability() async => available;

  @override
  Future<PoseSolveCheckResult> check() async {
    calls.add('check');
    return results[calls.length - 1];
  }

  @override
  Future<PoseSolveCheckResult> sync(String checkId) async {
    calls.add('sync $checkId');
    return results[calls.length - 1];
  }

  @override
  void close() {}
}

const _available = PoseSolveAvailability(
  available: true,
  camera: PoseSolveCamera.main,
  unavailableReason: null,
);

PoseSolveCheckResult _result(
  PoseSolveOutcome outcome, {
  PoseSolveCamera camera = PoseSolveCamera.main,
  String? checkId,
}) => PoseSolveCheckResult(
  outcome: outcome,
  camera: camera,
  frame: 'JNOW',
  mountRaHours: 5.5,
  mountDecDegrees: 90.0,
  solvedRaHours: 6.0,
  solvedDecDegrees: 69.5,
  separationDegrees: 20.5,
  checkId: checkId,
  detail: 'English detail',
);

Future<void> _pump(
  WidgetTester tester,
  _Client client, {
  bool unverified = true,
  bool simple = false,
}) async {
  // A fresh scope each time: a ProviderScope keeps its first overrides.
  await tester.pumpWidget(const SizedBox());
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        mountProvider.overrideWith(() => _Mount(unverified: unverified)),
        poseSolveCheckApiProvider.overrideWithValue(client),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: SingleChildScrollView(
            child: simple ? const SimpleMountWindow() : const PoseSolveCheck(),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
}

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  testWidgets(
    'the button shows only while the rig says the check can run (#2173)',
    (tester) async {
      await _pump(tester, _Client(_available));
      expect(find.byKey(const Key('pose-check-button')), findsOneWidget);
      expect(find.text(en.poseCheckButton), findsOneWidget);

      await _pump(
        tester,
        _Client(
          const PoseSolveAvailability(
            available: false,
            camera: PoseSolveCamera.main,
            unavailableReason: 'camera_busy',
          ),
        ),
      );
      expect(find.byKey(const Key('pose-check-button')), findsNothing);

      await _pump(tester, _Client(_available), unverified: false);
      expect(
        find.byKey(const Key('pose-check-button')),
        findsNothing,
        reason: 'a trusted mount has nothing to check',
      );
    },
  );

  testWidgets('Simple Mode shows the button on the unverified row (#2173)', (
    tester,
  ) async {
    await _pump(tester, _Client(_available), simple: true);
    expect(
      find.descendant(
        of: find.byKey(const Key('simple-mount-position-unverified')),
        matching: find.byKey(const Key('pose-check-button')),
      ),
      findsOneWidget,
    );
  });

  testWidgets(
    'a mismatch says both positions and offers one Sync with its check id (#2173)',
    (tester) async {
      final client = _Client(
        _available,
        results: [
          _result(PoseSolveOutcome.mismatch, checkId: 'offer-1'),
          _result(PoseSolveOutcome.trusted),
        ],
      );
      await _pump(tester, client);

      await tester.tap(find.byKey(const Key('pose-check-button')));
      await tester.pump();
      await tester.pump();

      expect(
        find.text(poseSolveResultText(en, client.results[0])),
        findsOneWidget,
      );
      expect(find.byKey(const Key('pose-check-sync')), findsOneWidget);
      await tester.tap(find.byKey(const Key('pose-check-sync')));
      await tester.pump();
      await tester.pump();
      expect(client.calls, ['check', 'sync offer-1']);
      expect(find.byKey(const Key('pose-check-sync')), findsNothing);
    },
  );

  test('the result texts name the camera and the positions (en)', () {
    expect(
      poseSolveResultText(en, _result(PoseSolveOutcome.mismatch)),
      'The mount reads RA 05h 30m 00s, Dec +90° 00′ 00″, the sky says '
      'RA 06h 00m 00s, Dec +69° 30′ 00″ (Δ 20.5°). Checked with the main camera.',
    );
    expect(
      poseSolveResultText(
        en,
        _result(PoseSolveOutcome.trusted, camera: PoseSolveCamera.guide),
      ),
      'The sky agrees with the mount (checked with the guide camera): its '
      'position is trusted again.',
    );
    expect(
      poseSolveResultText(en, _result(PoseSolveOutcome.pierSideDisagrees)),
      contains('pier side does not match'),
    );
    expect(
      poseSolveResultText(en, _result(PoseSolveOutcome.solveFailed)),
      contains('did not solve'),
    );
    expect(
      poseSolveResultText(en, _result(PoseSolveOutcome.syncNotTaken)),
      contains('After the Sync'),
    );
  });

  test('the result texts read in pt-BR, with a decimal comma', () {
    expect(
      poseSolveResultText(pt, _result(PoseSolveOutcome.mismatch)),
      'A montagem indica AR 05h 30m 00s, Dec +90° 00′ 00″, o céu diz '
      'AR 06h 00m 00s, Dec +69° 30′ 00″ (Δ 20,5°). Verificado com a câmera principal.',
    );
    expect(
      poseSolveResultText(
        pt,
        _result(PoseSolveOutcome.trusted, camera: PoseSolveCamera.guide),
      ),
      contains('câmera de guiagem'),
    );
    for (final outcome in PoseSolveOutcome.values) {
      expect(
        poseSolveResultText(pt, _result(outcome)),
        isNot(poseSolveResultText(en, _result(outcome))),
        reason: '$outcome has its own pt-BR sentence',
      );
    }
  });

  test('a result missing a key fails the read (#2133)', () {
    final wire = {
      'outcome': 'trusted',
      'camera': 'main',
      'frame': 'JNOW',
      'mount_ra_hours': 1.0,
      'mount_dec_degrees': 2.0,
      'solved_ra_hours': 1.0,
      'solved_dec_degrees': 2.0,
      'separation_degrees': 0.1,
      'check_id': null,
      'detail': 'ok',
    };
    expect(
      PoseSolveCheckResult.fromJson(wire).outcome,
      PoseSolveOutcome.trusted,
    );
    expect(
      () => PoseSolveCheckResult.fromJson({...wire}..remove('check_id')),
      throwsFormatException,
    );
    expect(
      () =>
          PoseSolveAvailability.fromJson({'available': true, 'camera': 'main'}),
      throwsFormatException,
    );
  });
}
