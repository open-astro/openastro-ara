import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';

void main() {
  // #1185 — §25.5.5 capability gaps the daemon now reports.
  const base = <String, dynamic>{
    'sensor_width': 6248,
    'sensor_height': 4176,
    'pixel_size_um': 3.76,
    'can_set_temperature': true,
    'min_gain': 0,
    'max_gain': 500,
    'min_offset': 0,
    'max_offset': 100,
    'min_bin_x': 1,
    'max_bin_x': 4,
    'min_bin_y': 1,
    'max_bin_y': 4,
    'min_exposure_sec': 0.0001,
    'max_exposure_sec': 3600,
    'bayer_pattern': 'RGGB',
  };

  test('sensor type, shutter and the discrete gain list parse', () {
    final caps = CameraCapabilities.fromJson({
      ...base,
      'sensor_type': 'RGGB',
      'has_shutter': true,
      'gains': ['ISO 100', 'ISO 200', 400],
      'offsets': ['Low', 'High'],
      'exposure_resolution_sec': 0.001,
    });
    expect(caps.sensorType, 'RGGB');
    expect(caps.hasShutter, isTrue);
    expect(caps.gains, ['ISO 100', 'ISO 200', '400']);
    expect(caps.offsets, ['Low', 'High']);
    expect(caps.exposureResolutionSec, 0.001);
  });

  test('an older daemon leaves them at their defaults', () {
    final caps = CameraCapabilities.fromJson(base);
    expect(caps.sensorType, isNull);
    expect(caps.hasShutter, isFalse);
    expect(caps.gains, isEmpty);
    expect(caps.offsets, isEmpty);
    expect(caps.exposureResolutionSec, 0);
  });

  test('the runtime heat-sink temperature parses and defaults to null', () {
    final status = CameraStatus.fromJson({
      'device_id': 'cam',
      'name': 'Cam',
      'state': 'connected',
      'runtime': {'state': 'idle', 'cooler_on': true, 'heat_sink_temperature_c': 31.5},
    });
    expect(status.heatSinkTemperatureC, 31.5);
    expect(CameraStatus.fromJson({'device_id': 'cam', 'name': 'Cam', 'state': 'connected', 'runtime': {'state': 'idle'}}).heatSinkTemperatureC, isNull);
  });

  test('the new fields take part in equality', () {
    final a = CameraCapabilities.fromJson({...base, 'gains': ['1', '2']});
    final b = CameraCapabilities.fromJson({...base, 'gains': ['1', '2']});
    final c = CameraCapabilities.fromJson({...base, 'gains': ['1', '3']});
    expect(a, equals(b));
    expect(a.hashCode, b.hashCode);
    expect(a, isNot(equals(c)));
    expect(CameraCapabilities.fromJson({...base, 'has_shutter': true}), isNot(equals(a)));
  });
}
