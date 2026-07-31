import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';

WsEvent _event(String type, [Map<String, dynamic> payload = const {}]) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 7, 30, 4), seq: 1, payload: payload);

void main() {
  group('foldPolarAlignEvent', () {
    test('non polar-align events are a no-op (null)', () {
      expect(
        foldPolarAlignEvent(const PolarAlignLive(), _event('guider.state')),
        isNull,
      );
    });

    test('started resets to a fresh seeding view', () {
      const stale = PolarAlignLive(
        phase: PolarAlignStates.failed,
        iteration: 12,
        totalErrorArcmin: 42,
        errorReason: 'seed_solve_failed',
      );
      final folded = foldPolarAlignEvent(stale, _event(PolarAlignWsEvents.started))!;
      expect(folded.phase, PolarAlignStates.seeding);
      expect(folded.iteration, 0);
      expect(folded.totalErrorArcmin, isNull);
      expect(folded.errorReason, isNull);
    });

    test('progress carries the errors, zone, and adjusting phase', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.progress, {
          'iteration': 7,
          'altitude_error_arcmin': -12.5,
          'azimuth_error_arcmin': 3.25,
          'total_error_arcmin': 12.92,
          'zone': 'yellow',
          'solved': true,
        }),
      )!;
      expect(folded.phase, PolarAlignStates.adjusting);
      expect(folded.iteration, 7);
      expect(folded.altErrorArcmin, -12.5);
      expect(folded.azErrorArcmin, 3.25);
      expect(folded.totalErrorArcmin, 12.92);
      expect(folded.zone, 'yellow');
      expect(folded.consecutiveSolveFailures, 0, reason: 'a solved iteration clears the retry streak');
    });

    test('progress after paused resumes adjusting', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.paused, consecutiveSolveFailures: 5),
        _event(PolarAlignWsEvents.progress, {
          'iteration': 8,
          'altitude_error_arcmin': 1.0,
          'azimuth_error_arcmin': 1.0,
          'total_error_arcmin': 1.41,
          'zone': 'green',
        }),
      )!;
      expect(folded.phase, PolarAlignStates.adjusting);
      expect(folded.consecutiveSolveFailures, 0);
    });

    test('frame_complete tracks the failed-solve streak without touching errors', () {
      const current = PolarAlignLive(
        phase: PolarAlignStates.adjusting,
        totalErrorArcmin: 9.9,
      );
      final folded = foldPolarAlignEvent(
        current,
        _event(PolarAlignWsEvents.frameComplete,
            {'frame_id': 'live-9', 'solved': false, 'consecutive_solve_failures': 3}),
      )!;
      expect(folded.consecutiveSolveFailures, 3);
      expect(folded.totalErrorArcmin, 9.9);
      expect(folded.phase, PolarAlignStates.adjusting);
    });

    test('paused and stopped flip only the phase', () {
      const current = PolarAlignLive(
          phase: PolarAlignStates.adjusting, totalErrorArcmin: 2.0);
      expect(
        foldPolarAlignEvent(current, _event(PolarAlignWsEvents.paused))!.phase,
        PolarAlignStates.paused,
      );
      final stopped =
          foldPolarAlignEvent(current, _event(PolarAlignWsEvents.stopped))!;
      expect(stopped.phase, PolarAlignStates.stopped);
      expect(stopped.totalErrorArcmin, 2.0, reason: 'final error stays visible after stop');
    });

    test('error carries reason + message and the failed phase', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.error,
            {'reason': 'axis_fit_failed', 'message': 'the fitted axis is far from the pole'}),
      )!;
      expect(folded.phase, PolarAlignStates.failed);
      expect(folded.errorReason, 'axis_fit_failed');
      expect(folded.errorMessage, contains('fitted axis'));
    });
  });

  group('liveFromStatus', () {
    test('fully rebuilds the view — stale fields from a previous run are cleared', () {
      // A failed run's leftovers must not survive a REST resync into a new run
      // (the resync exists because the WS stream missed the started event).
      final fresh = liveFromStatus(PolarAlignStatus.fromJson(const {
        'state': 'adjusting',
        'current_error_arcmin': 5.0,
        'azimuth_adjustment_arcmin': 3.0,
        'altitude_adjustment_arcmin': 4.0,
      }));
      expect(fresh.phase, PolarAlignStates.adjusting);
      expect(fresh.totalErrorArcmin, 5.0);
      expect(fresh.errorReason, isNull);
      expect(fresh.zone, isNull);
      expect(fresh.consecutiveSolveFailures, 0);
      expect(fresh.iteration, 0);
    });

    test('a null-error snapshot clears previous error values', () {
      final idle = liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'idle'}));
      expect(idle.totalErrorArcmin, isNull);
      expect(idle.altErrorArcmin, isNull);
      expect(idle.azErrorArcmin, isNull);
    });
  });

  group('PolarAlignStatus', () {
    test('parses a full status payload', () {
      final status = PolarAlignStatus.fromJson(const {
        'state': 'adjusting',
        'current_error_arcmin': 27.3,
        'azimuth_adjustment_arcmin': -23.4,
        'altitude_adjustment_arcmin': 14.2,
        'frames_captured': 47,
        'last_frame_id': 'live-45',
      });
      expect(status.state, PolarAlignStates.adjusting);
      expect(status.currentErrorArcmin, 27.3);
      expect(status.azimuthAdjustmentArcmin, -23.4);
      expect(status.altitudeAdjustmentArcmin, 14.2);
      expect(status.framesCaptured, 47);
      expect(status.lastFrameId, 'live-45');
      expect(status.isActive, isTrue);
    });

    test('tolerates a minimal payload and reports inactive states', () {
      final status = PolarAlignStatus.fromJson(const {'state': 'stopped'});
      expect(status.currentErrorArcmin, isNull);
      expect(status.framesCaptured, 0);
      expect(status.isActive, isFalse);
    });
  });

  group('PolarAlignSettings', () {
    test('round-trips through json with playbook defaults for missing keys', () {
      const defaults = PolarAlignSettings();
      expect(PolarAlignSettings.fromJson(const {}).toJson(), defaults.toJson());

      const custom = PolarAlignSettings(
        exposureSeconds: 0.5,
        binning: 2,
        targetToleranceArcmin: 0.5,
        seedRotationDeg: 60,
        loopCadenceMs: 750,
        settleSeconds: 3,
      );
      expect(PolarAlignSettings.fromJson(custom.toJson()).toJson(), custom.toJson());
    });
  });
}
