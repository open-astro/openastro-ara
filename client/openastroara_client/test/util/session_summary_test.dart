import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/notifications_api.dart';
import 'package:openastroara/util/session_summary.dart';

AraNotification _n(
  String title,
  DateTime at, {
  NotificationSeverity severity = NotificationSeverity.warning,
  String message = '',
}) => AraNotification(
  id: title,
  postedUtc: at,
  severity: severity,
  category: NotificationCategory.safety,
  title: title,
  message: message,
  read: false,
  dismissed: false,
);

void main() {
  final start = DateTime.utc(2026, 10, 12, 22, 0);
  final seq = const SequenceListItem(id: 'seq-1', name: 'M81 night');

  group('buildSessionSummary (§58.13)', () {
    test(
      'a completed run reads as finished with its frames and no incidents',
      () {
        final run = SequenceRunStateInfo(
          sequenceId: 'seq-1',
          runId: 'r1',
          state: SequenceRunState.completed,
          currentTargetName: 'M81',
          startedUtc: start,
          completedUtc: start.add(const Duration(hours: 5)),
          framesCaptured: 47,
        );
        final s = buildSessionSummary(
          sequence: seq,
          run: run,
          notifications: [
            _n(
              'Frame analyzed',
              start.add(const Duration(hours: 1)),
              severity: NotificationSeverity.info,
            ),
          ],
        )!;
        expect(s.title, 'Session finished overnight');
        expect(s.headline, startsWith('M81 night (M81) ran to completion at '));
        expect(s.framesLine, '47 frames captured');
        expect(
          s.incidents,
          isEmpty,
          reason: 'info notifications are not incidents',
        );
      },
    );

    test('a shutdown leaves the run paused awaiting the user: the shutdown record is the incident and the end time', () {
      final run = SequenceRunStateInfo(
        sequenceId: 'seq-1',
        runId: 'r2',
        state: SequenceRunState.pausedAwaitingUser,
        currentTargetName: 'M81',
        startedUtc: start,
        framesCaptured: 47,
      );
      final flip = _n(
        'Meridian flip failed',
        start.add(const Duration(hours: 5, minutes: 14)),
        severity: NotificationSeverity.critical,
      );
      final shutdown = _n(
        'Unattended shutdown executed',
        start.add(const Duration(hours: 5, minutes: 24)),
        message: 'the equipment was put to bed',
      );
      final s = buildSessionSummary(
        sequence: seq,
        run: run,
        notifications: [
          shutdown, flip,
          _n(
            'Yesterday',
            start.subtract(const Duration(hours: 2)),
          ), // before the run
          _n('Next week', start.add(const Duration(days: 3))), // long after
        ],
      )!;
      expect(s.title, 'Session paused — the rig needs you');
      expect(
        s.endedUtc,
        shutdown.postedUtc,
        reason: 'no completedUtc: the last incident dates the end',
      );
      expect(s.incidents.map((n) => n.title), [
        'Unattended shutdown executed',
        'Meridian flip failed',
      ]);
      expect(s.headline, contains('waiting for you'));
    });

    test('an idle or running run is no card', () {
      for (final st in [
        SequenceRunState.idle,
        SequenceRunState.running,
        SequenceRunState.paused,
        null,
      ]) {
        final run = SequenceRunStateInfo(
          sequenceId: 'seq-1',
          runId: 'r',
          state: st,
          startedUtc: start,
        );
        expect(
          buildSessionSummary(sequence: seq, run: run, notifications: const []),
          isNull,
          reason: '$st',
        );
      }
    });

    test('incidents are capped and newest first', () {
      final run = SequenceRunStateInfo(
        sequenceId: 'seq-1',
        runId: 'r',
        state: SequenceRunState.failed,
        startedUtc: start,
        completedUtc: start.add(const Duration(hours: 3)),
      );
      final many = [
        for (var i = 0; i < 10; i++)
          _n('w$i', start.add(Duration(minutes: 10 * i))),
      ];
      final s = buildSessionSummary(
        sequence: seq,
        run: run,
        notifications: many,
      )!;
      expect(s.incidents, hasLength(6));
      expect(s.incidents.first.title, 'w9');
    });
  });

  group('newestSummary + shouldShowSessionSummary', () {
    SessionSummary at(String id, DateTime? ended) => SessionSummary(
      sequenceId: 's',
      runId: id,
      sequenceName: 's',
      state: SequenceRunState.completed,
      endedUtc: ended,
    );

    test('the run that ended last wins', () {
      final s = newestSummary([
        at('a', start),
        at('b', start.add(const Duration(hours: 2))),
        at('c', start.add(const Duration(hours: 1))),
      ]);
      expect(s!.runId, 'b');
      expect(newestSummary(const []), isNull);
    });

    test('shown once: not acknowledged, not watched live, recent', () {
      final now = start.add(const Duration(hours: 12));
      final s = at('r1', start);
      expect(
        shouldShowSessionSummary(
          s,
          acknowledgedRunIds: {},
          seenEndingLive: {},
          now: now,
        ),
        isTrue,
      );
      expect(
        shouldShowSessionSummary(
          s,
          acknowledgedRunIds: {'r1'},
          seenEndingLive: {},
          now: now,
        ),
        isFalse,
      );
      expect(
        shouldShowSessionSummary(
          s,
          acknowledgedRunIds: {},
          seenEndingLive: {'r1'},
          now: now,
        ),
        isFalse,
      );
      expect(
        shouldShowSessionSummary(
          s,
          acknowledgedRunIds: {},
          seenEndingLive: {},
          now: start.add(const Duration(days: 3)),
        ),
        isFalse,
        reason: 'history, not overnight',
      );
      expect(
        shouldShowSessionSummary(
          at('r2', null),
          acknowledgedRunIds: {},
          seenEndingLive: {},
          now: now,
        ),
        isFalse,
        reason: 'nothing dates it',
      );
    });
  });
}
