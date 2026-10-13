import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_spec_sheet.dart';

const _eqm35 = {
  'schema': 'openastro.mount-specs/1',
  'driver_id': 'SkyWatcher_EQM35',
  'model': 'Sky-Watcher EQM-35 Pro',
  'source': 'manufacturer',
  'worm_period_s': 638,
  'periodic_error_pp_arcsec': 25,
  'has_encoders': false,
  'has_pec': true,
  'mount_type': 'worm_eq',
  'max_payload_kg': 10,
};

void main() {
  test('parses the bridge sheet; null fields stay unknown, never a guess', () {
    final s = MountSpecSheet.fromJson(_eqm35)!;
    expect(s.model, 'Sky-Watcher EQM-35 Pro');
    expect(s.source, 'manufacturer');
    expect(s.wormPeriodS, 638);
    expect(s.periodicErrorPpArcsec, 25);
    expect(s.hasPec, isTrue);
    expect(s.hasTrackingFigures, isTrue);
    expect(s.trackingFact, 'Periodic error 25″ p-p over 638 s · manufacturer');

    final sparse = MountSpecSheet.fromJson(const {
      'model': 'ZWO AM5',
      'worm_period_s': null,
    })!;
    expect(sparse.hasTrackingFigures, isFalse);
    expect(sparse.source, isNull, reason: 'the sheet did not say');
    expect(sparse.trackingFact, 'Periodic error unknown');
    expect(
      MountSpecSheet.fromJson(const {
        'has_encoders': true,
        'source': 'measured',
      })!.trackingFact,
      'Encoders (no periodic error) · measured',
    );
    expect(MountSpecSheet.fromJson(null), isNull);
    expect(MountSpecSheet.fromJson('x'), isNull);
  });

  test('value equality', () {
    expect(MountSpecSheet.fromJson(_eqm35), MountSpecSheet.fromJson(_eqm35));
    expect(
      MountSpecSheet.fromJson(_eqm35),
      isNot(MountSpecSheet.fromJson({..._eqm35, 'has_pec': false})),
    );
  });
}
