import 'package:flutter/foundation.dart' show debugDefaultTargetPlatformOverride;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_safety_alarm.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/simple_snack_bar_messenger.dart';

Future<(ProviderContainer, BuildContext)> _pump(
  WidgetTester tester, {
  bool simple = true,
}) async {
  late BuildContext page;
  final c = ProviderContainer(
    overrides: [simpleModeActiveProvider.overrideWithValue(simple)],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        builder: (context, child) => SimpleScaffoldMessenger(
          child: SimpleSafetyAlarmLayer(child: child!),
        ),
        home: Scaffold(
          body: Builder(
            builder: (context) {
              page = context;
              return const SizedBox.expand();
            },
          ),
        ),
      ),
    ),
  );
  return (c, page);
}

void main() {
  group('simpleSnackBarText', () {
    test('Text, rich text and text nested in rows and columns', () {
      expect(simpleSnackBarText(const Text('Saved')), 'Saved');
      expect(
        simpleSnackBarText(
          const Text.rich(
            TextSpan(
              text: 'A',
              children: [TextSpan(text: 'B')],
            ),
          ),
        ),
        'AB',
      );
      expect(
        simpleSnackBarText(
          const Row(
            children: [
              Icon(Icons.done),
              Expanded(
                child: Column(
                  children: [
                    Text('one'),
                    Flexible(child: Text('two')),
                  ],
                ),
              ),
            ],
          ),
        ),
        'one two',
      );
    });

    test('nothing to read gives null, never empty text', () {
      expect(simpleSnackBarText(const Icon(Icons.done)), isNull);
      expect(simpleSnackBarText(const Text('  ')), isNull);
      expect(simpleSnackBarText(const SizedBox()), isNull);
    });
  });

  group('simpleSnackBarKind', () {
    test('a red background or a failure in the words is an error', () {
      expect(
        simpleSnackBarKind(AraColors.accentError, 'Done'),
        SimpleMessageKind.error,
      );
      expect(
        simpleSnackBarKind(null, "Couldn't save the plan"),
        SimpleMessageKind.error,
      );
      expect(
        simpleSnackBarKind(null, 'Eject failed: busy'),
        SimpleMessageKind.error,
      );
      expect(
        simpleSnackBarKind(null, 'Não foi possível salvar'),
        SimpleMessageKind.error,
      );
    });

    test('anything else is a result', () {
      expect(simpleSnackBarKind(null, 'Saved'), SimpleMessageKind.result);
      expect(
        simpleSnackBarKind(AraColors.bgPanel, 'No errors recorded'),
        SimpleMessageKind.result,
      );
    });
  });

  testWidgets(
    'a showSnackBar under Simple Mode is a pill, nothing paints, and closed completes',
    (tester) async {
      final (c, page) = await _pump(tester);
      final done = ScaffoldMessenger.of(page)
          .showSnackBar(const SnackBar(content: Text('Saved it')));
      var closed = false;
      done.closed.then((_) => closed = true);
      await tester.pump();
      expect(find.byType(SnackBar), findsNothing);
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.title, 'Saved it');
      expect(pill.kind, SimpleMessageKind.result);
      expect(closed, isTrue);
      expect(
        find.text('Saved it'),
        findsOneWidget,
        reason: 'and the pill is on screen',
      );
      await tester.pump(kSimpleResultLife);
      await tester.pumpAndSettle();
    },
  );

  testWidgets('with Simple Mode off it stays a real SnackBar', (tester) async {
    final (c, page) = await _pump(tester, simple: false);
    ScaffoldMessenger.of(page)
        .showSnackBar(const SnackBar(content: Text('Saved it')));
    await tester.pump();
    expect(find.byType(SnackBar), findsOneWidget);
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('an error SnackBar is an error pill', (tester) async {
    final (c, page) = await _pump(tester);
    ScaffoldMessenger.of(page).showSnackBar(
      const SnackBar(
        content: Text('Nope'),
        backgroundColor: AraColors.accentError,
      ),
    );
    await tester.pump();
    expect(c.read(simpleMessagesProvider).single.kind, SimpleMessageKind.error);
    await tester.pump(kSimpleErrorLife);
    await tester.pumpAndSettle();
  });

  testWidgets(
    'a SnackBarAction becomes the pill action and runs the same callback',
    (tester) async {
      final (c, page) = await _pump(tester);
      var undone = 0;
      ScaffoldMessenger.of(page).showSnackBar(
        SnackBar(
          content: const Text('Deleted'),
          action: SnackBarAction(label: 'Undo', onPressed: () => undone++),
        ),
      );
      await tester.pump();
      await tester.tap(find.text('Undo'));
      await tester.pump();
      expect(undone, 1);
      expect(c.read(simpleMessagesProvider), isEmpty, reason: 'answered');
    },
  );

  testWidgets('an action on an error pill is on the pill too', (tester) async {
    final (_, page) = await _pump(tester);
    var retried = 0;
    ScaffoldMessenger.of(page).showSnackBar(
      SnackBar(
        content: const Text("Couldn't save"),
        action: SnackBarAction(label: 'Retry', onPressed: () => retried++),
      ),
    );
    await tester.pump();
    await tester.tap(find.text('Retry'));
    expect(retried, 1);
    await tester.pumpAndSettle();
  });

  testWidgets('a SnackBar with no text falls back to the real one', (
    tester,
  ) async {
    final (c, page) = await _pump(tester);
    ScaffoldMessenger.of(page)
        .showSnackBar(const SnackBar(content: Icon(Icons.done)));
    await tester.pump();
    expect(find.byType(SnackBar), findsOneWidget);
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets(
    'a message posted on a page pushed over the shell shows on that page',
    (tester) async {
      final (c, page) = await _pump(tester);
      Navigator.of(page).push(
        MaterialPageRoute<void>(
          builder: (_) =>
              Scaffold(appBar: AppBar(title: const Text('Settings'))),
        ),
      );
      await tester.pumpAndSettle();
      c
          .read(simpleMessagesProvider.notifier)
          .show(SimpleMessageKind.result, 'Disk checked');
      await tester.pump();
      expect(find.text('Disk checked'), findsOneWidget);
      expect(tester.takeException(), isNull);
      expect(
        tester.getTopLeft(find.text('Disk checked')).dy,
        greaterThan(tester.getBottomLeft(find.byType(AppBar)).dy),
        reason: 'under the app bar, clear of Back',
      );
      await tester.pump(kSimpleResultLife);
      await tester.pumpAndSettle();
    },
  );

  testWidgets("a pill's Dismiss is 44 px on the desktop too", (tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.macOS;
    addTearDown(() => debugDefaultTargetPlatformOverride = null);
    final (c, _) = await _pump(tester);
    c.read(simpleMessagesProvider.notifier).show(SimpleMessageKind.error, 'Failed');
    await tester.pumpAndSettle();
    final size = tester.getSize(find.byType(IconButton));
    expect(size.height, greaterThanOrEqualTo(44));
    expect(size.width, greaterThanOrEqualTo(44));
    debugDefaultTargetPlatformOverride = null;
    await tester.pump(kSimpleErrorLife);
  });

  testWidgets("a pill over a pushed page keeps Simple Mode's 44 px, readable buttons", (tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.macOS;
    addTearDown(() => debugDefaultTargetPlatformOverride = null);
    final (c, _) = await _pump(tester);
    c.read(simpleMessagesProvider.notifier).show(
      SimpleMessageKind.question,
      'Q',
      actions: [SimpleMessageAction('Later', () {}), SimpleMessageAction('Allow', () {})],
    );
    await tester.pumpAndSettle();
    final later = find.ancestor(of: find.text('Later'), matching: find.byType(TextButton));
    expect(tester.getSize(later).height, greaterThanOrEqualTo(44));
    final label = tester.widget<RichText>(find.descendant(of: later, matching: find.byType(RichText)).first);
    expect(label.text.style!.color, AraColors.linkText);
    debugDefaultTargetPlatformOverride = null;
  });
}
