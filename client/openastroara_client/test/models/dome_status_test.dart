import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';

void main() {
  test('fromJson reads the DomeDto envelope (caps + runtime)', () {
    final d = DomeStatus.fromJson(const {
      'device_id': 'dome-0',
      'name': 'Observatory',
      'state': 'connected',
      'capabilities': {
        'can_set_shutter': true,
        'can_set_azimuth': true,
        'can_sync_azimuth': false,
        'can_park': true,
        'can_find_home': true,
      },
      'runtime': {
        'state': 'slewing',
        'azimuth_deg': 180.0,
        'shutter_open': true,
        'at_home': false,
        'parked': false,
      },
    });
    expect(d.connectionState, EquipmentConnectionState.connected);
    expect(d.capabilities!.canSetShutter, isTrue);
    expect(d.capabilities!.canPark, isTrue);
    expect(d.azimuthDeg, 180.0);
    expect(d.shutterOpen, isTrue);
    expect(d.isBusy, isTrue); // slewing
  });

  test('shutter_moving is busy; absent caps parse null', () {
    final d = DomeStatus.fromJson(const {
      'state': 'connected',
      'runtime': {'state': 'shutter_moving'},
    });
    expect(d.capabilities, isNull);
    expect(d.isBusy, isTrue);
    expect(d.shutterOpen, isFalse);
  });
}
