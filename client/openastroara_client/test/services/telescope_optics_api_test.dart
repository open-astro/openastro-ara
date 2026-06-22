import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/telescope_optics_api.dart';

void main() {
  group('TelescopeOptics.fromTelescopeJson', () {
    test('parses focal length + aperture (mm) from a connected mount', () {
      final o = TelescopeOptics.fromTelescopeJson(const {
        'state': 'connected',
        'capabilities': {
          'focal_length_mm': 714.0,
          'aperture_diameter_mm': 102.0,
        },
      });
      expect(o, isNotNull);
      expect(o!.focalLengthMm, 714.0);
      expect(o.apertureMm, 102.0);
      expect(o.hasAny, isTrue);
    });

    test('null when no mount is connected', () {
      expect(
          TelescopeOptics.fromTelescopeJson(const {'state': 'disconnected'}),
          isNull);
    });

    test('null when connected but the body has no capabilities block', () {
      // A malformed/unexpected response (connected, no caps) reads the same as
      // "no device" — the UI hint covers both, matching the camera-geometry pattern.
      expect(TelescopeOptics.fromTelescopeJson(const {'state': 'connected'}),
          isNull);
    });

    test('connected mount that reports neither value → hasAny false', () {
      // Most mounts NotImplement optics — caps present but the fields absent.
      final o = TelescopeOptics.fromTelescopeJson(const {
        'state': 'connected',
        'capabilities': {'can_slew': true},
      });
      expect(o, isNotNull);
      expect(o!.focalLengthMm, isNull);
      expect(o.apertureMm, isNull);
      expect(o.hasAny, isFalse);
    });

    test('non-positive / junk values map to null per field', () {
      final o = TelescopeOptics.fromTelescopeJson(const {
        'state': 'connected',
        'capabilities': {
          'focal_length_mm': 0, // unconfigured 0 → null
          'aperture_diameter_mm': 'oops', // wrong type → null
        },
      });
      expect(o!.focalLengthMm, isNull);
      expect(o.apertureMm, isNull);
    });
  });
}
