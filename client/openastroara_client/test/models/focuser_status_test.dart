import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';

void main() {
  test('fromJson reads the daemon FocuserDto envelope (caps + runtime)', () {
    final f = FocuserStatus.fromJson(const {
      'device_id': 'foc-0',
      'name': 'MoonLite',
      'state': 'connected',
      'capabilities': {
        'min_position': 0,
        'max_position': 50000,
        'step_size_um': 1.5,
        'can_temp_comp': true,
        'absolute_focuser': true,
      },
      'runtime': {
        'state': 'moving',
        'position': 12345,
        'temperature': 3.2,
        'temp_comp_enabled': true,
      },
    });
    expect(f.connectionState, EquipmentConnectionState.connected);
    expect(f.capabilities!.maxPosition, 50000);
    expect(f.capabilities!.canTempComp, isTrue);
    expect(f.position, 12345);
    expect(f.temperature, 3.2);
    expect(f.tempCompEnabled, isTrue);
    expect(f.isMoving, isTrue);
  });

  test('absent capabilities (pre-connect) parse as null without throwing', () {
    final f = FocuserStatus.fromJson(const {
      'state': 'connecting',
      'runtime': {'state': 'idle', 'position': null},
    });
    expect(f.capabilities, isNull);
    expect(f.position, isNull);
    expect(f.isMoving, isFalse);
    expect(f.connectionState, EquipmentConnectionState.connecting);
  });
}
