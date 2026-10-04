import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/util/guide_graph_stats.dart';

void main() {
  final t = DateTime.utc(2026);
  GuideStep s(double? ra, double? dec) => GuideStep(at: t, raPx: ra, decPx: dec);
  (double?, double?) pick(GuideStep g) => (g.raPx, g.decPx);

  test('empty window → all null, zero samples', () {
    final st = GuideGraphStats.compute(const [], pick);
    expect(st.rmsTotal, isNull);
    expect(st.raOscIndex, isNull);
    expect(st.samples, 0);
  });

  test('RMS, peak and total match PHD2 definitions', () {
    final st = GuideGraphStats.compute([s(3, 4), s(-3, -4)], pick);
    expect(st.rmsRa, closeTo(3, 1e-9));
    expect(st.rmsDec, closeTo(4, 1e-9));
    expect(st.rmsTotal, closeTo(5, 1e-9));
    expect(st.peakRa, 3);
    expect(st.peakDec, 4);
    expect(st.samples, 2);
  });

  test('RA oscillation index is the fraction of sign flips between '
      'consecutive RA samples', () {
    // + − + − : 3 flips / 3 pairs = 1.0
    expect(GuideGraphStats.compute([s(1, 0), s(-1, 0), s(1, 0), s(-1, 0)], pick).raOscIndex,
        closeTo(1.0, 1e-9));
    // + + + + : 0 flips
    expect(GuideGraphStats.compute([s(1, 0), s(2, 0), s(1, 0), s(3, 0)], pick).raOscIndex, 0);
    // + + − − : 1 flip / 3 pairs
    expect(GuideGraphStats.compute([s(1, 0), s(2, 0), s(-1, 0), s(-3, 0)], pick).raOscIndex,
        closeTo(1 / 3, 1e-9));
  });

  test('a lost-star frame (null axis) is skipped and breaks the osc pairing', () {
    final st = GuideGraphStats.compute([s(1, 1), s(null, null), s(-1, -1)], pick);
    expect(st.samples, 2);
    expect(st.rmsRa, closeTo(1, 1e-9));
    expect(st.raOscIndex, isNull, reason: 'no consecutive RA pair survives the gap');
  });
}
