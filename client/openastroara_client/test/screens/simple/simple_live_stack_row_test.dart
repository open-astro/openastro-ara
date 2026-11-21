import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/l10n/app_localizations_en.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_live_stack_row.dart';
import 'package:openastroara/screens/simple/simple_queue_screen.dart';
import 'package:openastroara/services/session_live_stack_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_live_stack_state.dart';

import '../../helpers/fake_session_live_stack_client.dart';

class _MemPrefs extends SimpleModePrefsService {
  _MemPrefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSessionLiveStackClient daemon, {
  bool lastLight = false,
  Widget child = const SimpleLiveStackRow(),
}) async {
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: '192.0.2.10', port: 5000),
      ),
      sessionLiveStackApiFactoryProvider.overrideWithValue((_) => daemon),
      simpleLiveStackViewPrefsProvider.overrideWithValue(_MemPrefs(lastLight)),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Center(child: SizedBox(width: 248, child: child)),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return c;
}

final _row = find.byKey(const Key('simple-live-stack-row'));

void main() {
  group('simpleLiveStackReadout', () {
    final l = AppLocalizationsEn();
    SessionLiveStackStatus status({
      int lights = 3,
      int unregistered = 0,
      int skipped = 0,
    }) => SessionLiveStackStatus(
      enabled: true,
      stackSeq: lights,
      lightsStacked: lights,
      lightsUnregistered: unregistered,
      lightsSkippedBusy: skipped,
    );

    test('lights only: "Live Stack · N lights", nothing left out', () {
      expect(simpleLiveStackReadout(l, status()), 'Live Stack · 3 lights');
    });

    test('one light reads singular', () {
      expect(
        simpleLiveStackReadout(l, status(lights: 1)),
        'Live Stack · 1 light',
      );
    });

    test('unregistered plus skipped-busy lights read "· M left out"', () {
      expect(
        simpleLiveStackReadout(l, status(unregistered: 2, skipped: 1)),
        'Live Stack · 3 lights · 3 left out',
      );
    });

    test('the reason words for not_colour and too_large, none otherwise', () {
      expect(simpleLiveStackReason(l, 'not_colour'), isNotEmpty);
      expect(simpleLiveStackReason(l, 'too_large'), isNotEmpty);
      expect(
        simpleLiveStackReason(l, 'not_colour'),
        isNot(simpleLiveStackReason(l, 'too_large')),
      );
      expect(simpleLiveStackReason(l, 'stacked'), isNull);
      expect(simpleLiveStackReason(l, null), isNull);
    });
  });

  for (final token in ['not_colour', 'too_large']) {
    testWidgets('a camera the daemon calls $token: the row shows unavailable '
        'with its reason, and no switch', (tester) async {
      await _pump(tester, FakeSessionLiveStackClient()..unsupported = token);
      expect(_row, findsOneWidget);
      final reason = find.byKey(const Key('simple-live-stack-unavailable'));
      expect(reason, findsOneWidget);
      expect(
        tester.widget<Text>(reason).data,
        simpleLiveStackReason(AppLocalizationsEn(), token),
      );
      expect(find.byKey(const Key('simple-live-stack-switch')), findsNothing);
    });
  }

  for (final token in ['no_camera', 'not_available']) {
    testWidgets('$token: no Live Stack row at all', (tester) async {
      await _pump(tester, FakeSessionLiveStackClient()..unsupported = token);
      expect(_row, findsNothing);
    });
  }

  testWidgets('a binned colour camera (supported, last_outcome not_colour): '
      'the stack that never starts says why in the row', (tester) async {
    final daemon = FakeSessionLiveStackClient()
      ..enabled = true
      ..lastOutcome = 'not_colour';
    await _pump(tester, daemon);
    final why = find.byKey(const Key('simple-live-stack-outcome'));
    expect(why, findsOneWidget);
    expect(
      tester.widget<Text>(why).data,
      simpleLiveStackReason(AppLocalizationsEn(), 'not_colour'),
    );
  });

  testWidgets('a colour camera: the row shows with the switch off and Reset '
      'disabled', (tester) async {
    await _pump(tester, FakeSessionLiveStackClient());
    expect(_row, findsOneWidget);
    expect(
      tester
          .widget<Switch>(find.byKey(const Key('simple-live-stack-switch')))
          .value,
      isFalse,
    );
    expect(
      find.byKey(const Key('simple-live-stack-view')),
      findsNothing,
      reason: 'the view choice is for a stack that is on',
    );
    expect(
      tester
          .widget<TextButton>(find.byKey(const Key('simple-live-stack-reset')))
          .onPressed,
      isNull,
    );
  });

  testWidgets('the switch starts the stack; Reset then resets it, the stack '
      'stays on', (tester) async {
    final daemon = FakeSessionLiveStackClient();
    final c = await _pump(tester, daemon);
    await tester.tap(find.byKey(const Key('simple-live-stack-switch')));
    await tester.pump();
    await tester.pump();
    expect(daemon.count('start'), 1);
    expect(c.read(simpleLiveStackProvider).on, isTrue);
    await tester.tap(find.byKey(const Key('simple-live-stack-reset')));
    await tester.pump();
    expect(daemon.count('reset'), 1);
    expect(
      tester
          .widget<Switch>(find.byKey(const Key('simple-live-stack-switch')))
          .value,
      isTrue,
    );
  });

  testWidgets('switching off posts stop', (tester) async {
    final daemon = FakeSessionLiveStackClient()..enabled = true;
    await _pump(tester, daemon);
    await tester.tap(find.byKey(const Key('simple-live-stack-switch')));
    await tester.pump();
    expect(daemon.count('stop'), 1);
  });

  testWidgets('with the stack on, Live Stack / Last light are chosen in the '
      'row; Last light is saved', (tester) async {
    final daemon = FakeSessionLiveStackClient()..enabled = true;
    final c = await _pump(tester, daemon);
    expect(find.byKey(const Key('simple-live-stack-view')), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-live-stack-view-last')));
    await tester.pump();
    expect(c.read(simpleLiveStackProvider).view, SimpleLiveStackView.lastLight);
  });

  testWidgets('the readout names the stacked lights and the target', (
    tester,
  ) async {
    final daemon = FakeSessionLiveStackClient()
      ..enabled = true
      ..target = 'M31'
      ..stackOne()
      ..stackOne()
      ..stackOne();
    await _pump(tester, daemon, child: const SimpleLiveStackReadout());
    final readout = find.byKey(const Key('simple-live-stack-readout'));
    expect(readout, findsOneWidget);
    expect(
      find.descendant(of: readout, matching: find.textContaining('3')),
      findsWidgets,
    );
    expect(
      find.descendant(of: readout, matching: find.textContaining('M31')),
      findsWidgets,
    );
  });

  testWidgets('the readout carries the same words as its semantics label', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    final daemon = FakeSessionLiveStackClient()
      ..enabled = true
      ..unregistered = 1
      ..skippedBusy = 1
      ..stackOne()
      ..stackOne();
    await _pump(tester, daemon, child: const SimpleLiveStackReadout());
    final text = simpleLiveStackReadout(
      AppLocalizationsEn(),
      SessionLiveStackStatus(
        enabled: true,
        stackSeq: 2,
        lightsStacked: 2,
        lightsUnregistered: 1,
        lightsSkippedBusy: 1,
      ),
    );
    final node = tester.getSemantics(
      find.byKey(const Key('simple-live-stack-readout')),
    );
    expect(node.label, contains(text));
    handle.dispose();
  });

  testWidgets('the Queue screen fills the screen with the stacked frame while '
      'the stack is on and chosen, with the last light otherwise', (
    tester,
  ) async {
    final daemon = FakeSessionLiveStackClient()
      ..enabled = true
      ..stackOne();
    Widget? shown;
    final probe = Consumer(
      builder: (context, ref, _) {
        shown = const SimpleQueueScreen().frame(ref);
        return const SizedBox();
      },
    );
    final c = await _pump(tester, daemon, child: probe);
    await tester.pump();
    expect(shown, isA<Image>(), reason: 'the stack, on and chosen');
    await c
        .read(simpleLiveStackProvider.notifier)
        .setView(SimpleLiveStackView.lastLight);
    await tester.pump();
    expect(shown, isNull, reason: 'Last light: the shell falls back to it');
  });
}
