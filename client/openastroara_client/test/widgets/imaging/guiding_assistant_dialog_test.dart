import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guiding_assistant.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guiding_assistant_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/guider/guiding_assistant_state.dart';
import 'package:openastroara/widgets/imaging/guiding_assistant_dialog.dart';

class _Fake implements GuidingAssistantClient {
  GuidingAssistantStatus status;
  final calls = <String>[];
  bool refuseStart = false;
  _Fake(this.status);
  @override
  Future<GuidingAssistantStatus> getStatus() async => status;
  @override
  Future<GuidingAssistantStatus> start({
    double? durationSec,
    required bool measureBacklash,
  }) async {
    calls.add('start $durationSec $measureBacklash');
    if (refuseStart) {
      throw StateError(
        'no star selected - start looping and select a star first',
      );
    }
    status = GuidingAssistantStatus.fromJson({
      'connected': true,
      'state': 'measuring',
      'duration_sec': durationSec ?? 120,
      'measure_backlash': measureBacklash,
      'elapsed_sec': 0,
      'samples': 0,
    });
    return status;
  }

  /// Finish the fake's run with the same sample count as [_done] (a fixed
  /// duration and exposure give the same count run after run).
  void finish() => status = _done();

  @override
  Future<GuidingAssistantStatus> stop() async {
    calls.add('stop');
    status = const GuidingAssistantStatus(connected: true, state: 'done');
    return status;
  }

  @override
  Future<List<String>> apply(List<String> which) async {
    calls.add('apply ${which.join(',')}');
    return which;
  }

  @override
  void close() {}
}

GuidingAssistantStatus _done() => GuidingAssistantStatus.fromJson({
  'connected': true,
  'state': 'done',
  'duration_sec': 120,
  'measure_backlash': true,
  'elapsed_sec': 121,
  'samples': 60,
  'pixel_scale': 2.0,
  'ra_drift_px_min': 0.42,
  'dec_drift_px_min': -1.1,
  'ra_rms_px': 0.31,
  'dec_rms_px': 0.22,
  'high_freq_rms_px': 0.18,
  'ra_peak_peak_px': 1.6,
  'snr': 41.5,
  'polar_align_error_arcmin': 4.2,
  'polar_align_error_is_lower_bound': true,
  'backlash_ms': 350,
  'backlash_sigma_ms': 40,
  'backlash_px': 2.1,
  'backlash_result': 'Backlash is moderate',
  'recommendations': {
    'min_move_ra_px': 0.15,
    'min_move_dec_px': 0.2,
    'min_move_seeing_based': true,
    'exposure_sec': 2.0,
    'exposure_max_sec': 4.0,
    'dec_backlash_ms': 350,
    'messages': [
      {'id': 'dec_backlash', 'text': 'Dec backlash is large'},
    ],
  },
});

Future<_Fake> _pump(WidgetTester t, GuidingAssistantStatus status) async {
  final fake = _Fake(status);
  t.view.physicalSize = const Size(900, 900);
  t.view.devicePixelRatio = 1;
  addTearDown(t.view.resetPhysicalSize);
  addTearDown(t.view.resetDevicePixelRatio);
  await t.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'localhost', port: 5555),
        ),
        guidingAssistantApiFactoryProvider.overrideWithValue((_) => fake),
      ],
      child: const MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 700,
            height: 700,
            child: GuidingAssistantPanel(),
          ),
        ),
      ),
    ),
  );
  await t.pump();
  await t.pump();
  return fake;
}

String _text(WidgetTester t, Key key) => t.widget<Text>(find.byKey(key)).data!;

void main() {
  testWidgets('idle: Start sends the chosen duration and backlash flag', (
    t,
  ) async {
    final fake = await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
    );
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      startsWith('Ready.'),
    );
    expect(find.byKey(const Key('guiding-assistant-apply')), findsNothing);

    await t.tap(find.byKey(const Key('guiding-assistant-backlash')));
    await t.pump();
    await t.tap(find.byKey(const Key('guiding-assistant-start')));
    await t.pump();
    await t.pump();
    expect(fake.calls, ['start 120.0 false']);
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      'Measuring drift…',
    );
    expect(
      _text(t, const Key('guiding-assistant-progress')),
      '0 of 120 s · 0 samples',
    );
    expect(find.byKey(const Key('guiding-assistant-stop')), findsOneWidget);
    expect(find.byKey(const Key('guiding-assistant-start')), findsNothing);
  });

  testWidgets('a refused start shows the guider\'s words', (t) async {
    final fake = await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
    );
    fake.refuseStart = true;
    await t.tap(find.byKey(const Key('guiding-assistant-start')));
    await t.pump();
    await t.pump();
    expect(
      _text(t, const Key('guiding-assistant-error')),
      contains('no star selected'),
    );
    expect(find.byKey(const Key('guiding-assistant-start')), findsOneWidget);
  });

  testWidgets(
    'done: figures, backlash, every suggestion ticked; Accept applies the ticked ones in order',
    (t) async {
      final fake = await _pump(t, _done());
      expect(
        _text(t, const Key('guiding-assistant-headline')),
        startsWith('Done'),
      );
      // RA drift in px and arcsec (scale 2), polar error as a lower bound.
      expect(_text(t, const Key('ga-figure-0')), '0.42 px (0.84″)/min');
      expect(_text(t, const Key('ga-figure-6')), '≥ 4.2′');
      expect(_text(t, const Key('ga-backlash-0')), '350 ms ± 40 (2.1 px)');
      expect(find.text('Dec backlash is large'), findsOneWidget);
      for (final k in [
        'exposure',
        'min_move_ra',
        'min_move_dec',
        'dec_backlash',
      ]) {
        expect(
          t.widget<CheckboxListTile>(find.byKey(Key('ga-suggest-$k'))).value,
          isTrue,
          reason: k,
        );
      }
      expect(find.text('2–4 s'), findsOneWidget);

      await t.ensureVisible(find.byKey(const Key('ga-suggest-min_move_dec')));
      await t.tap(find.byKey(const Key('ga-suggest-min_move_dec')));
      await t.pump();
      await t.tap(find.byKey(const Key('guiding-assistant-apply')));
      await t.pump();
      await t.pump();
      expect(fake.calls, ['apply exposure,min_move_ra,dec_backlash']);
      expect(find.text('2–4 s · applied'), findsOneWidget);
      expect(
        find.text('0.20 px'),
        findsOneWidget,
        reason: 'the unticked one is not marked applied',
      );
      expect(
        t
            .widget<FilledButton>(
              find.byKey(const Key('guiding-assistant-start')),
            )
            .enabled,
        isTrue,
      );
      expect(find.text('Run again'), findsOneWidget);
    },
  );

  testWidgets('failed and not-available states', (t) async {
    await _pump(
      t,
      const GuidingAssistantStatus(
        connected: true,
        state: 'failed',
        error: 'star lost',
      ),
    );
    expect(
      _text(t, const Key('guiding-assistant-failed')),
      'The measurement failed: star lost',
    );

    await _pump(t, GuidingAssistantStatus.disconnected);
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      startsWith('Not available'),
    );
    expect(
      t
          .widget<FilledButton>(
            find.byKey(const Key('guiding-assistant-start')),
          )
          .enabled,
      isFalse,
    );
  });

  testWidgets(
    'Run again re-ticks every suggestion even when the sample count repeats',
    (t) async {
      final fake = await _pump(t, _done());
      await t.ensureVisible(find.byKey(const Key('ga-suggest-exposure')));
      await t.tap(find.byKey(const Key('ga-suggest-exposure')));
      await t.pump();
      expect(
        t
            .widget<CheckboxListTile>(
              find.byKey(const Key('ga-suggest-exposure')),
            )
            .value,
        isFalse,
      );

      await t.ensureVisible(find.byKey(const Key('guiding-assistant-start')));
      await t.tap(find.byKey(const Key('guiding-assistant-start')));
      await t.pump();
      await t.pump();
      expect(find.byKey(const Key('guiding-assistant-stop')), findsOneWidget);
      // Run 2 ends with samples: 60 again, exactly as run 1 did.
      fake.finish();
      await t.pump(GuidingAssistantNotifier.runningPollInterval);
      await t.pump();
      await t.pump();
      expect(find.byKey(const Key('guiding-assistant-apply')), findsOneWidget);
      for (final k in [
        'exposure',
        'min_move_ra',
        'min_move_dec',
        'dec_backlash',
      ]) {
        expect(
          t.widget<CheckboxListTile>(find.byKey(Key('ga-suggest-$k'))).value,
          isTrue,
          reason: '$k re-ticked for the new run',
        );
      }
    },
  );

  testWidgets('polling continues after a refused action', (t) async {
    // Review of #1387: an action's failure path never re-armed the poll, so a
    // Stop refused because the run had just finished left "Measuring…" on
    // screen for good.
    final fake = await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
    );
    fake.refuseStart = true;
    await t.tap(find.byKey(const Key('guiding-assistant-start')));
    await t.pump();
    await t.pump();
    expect(find.byKey(const Key('guiding-assistant-error')), findsOneWidget);
    // The guider moved on regardless; the next poll must show it.
    fake.status = GuidingAssistantStatus.fromJson({
      'connected': true,
      'state': 'measuring',
      'duration_sec': 120,
      'elapsed_sec': 5,
      'samples': 2,
    });
    await t.pump(GuidingAssistantNotifier.idlePollInterval);
    await t.pump();
    await t.pump();
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      'Measuring drift…',
    );
  });
}
