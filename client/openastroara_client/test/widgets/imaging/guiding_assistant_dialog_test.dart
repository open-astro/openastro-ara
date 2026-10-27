import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/legacy.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/guiding_assistant.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guiding_assistant_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/guider/guiding_assistant_state.dart';
import 'package:openastroara/widgets/imaging/guiding_assistant_dialog.dart';

/// What the daemon's profile holds after an Apply: the guider's North (#1448).
class _Profile extends ProfileApi {
  _Profile() : super(const AraServer(hostname: 'localhost', port: 5555));
  int reads = 0;
  @override
  Future<Phd2Settings> getPhd2Settings() async {
    reads++;
    return const Phd2Settings(decGuideMode: 'north', guideExposureMs: 2000);
  }
}

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

  /// When set, apply waits for it — a write still in flight.
  Completer<void>? applyGate;

  @override
  Future<List<String>> apply(List<String> which) async {
    calls.add('apply ${which.join(',')}');
    if (applyGate != null) await applyGate!.future;
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

class _GuiderAt extends GuiderStatusNotifier {
  final GuiderRuntimeState runtime;
  _GuiderAt(this.runtime);
  @override
  Future<GuiderStatus?> build() async => GuiderStatus(
    name: 'Guider',
    connectionState: GuiderConnectionState.connected,
    runtimeState: runtime,
  );
}

/// Open the panel. Pass [open] false to leave it closed (the dialog's own
/// state is autoDispose) and [fake] to reuse a previous pump's guider; with
/// [shared] the pumps share one ProviderScope, so root-scoped providers (what
/// Accept applied) survive the panel's close and reopen as in the app.
Future<_Fake> _pump(
  WidgetTester t,
  GuidingAssistantStatus status, {
  GuiderRuntimeState guider = GuiderRuntimeState.starSelected,
  _Fake? fake,
  bool open = true,
  bool shared = false,
  _Profile? profile,
}) async {
  fake ??= _Fake(status);
  t.view.physicalSize = const Size(900, 900);
  t.view.devicePixelRatio = 1;
  addTearDown(t.view.resetPhysicalSize);
  addTearDown(t.view.resetDevicePixelRatio);
  await t.pumpWidget(
    ProviderScope(
      // A fresh scope per pump (new overrides), one shared scope on request.
      key: shared ? _scope : UniqueKey(),
      overrides: [
        activeServerProvider.overrideWith((ref) => ref.watch(_server)),
        guidingAssistantApiFactoryProvider.overrideWithValue((_) => fake!),
        guiderStatusProvider.overrideWith(() => _GuiderAt(guider)),
        profileApiProvider.overrideWithValue(profile ?? _Profile()),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 700,
            height: 700,
            child: open ? const GuidingAssistantPanel() : const SizedBox(),
          ),
        ),
      ),
    ),
  );
  await t.pump();
  await t.pump();
  return fake;
}

final _scope = GlobalKey();

/// The active server, switchable from a test through the scope's container.
final _server = StateProvider<AraServer?>(
  (_) => const AraServer(hostname: 'localhost', port: 5555),
);

ProviderContainer _container(WidgetTester t) =>
    ProviderScope.containerOf(t.element(find.byType(MaterialApp)));

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

  testWidgets('Accept finished after the dialog closed still marks applied', (
    t,
  ) async {
    final fake = await _pump(t, _done(), shared: true);
    final gate = Completer<void>();
    fake.applyGate = gate;
    await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
    await t.tap(find.byKey(const Key('guiding-assistant-apply')));
    await t.pump();
    // Close while the write is in flight, then let it finish.
    await _pump(t, _done(), fake: fake, open: false, shared: true);
    gate.complete();
    await t.pump();
    await _pump(t, _done(), fake: fake, shared: true);
    expect(find.text('2–4 s · applied'), findsOneWidget);
  });

  testWidgets('a run another client started clears the applied marks', (
    t,
  ) async {
    final fake = await _pump(t, _done(), shared: true);
    await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
    await t.tap(find.byKey(const Key('guiding-assistant-apply')));
    await t.pump();
    await t.pump();
    expect(find.text('2–4 s · applied'), findsOneWidget);
    await _pump(t, _done(), fake: fake, open: false, shared: true);
    // No Start from here: the guider's page (or another client) ran it.
    fake.status = GuidingAssistantStatus.fromJson({
      'connected': true,
      'state': 'measuring',
      'duration_sec': 120,
      'elapsed_sec': 5,
      'samples': 2,
    });
    await _pump(t, fake.status, fake: fake, shared: true);
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      'Measuring drift…',
    );
    fake.finish();
    await t.pump(GuidingAssistantNotifier.runningPollInterval);
    await t.pump();
    await t.pump();
    expect(find.byKey(const Key('guiding-assistant-apply')), findsOneWidget);
    expect(find.textContaining('· applied'), findsNothing);
    expect(
      t
          .widget<CheckboxListTile>(find.byKey(const Key('ga-suggest-exposure')))
          .value,
      isTrue,
      reason: 'the new run offers everything again',
    );
  });

  testWidgets('switching servers drops the applied marks', (t) async {
    final fake = await _pump(t, _done(), shared: true);
    await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
    await t.tap(find.byKey(const Key('guiding-assistant-apply')));
    await t.pump();
    await t.pump();
    expect(find.text('2–4 s · applied'), findsOneWidget);
    await _pump(t, _done(), fake: fake, open: false, shared: true);
    _container(t).read(_server.notifier).state = const AraServer(
      hostname: 'other',
      port: 5555,
    );
    await t.pump();
    await _pump(t, _done(), fake: fake, shared: true);
    expect(find.textContaining('· applied'), findsNothing);
    expect(
      t
          .widget<CheckboxListTile>(find.byKey(const Key('ga-suggest-exposure')))
          .value,
      isTrue,
    );
  });

  testWidgets('Accept re-reads the guider settings the daemon wrote', (
    t,
  ) async {
    // #1448 — Apply set North in the guider; the shared settings must show
    // it, or a later save from Settings → Guider puts Auto back.
    final profile = _Profile();
    await _pump(t, _done(), profile: profile);
    await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
    await t.tap(find.byKey(const Key('guiding-assistant-apply')));
    await t.pump();
    await t.pump();
    expect(profile.reads, 1);
    final phd2 = _container(t).read(phd2SettingsProvider);
    expect((phd2.decGuideMode, phd2.guideExposureMs), ('north', 2000));
  });

  testWidgets('an Accept that lands after a server switch marks nothing', (
    t,
  ) async {
    final profile = _Profile();
    final fake = await _pump(t, _done(), shared: true, profile: profile);
    final gate = Completer<void>();
    fake.applyGate = gate;
    await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
    await t.tap(find.byKey(const Key('guiding-assistant-apply')));
    await t.pump();
    _container(t).read(_server.notifier).state = const AraServer(
      hostname: 'other',
      port: 5555,
    );
    await t.pump();
    gate.complete();
    await t.pump();
    await t.pump();
    expect(_container(t).read(guidingAssistantAppliedProvider), isEmpty);
    expect(profile.reads, 0, reason: 'the old server\'s settings stay out');
  });

  testWidgets('a missing drift reads a bare dash', (t) async {
    await _pump(
      t,
      GuidingAssistantStatus.fromJson({
        'connected': true,
        'state': 'done',
        'samples': 1,
        'ra_rms_px': 0.3,
        'recommendations': {'messages': []},
      }),
    );
    expect(_text(t, const Key('ga-figure-0')), '—');
    expect(_text(t, const Key('ga-figure-2')), '0.30 px');
  });

  testWidgets('Start waits while the guider is guiding', (t) async {
    await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
      guider: GuiderRuntimeState.guiding,
    );
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      startsWith('Stop guiding first'),
    );
    expect(
      t
          .widget<FilledButton>(
            find.byKey(const Key('guiding-assistant-start')),
          )
          .enabled,
      isFalse,
    );
    // Settling after a dither is still a run's guiding.
    await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
      guider: GuiderRuntimeState.settling,
    );
    expect(
      t
          .widget<FilledButton>(
            find.byKey(const Key('guiding-assistant-start')),
          )
          .enabled,
      isFalse,
    );
    // Looping with a star picked (what the assistant wants) is allowed.
    await _pump(
      t,
      const GuidingAssistantStatus(connected: true, state: 'idle'),
      guider: GuiderRuntimeState.starSelected,
    );
    expect(
      _text(t, const Key('guiding-assistant-headline')),
      startsWith('Ready.'),
    );
    expect(
      t
          .widget<FilledButton>(
            find.byKey(const Key('guiding-assistant-start')),
          )
          .enabled,
      isTrue,
    );
  });

  testWidgets(
    'closing and reopening keeps the applied marks and does not re-tick them',
    (t) async {
      final fake = await _pump(t, _done(), shared: true);
      await t.ensureVisible(find.byKey(const Key('ga-suggest-min_move_dec')));
      await t.tap(find.byKey(const Key('ga-suggest-min_move_dec')));
      await t.pump();
      await t.tap(find.byKey(const Key('guiding-assistant-apply')));
      await t.pump();
      await t.pump();
      expect(fake.calls, ['apply exposure,min_move_ra,dec_backlash']);

      // Close (the panel's autoDispose state goes) and reopen on the same run.
      await _pump(t, _done(), fake: fake, open: false, shared: true);
      await _pump(t, _done(), fake: fake, shared: true);
      expect(find.text('2–4 s · applied'), findsOneWidget);
      expect(find.text('0.20 px'), findsOneWidget);
      for (final (k, ticked) in [
        ('exposure', false),
        ('min_move_ra', false),
        ('dec_backlash', false),
        ('min_move_dec', true),
      ]) {
        expect(
          t.widget<CheckboxListTile>(find.byKey(Key('ga-suggest-$k'))).value,
          ticked,
          reason: k,
        );
      }
      // Accept has nothing ticked but the one left out before.
      await t.ensureVisible(find.byKey(const Key('guiding-assistant-apply')));
      await t.tap(find.byKey(const Key('guiding-assistant-apply')));
      await t.pump();
      await t.pump();
      expect(fake.calls.last, 'apply min_move_dec');

      // A new run starts clean.
      await t.ensureVisible(find.byKey(const Key('guiding-assistant-start')));
      await t.tap(find.byKey(const Key('guiding-assistant-start')));
      await t.pump();
      await t.pump();
      fake.finish();
      await t.pump(GuidingAssistantNotifier.runningPollInterval);
      await t.pump();
      await t.pump();
      expect(find.textContaining('· applied'), findsNothing);
    },
  );
}
