import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/flat_panel_status.dart';

void main() {
  test('fromJson reads the FlatDeviceDto envelope (cover/light/brightness)', () {
    final f = FlatPanelStatus.fromJson(const {
      'device_id': 'flat-0',
      'name': 'Flat Panel',
      'state': 'connected',
      'runtime': {
        'state': 'cover_moving',
        'cover_open': true,
        'light_on': false,
        'brightness': 128,
      },
    });
    expect(f.connectionState, EquipmentConnectionState.connected);
    expect(f.runtimeState, 'cover_moving');
    expect(f.coverOpen, isTrue);
    expect(f.lightOn, isFalse);
    expect(f.brightness, 128);
    // Cover in motion → busy (drives the chip's amber dot).
    expect(f.isBusy, isTrue);
  });

  test('absent runtime parses to defaults without throwing', () {
    final f = FlatPanelStatus.fromJson(const {'state': 'connecting'});
    expect(f.connectionState, EquipmentConnectionState.connecting);
    expect(f.runtimeState, '');
    expect(f.coverOpen, isFalse);
    expect(f.lightOn, isFalse);
    expect(f.brightness, 0);
    expect(f.isBusy, isFalse);
  });

  test('light_on / closed cover is not busy', () {
    final f = FlatPanelStatus.fromJson(const {
      'state': 'connected',
      'runtime': {'state': 'light_on', 'light_on': true, 'brightness': 200},
    });
    expect(f.lightOn, isTrue);
    expect(f.isBusy, isFalse);
  });
}
