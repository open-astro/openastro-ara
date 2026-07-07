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
}
