import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';

FocuserStatus _focuser({int? position = 1000, bool absolute = true, int max = 1100, String runtime = 'idle'}) =>
    FocuserStatus(
      deviceId: 'f',
      name: 'Focuser',
      connectionState: EquipmentConnectionState.connected,
      capabilities: FocuserCapabilities(
        minPosition: 0,
        maxPosition: max,
        stepSizeUm: 1,
        canTempComp: false,
        absoluteFocuser: absolute,
      ),
      runtimeState: runtime,
      position: position,
      temperature: null,
      tempCompEnabled: false,
    );

AutofocusProbe _probe(int i, double hfr, {bool kept = true}) =>
    AutofocusProbe(index: i, phase: AutofocusProbePhases.fine, position: 900 + i * 50, hfr: hfr, stars: 40, kept: kept);

class _Focuser extends FocuserNotifier {
  _Focuser(this.status);
  final FocuserStatus? status;
  final moves = <int>[];
  @override
  Future<FocuserStatus?> build() async => status;
  @override
  Future<bool> move(int targetPosition, {bool useTempComp = false}) async {
    moves.add(targetPosition);
    return true;
  }
}

class _Live extends AutofocusLiveNotifier {
  _Live(this.live);
  final AutofocusLive live;
  int starts = 0;
  int cancels = 0;
  @override
  AutofocusLive build() => live;
  @override
  Future<void> start() async => starts++;
  @override
  Future<void> cancel() async => cancels++;
}

Future<(_Focuser, _Live)> _pump(WidgetTester tester, {FocuserStatus? focuser, AutofocusRun run = AutofocusRun.idle}) async {
  final f = _Focuser(focuser);
  final l = _Live(AutofocusLive(run: run));
  await tester.pumpWidget(ProviderScope(
    overrides: [
      focuserProvider.overrideWith(() => f),
      autofocusLiveProvider.overrideWith(() => l),
    ],
    child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusPanel()))),
  ));
  await tester.pump();
  return (f, l);
}

void main() {
  group('simpleFocusHfr', () {
    test('nothing before a run', () => expect(simpleFocusHfr(AutofocusRun.idle), isNull));
    test('the latest kept probe while running', () {
      final run = AutofocusRun(state: AutofocusRunStates.running, probes: [_probe(0, 3.1), _probe(1, 2.4), _probe(2, 9, kept: false)]);
      expect(simpleFocusHfr(run), 2.4);
    });
    test('the confirmed HFR once complete', () {
      final run = AutofocusRun(state: AutofocusRunStates.complete, finalHfr: 1.98, probes: [_probe(0, 2.4)]);
      expect(simpleFocusHfr(run), 1.98);
    });
  });

  group('simpleNudgeTarget', () {
    test('absolute: position + delta, clamped to the range', () {
      expect(simpleNudgeTarget(_focuser(), -50), 950);
      expect(simpleNudgeTarget(_focuser(), 250), 1100);
    });
    test('absolute with no range known: not clamped', () {
      expect(simpleNudgeTarget(_focuser(max: 0), 250), 1250);
    });
    test('absolute with no position yet: nothing to send', () {
      expect(simpleNudgeTarget(_focuser(position: null), 50), isNull);
    });
    test('relative: the delta itself', () {
      expect(simpleNudgeTarget(_focuser(absolute: false), -250), -250);
    });
  });

  testWidgets('no focuser: says so, Autofocus and the nudges are off', (tester) async {
    await _pump(tester);
    expect(find.text('No focuser connected'), findsOneWidget);
    expect(tester.widget<FilledButton>(find.byKey(const Key('simple-autofocus'))).onPressed, isNull);
    for (final icon in [Icons.chevron_left, Icons.chevron_right]) {
      final ink = find.ancestor(of: find.byIcon(icon), matching: find.byType(InkWell));
      expect(tester.widget<InkWell>(ink).onTap, isNull);
    }
  });

  testWidgets('a connected focuser: position, Autofocus starts a run, nudges move by the step size', (tester) async {
    final (f, l) = await _pump(tester, focuser: _focuser());
    expect(find.text('Position 1000'), findsOneWidget);
    expect(find.text('HFR —'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-autofocus')));
    expect(l.starts, 1);
    // The profile's default step is 50; large is five steps.
    await tester.tap(find.byIcon(Icons.chevron_left));
    await tester.tap(find.byIcon(Icons.keyboard_double_arrow_right));
    expect(f.moves, [950, 1100]);
  });

  testWidgets('while a run goes: the curve, Cancel, and no nudges', (tester) async {
    final run = AutofocusRun(
      state: AutofocusRunStates.running,
      phase: 'sweep',
      totalSteps: 7,
      completedSteps: 2,
      stepSize: 80,
      probes: [_probe(0, 3.1), _probe(1, 2.4)],
    );
    final (f, l) = await _pump(tester, focuser: _focuser(), run: run);
    expect(find.text('HFR 2.40'), findsOneWidget);
    expect(find.textContaining('Sweeping'), findsOneWidget);
    expect(find.byKey(const Key('simple-autofocus')), findsNothing);
    await tester.tap(find.byKey(const Key('simple-focus-cancel')));
    expect(l.cancels, 1);
    await tester.tap(find.byIcon(Icons.chevron_left));
    expect(f.moves, isEmpty);
  });
}
