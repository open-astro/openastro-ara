import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/wizard_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// §2 / #1137 — with no server in reach, Save keeps the profile on this device
/// instead of refusing, and the wizard closes.
/// In-memory store: file I/O never completes inside testWidgets' fake-async
/// zone, so the production service's temp-dir path cannot be used here.
class _MemStore extends PendingProfileDraftService {
  final Map<String, PendingProfileDraft> store = {};
  @override
  Future<List<PendingProfileDraft>> loadAll() async =>
      store.values.toList()..sort((a, b) => a.savedUtc.compareTo(b.savedUtc));
  @override
  Future<void> save(PendingProfileDraft draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

void main() {
  testWidgets(
    'Save Profile with no server stores a pending draft and leaves the wizard',
    (tester) async {
      final store = _MemStore();
      var completed = 0;
      final container = ProviderContainer(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          pendingProfileDraftServiceProvider.overrideWithValue(store),
          wsEventStreamProvider.overrideWithValue(null),
        ],
      );
      addTearDown(container.dispose);

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: MaterialApp(
            home: Builder(
              builder: (context) => Scaffold(
                body: Center(
                  child: ElevatedButton(
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute(
                        builder: (_) =>
                            WizardShell(onComplete: (_) => completed++),
                      ),
                    ),
                    child: const Text('open'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      // The controller is autoDispose: drive it only once the shell is mounted
      // and listening, or the jump lands on a provider about to be reset.
      container
          .read(wizardControllerProvider.notifier)
          .jumpTo(ProfileWizard.totalSteps);
      container.read(wizardControllerProvider).draft.profileName =
          'Dark site rig';
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }
      expect(find.text('Save Profile'), findsOneWidget);

      await tester.tap(find.text('Save Profile'));
      // Bounded pumps: wizard screens carry indeterminate animations, so
      // pumpAndSettle would never return.
      for (var i = 0; i < 10; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      final pending = await store.loadAll();
      expect(pending.single.name, 'Dark site rig');
      expect(pending.single.finalSave, isTrue);
      expect(pending.single.draft.profileName, 'Dark site rig');
      expect(
        completed,
        1,
        reason: 'the wizard exits as after a connected save',
      );
      expect(find.text('Save Profile'), findsNothing);
      expect(find.textContaining('saved on this device'), findsOneWidget);
    },
  );
}
