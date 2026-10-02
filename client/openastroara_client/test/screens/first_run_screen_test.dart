import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/first_run_screen.dart';
import 'package:openastroara/services/server_discovery_service.dart';
import 'package:openastroara/services/multicast_lock.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/server_state.dart';

/// Counts the cache resets the screen asks for; discovery yields [rigs] and
/// then whatever [later] pushes; [sweeping] stands in for a sweep in flight.
class _FakeDiscovery extends ServerDiscoveryService {
  _FakeDiscovery([this.rigs = const []]);
  final List<AraServer> rigs;
  final later = StreamController<AraServer>.broadcast();
  bool sweeping = false;
  int resets = 0;
  final blocked = ValueNotifier<bool>(false);

  @override
  Stream<AraServer> discover() async* {
    yield* Stream.fromIterable(rigs);
    yield* later.stream;
  }

  @override
  bool get sweepInFlight => sweeping;

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

  // #1129: the setting lives in a different place per OS.
  testWidgets('the Local Network banner names the right settings path per platform',
      (tester) async {
    for (final (platform, needle) in [
      (TargetPlatform.iOS, 'Settings → Privacy & Security → Local Network → OpenAstro Ara'),
      (TargetPlatform.macOS, 'System Settings → Privacy & Security → Local Network'),
      (TargetPlatform.android, 'network privacy settings'),
    ]) {
      debugDefaultTargetPlatformOverride = platform;
      final fake = _FakeDiscovery();
      await tester.pumpWidget(
        ProviderScope(
          overrides: [discoveryServiceProvider.overrideWithValue(fake)],
          child: const MaterialApp(home: FirstRunScreen()),
        ),
      );
      fake.blocked.value = true;
      await tester.pump();
      expect(find.textContaining(needle), findsOneWidget, reason: '$platform');
      await tester.pumpWidget(const SizedBox.shrink());
      debugDefaultTargetPlatformOverride = null;
    }
  });

  // #1129: nothing is remembered between launches, so every launch scans; a
  // scan that settles on exactly one rig connects to it without a tap.
  group('auto-connect', () {
    // Stand-ins for whatever the scan returns this launch — test data only.
    const rigA = AraServer(hostname: 'rig-a.test', port: 5555, mdnsName: 'openastro');
    const rigB = AraServer(hostname: 'rig-b.test', port: 5555, mdnsName: 'openastro');

    Future<ProviderContainer> pump(WidgetTester tester, List<AraServer> rigs,
        {bool chooserRequested = false,
        _FakeDiscovery? discovery,
        Map<String, String?> ids = const {}}) async {
      FlutterSecureStorage.setMockInitialValues({});
      final container = ProviderContainer(overrides: [
        discoveryServiceProvider.overrideWithValue(discovery ?? _FakeDiscovery(rigs)),
        // A rig's identity, by address — the test's stand-in for /server/info.
        rigIdentityProvider.overrideWithValue((s) async => ids[s.hostname]),
        serverHandshakeProvider.overrideWith((ref) async {
          final s = ref.watch(selectedServerProvider);
          return s == null ? null : const ServerInfo(name: 'openastro', version: '1', apiVersion: 'v1');
        }),
      ]);
      addTearDown(container.dispose);
      if (chooserRequested) container.read(serverChooserRequestedProvider.notifier).request();
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: FirstRunScreen()),
      ));
      await tester.pump();
      return container;
    }

    Future<void> settle(WidgetTester tester) async {
      await tester.pump(autoConnectSettle + const Duration(milliseconds: 100));
      await tester.pump();
      await tester.pump();
    }

    testWidgets('one rig on the network: connects without a tap', (tester) async {
      final c = await pump(tester, const [rigA]);
      expect(c.read(selectedServerProvider), isNull, reason: 'waits for the scan to settle');
      await settle(tester);
      expect(c.read(selectedServerProvider), rigA);
      expect(await c.read(savedServersProvider.future), [rigA], reason: 'confirmed for this session');
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('two rigs: never picks one for you', (tester) async {
      final c = await pump(tester, const [rigA, rigB], ids: {'rig-a.test': 'uuid-a', 'rig-b.test': 'uuid-b'});
      await settle(tester);
      expect(c.read(selectedServerProvider), isNull);
      expect(await c.read(savedServersProvider.future), isEmpty);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    // #1129 review: the sweep walks .1–.254 in batches, so a second rig can
    // turn up well after the first; deciding at the first hit picked rig A.
    testWidgets('a second rig found late by a slow sweep: still never picks', (tester) async {
      final discovery = _FakeDiscovery(const [rigA])..sweeping = true;
      final c = await pump(tester, const [], discovery: discovery,
          ids: {'rig-a.test': 'uuid-a', 'rig-b.test': 'uuid-b'});
      await settle(tester);
      await tester.pump(const Duration(seconds: 2));
      expect(c.read(selectedServerProvider), isNull, reason: 'waits for the sweep');
      discovery.later.add(rigB);
      await tester.pump();
      discovery.sweeping = false;
      await settle(tester);
      await tester.pump(const Duration(seconds: 2));
      expect(c.read(selectedServerProvider), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('one rig: connects once the sweep has finished, not before', (tester) async {
      final discovery = _FakeDiscovery(const [rigA])..sweeping = true;
      final c = await pump(tester, const [], discovery: discovery);
      await settle(tester);
      expect(c.read(selectedServerProvider), isNull);
      discovery.sweeping = false;
      await tester.pump(const Duration(seconds: 1));
      await tester.pump();
      await tester.pump();
      expect(c.read(selectedServerProvider), rigA);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('one rig on two addresses counts as one rig', (tester) async {
      const wired = AraServer(hostname: 'rig-eth.test', port: 5555, mdnsName: 'openastro');
      const wifi = AraServer(hostname: 'rig-wlan.test', port: 5555, mdnsName: 'openastro');
      final c = await pump(tester, const [wired, wifi],
          ids: {'rig-eth.test': 'uuid-a', 'rig-wlan.test': 'uuid-a'});
      await settle(tester);
      expect(c.read(selectedServerProvider), wired);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('a dead second address is skipped for the one that answers', (tester) async {
      const dead = AraServer(hostname: 'rig-dead.test', port: 5555, mdnsName: 'openastro');
      final c = await pump(tester, const [dead, rigA], ids: {'rig-a.test': 'uuid-a'});
      await settle(tester);
      expect(c.read(selectedServerProvider), rigA);
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('picking another rig cancels a pending auto-connect', (tester) async {
      FlutterSecureStorage.setMockInitialValues({});
      final gate = Completer<void>();
      final container = ProviderContainer(overrides: [
        discoveryServiceProvider.overrideWithValue(_FakeDiscovery(const [rigA])),
        rigIdentityProvider.overrideWithValue((_) async => 'uuid-a'),
        // The handshake is slow, so the user can change their mind mid-way.
        serverHandshakeProvider.overrideWith((ref) async {
          final s = ref.watch(selectedServerProvider);
          if (s == null) return null;
          await gate.future;
          return const ServerInfo(name: 'openastro', version: '1', apiVersion: 'v1');
        }),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: FirstRunScreen()),
      ));
      await tester.pump();
      await settle(tester);
      expect(container.read(selectedServerProvider), rigA, reason: 'auto-picked, handshake pending');

      container.read(selectedServerProvider.notifier).select(rigB); // user taps another rig
      await tester.pump();
      container.read(selectedServerProvider.notifier).select(rigA); // ...and back
      await tester.pump();
      gate.complete();
      await tester.pump();
      await tester.pump();
      expect(await container.read(savedServersProvider.future), isEmpty,
          reason: 'a hand-picked rig waits for the Continue tap');
      await tester.pumpWidget(const SizedBox.shrink());
    });

    testWidgets('opened to choose a rig: lists it, does not auto-connect', (tester) async {
      final c = await pump(tester, const [rigA], chooserRequested: true);
      await settle(tester);
      expect(find.text('openastro'), findsOneWidget);
      expect(c.read(selectedServerProvider), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  });

  // #1129: Android drops mDNS answers without a multicast lock; the scan
  // screen holds it only while it is open (battery), nowhere else.
  group('multicast lock', () {
    late List<String> calls;
    const channel = MethodChannel('openastroara/multicast_lock');

    setUp(() {
      calls = [];
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, (call) async {
        calls.add(call.method);
        return null;
      });
    });

    tearDown(() {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(channel, null);
      MulticastLock.isAndroid = () => defaultTargetPlatform == TargetPlatform.android;
    });

    Future<void> openAndClose(WidgetTester tester) async {
      await tester.pumpWidget(ProviderScope(
        overrides: [discoveryServiceProvider.overrideWithValue(_FakeDiscovery())],
        child: const MaterialApp(home: FirstRunScreen()),
      ));
      await tester.pump();
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pump();
    }

    testWidgets('Android: held while the scan screen is open', (tester) async {
      MulticastLock.isAndroid = () => true;
      await openAndClose(tester);
      expect(calls, ['acquire', 'release']);
    });

    testWidgets('other platforms: never touched', (tester) async {
      MulticastLock.isAndroid = () => false;
      await openAndClose(tester);
      expect(calls, isEmpty);
    });
  });
}
