import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/util/banked_hours.dart';

TonightSkyObject _o(String id, String name) => TonightSkyObject(
  id: id,
  name: name,
  type: 'galaxy',
  magnitude: 5,
  raDeg: 0,
  decDeg: 0,
  altitudeDeg: 50,
  maxAltitudeDeg: 60,
);

void main() {
  const stats = [
    StatsTarget(targetName: 'M 31', integrationHours: 2.5),
    StatsTarget(targetName: 'Andromeda Galaxy', integrationHours: 1.0),
    StatsTarget(targetName: 'NGC 7000', integrationHours: 4.0),
    StatsTarget(targetName: 'Empty', integrationHours: 0),
  ];

  test(
    'sums every stats row whose normalized name matches the id or the name',
    () {
      expect(bankedHoursFor(stats, id: 'M31', name: 'Andromeda Galaxy'), 3.5);
      expect(
        bankedHoursFor(stats, id: 'NGC 7000', name: 'North America Nebula'),
        4.0,
      );
      expect(bankedHoursFor(stats, id: 'M 42', name: 'Orion Nebula'), isNull);
      expect(
        bankedHoursFor(stats, id: 'Empty', name: 'Empty'),
        isNull,
        reason: 'zero hours is nothing banked',
      );
    },
  );

  test('bankedHoursByObjectId keys the objects that have hours; null or empty stats give nothing', () {
    final map = bankedHoursByObjectId(stats, [
      _o('M31', 'Andromeda Galaxy'),
      _o('M42', 'Orion Nebula'),
    ]);
    expect(map, {'M31': 3.5});
    expect(bankedHoursByObjectId(null, [_o('M31', 'x')]), isEmpty);
    expect(bankedHoursByObjectId(const [], [_o('M31', 'x')]), isEmpty);
  });
}
