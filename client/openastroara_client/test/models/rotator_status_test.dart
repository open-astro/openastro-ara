import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/rotator_status.dart';

void main() {
  test('fromJson reads the RotatorDto envelope (caps + runtime)', () {
    final r = RotatorStatus.fromJson(const {
      'device_id': 'rot-0',
      'name': 'Pyxis',
      'state': 'connected',
      'capabilities': {'can_reverse': true, 'step_size': 0.5},
      'runtime': {
        'state': 'moving',
        'mechanical_angle_deg': 12.5,
        'sky_angle_deg': 100.0,
        'reverse': true,
      },
    });
    expect(r.connectionState, EquipmentConnectionState.connected);
    expect(r.capabilities!.canReverse, isTrue);
    expect(r.capabilities!.stepSize, 0.5);
    expect(r.mechanicalAngleDeg, 12.5);
    expect(r.skyAngleDeg, 100.0);
    expect(r.reverse, isTrue);
    expect(r.isBusy, isTrue);
  });

  test('absent capabilities parse as null without throwing', () {
    final r = RotatorStatus.fromJson(const {
      'state': 'connecting',
      'runtime': {'state': 'idle'},
    });
    expect(r.capabilities, isNull);
    expect(r.mechanicalAngleDeg, isNull);
    expect(r.reverse, isFalse);
    expect(r.isBusy, isFalse);
  });
}
