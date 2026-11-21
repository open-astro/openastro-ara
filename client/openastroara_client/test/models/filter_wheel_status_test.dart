import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';

void main() {
  test('fromJson reads the FilterWheelDto envelope (runtime + slots)', () {
    final f = FilterWheelStatus.fromJson(const {
      'device_id': 'fw-0',
      'name': 'EFW',
      'state': 'connected',
      'runtime': {'state': 'idle', 'current_slot': 2},
      'slots': [
        {'position': 0, 'name': 'L', 'focus_offset': 0},
        {'position': 1, 'name': 'R', 'focus_offset': 12},
        {'position': 2, 'name': 'G', 'focus_offset': -5},
      ],
    });
    expect(f.connectionState, EquipmentConnectionState.connected);
    expect(f.slots, hasLength(3));
    expect(f.currentSlot, 2);
    expect(f.current!.name, 'G');
    expect(f.current!.focusOffset, -5);
    expect(f.isBusy, isFalse);
  });

  test('moving wheel is busy and has no resolved current slot', () {
    final f = FilterWheelStatus.fromJson(const {
      'state': 'connected',
      'runtime': {'state': 'moving', 'current_slot': -1},
      'slots': [
        {'position': 0, 'name': 'L', 'focus_offset': 0},
      ],
    });
    expect(f.isMoving, isTrue);
    expect(f.isBusy, isTrue);
    expect(f.currentSlot, isNull); // wire -1 normalized to null
    expect(f.current, isNull);
  });

  test('#1298 manual wheel: manual flag and pending slot parse', () {
    final s = FilterWheelStatus.fromJson({
      'device_id': 'ara-manual-filter-wheel',
      'name': 'Manual filter wheel',
      'state': 'connected',
      'manual': true,
      'runtime': {'state': 'awaiting_user', 'current_slot': 0, 'pending_slot': 1},
      'slots': [
        {'position': 0, 'name': 'L', 'focus_offset': 0},
        {'position': 1, 'name': 'Ha', 'focus_offset': 0},
      ],
    });
    expect(s.manual, isTrue);
    expect(s.isAwaitingUser, isTrue);
    expect(s.pending?.name, 'Ha');
    expect(s.current?.name, 'L');
    expect(s.isBusy, isFalse, reason: 'a hand swap is not a fast-poll move');

    final plain = FilterWheelStatus.fromJson({
      'state': 'connected',
      'runtime': {'state': 'idle', 'current_slot': 0},
    });
    expect(plain.manual, isFalse);
    expect(plain.pendingSlot, isNull);
    expect(plain.isAwaitingUser, isFalse);
  });
}
