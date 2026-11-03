import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/planning_prefs_service.dart';

void main() {
  test(
    'magnitude floor: off by default, round-trips, clamps, bad value = off',
    () {
      expect(const PlanningPrefs().magnitudeFloor, isNull);
      expect(PlanningPrefs.fromJson(const {}).magnitudeFloor, isNull);
      expect(
        PlanningPrefs.fromJson(const {'magnitude_floor': 10.5}).magnitudeFloor,
        10.5,
      );
      expect(
        PlanningPrefs.fromJson(const {'magnitude_floor': 30}).magnitudeFloor,
        15.0,
      );
      expect(
        PlanningPrefs.fromJson(const {'magnitude_floor': 1}).magnitudeFloor,
        6.0,
      );
      expect(
        PlanningPrefs.fromJson(const {'magnitude_floor': 'x'}).magnitudeFloor,
        isNull,
      );
      final p = const PlanningPrefs().withMagnitudeFloor(9);
      expect(PlanningPrefs.fromJson(p.toJson()).magnitudeFloor, 9.0);
      expect(p.copyWith(noiseTolerancePct: 7).magnitudeFloor, 9.0);
      expect(p.withMagnitudeFloor(null).magnitudeFloor, isNull);
    },
  );
}
