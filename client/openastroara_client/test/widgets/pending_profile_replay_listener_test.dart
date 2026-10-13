import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/widgets/pending_profile_replay_listener.dart';

/// §2 / #1137 — the replay notice reaches the user as a snackbar, whether it
/// was posted before the shell mounted or while it is up, and is consumed.
void main() {
  Future<ProviderContainer> pump(WidgetTester tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: Scaffold(
            body: PendingProfileReplayListener(child: Text('shell')),
          ),
        ),
      ),
    );
    return container;
  }

  testWidgets(
    'a notice posted before the shell mounted shows on the first frame',
    (tester) async {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      container
          .read(pendingProfileReplayNoticeProvider.notifier)
          .post('1 profile saved offline was applied to the rig.');

      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: container,
          child: const MaterialApp(
            home: Scaffold(
              body: PendingProfileReplayListener(child: Text('shell')),
            ),
          ),
        ),
      );
      await tester.pump(); // post-frame callback + snackbar entrance

      expect(find.byType(SnackBar), findsOneWidget);
      expect(
        find.text('1 profile saved offline was applied to the rig.'),
        findsOneWidget,
      );
      expect(
        container.read(pendingProfileReplayNoticeProvider),
        isNull,
        reason: 'consumed',
      );
    },
  );

  testWidgets('a live notice becomes a snackbar and is consumed', (
    tester,
  ) async {
    final container = await pump(tester);
    expect(find.byType(SnackBar), findsNothing);

    container
        .read(pendingProfileReplayNoticeProvider.notifier)
        .post('1 profile saved offline was rejected by the rig and discarded.');
    await tester.pump();
    await tester.pump();

    expect(find.textContaining('rejected by the rig'), findsOneWidget);
    expect(container.read(pendingProfileReplayNoticeProvider), isNull);
  });
}
