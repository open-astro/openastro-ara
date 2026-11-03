import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guide_focus.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

class _Guider extends GuiderStatusNotifier {
  _Guider(this.state0);
  final String state0;
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(
      {'device_id': 'g', 'name': 'PHD2', 'state': state0, 'runtime': {'state': 'stopped'}});
}

class _Live extends AutofocusLiveNotifier {
  int marked = 0;
  @override
  AutofocusLive build() => const AutofocusLive();
  @override
  void markFocusedManually() => marked++;
}

class _GuideFocus extends GuideFocusNotifier {
  _GuideFocus(this.live0);
  final GuideFocusLive live0;
  final calls = <String>[];
  @override
  GuideFocusLive build() => live0;
  @override
  Future<void> start({required double exposureSec, int? binning}) async => calls.add('start $exposureSec');
  @override
  Future<void> stop() async => calls.add('stop');
}

GuideFocusSample _s(int seq, double hfr) => GuideFocusSample(seq: seq, hfr: hfr, stars: 48, peakAdu: 1000, fwhm: hfr * 2);

GuideFocusStatus _live(List<double> hfrs) {
  final recent = [for (var i = 0; i < hfrs.length; i++) _s(i + 1, hfrs[i])];
  return GuideFocusStatus(
      active: true, state: 'running', exposureSec: 2, seq: recent.length, latest: recent.last, recent: recent,
      bestHfr: hfrs.reduce((a, b) => a < b ? a : b));
}

late ProviderContainer _c;
late _GuideFocus _g;
late _Live _af;

Future<void> _pump(WidgetTester tester,
    {GuideFocusLive live = GuideFocusLive.idle, String guider = 'connected', bool oag = false, Widget child = const SimpleFocusPanel()}) async {
  _g = _GuideFocus(live);
  _af = _Live();
  _c = ProviderContainer(overrides: [
    guideFocusProvider.overrideWith(() => _g),
    guiderStatusProvider.overrideWith(() => _Guider(guider)),
    autofocusLiveProvider.overrideWith(() => _af),
  ]);
  addTearDown(_c.dispose);
  if (oag) _c.read(phd2SettingsProvider.notifier).setGuiderSetupType('oag');
  _c.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.guide);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: _c,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pump();
}

void main() {
  testWidgets('Main telescope / Guide camera on top; Auto / Manual only under Main', (tester) async {
    await _pump(tester);
    expect(find.byKey(const Key('simple-focus-method-auto')), findsNothing, reason: 'the guide camera is focused by hand');
    await tester.tap(find.byKey(const Key('simple-focus-scope-main')));
    await tester.pump();
    expect(_c.read(simpleFocusScopeProvider), SimpleFocusScope.main);
    expect(find.byKey(const Key('simple-focus-method-auto')), findsOneWidget);
  });

  testWidgets('ready: what to turn, and the exposure', (tester) async {
    await _pump(tester);
    expect(find.text('Ready to focus'), findsOneWidget);
    expect(find.byKey(const Key('simple-guide-exposure-2')), findsOneWidget);
  });

  testWidgets('no guider: says to connect it, in the panel', (tester) async {
    await _pump(tester, guider: 'disconnected');
    expect(find.text('Connect the guider first — the frames come through it.'), findsOneWidget);
  });

  testWidgets('an off-axis guider waits for the main telescope; Already in focus lets it go', (tester) async {
    await _pump(tester, oag: true);
    expect(find.text('Waiting for the main telescope'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guide-already-focused')));
    expect(_af.marked, 1);
  });

  testWidgets('live: the HFR, stars and frames, and which way to turn', (tester) async {
    await _pump(tester, live: GuideFocusLive(status: _live([2.0, 4.0, 3.8, 3.6, 3.2, 3.0, 2.8, 2.6])));
    expect(find.textContaining('HFR 2.60'), findsOneWidget);
    expect(find.text('Stars'), findsOneWidget);
    expect(find.text('Frames'), findsOneWidget);
    expect(find.text('Keep going'), findsOneWidget);
  });

  testWidgets('the camera choice is locked while the guide camera reads', (tester) async {
    await _pump(tester, live: GuideFocusLive(status: _live([3.0])));
    expect(tester.widget<InkWell>(find.descendant(of: find.byKey(const Key('simple-focus-scope-main')), matching: find.byType(InkWell))).onTap,
        isNull);
  });

  testWidgets("the shutter starts the guide camera's live focus, and its square stops it", (tester) async {
    await _pump(tester, child: const SimpleFocusShutter());
    await tester.tap(find.byType(SimpleFocusShutter));
    expect(_g.calls, ['start 2.0']);
  });

  testWidgets('while it reads, the shutter stops it', (tester) async {
    await _pump(tester, live: GuideFocusLive(status: _live([3.0])), child: const SimpleFocusShutter());
    await tester.tap(find.byType(SimpleFocusShutter));
    expect(_g.calls, ['stop']);
  });
}
