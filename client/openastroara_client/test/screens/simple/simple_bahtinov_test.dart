import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';

class _Pick extends MainFocusMethodNotifier {
  @override
  MainFocusMethod? build() => MainFocusMethod.bahtinov;
}

class _NoFocuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => null;
}

class _Bahtinov extends BahtinovFocusNotifier {
  _Bahtinov(this.live0);
  final BahtinovFocusLive live0;
  final calls = <String>[];
  @override
  BahtinovFocusLive build() => live0;
  @override
  Future<void> start({required double exposureSec}) async => calls.add('start $exposureSec');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> finish() async {
    calls.add('finish');
    state = state.copyWith(finished: true);
  }
}

BahtinovSample _sample(int seq, double offset) =>
    BahtinovSample(seq: seq, detected: true, offsetPx: offset, withinZone: offset.abs() <= 0.3);

BahtinovFocusStatus _running(List<double> offsets, {bool active = true}) {
  final recent = [for (var i = 0; i < offsets.length; i++) _sample(i + 1, offsets[i])];
  return BahtinovFocusStatus(
    active: active,
    state: active ? BahtinovFocusStates.running : BahtinovFocusStates.stopped,
    exposureSec: 2,
    seq: recent.length,
    latest: recent.last,
    recent: recent,
    zonePx: 0.3,
    zoneFromOptics: false,
    hasFrame: false,
  );
}

late _Bahtinov _b;

Future<void> _pump(WidgetTester tester, BahtinovFocusLive live, {Widget child = const SimpleFocusPanel()}) async {
  _b = _Bahtinov(live);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      mainFocusMethodProvider.overrideWith(_Pick.new),
      focuserProvider.overrideWith(_NoFocuser.new),
      bahtinovFocusProvider.overrideWith(() => _b),
    ],
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pump();
}

void main() {
  testWidgets('Manual before a start: fit the mask, what to do, the exposure', (tester) async {
    await _pump(tester, BahtinovFocusLive.idle);
    expect(find.text('Fit the mask, then Start'), findsOneWidget);
    expect(find.textContaining('Put the Bahtinov mask over the front'), findsOneWidget);
    expect(find.byKey(const Key('simple-bahtinov-exposure-2')), findsOneWidget);
  });

  testWidgets('while it reads: the offset big, in px from focus, and which way to turn', (tester) async {
    await _pump(tester, BahtinovFocusLive(status: _running([3.0, 3.0, 3.0, 2.0, 2.0, 2.0])));
    expect(find.text('2.00'), findsOneWidget);
    expect(find.text('px from focus'), findsOneWidget);
    expect(find.text('Keep going'), findsOneWidget);
    expect(find.text('Same way — about twice your last move again.'), findsOneWidget);
  });

  testWidgets('in focus: Finish asks for the mask to come off in the panel, then finishes', (tester) async {
    await _pump(tester, BahtinovFocusLive(status: _running([0.1, 0.1, 0.1])));
    await tester.tap(find.byKey(const Key('simple-bahtinov-finish')));
    await tester.pump();
    expect(find.text('Take the Bahtinov mask off'), findsOneWidget);
    expect(_b.calls, isEmpty, reason: 'nothing until the answer');
    await tester.tap(find.text('Not yet'));
    await tester.pump();
    expect(find.text('Take the Bahtinov mask off'), findsNothing);
    await tester.tap(find.byKey(const Key('simple-bahtinov-finish')));
    await tester.pump();
    await tester.tap(find.text('Mask removed'));
    await tester.pump();
    expect(_b.calls, ['finish']);
    expect(find.text('In focus · mask off'), findsOneWidget);
  });

  testWidgets("Manual's shutter starts the readout at the picked exposure", (tester) async {
    await _pump(tester, BahtinovFocusLive.idle, child: const SimpleFocusShutter());
    await tester.tap(find.byType(SimpleFocusShutter));
    await tester.pump();
    expect(_b.calls, ['start 2.0']);
  });

  testWidgets("while it reads, Manual's shutter is a square that stops it", (tester) async {
    await _pump(tester, BahtinovFocusLive(status: _running([2.0])), child: const SimpleFocusShutter());
    await tester.tap(find.byType(SimpleFocusShutter));
    await tester.pump();
    expect(_b.calls, ['stop']);
  });

  testWidgets('leaving Manual before Finish asks for the mask first, then switches', (tester) async {
    await _pump(tester, BahtinovFocusLive(status: _running([1.0], active: false)));
    await tester.tap(find.byKey(const Key('simple-focus-method-auto')));
    await tester.pump();
    expect(find.text('Take the Bahtinov mask off'), findsOneWidget);
    await tester.tap(find.text('Mask removed'));
    await tester.pumpAndSettle();
    expect(_b.calls, ['finish']);
  });
}
