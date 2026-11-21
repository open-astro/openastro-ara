import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/filter_wheel_names_api.dart';

void main() {
  group('FilterWheelSlots.fromFilterWheelJson', () {
    test('parses slot names + focus offsets from a connected wheel', () {
      final w = FilterWheelSlots.fromFilterWheelJson(const {
        'state': 'connected',
        'slots': [
          {'position': 0, 'name': 'L', 'focus_offset': 0},
          {'position': 1, 'name': 'Ha', 'focus_offset': -35},
        ],
      });
      expect(w, isNotNull);
      expect(w!.slots.map((s) => s.name).toList(), ['L', 'Ha']);
      expect(w.slots.map((s) => s.focusOffset).toList(), [0, -35]);
    });

    test('null when no wheel is connected', () {
      expect(
          FilterWheelSlots.fromFilterWheelJson(const {'state': 'disconnected'}),
          isNull);
    });

    test('null when connected but the body has no slots list', () {
      expect(FilterWheelSlots.fromFilterWheelJson(const {'state': 'connected'}),
          isNull);
    });

    test('connected with an empty slots list → empty (not null)', () {
      final w = FilterWheelSlots.fromFilterWheelJson(
          const {'state': 'connected', 'slots': []});
      expect(w, isNotNull);
      expect(w!.slots, isEmpty);
    });

    test('tolerates a missing name / non-numeric offset per slot', () {
      final w = FilterWheelSlots.fromFilterWheelJson(const {
        'state': 'connected',
        'slots': [
          {'position': 0, 'focus_offset': 'oops'}, // no name, bad offset
        ],
      });
      expect(w!.slots.single.name, '');
      expect(w.slots.single.focusOffset, 0);
    });
  });
}
