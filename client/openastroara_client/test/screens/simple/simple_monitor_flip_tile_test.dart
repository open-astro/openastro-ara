import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_monitor_screen.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';

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

MonitorSnapshot _snap(MonitorFlip flip) => MonitorSnapshot(
      asOf: DateTime(2026, 11, 11, 23, 52),
      verdict: const MonitorVerdict(MonitorVerdictKind.noPlan, MonitorStatus.normal),
      now: const MonitorNow(status: MonitorStatus.off, word: MonitorNowWord.idle),
      flip: flip,
      lastFrame: const MonitorLastFrame(),
      night: const MonitorNight(),
    );

Future<void> _pump(WidgetTester tester, MonitorFlip flip) async {
  tester.view.physicalSize = const Size(1180, 820);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      keepAwakeProvider.overrideWithValue(const _KeepAwake()),
      monitorProvider.overrideWith(() => _Fixed(_snap(flip))),
    ],
    child: MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SimpleMonitorScreen(onOpen: (_) {}),
    ),
  ));
  await tester.pump();
}

Finder _inFlip(Finder f) => find.descendant(of: find.byKey(const Key('simple-monitor-flip')), matching: f);

void main() {
  testWidgets('the Flip tile says Waiting for flip, not Moving, during the pass-meridian wait (#1897)', (tester) async {
    await _pump(tester, MonitorFlip(state: 'waiting_for_meridian', at: DateTime(2026, 11, 11, 23, 52, 0).add(const Duration(seconds: 182))));
    expect(_inFlip(find.text('Waiting for flip')), findsOneWidget);
    expect(find.byKey(const Key('simple-monitor-flip-countdown')), findsOneWidget);
    expect(_inFlip(find.text('03:02')), findsOneWidget);
    expect(_inFlip(find.text('Moving…')), findsNothing);
  });

  testWidgets('the Flip tile keeps Moving and the step once the wait is over (#1897)', (tester) async {
    await _pump(tester, const MonitorFlip(state: 'flipping', step: 'Slewing'));
    expect(_inFlip(find.text('Moving…')), findsOneWidget);
    expect(_inFlip(find.text('Slewing')), findsOneWidget);
  });
}
