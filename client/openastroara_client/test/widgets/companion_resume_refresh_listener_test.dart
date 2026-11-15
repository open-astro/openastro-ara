import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/companion_resume_refresh_listener.dart';

/// Counts status reads; no device connected (null), so no poll is armed.
class _CountingApi<T> implements EquipmentDeviceClient<T> {
  int reads = 0;
  @override
  Future<T?> getStatus() async {
    reads++;
    return null;
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> forget() async {}
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {}
  @override
  void close() {}
}

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

  testWidgets(
    'a resume re-reads the equipment the app holds and nothing it does not',
    (tester) async {
      final rotator = _CountingApi<RotatorStatus>();
      final focuser = _CountingApi<FocuserStatus>();
      final container = ProviderContainer(
        overrides: [
          companionModeProvider.overrideWithValue(true),
          wsEventsProvider.overrideWith((ref) => const Stream.empty()),
          rotatorApiProvider.overrideWithValue(rotator),
          focuserApiProvider.overrideWithValue(focuser),
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
      // The rotator is on screen (built); the focuser never was.
      container.listen(rotatorProvider, (_, _) {});
      await tester.runAsync(() => container.read(rotatorProvider.future));
      expect(rotator.reads, 1);

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
      expect(rotator.reads, 2);
      expect(focuser.reads, 0);
    },
  );
}
