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

MonitorSnapshot _snap(MonitorFlip flip, [MonitorNow? now]) => MonitorSnapshot(
  asOf: DateTime(2026, 11, 11, 23, 52),
  verdict: const MonitorVerdict(
    MonitorVerdictKind.noPlan,
    MonitorStatus.normal,
  ),
  now:
      now ??
      const MonitorNow(status: MonitorStatus.off, word: MonitorNowWord.idle),
  upNext: const [
    MonitorUpNext(MonitorUpKind.meridianFlip),
    MonitorUpNext(MonitorUpKind.park),
  ],
  flip: flip,
  lastFrame: const MonitorLastFrame(),
  night: const MonitorNight(),
);

Future<void> _pump(
  WidgetTester tester,
  MonitorFlip flip, {
  MonitorNow? now,
  Size size = const Size(1180, 820),
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        keepAwakeProvider.overrideWithValue(const _KeepAwake()),
        monitorProvider.overrideWith(() => _Fixed(_snap(flip, now))),
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

Finder _inFlip(Finder f) => find.descendant(
  of: find.byKey(const Key('simple-monitor-flip')),
  matching: f,
);

void main() {
  testWidgets(
    'the Flip tile says Waiting for flip, not Moving, during the pass-meridian wait (#1897)',
    (tester) async {
      await _pump(
        tester,
        MonitorFlip(
          state: 'waiting_for_meridian',
          at: DateTime(
            2026,
            11,
            11,
            23,
            52,
            0,
          ).add(const Duration(seconds: 182)),
        ),
      );
      expect(_inFlip(find.text('Waiting for flip')), findsOneWidget);
      expect(
        find.byKey(const Key('simple-monitor-flip-countdown')),
        findsOneWidget,
      );
      expect(_inFlip(find.text('03:02')), findsOneWidget);
      expect(_inFlip(find.text('Moving…')), findsNothing);
    },
  );

  testWidgets(
    'the Flip tile keeps Moving and the step once the wait is over (#1897)',
    (tester) async {
      await _pump(
        tester,
        const MonitorFlip(state: 'flipping', step: 'Slewing'),
      );
      expect(_inFlip(find.text('Moving…')), findsOneWidget);
      expect(_inFlip(find.text('Slewing')), findsOneWidget);
    },
  );

  // #1915: the phone column reuses the Now tile; Up next beside the state
  // block left it about 136 px.
  for (final size in const [Size(390, 844), Size(1280, 800)]) {
    final phone = size.width < 900;
    testWidgets(
      'the Now tile shows the pass-meridian countdown whole at ${size.width.toInt()}x${size.height.toInt()} (#1915)',
      (tester) async {
        final asOf = DateTime(2026, 11, 11, 23, 52);
        await _pump(
          tester,
          MonitorFlip(
            state: 'waiting_for_meridian',
            at: asOf.add(const Duration(seconds: 182)),
          ),
          size: size,
          now: MonitorNow(
            status: MonitorStatus.normal,
            word: MonitorNowWord.waitingForFlip,
            countdownTo: asOf.add(const Duration(seconds: 182)),
          ),
        );
        final tile = find.byKey(const Key('simple-monitor-now'));
        final count = find.descendant(
          of: tile,
          matching: find.byKey(const Key('simple-monitor-countdown')),
        );
        expect(count, findsOneWidget);
        expect(tester.takeException(), isNull);
        final box = tester.getRect(count);
        expect(
          box.height,
          greaterThan(60),
          reason: 'countdown scaled to a readable size',
        );
        expect(
          tester.getRect(tile).contains(box.topLeft) &&
              tester.getRect(tile).contains(box.bottomRight),
          isTrue,
        );
        if (phone) {
          expect(
            find.descendant(
              of: tile,
              matching: find.byKey(const Key('simple-monitor-up-next')),
            ),
            findsOneWidget,
          );
        }
      },
    );

    testWidgets(
      'the Now tile keeps Flipping on one line with its step at ${size.width.toInt()}x${size.height.toInt()} (#1915)',
      (tester) async {
        await _pump(
          tester,
          const MonitorFlip(state: 'flipping', step: 'Slewing'),
          size: size,
          now: const MonitorNow(
            status: MonitorStatus.normal,
            word: MonitorNowWord.flipping,
            instruction: 'Slewing to the other side',
          ),
        );
        final tile = find.byKey(const Key('simple-monitor-now'));
        final word = find.descendant(
          of: tile,
          matching: find.byWidgetPredicate(
            (w) => w is Text && w.data == 'Flipping' && w.style?.fontSize == 44,
          ),
        );
        final step = find.descendant(
          of: tile,
          matching: find.text('Slewing to the other side'),
        );
        expect(word, findsOneWidget);
        expect(step, findsOneWidget);
        expect(tester.takeException(), isNull);
        // The test font is wider than the app's, so assert the room, not the
        // line count: beside Up next the word had about 136 px, under it the tile's width.
        expect(tester.getRect(word).width, greaterThan(300));
        expect(
          tester.getRect(tile).contains(tester.getRect(step).bottomRight),
          isTrue,
        );
      },
    );
  }
}
