import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/imaging_actions.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _Prefs extends SimpleModePrefsService {
  _Prefs(this.value);
  final bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async {}
}

Future<ProviderContainer> _pump(WidgetTester tester, {required bool simple}) async {
  final c = ProviderContainer(overrides: [
    simpleModePrefsProvider.overrideWithValue(_Prefs(simple)),
    activeServerProvider.overrideWithValue(null),
  ]);
  addTearDown(c.dispose);
  await c.read(simpleModeProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: Consumer(
          builder: (context, ref, _) => TextButton(
            onPressed: () => takeOne(context, ref),
            child: const Text('Take One'),
          ),
        ),
      ),
    ),
  ));
  return c;
}

void main() {
  // No server saved in a test: Take One can't go anywhere.
  testWidgets('Take One with no rig: an error pill in Simple Mode', (tester) async {
    final c = await _pump(tester, simple: true);
    await tester.tap(find.text('Take One'));
    await tester.pump(const Duration(milliseconds: 10));
    final m = c.read(simpleMessagesProvider).single;
    expect(m.kind, SimpleMessageKind.error);
    expect(m.title, 'Not connected to a server.');
    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('Take One with no rig: the full UI keeps its plain snackbar', (tester) async {
    final c = await _pump(tester, simple: false);
    await tester.tap(find.text('Take One'));
    await tester.pump(const Duration(milliseconds: 10));
    expect(c.read(simpleMessagesProvider), isEmpty);
    expect(find.text('Not connected to a server.'), findsOneWidget);
    expect(tester.widget<SnackBar>(find.byType(SnackBar)).backgroundColor, isNull, reason: 'not red, as before');
  });
}
