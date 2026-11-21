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

  test('#1264 MountProps carries the bridge spec sheet from capabilities.mount_specs', () {
    final p = MountProps.fromTelescopeJson(const {
      'state': 'connected',
      'name': 'EQM-35',
      'capabilities': {
        'mount_specs': {
          'model': 'Sky-Watcher EQM-35 Pro',
          'source': 'community',
          'worm_period_s': 638,
          'periodic_error_pp_arcsec': 25,
        },
      },
    });
    expect(p!.specs, isNotNull);
    expect(p.specs!.source, 'community');
    expect(p.specs!.wormPeriodS, 638);
    expect(p.hasAny, isTrue);
    final none = MountProps.fromTelescopeJson(const {'state': 'connected', 'name': 'x', 'capabilities': {}});
    expect(none!.specs, isNull);
  });
}
