import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guiding_assistant.dart';

void main() {
  test('fromJson reads the daemon shape; missing figures stay null', () {
    final s = GuidingAssistantStatus.fromJson({
      'connected': true,
      'state': 'measuring',
      'duration_sec': 120,
      'measure_backlash': true,
      'elapsed_sec': 30.5,
      'samples': 15,
      'pixel_scale': 2.0,
    });
    expect(s.isRunning, isTrue);
    expect(s.progress, closeTo(30.5 / 120, 1e-9));
    expect(s.hasStats, isFalse);
    expect(s.raDriftPxMin, isNull);
    expect(s.arcsec(0.5), 1.0);
    expect(GuidingAssistantStatus.fromJson({}).state, 'idle');
  });

  test('suggestions list only what the guider recommended, in order', () {
    final r = GuidingAssistantRecommendations.fromJson({
      'min_move_ra_px': 0.15,
      'min_move_dec_px': 0.2,
      'min_move_seeing_based': true,
      'exposure_sec': 2.0,
      'exposure_max_sec': 4.0,
      'dec_backlash_ms': 350,
      'messages': [
        {'id': 'dec_backlash', 'text': 'Dec backlash is large'},
        {'id': 'no_text'},
      ],
    });
    expect(r.suggestions.map((s) => s.key), [
      'exposure',
      'min_move_ra',
      'min_move_dec',
      'dec_backlash',
    ]);
    expect(r.suggestions.first.value, '2–4 s');
    expect(r.suggestions[1].value, '0.15 px');
    expect(r.suggestions.last.value, '350 ms');
    expect(r.messages.map((m) => m.text), ['Dec backlash is large']);
    expect(
      GuidingAssistantRecommendations.fromJson({
        'exposure_sec': 1.5,
        'dec_guide_mode': 'North',
      }).suggestions.map((s) => '${s.key}=${s.value}'),
      ['exposure=1.5 s', 'dec_guide_mode=North'],
    );
  });
}
