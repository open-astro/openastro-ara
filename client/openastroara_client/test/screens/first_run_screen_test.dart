import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/first_run_screen.dart';
import 'package:openastroara/services/server_discovery_service.dart';
import 'package:openastroara/state/server_state.dart';

/// Counts the cache resets the screen asks for; discovery itself is inert.
class _FakeDiscovery extends ServerDiscoveryService {
  int resets = 0;
  final blocked = ValueNotifier<bool>(false);

  @override
  Stream<AraServer> discover() => const Stream.empty();

  @override
  void resetSweepCache() => resets++;

  @override
  ValueListenable<bool> get localNetworkBlocked => blocked;
}

void main() {
  testWidgets('⟳ Rescan forgets the shared sweep before re-running discovery', (
    tester,
  ) async {
    // The PR guarantee: a daemon that went away must not replay from the
    // cached sweep onto the list when the user taps Rescan. The service-level
    // contract is covered elsewhere; this pins the screen's call to it.
    final fake = _FakeDiscovery();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [discoveryServiceProvider.overrideWithValue(fake)],
        child: const MaterialApp(home: FirstRunScreen()),
      ),
    );
    await tester.pump();
    expect(fake.resets, 0, reason: 'no reset until the user asks');

    await tester.tap(find.byTooltip('Rescan for servers'));
    await tester.pump();
    expect(fake.resets, 1);

    // Dispose the screen so its 4 s rescan timer does not outlive the test.
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('a Local Network block shows how to allow it, and clears',
      (tester) async {
    // #1111 — on macOS a denied Local Network prompt made every mDNS send
    // fail silently; the screen said "looking for servers" forever.
    final fake = _FakeDiscovery();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [discoveryServiceProvider.overrideWithValue(fake)],
        child: const MaterialApp(home: FirstRunScreen()),
      ),
    );
    await tester.pump();
    expect(find.textContaining('blocking local network'), findsNothing);

    fake.blocked.value = true;
    await tester.pump();
    expect(find.textContaining('blocking local network'), findsOneWidget);
    expect(find.textContaining('Adding the rig manually below'), findsOneWidget);

    fake.blocked.value = false;
    await tester.pump();
    expect(find.textContaining('blocking local network'), findsNothing);

    await tester.pumpWidget(const SizedBox.shrink());
  });
}
