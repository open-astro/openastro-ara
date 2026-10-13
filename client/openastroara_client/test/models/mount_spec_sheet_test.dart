import 'dart:math' as math;

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
  test('parses the bridge sheet; null fields stay unknown', () {
    final s = MountSpecSheet.fromJson(_eqm35)!;
    expect(s.model, 'Sky-Watcher EQM-35 Pro');
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
    expect(sparse.trackingFact, 'Periodic error unknown · manufacturer');
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

  test('the EQM-35 example gives the same prior as the daemon', () {
    final s = MountSpecSheet.fromJson(_eqm35);
    // RedCat at 2.3"/px, seeing 3": tolerance max(2.3, 1.5) = 2.3"; pe_rate 2π·12.5/638.
    final prior = unguidedPriorFromSpecs(
      s,
      pixelScaleArcsec: 2.3,
      seeingFwhmArcsec: 3.0,
    )!;
    expect(prior, closeTo(2.3 / (2 * math.pi * 12.5 / 638), 1e-6));
    expect(prior, closeTo(18.7, 0.1));
    // Fine pixels: seeing sets the tolerance.
    expect(
      unguidedPriorFromSpecs(s, pixelScaleArcsec: 0.8, seeingFwhmArcsec: 3.0),
      closeTo(1.5 / (2 * math.pi * 12.5 / 638), 1e-6),
    );
  });

  test('encoders drop the periodic-error term; polar misalignment adds drift; floors and nulls', () {
    final enc = MountSpecSheet.fromJson({..._eqm35, 'has_encoders': true});
    expect(
      unguidedPriorFromSpecs(enc, pixelScaleArcsec: 2.3, seeingFwhmArcsec: 3.0),
      isNull,
    );
    final paRate = 15.04 * math.sin(10 / 60 * math.pi / 180);
    expect(
      unguidedPriorFromSpecs(
        enc,
        pixelScaleArcsec: 2.3,
        seeingFwhmArcsec: 3.0,
        polarAlignErrorArcmin: 10,
      ),
      closeTo(2.3 / paRate, 1e-6),
    );
    final both = unguidedPriorFromSpecs(
      MountSpecSheet.fromJson(_eqm35),
      pixelScaleArcsec: 2.3,
      seeingFwhmArcsec: 3.0,
      polarAlignErrorArcmin: 10,
    )!;
    expect(both, lessThan(2.3 / paRate));
    expect(
      unguidedPriorFromSpecs(MountSpecSheet.fromJson(_eqm35)),
      isNull,
      reason: 'no tolerance',
    );
    final wild = MountSpecSheet.fromJson({
      ..._eqm35,
      'periodic_error_pp_arcsec': 5000,
      'worm_period_s': 10,
    });
    expect(
      unguidedPriorFromSpecs(
        wild,
        pixelScaleArcsec: 2.3,
        seeingFwhmArcsec: 3.0,
      ),
      minUnguidedPriorSeconds,
    );
    expect(
      unguidedPriorFromSpecs(null, pixelScaleArcsec: 2.3),
      isNull,
      reason: 'no sheet, no PA: nothing to bound',
    );
  });
}
