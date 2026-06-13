import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';

void main() {
  group('CalibrationStatus.fromJson', () {
    test('parses a fully-populated status', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{
        'profile_id': 7,
        'dark_library_path': '/darks/p7',
        'defect_map_path': '/defects/p7',
        'dark_library_exists': true,
        'defect_map_exists': true,
        'dark_library_compatible': true,
        'defect_map_compatible': false,
        'dark_library_loaded': true,
        'defect_map_loaded': false,
        'auto_load_darks': true,
        'auto_load_defect_map': false,
        'dark_count_loaded': 20,
        'dark_min_exposure_seconds_loaded': 1.0,
        'dark_max_exposure_seconds_loaded': 4.5,
      });
      expect(s.profileId, 7);
      expect(s.darkLibraryPath, '/darks/p7');
      expect(s.darkLibraryExists, isTrue);
      expect(s.defectMapCompatible, isFalse);
      expect(s.darkCountLoaded, 20);
      expect(s.darkMaxExposureSecondsLoaded, 4.5);
    });

    test('absent booleans default to false; absent numbers to null', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{'profile_id': 1});
      expect(s.darkLibraryExists, isFalse);
      expect(s.autoLoadDarks, isFalse);
      expect(s.darkCountLoaded, isNull);
      expect(s.darkMinExposureSecondsLoaded, isNull);
    });

    test('wrong-typed fields degrade rather than throwing', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{
        'profile_id': 'nope',
        'dark_library_path': 5,
        'dark_library_exists': 'yes',
        'dark_count_loaded': 'many',
        'dark_min_exposure_seconds_loaded': 'x',
      });
      expect(s.profileId, isNull, reason: 'wrong-typed id → null, not a coerced 0');
      expect(s.darkLibraryPath, isNull);
      expect(s.darkLibraryExists, isFalse);
      expect(s.darkCountLoaded, isNull);
      expect(s.darkMinExposureSecondsLoaded, isNull);
    });

    test('a fractional profile_id degrades to null (no silent truncation)', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{'profile_id': 1.9});
      expect(s.profileId, isNull, reason: 'a fractional id must not become a valid-looking 1');
    });

    test('non-finite exposures are filtered to null', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{
        'profile_id': 1,
        'dark_min_exposure_seconds_loaded': double.nan,
        'dark_max_exposure_seconds_loaded': double.infinity,
      });
      expect(s.darkMinExposureSecondsLoaded, isNull);
      expect(s.darkMaxExposureSecondsLoaded, isNull);
    });

    test('integer exposure widens to double', () {
      final s = CalibrationStatus.fromJson(<String, dynamic>{
        'profile_id': 1,
        'dark_min_exposure_seconds_loaded': 2,
      });
      expect(s.darkMinExposureSecondsLoaded, 2.0);
    });
  });

  group('value equality', () {
    Map<String, dynamic> frame() => <String, dynamic>{
          'connected': true,
          'status': <String, dynamic>{
            'profile_id': 3,
            'dark_library_exists': true,
            'dark_count_loaded': 12,
          },
        };
    test('equal-content responses compare equal (no rebuild churn)', () {
      final a = CalibrationStatusResponse.fromJson(frame());
      final b = CalibrationStatusResponse.fromJson(frame());
      expect(a, equals(b));
      expect(a.hashCode, b.hashCode);
      expect(a.status, equals(b.status));
    });
    test('a changed field breaks equality', () {
      final a = CalibrationStatusResponse.fromJson(frame());
      final c = CalibrationStatusResponse.fromJson(frame()..['status']['dark_library_exists'] = false);
      expect(a, isNot(equals(c)));
    });
  });

  group('CalibrationStatusResponse.fromJson', () {
    test('connected with status', () {
      final r = CalibrationStatusResponse.fromJson(<String, dynamic>{
        'connected': true,
        'status': <String, dynamic>{'profile_id': 3, 'dark_library_exists': true},
      });
      expect(r.connected, isTrue);
      expect(r.status!.profileId, 3);
      expect(r.status!.darkLibraryExists, isTrue);
    });

    test('not connected → null status', () {
      final r = CalibrationStatusResponse.fromJson(<String, dynamic>{'connected': false, 'status': null});
      expect(r.connected, isFalse);
      expect(r.status, isNull);
    });
  });
}
