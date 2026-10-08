import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/loop_summary.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';

/// The run the planner builds for "Narrowband · Ha / OIII": a target block
/// (Loop 1 + until above horizon) holding Ha and OIII imaging loops.
Map<String, dynamic> _run() => buildImagingRunBody(
  raDeg: 10.1,
  decDeg: 41.7,
  targetName: 'NGC0205',
  exposureSeconds: 310,
  frameCount: 56,
  autofocusEveryNExposures: 23,
  startGuiding: true,
  ditherEveryNExposures: 1,
  filterPlan: const [
    FilterPlanStep(filterName: 'Ha', exposureSeconds: 310, frameCount: 56),
    FilterPlanStep(filterName: 'OIII', exposureSeconds: 310, frameCount: 56),
  ],
);

// Searches BELOW [n]: the run's root container shares the target's name.
Map<String, dynamic> _find(Map<String, dynamic> n, String name) {
  for (final c in childrenOf(n)) {
    final hit = _findOrNull(c, name);
    if (hit != null) return hit;
  }
  throw StateError('no $name');
}

Map<String, dynamic>? _findOrNull(Map<String, dynamic> n, String name) {
  if (n['Name'] == name) return n;
  for (final c in childrenOf(n)) {
    final hit = _findOrNull(c, name);
    if (hit != null) return hit;
  }
  return null;
}

void main() {
  test('an imaging loop says how many frames, how long and what fires', () {
    final ha = summarizeContainer(_find(_run(), 'Ha Imaging'));
    expect(ha.iterations, 56);
    expect(ha.exposuresPerPass, 1);
    expect(ha.totalFrames, 56);
    expect(ha.loopChip, '× 56 · 5.2 min · ≈ 4.8 h');
    expect(ha.triggerChip, 'AF every 23 · dither every 1');
  });

  test('a target block loops once: no × chip, its horizon end is named', () {
    final block = summarizeContainer(_find(_run(), 'NGC0205'));
    // Loop 1 + horizon: a lone horizon condition would re-slew and re-image
    // the whole block until the target set (imaging_run_body.dart).
    expect(block.iterations, 1);
    expect(block.loopChip, isNull, reason: '"× 1" says nothing a user needs');
    expect(block.conditionChip, 'while above horizon');
    expect(
      block.exposuresPerPass,
      0,
      reason: 'the slew is NOT inside the frame loop',
    );
  });

  test(
    'the Take Exposure line names its loop, the frame count and the cadence',
    () {
      final ha = _find(_run(), 'Ha Imaging');
      expect(
        exposureRepeatLine(ha),
        'Repeats 56× in Ha Imaging (≈ 4.8 h) · AF every 23 · dither every 1',
      );
    },
  );

  test('short exposures read in seconds, long totals in hours', () {
    expect(formatSeconds(30), '30 s');
    expect(formatSeconds(300), '5 min');
    expect(formatSeconds(310), '5.2 min');
    expect(formatDuration(const Duration(minutes: 25)), '25 min');
    expect(formatDuration(const Duration(minutes: 289)), '4.8 h');
  });
}
