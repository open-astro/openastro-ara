import 'dart:typed_data';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';

void main() {
  group('applyRunSnapshot', () {
    test('a run that completes during this session marks it as focused', () {
      final running = applyRunSnapshot(
          const AutofocusLive(), AutofocusRun(state: AutofocusRunStates.running, startedUtc: DateTime.utc(2026, 10, 3, 21)));
      expect(running.focusedThisSession, isFalse);
      final next = applyRunSnapshot(running,
          AutofocusRun(state: AutofocusRunStates.complete, startedUtc: DateTime.utc(2026, 10, 3, 21), finalPosition: 100));
      expect(next.focusedThisSession, isTrue);
      expect(next.run.finalPosition, 100);
    });

    test('a run already complete on the first snapshot is from an earlier session', () {
      // Ara keeps its last run until it restarts: last night's 05:40 run must not
      // tick the Smart Focus step (or open the OAG gate) tonight.
      final lastNight = AutofocusRun(state: AutofocusRunStates.complete, startedUtc: DateTime.utc(2026, 10, 3, 5, 40));
      final first = applyRunSnapshot(const AutofocusLive(), lastNight);
      expect(first.focusedThisSession, isFalse);
      expect(first.run.isComplete, isTrue, reason: 'the old run is still shown');
      expect(applyRunSnapshot(first, lastNight).focusedThisSession, isFalse,
          reason: 'seeing the same old run again on a later refresh does not count either');
    });

    test('an earlier session\'s run with no start time still never counts', () {
      const old = AutofocusRun(state: AutofocusRunStates.complete);
      final first = applyRunSnapshot(const AutofocusLive(), old);
      expect(applyRunSnapshot(first, old).focusedThisSession, isFalse);
    });

    test('a new run that completes after an earlier session\'s run counts', () {
      final first = applyRunSnapshot(const AutofocusLive(),
          AutofocusRun(state: AutofocusRunStates.complete, startedUtc: DateTime.utc(2026, 10, 3, 5, 40)));
      final next = applyRunSnapshot(
          first, AutofocusRun(state: AutofocusRunStates.complete, startedUtc: DateTime.utc(2026, 10, 3, 21, 15)));
      expect(next.focusedThisSession, isTrue);
    });

    test('focusedThisSession survives a later failed run', () {
      const current = AutofocusLive(focusedThisSession: true);
      final next = applyRunSnapshot(current, const AutofocusRun(state: AutofocusRunStates.failed, reason: 'clouds'));
      expect(next.focusedThisSession, isTrue, reason: 'the scope was focused once this session');
      expect(next.run.isFailed, isTrue);
    });

    test('a new run without a frame drops the previous picture', () {
      final current = AutofocusLive(frame: Uint8List.fromList([1, 2]), frameSeq: 4);
      final next = applyRunSnapshot(current, const AutofocusRun(state: AutofocusRunStates.running, hasFrame: false));
      expect(next.frame, isNull);
      expect(next.frameSeq, 0);
    });

    test('a running run that already has a frame keeps the picture', () {
      final current = AutofocusLive(frame: Uint8List.fromList([1, 2]), frameSeq: 4);
      final next = applyRunSnapshot(current, const AutofocusRun(state: AutofocusRunStates.running, hasFrame: true, frameSeq: 4));
      expect(next.frame, isNotNull);
    });
  });

  group('focusStepState', () {
    test('pending before any run', () {
      expect(focusStepState(const AutofocusLive()), SetupStepState.pending);
    });
    test('inProgress while running', () {
      expect(focusStepState(const AutofocusLive(run: AutofocusRun(state: AutofocusRunStates.running))), SetupStepState.inProgress);
    });
    test('done once focused this session', () {
      expect(focusStepState(const AutofocusLive(focusedThisSession: true, run: AutofocusRun(state: AutofocusRunStates.complete))), SetupStepState.done);
    });
    test('problem after a failed run', () {
      expect(focusStepState(const AutofocusLive(run: AutofocusRun(state: AutofocusRunStates.failed))), SetupStepState.problem);
    });
    test('a cancelled run is pending, not a problem', () {
      expect(focusStepState(const AutofocusLive(run: AutofocusRun(state: AutofocusRunStates.cancelled))), SetupStepState.pending);
    });
    test('a manual "already in focus" wins over an old failure', () {
      expect(focusStepState(const AutofocusLive(focusedThisSession: true, run: AutofocusRun(state: AutofocusRunStates.failed))), SetupStepState.done);
    });
  });

  group('describeRequestError', () {
    test('prefers the daemon Problem detail', () {
      final e = DioException(
        requestOptions: RequestOptions(path: '/x'),
        response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: 409, data: {'title': 'guide_camera_in_use', 'detail': 'Stop guiding first.'}),
      );
      expect(describeRequestError(e), 'Stop guiding first.');
    });
    test('falls back to the title, then the status code', () {
      final titled = DioException(
        requestOptions: RequestOptions(path: '/x'),
        response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: 409, data: {'title': 'not_running'}),
      );
      expect(describeRequestError(titled), 'not_running');
      final bare = DioException(
        requestOptions: RequestOptions(path: '/x'),
        response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: 500),
      );
      expect(describeRequestError(bare), 'server returned 500');
    });
    test('non-Dio errors are stringified', () {
      expect(describeRequestError(StateError('boom')), contains('boom'));
    });
  });
}
