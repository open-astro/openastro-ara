import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/camera_geometry_api.dart';

void main() {
  group('CameraGeometry.fromCameraJson', () {
    Map<String, dynamic> body({
      String state = 'connected',
      Object? capabilities = const {
        'sensor_width': 6248,
        'sensor_height': 4176,
        'pixel_size_um': 3.76,
      },
    }) =>
        {'state': state, 'capabilities': capabilities};

    test('a connected camera with sensor caps yields the geometry', () {
      final g = CameraGeometry.fromCameraJson(body());
      expect(g, isNotNull);
      expect(g!.sensorWidthPx, 6248);
      expect(g.sensorHeightPx, 4176);
      expect(g.pixelSizeUm, 3.76);
    });

    test('a disconnected camera yields null', () {
      expect(CameraGeometry.fromCameraJson(body(state: 'disconnected')), isNull);
      expect(CameraGeometry.fromCameraJson(body(state: 'connecting')), isNull);
    });

    test('a connected camera with no capabilities yields null', () {
      expect(CameraGeometry.fromCameraJson(body(capabilities: null)), isNull);
    });

    test('a camera reporting zero/missing geometry yields null (no junk write)', () {
      expect(
        CameraGeometry.fromCameraJson(
            body(capabilities: const {'sensor_width': 0, 'sensor_height': 0, 'pixel_size_um': 0})),
        isNull,
      );
      expect(
        CameraGeometry.fromCameraJson(
            body(capabilities: const {'sensor_width': 6248, 'sensor_height': 4176})),
        isNull,
        reason: 'a missing pixel size can\'t drive the FOV',
      );
    });

    test('a wrong-typed field returns null instead of throwing', () {
      expect(
        CameraGeometry.fromCameraJson(body(capabilities: const {
          'sensor_width': '6248', // stringified — must not throw a TypeError
          'sensor_height': 4176,
          'pixel_size_um': 3.76,
        })),
        isNull,
      );
    });
  });
}
