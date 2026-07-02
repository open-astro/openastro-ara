import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';

void main() {
  test('fromJson reads the CameraDto envelope (caps + runtime)', () {
    final c = CameraStatus.fromJson(const {
      'device_id': 'cam-0',
      'name': 'ASI2600',
      'state': 'connected',
      'capabilities': {
        'sensor_width': 6248,
        'sensor_height': 4176,
        'pixel_size_um': 3.76,
        'can_set_temperature': true,
        'min_gain': 0,
        'max_gain': 500,
        'min_offset': 0,
        'max_offset': 80,
        'min_bin_x': 1,
        'max_bin_x': 4,
        'min_bin_y': 1,
        'max_bin_y': 4,
        'min_exposure_sec': 0.0001,
        'max_exposure_sec': 3600.0,
        'bayer_pattern': 'RGGB',
      },
      'runtime': {
        'state': 'exposing',
        'ccd_temperature': -9.8,
        'cooler_power_pct': 42.0,
        'cooler_on': true,
        'exposure_progress_pct': 30.0,
      },
    });
    expect(c.connectionState, EquipmentConnectionState.connected);
    expect(c.capabilities!.canSetTemperature, isTrue);
    // has_cooler absent (pre-§25.5.5 daemon) → falls back to canSetTemperature.
    expect(c.capabilities!.hasCooler, isTrue);
    expect(c.capabilities!.isColor, isTrue);
    expect(c.capabilities!.maxGain, 500);
    expect(c.ccdTemperature, -9.8);
    expect(c.coolerPowerPct, 42.0);
    expect(c.coolerOn, isTrue);
    expect(c.isBusy, isTrue); // exposing
  });

  test('mono sensor (no bayer) is not colour; absent caps parse null', () {
    final mono = CameraStatus.fromJson(const {
      'state': 'connected',
      'capabilities': {'sensor_width': 9576, 'sensor_height': 6388},
      'runtime': {'state': 'idle', 'ccd_temperature': -10.0, 'cooler_on': false},
    });
    expect(mono.capabilities!.isColor, isFalse);
    expect(mono.coolerOn, isFalse);
    expect(mono.isBusy, isFalse);

    final preConnect = CameraStatus.fromJson(const {'state': 'connecting'});
    expect(preConnect.capabilities, isNull);
  });

  test('§25.5.5 has_cooler parses independently of can_set_temperature', () {
    // The dumb-cooler shape: on/off cooler, no TEC regulation.
    final dumb = CameraStatus.fromJson(const {
      'state': 'connected',
      'capabilities': {
        'sensor_width': 100,
        'sensor_height': 100,
        'can_set_temperature': false,
        'has_cooler': true,
      },
      'runtime': {'state': 'idle'},
    });
    expect(dumb.capabilities!.hasCooler, isTrue);
    expect(dumb.capabilities!.canSetTemperature, isFalse);

    // Explicit false wins over the fallback even when the TEC flag is true
    // (a daemon that reports both is trusted verbatim).
    final explicit = CameraStatus.fromJson(const {
      'state': 'connected',
      'capabilities': {
        'sensor_width': 100,
        'sensor_height': 100,
        'can_set_temperature': true,
        'has_cooler': false,
      },
      'runtime': {'state': 'idle'},
    });
    expect(explicit.capabilities!.hasCooler, isFalse);

    // Absent (pre-§25.5.5 daemon) → falls back to canSetTemperature=false too.
    final absent = CameraStatus.fromJson(const {
      'state': 'connected',
      'capabilities': {
        'sensor_width': 100,
        'sensor_height': 100,
        'can_set_temperature': false,
      },
      'runtime': {'state': 'idle'},
    });
    expect(absent.capabilities!.hasCooler, isFalse);
  });
}
