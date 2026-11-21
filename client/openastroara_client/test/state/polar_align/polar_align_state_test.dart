import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
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

    test('started carries the run camera and progress the noisy flag; a new run clears it (#2082)', () {
      final started = foldPolarAlignEvent(
        const PolarAlignLive(),
        _event(PolarAlignWsEvents.started, {'state': 'seeding', 'stage': 'fine', 'camera': 'main'}),
      )!;
      expect(started.camera, PolarAlignCameras.main);
      expect(started.usesMainCamera, isTrue);
      final noisy = foldPolarAlignEvent(
        started,
        _event(PolarAlignWsEvents.progress, {
          'iteration': 0,
          'altitude_error_arcmin': 1.0,
          'azimuth_error_arcmin': 1.0,
          'total_error_arcmin': 1.41,
          'measurement_noisy': true,
        }),
      )!;
      expect(noisy.measurementNoisy, isTrue);
      expect(noisy.camera, PolarAlignCameras.main, reason: 'progress keeps the run camera');
      final next = foldPolarAlignEvent(noisy, _event(PolarAlignWsEvents.started, {'stage': 'fine', 'camera': 'guide'}))!;
      expect((next.measurementNoisy, next.usesMainCamera), (false, false));
      final fromStatus = liveFromStatus(PolarAlignStatus.fromJson({
        'state': 'adjusting', 'stage': 'fine', 'camera': 'main', 'measurement_noisy': true,
      }));
      expect((fromStatus.camera, fromStatus.measurementNoisy), (PolarAlignCameras.main, true));
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

    test('frame_started marks a frame in progress; frame_complete records it', () {
      final at = DateTime.utc(2026, 10, 3, 3);
      final started = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.adjusting),
        _event(PolarAlignWsEvents.frameStarted, {'frame_id': 'live-3', 'exposure_seconds': 1.5}),
        now: () => at,
      )!;
      expect(started.frameInProgress, isTrue);
      expect(started.frameStartedAt, at);
      expect(started.frameExposureSeconds, 1.5);

      final done = foldPolarAlignEvent(
        started,
        _event(PolarAlignWsEvents.frameComplete, {
          'frame_id': 'live-3',
          'solved': true,
          'consecutive_solve_failures': 0,
          'exposure_seconds': 1.5,
          'capture_ms': 2100,
          'solve_ms': 480,
          'ra_deg': 138.6667,
          'dec_deg': 87.1822,
        }),
      )!;
      expect(done.frameInProgress, isFalse);
      expect(done.frameExposureSeconds, isNull);
      expect(done.lastFrame!.frameId, 'live-3');
      expect(done.lastFrame!.captureMs, 2100);
      expect(done.lastFrame!.solveMs, 480);
      expect(done.lastFrame!.decDeg, 87.1822);
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

    // #2107 the stop's name rides on the error, and never carries over to the
    // next error that has none.
    test('a stop from outside keeps stopped_by, and the next error drops it', () {
      final stopped = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.error, {
          'reason': 'slew_aborted_externally',
          'message': 'the mount was stopped from the direction pad',
          'stopped_by': 'pad_stop',
        }),
      )!;
      expect(stopped.errorStoppedBy, 'pad_stop');
      final next = foldPolarAlignEvent(
        stopped,
        _event(PolarAlignWsEvents.error, {'reason': 'mount_did_not_stop', 'message': 'cut power'}),
      )!;
      expect(next.errorStoppedBy, isNull);
    });

    // #2066 an error whose run could not confirm its stop says so, and the
    // next error that does not say so drops it.
    test('stop_unconfirmed rides on the error, and the next error drops it', () {
      final unconfirmed = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.error, {
          'reason': 'slew_aborted_externally',
          'message': 'stopped from the direction pad. Cut the mount\'s power now',
          'stopped_by': 'pad_stop',
          'stop_unconfirmed': true,
        }),
      )!;
      expect(unconfirmed.errorStopUnconfirmed, isTrue);
      final next = foldPolarAlignEvent(
        unconfirmed,
        _event(PolarAlignWsEvents.error, {
          'reason': 'slew_aborted_externally',
          'message': 'stopped (Stop Mount)',
          'stopped_by': 'user_request',
        }),
      )!;
      expect(next.errorStopUnconfirmed, isFalse);
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
      expect(fresh.zone, 'green', reason: "the snapshot's own error, not a previous run's zone");
      expect(fresh.consecutiveSolveFailures, 0);
      expect(fresh.iteration, 0);
    });

    test('reopening the panel after aligning keeps the Checklist step done', () {
      // The panel resyncs from REST on open; the snapshot carries the error but
      // no zone, so the green zone (and the Checklist tick) used to vanish.
      final reopened = liveFromStatus(PolarAlignStatus.fromJson(const {
        'state': 'stopped',
        'current_error_arcmin': 3.2,
      }));
      expect(polarAlignStepState(reopened), SetupStepState.done);
      expect(liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'stopped', 'current_error_arcmin': 30.0})).zone,
          'yellow');
      expect(liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'stopped', 'current_error_arcmin': 90.0})).zone,
          'red');
    });

    test('a null-error snapshot clears previous error values', () {
      final idle = liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'idle'}));
      expect(idle.zone, isNull);
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

  group('stages', () {
    test('started carries the stage and opening state', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(),
        _event(PolarAlignWsEvents.started, {'state': 'homing', 'stage': 'coarse'}),
      )!;
      expect(folded.phase, PolarAlignStates.homing);
      expect(folded.stage, PolarAlignStages.coarse);
      expect(folded.isActive, isTrue);
    });

    test('needs_home parks the view on the user; stage clears it', () {
      final waiting = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.homing, stage: PolarAlignStages.coarse),
        _event(PolarAlignWsEvents.needsHome, {'message': 'put it at Home'}),
      )!;
      expect(waiting.waitingForHome, isTrue);
      final moved = foldPolarAlignEvent(
        waiting,
        _event(PolarAlignWsEvents.stage, {'stage': 'fine', 'state': 'seeding', 'check_solves_done': 0}),
      )!;
      expect(moved.waitingForHome, isFalse);
      expect(moved.stage, PolarAlignStages.fine);
      expect(moved.phase, PolarAlignStates.seeding);
    });

    test('a new stage clears the readout of the stage before it', () {
      // Rough reached green; the hand-off to fine must not carry that green
      // through the fine stage's homing and seeding (or a Stop from them).
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(
            stage: PolarAlignStages.coarse,
            phase: PolarAlignStates.adjusting,
            altErrorArcmin: 0.3,
            azErrorArcmin: -0.2,
            totalErrorArcmin: 0.36,
            zone: 'green'),
        _event(PolarAlignWsEvents.stage, {'stage': 'fine', 'state': 'homing'}),
      )!;
      expect(folded.stage, PolarAlignStages.fine);
      expect(folded.zone, isNull);
      expect(folded.totalErrorArcmin, isNull);
      expect(folded.altErrorArcmin, isNull);
      // The same stage's own state changes keep the readout.
      final same = foldPolarAlignEvent(
        const PolarAlignLive(stage: PolarAlignStages.check, phase: PolarAlignStates.homing, zone: 'green'),
        _event(PolarAlignWsEvents.stage, {'stage': 'check', 'state': 'checking'}),
      )!;
      expect(same.zone, 'green');
    });

    test('stage counts the check solves', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(stage: PolarAlignStages.check, phase: PolarAlignStates.checking),
        _event(PolarAlignWsEvents.stage, {'stage': 'check', 'state': 'checking', 'check_solves_done': 2}),
      )!;
      expect(folded.checkSolvesDone, 2);
    });

    test('checked is terminal and carries the result into the error fields', () {
      final folded = foldPolarAlignEvent(
        const PolarAlignLive(stage: PolarAlignStages.check, phase: PolarAlignStates.checking),
        _event(PolarAlignWsEvents.checked, {
          'altitude_error_arcmin': -0.9,
          'azimuth_error_arcmin': 0.4,
          'total_error_arcmin': 0.98,
          'cone_half_angle_deg': 5.0,
          'turn_mismatch_deg': 0.01,
          'fine_error_arcmin': 1.5,
          'fine_delta_arcmin': 0.52,
          'verdict': 'excellent',
        }),
      )!;
      expect(folded.phase, PolarAlignStates.checked);
      expect(folded.isActive, isFalse);
      expect(folded.checkResult!.verdict, 'excellent');
      expect(folded.checkResult!.fineDeltaArcmin, 0.52);
      expect(folded.totalErrorArcmin, 0.98);
      expect(folded.zone, 'green');
      expect(polarAlignStepState(folded), SetupStepState.done);
    });

    test('a checked event and a status check result carry the noisy flag (#2082)', () {
      final noisy = foldPolarAlignEvent(
        const PolarAlignLive(stage: PolarAlignStages.check, phase: PolarAlignStates.checking),
        _event(PolarAlignWsEvents.checked, {'total_error_arcmin': 0.98, 'verdict': 'excellent', 'noisy': true}),
      )!;
      expect(noisy.checkResult!.noisy, isTrue);
      final steady = foldPolarAlignEvent(
        const PolarAlignLive(stage: PolarAlignStages.check, phase: PolarAlignStates.checking),
        _event(PolarAlignWsEvents.checked, {'total_error_arcmin': 0.98, 'verdict': 'excellent'}),
      )!;
      expect(steady.checkResult!.noisy, isFalse, reason: 'an older daemon sends no key');
      final fromStatus = liveFromStatus(PolarAlignStatus.fromJson(const {
        'state': 'checked',
        'check_result': {'total_error_arcmin': 0.98, 'verdict': 'excellent', 'noisy': true},
      }));
      expect(fromStatus.checkResult!.noisy, isTrue);
    });

    test('progress carries the stage; an older daemon leaves it null', () {
      final coarse = foldPolarAlignEvent(
        const PolarAlignLive(),
        _event(PolarAlignWsEvents.progress, {'stage': 'coarse', 'total_error_arcmin': 70.0}),
      )!;
      expect(coarse.stage, PolarAlignStages.coarse);
      final old = foldPolarAlignEvent(
        const PolarAlignLive(),
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 70.0}),
      )!;
      expect(old.stage, isNull);
    });

    test('each progress event says the readout caution afresh; a snapshot carries it too (#2072)', () {
      final weak = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 4.0, 'readout_caution': 'altitude_weak'}),
      )!;
      expect(weak.readoutCaution, PolarAlignReadoutCautions.altitudeWeak);
      final again = foldPolarAlignEvent(
        weak,
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 3.0, 'readout_caution': 'altitude_weak'}),
      )!;
      expect(again.readoutCaution, PolarAlignReadoutCautions.altitudeWeak);
      expect(again.totalErrorArcmin, 3.0);
      final sound = foldPolarAlignEvent(
        again,
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 2.0, 'readout_caution': null}),
      )!;
      expect(sound.readoutCaution, isNull, reason: 'a new seed with a sound readout clears it');
      expect(
        liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'adjusting', 'readout_caution': 'altitude_weak'}))
            .readoutCaution,
        PolarAlignReadoutCautions.altitudeWeak,
      );
      expect(liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'adjusting'})).readoutCaution, isNull);
    });

    test('each progress event says the Dec-spread warning afresh; a snapshot and a verdict carry it too (#2081)', () {
      final spread = foldPolarAlignEvent(
        const PolarAlignLive(phase: PolarAlignStates.seeding),
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 4.0, 'dec_spread_arcsec': 3.1}),
      )!;
      expect(spread.decSpreadArcsec, 3.1);
      final held = foldPolarAlignEvent(
        spread,
        _event(PolarAlignWsEvents.progress, {'total_error_arcmin': 2.0, 'dec_spread_arcsec': null}),
      )!;
      expect(held.decSpreadArcsec, isNull, reason: 'a new seed whose Dec held clears it');
      expect(liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'adjusting', 'dec_spread_arcsec': 2.6})).decSpreadArcsec, 2.6);
      expect(
        PolarAlignCheckResult.fromJson(const {'total_error_arcmin': 1.0, 'dec_spread_arcsec': 4.2}).decSpreadArcsec,
        4.2,
      );
      expect(PolarAlignCheckResult.fromJson(const {'total_error_arcmin': 1.0}).decSpreadArcsec, isNull);
    });

    test('the status snapshot hydrates stage, home wait, solves and the check result', () {
      final live = liveFromStatus(PolarAlignStatus.fromJson(const {
        'state': 'checked',
        'stage': 'check',
        'waiting_for_home': false,
        'check_solves_done': 3,
        'current_error_arcmin': 0.98,
        'check_result': {'total_error_arcmin': 0.98, 'verdict': 'excellent'},
      }));
      expect(live.stage, PolarAlignStages.check);
      expect(live.checkSolvesDone, 3);
      expect(live.checkResult!.verdict, 'excellent');
      final homing = liveFromStatus(PolarAlignStatus.fromJson(const {'state': 'homing', 'waiting_for_home': true}));
      expect(homing.waitingForHome, isTrue);
      expect(homing.isActive, isTrue);
    });
  });
}
