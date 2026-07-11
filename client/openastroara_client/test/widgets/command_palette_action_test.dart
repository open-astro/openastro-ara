import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/command_palette.dart';

/// No saved servers — the backup modal renders its "connect a server" empty
/// state without touching FlutterSecureStorage (absent in widget tests).
class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const <AraServer>[];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

void main() {
  testWidgets(
      '§61.10 activating an action hit closes the palette and runs its launcher',
      (tester) async {
    // #829 r1 note — the dispatch's context timing is the fragile part: the
    // launcher must run on the ROOT navigator grabbed BEFORE the palette pops
    // (the palette's own context dies with the pop). One action proves the
    // mechanism; the per-action launchers are a 1:1 mirror of app_shell's
    // top-bar buttons.
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => showCommandPalette(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // A label-prefix query ranks the action first (500 > any keyword tier), so
    // the row is guaranteed to be built inside the palette's lazy ListView.
    await tester.enterText(find.byType(TextField), 'back up');
    await tester.pump();
    expect(find.text('Back up & restore…'), findsOneWidget,
        reason: 'the backup action is the top hit for its label prefix');

    await tester.tap(find.text('Back up & restore…'));
    await tester.pumpAndSettle();

    // The Backup & Restore dialog is open on the root navigator…
    expect(find.text('Backup & Restore'), findsOneWidget);
    // …and the palette itself popped (its search field is gone).
    expect(find.byType(TextField), findsNothing);
  });
}
