import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/screens/simple/simple_message_pills.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

class _Prefs extends SimpleModePrefsService {
  _Prefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

Future<ProviderContainer> _pump(WidgetTester tester,
    {bool simple = true, bool feature = true, double maxHeight = 600}) async {
  final c = ProviderContainer(overrides: [
    simpleModePrefsProvider.overrideWithValue(_Prefs(simple)),
    simpleModeFeaturePrefsProvider.overrideWithValue(_Prefs(feature)),
  ]);
  addTearDown(c.dispose);
  await c.read(simpleModeProvider.future);
  await c.read(simpleModeFeatureProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: Builder(
          builder: (context) => Column(children: [
            SizedBox(width: 420, child: SimpleMessagePills(maxHeight: maxHeight)),
            TextButton(
              key: const Key('notify'),
              onPressed: () => userNotifier(context)("Couldn't move", detail: 'Focuser is busy', error: true),
              child: const Text('notify'),
            ),
          ]),
        ),
      ),
    ),
  ));
  return c;
}

void main() {
  testWidgets('the immersive view (no height limit) shows every message instead of crashing', (tester) async {
    final c = await _pump(tester, maxHeight: double.infinity);
    c.read(simpleMessagesProvider.notifier)
      ..show(SimpleMessageKind.error, "Couldn't start imaging")
      ..show(SimpleMessageKind.error, "Couldn't stop the run");
    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(find.textContaining("Couldn't start imaging"), findsOneWidget);
    expect(find.textContaining("Couldn't stop the run"), findsOneWidget);
  });

  testWidgets('with the Experimental switch off the full UI shows, so messages go to a snackbar', (tester) async {
    final c = await _pump(tester, feature: false);
    await tester.tap(find.byKey(const Key('notify')));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), isEmpty, reason: 'no pill nobody would see');
    expect(find.byType(SnackBar), findsOneWidget);
  });

  testWidgets('a result fades after 10 s; an error stays longer, then fades too (user, 2026-11-04); × closes it',
      (tester) async {
    final c = await _pump(tester);
    final n = c.read(simpleMessagesProvider.notifier);
    n.show(SimpleMessageKind.result, 'Frame saved', detail: 'M 42 · 5 s');
    n.show(SimpleMessageKind.error, "Couldn't start live view");
    await tester.pump();
    expect(find.textContaining('Frame saved'), findsOneWidget);
    await tester.pump(kSimpleResultLife + const Duration(milliseconds: 100));
    expect(find.textContaining('Frame saved'), findsNothing, reason: 'the result faded');
    expect(find.textContaining("Couldn't start live view"), findsOneWidget, reason: 'the error stays longer');
    await tester.pump(kSimpleErrorLife);
    expect(c.read(simpleMessagesProvider), isEmpty, reason: 'then it fades too, not a sticky pill');
    n.show(SimpleMessageKind.error, "Couldn't start live view");
    await tester.pump();
    await tester.tap(find.byTooltip('Dismiss'));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), isEmpty, reason: '× closes it at once');
    await tester.pump(kSimpleErrorLife);
  });

  testWidgets('a warning is amber and stays until ×, with no time-left line (it ran out while the pill stayed)',
      (tester) async {
    final c = await _pump(tester);
    c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.warning, 'Emergency stop: mount parked');
    await tester.pump();
    expect(find.byType(TweenAnimationBuilder<double>), findsNothing, reason: 'no countdown for a pill that stays');
    final box = tester.widget<Container>(find.byKey(const Key('simple-message-0'))).decoration! as BoxDecoration;
    expect((box.border! as Border).top.color, AraColors.accentWarning);
    await tester.pump(kSimpleResultLife * 3);
    expect(find.textContaining('Emergency stop'), findsOneWidget);
    await tester.tap(find.byTooltip('Dismiss'));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('a question stays until answered, and the answer runs', (tester) async {
    final c = await _pump(tester);
    var slewed = false;
    c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.question, 'Slew anyway?',
        detail: 'A run is in progress',
        actions: [SimpleMessageAction('Cancel', () {}), SimpleMessageAction('Slew anyway', () => slewed = true)]);
    await tester.pump(const Duration(seconds: 30));
    expect(find.textContaining('Slew anyway?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Slew anyway'));
    await tester.pump();
    expect(slewed, isTrue);
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('a result can carry one action (Undo), shown on the pill while it lasts', (tester) async {
    final c = await _pump(tester);
    var undone = false;
    c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.result, 'M31 removed',
        actions: [SimpleMessageAction('Undo', () => undone = true)]);
    await tester.pump();
    await tester.tap(find.widgetWithText(TextButton, 'Undo'));
    await tester.pump();
    expect(undone, isTrue);
    expect(c.read(simpleMessagesProvider), isEmpty, reason: 'the action answers it');
  });

  testWidgets('newest on top; past the room the rest fold into "+N more", which shows them all', (tester) async {
    // Room for two pills and the fold.
    final c = await _pump(tester, maxHeight: 2 * kSimpleMessageHeight + 8 + 8 + 44);
    final n = c.read(simpleMessagesProvider.notifier);
    for (final t in ['one', 'two', 'three', 'four']) {
      n.show(SimpleMessageKind.error, t);
    }
    await tester.pump();
    expect(tester.getTopLeft(find.text('four')).dy, lessThan(tester.getTopLeft(find.text('three')).dy),
        reason: 'newest on top');
    expect(find.text('two'), findsNothing);
    expect(find.text('+2 more'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-messages-more')));
    await tester.pump();
    expect(find.text('one'), findsOneWidget);
    expect(find.text('Show fewer'), findsOneWidget);
  });

  testWidgets('userNotifier: a pill in Simple Mode, the snackbar in the full UI', (tester) async {
    final c = await _pump(tester);
    await tester.tap(find.byKey(const Key('notify')));
    await tester.pump();
    expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
    expect(find.byType(SnackBar), findsNothing);

    final full = await _pump(tester, simple: false);
    await tester.tap(find.byKey(const Key('notify')));
    await tester.pump();
    expect(full.read(simpleMessagesProvider), isEmpty);
    expect(find.text("Couldn't move: Focuser is busy"), findsOneWidget);
  });

  testWidgets('a pill is announced to screen readers as it appears', (tester) async {
    final c = await _pump(tester);
    c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.error, 'Not connected');
    await tester.pump();
    final node = tester.getSemantics(find.byKey(Key('simple-message-${c.read(simpleMessagesProvider).single.id}')));
    expect(node.flagsCollection.isLiveRegion, isTrue);
  });
}
