import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/companion_resume_refresh_listener.dart';

void main() {
  Future<(ProviderContainer, List<String>)> pump(
    WidgetTester tester, {
    required bool companion,
  }) async {
    final log = <String>[];
    late final ProviderContainer container;
    container = ProviderContainer(
      overrides: [
        companionModeProvider.overrideWithValue(companion),
        rigSnapshotRefreshProvider.overrideWithValue(() async {
          // Live values are held back while the snapshot is read.
          log.add(container.read(rigResumingProvider) ? 'refresh' : 'early');
        }),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(
          home: CompanionResumeRefreshListener(child: SizedBox.shrink()),
        ),
      ),
    );
    return (container, log);
  }

  testWidgets('a resume on a phone re-reads the REST snapshot', (tester) async {
    final (container, log) = await pump(tester, companion: true);
    expect(log, isEmpty);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
    expect(log, isEmpty, reason: 'going to the background reads nothing');
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    expect(log, ['refresh']);
    expect(container.read(rigResumingProvider), isFalse);
  });

  testWidgets('off a phone a resume reads nothing', (tester) async {
    final (_, log) = await pump(tester, companion: false);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    expect(log, isEmpty);
  });
}
