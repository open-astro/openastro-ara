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
      expect(p.exposureProgressPct, 0);
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

    test('complete() lands the done phase with the frame id', () {
      n.beginExposing(const Duration(seconds: 3));
      n.complete('abc');
      final p = container.read(captureProgressProvider);
      expect(p.phase, CapturePhase.done);
      expect(p.frameId, 'abc');
    });

    test('complete() measures the download and builds a rolling estimate', () {
      fakeAsync((async) {
        n.beginExposing(const Duration(seconds: 3));
        n.updateExposureProgress(100);
        async.elapse(kExposingMinVisible); // → downloading
        n.complete('abc');
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
        n.complete('def');
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
        n.complete('abc');
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
      n.fail('boom');
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
        n.fail('boom');
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
  });
}

// Test helper to set a deterministic rolling estimate.
extension on CaptureProgressNotifier {
  void updateRollingForTest(int ms) {
    state = state.copyWith(rollingDownloadMs: ms);
  }
}

