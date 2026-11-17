import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/plate_solve_result.dart';
import 'package:openastroara/models/verify_position_result.dart';
import 'package:openastroara/screens/simple/simple_mount_controls.dart';
import 'package:openastroara/services/plate_solve_api.dart';
import 'package:openastroara/services/refusal.dart';
import 'package:openastroara/state/app_locale_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/plate_solve/plate_solve_api_state.dart';
import 'package:openastroara/util/friendly_error.dart';

/// ADR 0022 rule 5 — the Mount window's Check picks its fix from the typed
/// Refusal cause, never from a status code or the words of the message.
class _Mount extends MountNotifier {
  @override
  Future<MountStatus?> build() async => null;
}

class _Solver implements PlateSolveApi {
  _Solver(this.error);
  final Object error;

  @override
  Future<VerifyPositionResult> verifyPosition({
    double? exposureSeconds,
  }) async => throw error;

  @override
  Future<PlateSolveResult> solve(String id) => throw UnimplementedError();
}

final _en = lookupAppLocalizations(const Locale('en'));

/// The app's sentence for [r]; never the generic one for a known cause.
String _words(Refusal r) {
  final words = failureReason(_en, r);
  expect(
    words,
    isNot(failureReason(_en, Exception('x'))),
    reason: 'a known cause has its own sentence',
  );
  return words;
}

// Every sentence names the optics, so only the cause can tell them apart.
const _opticsWords =
    'telescope focal length and camera pixel size: set them in Optics.';

Future<SimplePointing> _check(Refusal refusal) async {
  final c = ProviderContainer(
    overrides: [
      mountProvider.overrideWith(_Mount.new),
      plateSolveApiProvider.overrideWithValue(_Solver(refusal)),
    ],
  );
  addTearDown(c.dispose);
  c.read(appLocaleProvider.notifier).set(const Locale('en'));
  final pointing = c.read(simplePointingProvider.notifier)..opened();
  await pointing.check();
  return c.read(simplePointingProvider);
}

Refusal _r(
  RefusalCause? cause, {
  String? reason,
  Map<String, Object?> parameters = const {},
}) => Refusal(
  cause: cause,
  reason: reason ?? cause?.token,
  detail: _opticsWords,
  parameters: parameters,
);

void main() {
  test('optics not set offers the Optics settings', () async {
    final r = _r(
      RefusalCause.opticsNotSet,
      parameters: const {
        'missing': ['focal_length'],
      },
    );
    final s = await _check(r);
    expect(s.phase, SimplePointingPhase.error);
    expect(s.fix?.$1, 'img.optics');
    expect(s.error, _words(r), reason: 'a known cause is worded by the app');
  });

  test('a solver not set up offers Plate solving settings even when the words name the optics', () async {
    final s = await _check(_r(RefusalCause.solverNotSet));
    expect(s.fix?.$1, 'img.platesolve');
  });

  test('no camera offers the Camera settings', () async {
    final r = _r(RefusalCause.noCamera);
    final s = await _check(r);
    expect(s.fix?.$1, 'eq.camera');
    expect(s.error, _words(r));
  });

  test('a busy camera says so and offers no settings', () async {
    final s = await _check(_r(RefusalCause.cameraBusy));
    expect(s.fix, isNull);
    expect(s.error, _en.pointingCameraBusy);
  });

  test('a stuck capture gate offers no settings', () async {
    final r = _r(RefusalCause.gateStuck);
    final s = await _check(r);
    expect(s.fix, isNull);
    expect(s.error, _words(r));
  });

  test('a missing storage disk offers no settings, since plugging it back in fixes it', () async {
    final r = _r(RefusalCause.storageUnavailable);
    final s = await _check(r);
    expect(s.fix, isNull);
    expect(s.error, _words(r));
  });

  test(
    'a cause this app does not know shows the daemon detail and guesses no fix',
    () async {
      final s = await _check(_r(null, reason: 'dome-closed'));
      expect(s.error, _opticsWords);
      expect(s.fix, isNull, reason: 'no fix is guessed from the words');
    },
  );
}
