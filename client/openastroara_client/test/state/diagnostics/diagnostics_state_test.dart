import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/diagnostics/diagnostics_state.dart';
import 'package:openastroara/widgets/status_indicator.dart';

WsEvent _ev(String type, Map<String, dynamic> payload, {int seq = 1}) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 6, 12, 21, 30, seq), seq: seq, payload: payload);

void main() {
  group('severityToLevel', () {
    test('maps known tokens; unknown/absent → info (not healthy)', () {
      expect(severityToLevel('red'), StatusLevel.error);
      expect(severityToLevel('yellow'), StatusLevel.busy);
      expect(severityToLevel('green'), StatusLevel.connected);
      expect(severityToLevel('mauve'), StatusLevel.info);
      expect(severityToLevel(null), StatusLevel.info);
    });
  });

  group('DiagnosticsAccumulator', () {
    test('empty state reads as nominal', () {
      final snap = DiagnosticsAccumulator().snapshot;
      expect(snap.level, StatusLevel.connected);
      expect(snap.label, 'Diagnostics: nominal');
      expect(snap.events, isEmpty);
    });

    test('issue_detected logs an entry and rolls up the severity', () {
      final acc = DiagnosticsAccumulator();
      final snap = acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {
        'event_type': 'disk.low',
        'severity': 'yellow',
        'description': 'Low disk space',
        'recommended_action': 'free up space',
        'auto_action_taken': false,
      }));
      expect(snap.level, StatusLevel.busy);
      expect(snap.label, 'Diagnostics: 1 warning');
      expect(snap.events, hasLength(1));
      expect(snap.events.first.source, 'disk.low');
      expect(snap.events.first.message, 'Low disk space — free up space');
      expect(snap.events.first.level, StatusLevel.busy);
    });

    test('auto_action_taken prefers the auto-action description', () {
      final snap = DiagnosticsAccumulator().apply(_ev(DiagnosticsWsEvents.autoActionTaken, {
        'event_type': 'disk.critical',
        'severity': 'red',
        'description': 'Disk full',
        'auto_action_taken': true,
        'auto_action_description': 'paused sequence',
        'recommended_action': 'free up space',
      }));
      expect(snap.level, StatusLevel.error);
      expect(snap.events.first.message, 'Disk full — paused sequence');
    });

    test('worst open severity drives the overall level', () {
      final acc = DiagnosticsAccumulator();
      acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'disk.low', 'severity': 'yellow'}, seq: 1));
      final snap = acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'temp.high', 'severity': 'red'}, seq: 2));
      expect(snap.level, StatusLevel.error);
      expect(snap.label, 'Diagnostics: 2 critical issues');
    });

    test('cleared drops the open issue and re-rolls the level', () {
      final acc = DiagnosticsAccumulator();
      acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'disk.low', 'severity': 'yellow'}, seq: 1));
      acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'temp.high', 'severity': 'red'}, seq: 2));
      final snap = acc.apply(_ev(DiagnosticsWsEvents.cleared, {'event_type': 'temp.high', 'cleared_count': 1}, seq: 3));
      expect(snap.level, StatusLevel.busy, reason: 'only the yellow issue remains open');
      expect(snap.label, 'Diagnostics: 1 warning');
      expect(snap.events.first.source, 'temp.high');
      expect(snap.events.first.message, 'Cleared');
      expect(snap.events.first.level, StatusLevel.connected);
    });

    test('clearing the last open issue returns to nominal', () {
      final acc = DiagnosticsAccumulator();
      acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'disk.low', 'severity': 'yellow'}, seq: 1));
      final snap = acc.apply(_ev(DiagnosticsWsEvents.cleared, {'event_type': 'disk.low'}, seq: 2));
      expect(snap.level, StatusLevel.connected);
      expect(snap.label, 'Diagnostics: nominal');
    });

    test('re-detecting the same type updates severity without double-counting', () {
      final acc = DiagnosticsAccumulator();
      acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'disk.low', 'severity': 'yellow'}, seq: 1));
      final snap = acc.apply(_ev(DiagnosticsWsEvents.issueDetected, {'event_type': 'disk.low', 'severity': 'red'}, seq: 2));
      expect(snap.label, 'Diagnostics: 1 critical issue', reason: 'same event_type stays one open issue');
      expect(snap.level, StatusLevel.error);
    });

    test('non-diagnostics events are ignored', () {
      final acc = DiagnosticsAccumulator();
      final snap = acc.apply(_ev('guider.dark_library.complete', {'profile_id': 'p1'}));
      expect(snap.events, isEmpty);
      expect(snap.level, StatusLevel.connected);
    });

    test('the log is bounded to maxEvents, most-recent first', () {
      final acc = DiagnosticsAccumulator(maxEvents: 3);
      for (var i = 0; i < 5; i++) {
        acc.apply(_ev(DiagnosticsWsEvents.issueDetected,
            {'event_type': 'e$i', 'severity': 'yellow', 'description': 'issue $i'}, seq: i));
      }
      final snap = acc.snapshot;
      expect(snap.events, hasLength(3));
      expect(snap.events.first.message, 'issue 4', reason: 'newest first');
      expect(snap.events.last.message, 'issue 2', reason: 'oldest two evicted');
    });
  });
}
