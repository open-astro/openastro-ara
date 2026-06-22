import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';

void main() {
  test('fromJson reads the TelescopeDto envelope (caps + runtime)', () {
    final m = MountStatus.fromJson(const {
      'device_id': 'mount-0',
      'name': 'EQ6-R',
      'state': 'connected',
      'capabilities': {
        'can_slew': true,
        'can_sync': true,
        'can_park': true,
        'can_unpark': true,
        'can_set_tracking': true,
        'can_pulse_guide': true,
        'can_find_home': false,
        'supported_sidereal_rates': ['sidereal', 'lunar'],
      },
      'runtime': {
        'state': 'slewing',
        'right_ascension_hours': 5.5,
        'declination_degrees': -12.25,
        'tracking': true,
        'parked': false,
        'at_home': false,
      },
    });
    expect(m.connectionState, EquipmentConnectionState.connected);
    expect(m.capabilities!.canPark, isTrue);
    expect(m.capabilities!.canFindHome, isFalse);
    expect(m.rightAscensionHours, 5.5);
    expect(m.declinationDegrees, -12.25);
    expect(m.tracking, isTrue);
    expect(m.isBusy, isTrue); // slewing
  });

  test('absent caps parse null; parked runtime is not busy', () {
    final parked = MountStatus.fromJson(const {
      'state': 'connected',
      'runtime': {'state': 'parked', 'tracking': false, 'parked': true},
    });
    expect(parked.capabilities, isNull);
    expect(parked.parked, isTrue);
    expect(parked.isBusy, isFalse);
    expect(parked.rightAscensionHours, isNull);

    final preConnect = MountStatus.fromJson(const {'state': 'connecting'});
    expect(preConnect.capabilities, isNull);
  });

  test('RA/Dec sexagesimal formatters', () {
    expect(formatRaHours(5.5), '05h 30m 00s');
    expect(formatRaHours(0), '00h 00m 00s');
    expect(formatRaHours(null), '—');
    expect(formatRaHours(-0.5), '00h 00m 00s'); // clamped to [0, 24)
    expect(formatDecDegrees(-12.25), '-12° 15′ 00″');
    expect(formatDecDegrees(45.5), '+45° 30′ 00″');
    expect(formatDecDegrees(null), '—');
    expect(formatDecDegrees(120), '+90° 00′ 00″'); // clamped to [-90, 90]
  });
}
