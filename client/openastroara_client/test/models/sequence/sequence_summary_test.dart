import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';

void main() {
  group('SequenceRunState.fromWire', () {
    test('parses every state by its daemon wire name (lowercased enum name)',
        () {
      // The daemon lower-cases the whole C# enum name, so multi-word states
      // arrive with no separators — pausedawaitinguser, not
      // paused_awaiting_user. Round-trip every value through that convention.
      for (final s in SequenceRunState.values) {
        expect(SequenceRunState.fromWire(s.name.toLowerCase()), s);
      }
      expect(
        SequenceRunState.fromWire('pausedawaitinguser'),
        SequenceRunState.pausedAwaitingUser,
        reason: 'the §58.12 state is one word on the wire',
      );
    });

    test('unknown or non-string wire values degrade to null', () {
      expect(SequenceRunState.fromWire('paused_awaiting_user'), isNull,
          reason: 'snake_case is NOT the wire form for this enum');
      expect(SequenceRunState.fromWire('warpspeed'), isNull);
      expect(SequenceRunState.fromWire(3), isNull);
      expect(SequenceRunState.fromWire(null), isNull);
    });
  });

  group('SequenceRunState flags', () {
    test('pausedAwaitingUser is active and paused-flavored', () {
      expect(SequenceRunState.pausedAwaitingUser.isActive, isTrue,
          reason: 'the suspended worker still owns the run — Start must not '
              'reappear and the sequence file stays edit-locked');
      expect(SequenceRunState.pausedAwaitingUser.isAnyPaused, isTrue);
      expect(SequenceRunState.paused.isAnyPaused, isTrue);
      expect(SequenceRunState.running.isAnyPaused, isFalse);
      expect(SequenceRunState.failed.isAnyPaused, isFalse);
    });
  });

  group('SequenceRunStateInfo estimated seconds (#1068)', () {
    test('parses the daemon estimate from run state and keeps it on WS frames',
        () {
      final info = SequenceRunStateInfo.fromJson({
        'sequence_id': 's',
        'run_id': 'r',
        'state': 'running',
        'instructions_completed': 1,
        'instructions_total': 10,
        'estimated_total_seconds': 1215.0,
        'estimated_remaining_seconds': 1100,
      });
      expect(info.estimatedTotalSeconds, 1215);
      expect(info.estimatedRemainingSeconds, 1100);
      final next = info.applyWsProgress({
        'instructions_completed': 2,
        'estimated_remaining_seconds': 980.5,
      });
      expect(next.estimatedTotalSeconds, 1215, reason: 'kept when omitted');
      expect(next.estimatedRemainingSeconds, 980.5);
      expect(SequenceRunStateInfo.fromJson(const {}).estimatedTotalSeconds,
          isNull, reason: 'null until the run tree has loaded');
    });

    test('a progress frame carries the current instruction and target', () {
      // Rig night 2026-10-18: the Run tab read "Run started" for the 4.7 min
      // the camera cooled, because the description only came by REST.
      const info = SequenceRunStateInfo(
        state: SequenceRunState.running,
        currentInstructionDescription: 'Run started',
        currentTargetName: null,
      );
      final cooling = info.applyWsProgress({
        'current_instruction_description': 'Cooling camera to -10 °C',
        'current_target_name': 'Helix Nebula',
      });
      expect(cooling.currentInstructionDescription, 'Cooling camera to -10 °C');
      expect(cooling.currentTargetName, 'Helix Nebula');
      final older = cooling.applyWsProgress({'instructions_completed': 1});
      expect(older.currentInstructionDescription, 'Cooling camera to -10 °C',
          reason: 'an older daemon omits it: keep the last known');
      expect(older.currentTargetName, 'Helix Nebula');
    });
  });
}
