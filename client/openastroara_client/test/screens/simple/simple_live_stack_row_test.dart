import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_live_stack_row.dart';
import 'package:openastroara/screens/simple/simple_queue_screen.dart';
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
  testWidgets('a mono camera (not_colour): no Live Stack row at all', (
    tester,
  ) async {
    await _pump(
      tester,
      FakeSessionLiveStackClient()..unsupported = 'not_colour',
    );
    expect(_row, findsNothing);
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
