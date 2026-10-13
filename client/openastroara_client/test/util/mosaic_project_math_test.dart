import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/util/mosaic_project_math.dart';

void main() {
  test('#1146: a filter plan makes a panel owe the sum of its steps', () {
    const lrgb = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30),
      FilterPlanStep(filterName: 'R', exposureSeconds: 120, frameCount: 30),
      FilterPlanStep(filterName: 'G', exposureSeconds: 120, frameCount: 30),
      FilterPlanStep(filterName: 'B', exposureSeconds: 120, frameCount: 30),
    ];
    expect(
      mosaicFramesPerPanel(40, lrgb),
      120,
      reason: 'done after L+R+G+B, not after 40 lights',
    );
    expect(
      mosaicFramesPerPanel(40, const [
        FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 10),
      ]),
      10,
      reason: 'an Ha × 10 plan reaches done at 10, not never',
    );
    expect(mosaicFramesPerPanel(40, null), 40);
    expect(mosaicFramesPerPanel(40, const []), 40);
    expect(mosaicFramesPerPanel(0, null), 1, reason: 'never a zero target');
  });

  test('#1146: the centre RA is averaged on the circle', () {
    expect(circularMeanRaDeg([359, 1]), closeTo(0, 1e-9));
    expect(circularMeanRaDeg([350, 10, 0]), closeTo(0, 1e-9));
    expect(circularMeanRaDeg([311.5, 312.75, 314.0]), closeTo(312.75, 1e-9));
    expect(
      circularMeanRaDeg([90, 270]),
      0,
      reason: 'degenerate opposite points fall back to 0',
    );
    expect(circularMeanRaDeg(const []), 0);
  });
}
