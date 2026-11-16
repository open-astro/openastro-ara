import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';

// Monitor's text defects (#1875 slice 7): each case is red at the commit
// before the rework.

class _KeepAwake extends KeepAwake {
  const _KeepAwake();
  @override
  Future<void> set(bool on) async {}
}

class _Fixed extends MonitorState {
  _Fixed(this.snap);
  final MonitorSnapshot snap;
  @override
  MonitorSnapshot build() => snap;
}

final _asOf = DateTime(2026, 11, 15, 0, 23);

/// A colour camera mid-frame: one plan row with no filter name.
MonitorSnapshot _snap({String? filter = '', MonitorLastFrame lastFrame = const MonitorLastFrame()}) => MonitorSnapshot(
  asOf: _asOf,
  verdict: const MonitorVerdict(MonitorVerdictKind.allGood, MonitorStatus.normal, target: 'NGC 1269'),
  now: MonitorNow(
    status: MonitorStatus.working,
    word: MonitorNowWord.exposing,
    target: 'NGC 1269',
    filter: filter,
    subSeconds: 60,
    frameEndsAt: _asOf.add(const Duration(seconds: 36)),
    frameSeconds: 60,
    frame: 27,
    frames: 120,
    targetSecondsLeft: 5640,
    filters: [(filter: filter ?? '', done: 26, total: 120, inHand: true)],
  ),
  upNext: [MonitorUpNext(MonitorUpKind.nextFrame, at: _asOf.add(const Duration(seconds: 36)), frame: 28, name: filter)],
  flip: const MonitorFlip(state: null),
  lastFrame: lastFrame,
  night: const MonitorNight(),
);

Future<void> _pump(WidgetTester tester, MonitorSnapshot snap) async {
  tester.view.physicalSize = const Size(1288, 780);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(snap)),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: SimpleMonitorScreen(onOpen: (_) {}),
      ),
    ),
  );
  await tester.pump();
}

void main() {
  testWidgets('a camera with no filter shows no filter text and no separator left behind', (tester) async {
    await _pump(tester, _snap());
    expect(find.text('NGC 1269 · 60 s'), findsOneWidget);
    expect(find.text('Frame 28'), findsOneWidget);
  });

  testWidgets('a frame is counted once: Frame 27 of 120 beside "26 saved", never a bare 26/120', (tester) async {
    await _pump(tester, _snap());
    expect(find.textContaining('Frame 27 of 120'), findsOneWidget);
    expect(find.text('26 saved'), findsOneWidget);
    expect(find.text('26/120'), findsNothing);
  });

  testWidgets('the frames line does not repeat the target the tile already names', (tester) async {
    await _pump(tester, _snap());
    expect(find.textContaining(' on NGC 1269'), findsNothing);
  });

  testWidgets('the autofocus line ends on "since then", not a dangling "since"', (tester) async {
    await _pump(
      tester,
      _snap(
        lastFrame: MonitorLastFrame(
          focuser: true,
          autofocusAt: DateTime(2026, 11, 14, 23, 50),
          tempSinceAutofocus: -0.8,
        ),
      ),
    );
    expect(find.text('Autofocus 23:50 · −0.8 °C since then'), findsOneWidget);
  });
}
