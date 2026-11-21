import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_live.dart';

void main() {
  GuiderLive live(String state, {double? snr, double? mass}) =>
      GuiderLive.fromJson({
        'connected': true,
        'app_state': state,
        'snr': snr,
        'star_mass': mass,
        'stars': const [],
        'has_frame': true,
      });

  test('the header shows the SNR while guiding and for a chosen star', () {
    // #1364 tap item (2026-10-18): a tapped star read "Looping" with no SNR.
    expect(live('Guiding', snr: 42, mass: 12000).summary,
        'Guiding · SNR 42 · mass 12.0k');
    expect(live('Selected', snr: 42, mass: 12000).summary,
        'Star selected · SNR 42 · mass 12.0k');
  });

  test('a looping or stopped guider shows no stale reading', () {
    expect(live('Looping', snr: 42).summary, 'Looping');
    expect(live('Stopped', snr: 42).summary, 'Stopped');
    expect(live('LostLock', snr: 42).summary, 'Star lost');
  });
}
