import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
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

Future<ProviderContainer> _pump(WidgetTester tester, {bool simple = true, double maxHeight = 600}) async {
  final c = ProviderContainer(overrides: [simpleModePrefsProvider.overrideWithValue(_Prefs(simple))]);
  addTearDown(c.dispose);
  await c.read(simpleModeProvider.future);
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
  testWidgets('a result fades after 6 s; an error stays until ×', (tester) async {
    final c = await _pump(tester);
    final n = c.read(simpleMessagesProvider.notifier);
    n.show(SimpleMessageKind.result, 'Frame saved', detail: 'M 42 · 5 s');
    n.show(SimpleMessageKind.error, "Couldn't start live view");
    await tester.pump();
    expect(find.textContaining('Frame saved'), findsOneWidget);
    await tester.pump(kSimpleResultLife + const Duration(milliseconds: 100));
    expect(find.textContaining('Frame saved'), findsNothing, reason: 'the result faded');
    expect(find.textContaining("Couldn't start live view"), findsOneWidget, reason: 'the error stays');
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
