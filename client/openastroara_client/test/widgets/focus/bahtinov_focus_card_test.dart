import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/fit_pane.dart';
import 'package:openastroara/widgets/focus/bahtinov_focus_card.dart';
import 'package:openastroara/widgets/focus/focusing_pane.dart';
import 'package:openastroara/widgets/focus/guide_focus_card.dart' show TurnAdvice;

class _StubAutofocus extends AutofocusLiveNotifier {
  @override
  AutofocusLive build() => AutofocusLive.idle;
  @override
  Future<void> refresh() async {}
}

/// Records what the card asks of the readout; [finish] runs the real logic.
class _StubBahtinov extends BahtinovFocusNotifier {
  final BahtinovFocusLive initial;
  final List<String> calls;
  _StubBahtinov(this.initial, this.calls);
  @override
  BahtinovFocusLive build() => initial;
  @override
  Future<void> refresh() async {}
  @override
  Future<void> start({required double exposureSec}) async => calls.add('start $exposureSec');
  @override
  Future<void> stop() async {
    calls.add('stop');
    state = state.copyWith(status: BahtinovFocusStatus.fromJson({..._json(state.status), 'active': false, 'state': 'stopped'}));
  }
}

class _StubFocuser extends FocuserNotifier {
  final FocuserStatus? status;
  _StubFocuser(this.status);
  @override
  Future<FocuserStatus?> build() async => status;
}

Map<String, dynamic> _json(BahtinovFocusStatus s) => {
      'active': s.active,
      'state': s.state,
      'zone_px': s.zonePx,
      'zone_from_optics': s.zoneFromOptics,
      'zone_um': s.zoneUm,
      'seq': s.seq,
      'latest': s.latest == null ? null : _sampleJson(s.latest!),
      'recent': [for (final r in s.recent) _sampleJson(r)],
    };

Map<String, dynamic> _sampleJson(BahtinovSample s) => {
      'seq': s.seq,
      'detected': s.detected,
      'problem': s.problem,
      'offset_px': s.offsetPx,
      'within_zone': s.withinZone,
    };

BahtinovSample _sample(int seq, double? offset, {double zone = 0.3}) => BahtinovSample(
      seq: seq,
      detected: offset != null,
      problem: offset == null ? BahtinovProblems.noPattern : null,
      offsetPx: offset,
      withinZone: offset != null && offset.abs() <= zone,
    );

/// A running readout whose recent offsets are [offsets] (null = no pattern).
BahtinovFocusStatus _running(List<double?> offsets, {double zone = 0.3, double? zoneUm = 13, bool active = true}) {
  final recent = [for (var i = 0; i < offsets.length; i++) _sample(i + 1, offsets[i], zone: zone)];
  return BahtinovFocusStatus(
    active: active,
    state: active ? BahtinovFocusStates.running : BahtinovFocusStates.stopped,
    exposureSec: 1,
    seq: recent.length,
    latest: recent.isEmpty ? null : recent.last,
    recent: recent,
    zonePx: zone,
    zoneFromOptics: zoneUm != null,
    zoneUm: zoneUm,
    hasFrame: true,
  );
}

FocuserStatus _focuser() => FocuserStatus(
      deviceId: 'f1',
      name: 'EAF',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      runtimeState: 'idle',
      position: 12000,
      temperature: null,
      tempCompEnabled: false,
    );

Widget _harness({
  BahtinovFocusLive bahtinov = BahtinovFocusLive.idle,
  FocuserStatus? focuser,
  List<String>? calls,
  double width = 1200,
  double height = 3200,
}) =>
    ProviderScope(
      overrides: [
        autofocusLiveProvider.overrideWith(_StubAutofocus.new),
        bahtinovFocusProvider.overrideWith(() => _StubBahtinov(bahtinov, calls ?? [])),
        focuserProvider.overrideWith(() => _StubFocuser(focuser)),
      ],
      child: MaterialApp(home: Scaffold(body: SizedBox(width: width, height: height, child: const FocusingPane()))),
    );

void main() {
  group('bahtinovHint', () {
    test('waits, then explains a missing pattern', () {
      expect(bahtinovHint(BahtinovFocusStatus.idle).advice, TurnAdvice.wait);
      final none = bahtinovHint(_running([null]));
      expect(none.title, 'No spikes');
      expect(none.detail, contains('Fit the mask'));
    });

    test('asks for a move until there is history', () {
      expect(bahtinovHint(_running([2.0, 2.0])).title, 'Make a move');
    });

    test('keep going while the offset shrinks, sized from the last move', () {
      // 3.0 → 2.0: one pixel per move, two to go.
      final h = bahtinovHint(_running([3.0, 3.0, 3.0, 2.0, 2.0, 2.0]));
      expect(h.advice, TurnAdvice.keepGoing);
      expect(h.detail, 'Same way — about twice your last move again.');
    });

    test('go back when the offset grows', () {
      final h = bahtinovHint(_running([1.0, 1.0, 1.0, 2.0, 2.0, 2.0]));
      expect(h.advice, TurnAdvice.turnBack);
      expect(h.detail, contains('Wrong way'));
    });

    test('passed focus when the sign flips, and how far back', () {
      // 1.2 → −0.6: a 1.8 px move overshot by 0.6 — back a third.
      final h = bahtinovHint(_running([1.2, 1.2, 1.2, -0.6, -0.6, -0.6]));
      expect(h.advice, TurnAdvice.turnBack);
      expect(h.detail, 'You passed focus. Turn back about a third of your last move.');
    });

    test('a frame taken mid-move does not size the next move', () {
      // 2.9 → (1.4 while turning) → 0.77: about a third of the last move to go.
      final h = bahtinovHint(_running([2.9, 2.9, 2.9, 2.9, 1.4, 0.77, 0.77, 0.77, 0.77, 0.77]));
      expect(h.advice, TurnAdvice.keepGoing);
      expect(h.detail, 'Same way — about a third of your last move again.');
    });

    test('the advice holds after the move until the next one', () {
      // Ten fast frames at the new position: still the same advice.
      final h = bahtinovHint(_running([3.0, 3.0, 3.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0]));
      expect(h.advice, TurnAdvice.keepGoing);
      expect(h.detail, 'Same way — about twice your last move again.');
    });

    test('jitter is not a move; in focus inside the zone', () {
      expect(bahtinovHint(_running([2.0, 2.05, 1.95, 2.0, 2.02, 1.98])).title, 'Make a move');
      final h = bahtinovHint(_running([2.0, 1.0, 0.1]));
      expect(h.advice, TurnAdvice.atBest);
      expect(h.detail, contains('±13 µm'));
    });
  });

  group('headline', () {
    test('follows the readout', () {
      expect(BahtinovFocusCard.headlineFor(BahtinovFocusLive.idle).$1, 'Fit the mask, then Start');
      final inFocus = BahtinovFocusCard.headlineFor(BahtinovFocusLive(status: _running([1.0, 0.1])));
      expect(inFocus, ('In focus · 0.10 px', AraColors.accentConnected));
      final passed = BahtinovFocusCard.headlineFor(BahtinovFocusLive(status: _running([1.2, 1.2, 1.2, -0.6, -0.6, -0.6])));
      expect(passed, ('Go back · 0.60 px from focus', AraColors.accentWarning));
      final stopped = BahtinovFocusCard.headlineFor(BahtinovFocusLive(status: _running([1.5], active: false)));
      expect(stopped.$1, 'Stopped · last 1.50 px from focus');
      final off = BahtinovFocusCard.headlineFor(BahtinovFocusLive(status: _running([0.1], active: false), finished: true));
      expect(off.$1, 'In focus · mask off');
    });
  });

  test('status parses the daemon snapshot', () {
    final s = BahtinovFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'exposure_sec': 1.0,
      'seq': 4,
      'zone_px': 0.62,
      'zone_from_optics': true,
      'zone_um': 27.5,
      'best_offset_px': -0.4,
      'has_frame': true,
      'latest': {
        'seq': 4,
        'detected': true,
        'offset_px': -1.8,
        'defocus_um': -79.7,
        'within_zone': false,
        'peak_adu': 65535,
        'overlay': {
          'crop_size': 256,
          'intersection_x': 128.2,
          'intersection_y': 127.1,
          'lines': [
            {'role': 'outer', 'x1': 0, 'y1': 10, 'x2': 256, 'y2': 240},
            {'role': 'central', 'x1': 100, 'y1': 0, 'x2': 150, 'y2': 256},
            {'role': 'outer', 'x1': 256, 'y1': 10, 'x2': 0, 'y2': 240},
          ],
        },
      },
      'recent': [
        {'seq': 3, 'detected': false, 'problem': 'no_pattern'},
        {'seq': 4, 'detected': true, 'offset_px': -1.8},
      ],
    });
    expect(s.latest!.offsetPx, -1.8);
    expect(s.latest!.overlay!.lines.where((l) => l.isCentral), hasLength(1));
    expect(s.recent.first.problem, BahtinovProblems.noPattern);
    expect(s.zoneUm, 27.5);
    expect(s.hasMeasurement, isTrue);
  });

  testWidgets('without a focuser the main card opens on the Bahtinov mask', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Fit the mask, then Start'), findsWidgets);
    expect(find.textContaining('Manual focuser'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Start'), findsOneWidget);
    expect(find.text('Autofocus'), findsOneWidget, reason: 'the picker offers the other method');
  });

  testWidgets('with a focuser it opens on autofocus, and the picker switches', (t) async {
    await t.pumpWidget(_harness(focuser: _focuser()));
    await t.pump();
    await t.pump();
    expect(find.text('No autofocus yet'), findsOneWidget);
    await t.tap(find.text('Bahtinov mask'));
    await t.pumpAndSettle();
    expect(find.text('Fit the mask, then Start'), findsWidgets);
  });

  testWidgets('a live readout shows the offset, the advice and the overlay', (t) async {
    final calls = <String>[];
    await t.pumpWidget(_harness(
      calls: calls,
      bahtinov: BahtinovFocusLive(status: _running([1.2, 1.2, 1.2, -0.6, -0.6, -0.6]), frame: _png, frameSeq: 6),
    ));
    await t.pump();
    expect(find.text('Go back · 0.60 px from focus'), findsOneWidget);
    expect(find.text('−0.60'), findsOneWidget);
    expect(find.text('Go back'), findsOneWidget, reason: 'the advice capsule');
    expect(find.widgetWithText(FilledButton, 'Stop'), findsOneWidget);
    expect(find.text('Finish'), findsOneWidget);
    await t.tap(find.widgetWithText(FilledButton, 'Stop'));
    expect(calls, ['stop']);
  });

  // A 1080p laptop at 150 % leaves a ≈ 1280×640 window; the Setup shell takes
  // 362 px of width and 104 of height around the pane.
  testWidgets('a live readout fits a laptop window without scrolling', (t) async {
    t.view.physicalSize = const Size(1280, 640);
    t.view.devicePixelRatio = 1;
    addTearDown(t.view.reset);
    await t.pumpWidget(_harness(
      width: 1280 - 362,
      height: 640 - 104,
      bahtinov: BahtinovFocusLive(
        status: _running([1.2, 1.2, 1.2, -0.6, -0.6, -0.6]),
        frame: _png,
        frameSeq: 6,
      ),
    ));
    await t.pump();
    final scroll = t.state<ScrollableState>(
        find.descendant(of: find.byType(FitPane), matching: find.byType(Scrollable)).first);
    expect(scroll.position.maxScrollExtent, 0);
  });

  testWidgets('a live readout fits a phone-width card', (t) async {
    t.view.physicalSize = const Size(390 * 3, 3200 * 3);
    t.view.devicePixelRatio = 3;
    addTearDown(t.view.reset);
    await t.pumpWidget(_harness(
      width: 390,
      bahtinov: BahtinovFocusLive(status: _running([1.2, 1.2, 1.2, -0.6, -0.6, -0.6]), frame: _png, frameSeq: 6),
    ));
    await t.pump();
    expect(t.takeException(), isNull);
    expect(find.text('−0.60'), findsOneWidget);
  });

  testWidgets('Finish asks for the mask, then marks the scope focused', (t) async {
    final calls = <String>[];
    late WidgetRef ref;
    await t.pumpWidget(ProviderScope(
      overrides: [
        autofocusLiveProvider.overrideWith(_StubAutofocus.new),
        bahtinovFocusProvider.overrideWith(
            () => _StubBahtinov(BahtinovFocusLive(status: _running([0.8, 0.4, 0.1]), frame: _png, frameSeq: 3), calls)),
        focuserProvider.overrideWith(() => _StubFocuser(null)),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(builder: (context, r, _) {
            ref = r;
            return const SizedBox(width: 1200, height: 3200, child: FocusingPane());
          }),
        ),
      ),
    ));
    await t.pump();
    await t.tap(find.widgetWithText(FilledButton, 'Finish'));
    await t.pumpAndSettle();
    expect(find.text('Remove the Bahtinov mask'), findsOneWidget);
    await t.tap(find.text('Not yet'));
    await t.pumpAndSettle();
    expect(calls, isEmpty);
    expect(ref.read(autofocusLiveProvider).focusedThisSession, isFalse);

    await t.tap(find.widgetWithText(FilledButton, 'Finish'));
    await t.pumpAndSettle();
    await t.tap(find.text('Mask removed'));
    await t.pumpAndSettle();
    expect(calls, ['stop']);
    expect(ref.read(autofocusLiveProvider).focusedThisSession, isTrue);
    expect(find.text('In focus · mask off'), findsOneWidget);
  });

  testWidgets('leaving Bahtinov with the mask possibly on asks first', (t) async {
    await t.pumpWidget(_harness(
      bahtinov: BahtinovFocusLive(status: _running([1.5], active: false), frame: _png, frameSeq: 1),
    ));
    await t.pump();
    await t.tap(find.text('Autofocus'));
    await t.pumpAndSettle();
    expect(find.text('Remove the Bahtinov mask'), findsOneWidget);
    await t.tap(find.text('Not yet'));
    await t.pumpAndSettle();
    expect(find.text('Stopped · last 1.50 px from focus'), findsOneWidget, reason: 'still on the Bahtinov card');
    await t.tap(find.text('Autofocus'));
    await t.pumpAndSettle();
    await t.tap(find.text('Mask removed'));
    await t.pumpAndSettle();
    expect(find.text('No autofocus yet'), findsOneWidget);
  });
}

/// A 1×1 transparent PNG: enough for Image.memory.
final _png = Uint8List.fromList(const [
  0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
  0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
  0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
  0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
  0x42, 0x60, 0x82,
]);
