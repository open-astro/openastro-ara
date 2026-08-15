import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';

void main() {
  group('CaptureProgressNotifier', () {
    late ProviderContainer container;
    late CaptureProgressNotifier n;

    setUp(() {
      container = ProviderContainer();
      addTearDown(container.dispose);
      n = container.read(captureProgressProvider.notifier);
    });

    test('starts idle and resets cleanly', () {
      expect(container.read(captureProgressProvider).phase, CapturePhase.idle);
      n.beginExposing(const Duration(seconds: 3));
      n.reset();
      expect(container.read(captureProgressProvider).phase, CapturePhase.idle);
    });

    test('beginExposing sets the exposing phase with the requested duration',
        () {
      n.beginExposing(const Duration(seconds: 5));
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.exposing);
      expect(p.requestedExposure, const Duration(seconds: 5));
      expect(p.exposureProgressPct, isNull,
          reason: 'null seeds the elapsed-time fallback until the daemon '
              'reports a real percentage');
      expect(p.startedAt, isNotNull);
    });

    test('exposure remaining math from the daemon progress', () {
      n.beginExposing(const Duration(seconds: 10));
      // 25% done → 7.5s left.
      n.updateExposureProgress(25);
      expect(
          container.read(captureProgressProvider).exposureRemaining,
          const Duration(milliseconds: 7500));
      // 100% → the phase moves to downloading after the min-visible hold.
      fakeAsync((async) {
        n.updateExposureProgress(100);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing,
            reason: 'holds exposing at least the min-visible window');
        async.elapse(kExposingMinVisible);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.downloading);
      });
    });

    test(
        'isCapturing covers only the running phases — terminal display '
        'windows return false', () {
      // Regression: the abort-failure path guards on "still capturing". When
      // Cancel loses the race against a finishing capture, the card is in a
      // terminal phase (done/failed) and the guard must short-circuit — the
      // old isActive-based check stayed true and surfaced a bogus "couldn't
      // abort" error right after a successful shot.
      expect(n.isCapturing, isFalse, reason: 'idle');
      n.beginExposing(const Duration(seconds: 3));
      expect(n.isCapturing, isTrue, reason: 'exposing');
      n.complete('abc', generation: n.state.generation);
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.done);
      expect(p.isActive, isTrue, reason: 'the done card is still displayed');
      expect(n.isCapturing, isFalse,
          reason: 'done is a display window, not a running capture');
      n.beginExposing(const Duration(seconds: 3));
      n.fail('boom', generation: n.state.generation);
      expect(n.isCapturing, isFalse,
          reason: 'failed is a display window, not a running capture');
    });

    test('complete() lands the done phase with the frame id', () {
      n.beginExposing(const Duration(seconds: 3));
      n.complete('abc', generation: n.state.generation);
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.done);
      expect(p.frameId, 'abc');
    });

    test('complete() measures the download and builds a rolling estimate', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 3));
        n.updateExposureProgress(100);
        async.elapse(kExposingMinVisible); // → downloading
        n.complete('abc', generation: n.state.generation);
        async.elapse(kDownloadingMinVisible); // → done (rolling measured)
        final p = container.read(captureProgressProvider);
        expect(p.phase, CapturePhase.done);
        expect(p.rollingDownloadMs, isNotNull);
        expect(p.rollingDownloadMs!, greaterThanOrEqualTo(0));

        // reset() preserves the rolling estimate for the next capture.
        n.reset();
        expect(container.read(captureProgressProvider).rollingDownloadMs,
            p.rollingDownloadMs);
        // A second capture folds into the average (still non-null).
        n.beginExposing(const Duration(seconds: 3));
        n.updateExposureProgress(100);
        async.elapse(kExposingMinVisible);
        n.complete('def', generation: n.state.generation);
        async.elapse(kDownloadingMinVisible);
        expect(container.read(captureProgressProvider).rollingDownloadMs,
            isNotNull);
      });
    });

    test('timeToDisplay = exposure remaining + download estimate while exposing',
        () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 10));
        n.updateExposureProgress(50); // 5s left
        // Seed a 2s download estimate via a complete/reset cycle.
        n.updateExposureProgress(100);
        async.elapse(kExposingMinVisible);
        n.complete('abc', generation: n.state.generation);
        async.elapse(kDownloadingMinVisible);
        n.reset();
        // Fake the rolling estimate to a known 2000ms.
        n.updateRollingForTest(2000);
        n.beginExposing(const Duration(seconds: 10));
        n.updateExposureProgress(50);
        final ttd = container.read(captureProgressProvider).timeToDisplay;
        expect(ttd, const Duration(seconds: 7)); // 5s exposure + 2s download
      });
    });

    test('timeToDisplay falls back to the default estimate before measurement',
        () {
      n.beginExposing(const Duration(seconds: 10));
      n.updateExposureProgress(50);
      final ttd = container.read(captureProgressProvider).timeToDisplay;
      // 5s exposure + kDefaultDownloadEstimate.
      expect(ttd, const Duration(seconds: 5) + kDefaultDownloadEstimate);
    });

    test('fail() lands the failed phase with the reason', () {
      n.beginExposing(const Duration(seconds: 3));
      n.fail('boom', generation: n.state.generation);
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.failed);
      expect(p.error, 'boom');
    });

    test(
        'a stale expose-hold timer is cancelled on retry and cannot force '
        'the new cycle into downloading', () {
      fakeAsync((async) {
        // Cycle 1: the daemon reports 100% within the min-visible window, so
        // an expose-hold is armed to flip to downloading shortly.
        n.beginExposing(const Duration(seconds: 10));
        n.updateExposureProgress(100);
        // The capture fails before the hold fires; the user hits Retry
        // immediately.
        n.fail('boom', generation: n.state.generation);
        n.beginExposing(const Duration(seconds: 10));
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing);
        // The old hold would have fired by now — the new cycle must still be
        // exposing, not forced into downloading.
        async.elapse(kExposingMinVisible);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing);
      });
    });

    test('a stale expose-hold does not survive a reset + beginExposing', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 10));
        n.updateExposureProgress(100); // arms the expose-hold
        n.reset();
        n.beginExposing(const Duration(seconds: 10));
        async.elapse(kExposingMinVisible);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing);
      });
    });

    test('a stale cycle\'s fail() after cancel is a no-op (no resurrection)', () {
      n.beginExposing(const Duration(seconds: 3));
      final staleGen = n.state.generation;
      n.reset(); // user hit Cancel — the old poll loop is now stale
      // The old loop eventually times out and reports failure — must not
      // resurrect a card the user already dismissed.
      n.fail('Capture timed out.', generation: staleGen);
      expect(container.read(captureProgressProvider).phase, CapturePhase.idle);
      expect(container.read(captureProgressProvider).error, isNull);
    });

    test('a stale cycle\'s complete() cannot clobber a newer capture', () {
      fakeAsync((async) {
        // Capture 1 starts; the user cancels, then immediately starts
        // capture 2.
        n.beginExposing(const Duration(seconds: 10));
        final staleGen = n.state.generation;
        n.reset();
        n.beginExposing(const Duration(seconds: 30));
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing);
        // Capture 1's frame registers late — the stale loop calls complete.
        n.complete('old-frame', generation: staleGen);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing,
            reason: 'the new capture\'s card is untouched');
        expect(container.read(captureProgressProvider).frameId, isNull);
        // The current cycle still completes normally.
        n.complete('new-frame', generation: n.state.generation);
        async.elapse(kDownloadingMinVisible);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.done);
        expect(container.read(captureProgressProvider).frameId, 'new-frame');
      });
    });

    test('complete() with a matching generation still lands done', () {
      n.beginExposing(const Duration(seconds: 3));
      n.complete('abc', generation: n.state.generation);
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.done);
      expect(p.frameId, 'abc');
    });

    test('displayProgressPct clamps a driver reporting negative progress to 0',
        () {
      n.beginExposing(const Duration(seconds: 10));
      n.updateExposureProgress(-10);
      final p = container.read(captureProgressProvider);
      expect(p.displayProgressPct, 0);
      expect(p.exposureRemaining, const Duration(seconds: 10));
      expect(p.timeToDisplay,
          const Duration(seconds: 10) + kDefaultDownloadEstimate);
    });

    test(
        'displayProgressPct falls back to elapsed time before the daemon '
        'reports — a slow poll must not freeze the bar at 0%', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 4));
        // No daemon update yet (equipment poll every ~15s): the elapsed-time
        // estimate must drive the bar.
        async.elapse(const Duration(milliseconds: 2000));
        final p = container.read(captureProgressProvider);
        expect(p.exposureProgressPct, isNull);
        expect(p.displayProgressPct, closeTo(50, 0.5));
        expect(p.exposureRemaining!.inMilliseconds, closeTo(2000, 60));
        // A null update must clear the stored value, not absorb it.
        n.updateExposureProgress(null);
        expect(container.read(captureProgressProvider).exposureProgressPct,
            isNull);
        // And the fallback survives the null update.
        async.elapse(const Duration(milliseconds: 1000));
        expect(container.read(captureProgressProvider).displayProgressPct,
            closeTo(75, 0.5));
      });
    });

    test(
        'the local exposure clock enters downloading when the daemon never '
        'reports 100% — the rolling estimate still gets measured', () {
      // Regression: the exposing → downloading transition was gated solely on
      // the daemon's exposure_progress_pct reaching 100, which rides the 15 s
      // equipment poll. For any exposure shorter than that cadence the frame
      // (confirmed by the fast catalog poll) landed while still "exposing" —
      // the card skipped the downloading phase and rollingDownloadMs was
      // never learned.
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 3));
        async.elapse(const Duration(seconds: 3));
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing,
            reason: 'the clock pads for POST → shutter latency');
        async.elapse(kExposureClockPad);
        final p = container.read(captureProgressProvider);
        expect(p.phase, CapturePhase.downloading);
        expect(p.exposureEndedAt, isNotNull);
        // The frame lands 1s later: the download time is measured.
        async.elapse(const Duration(seconds: 1));
        n.complete('f1', generation: n.state.generation);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.done);
        expect(container.read(captureProgressProvider).rollingDownloadMs,
            closeTo(1000, 60));
      });
    });

    test('a sub-min-visible exposure still shows the exposing phase', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(milliseconds: 100));
        async.elapse(const Duration(milliseconds: 100) + kExposureClockPad);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing,
            reason: 'the clock never fires before kExposingMinVisible');
        async.elapse(kExposingMinVisible);
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.downloading);
      });
    });

    test('a cancelled cycle\'s exposure clock cannot fire into the next one',
        () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 2));
        async.elapse(const Duration(seconds: 1));
        n.reset(); // Cancel.
        n.beginExposing(const Duration(seconds: 30));
        // Past the OLD cycle's 2s+pad deadline: the new 30s exposure must
        // still be exposing.
        async.elapse(const Duration(seconds: 3));
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.exposing);
      });
    });

    test('a daemon 100% report beating the clock wins without a double '
        'transition', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 5));
        async.elapse(const Duration(seconds: 4));
        n.updateExposureProgress(100); // → downloading (past min-visible)
        expect(container.read(captureProgressProvider).phase,
            CapturePhase.downloading);
        final endedAt =
            container.read(captureProgressProvider).exposureEndedAt;
        // Past the local clock's 5s+pad deadline: exposureEndedAt must not
        // be overwritten by a second transition.
        async.elapse(const Duration(seconds: 2));
        expect(container.read(captureProgressProvider).exposureEndedAt,
            endedAt);
      });
    });
  });
}

// Test helper to set a deterministic rolling estimate.
extension on CaptureProgressNotifier {
  void updateRollingForTest(int ms) {
    state = state.copyWith(rollingDownloadMs: ms);
  }
}
