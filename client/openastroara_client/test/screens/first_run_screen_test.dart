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

/// Counts the cache resets the screen asks for; discovery yields [rigs].
class _FakeDiscovery extends ServerDiscoveryService {
  _FakeDiscovery([this.rigs = const []]);
  final List<AraServer> rigs;
  int resets = 0;
  final blocked = ValueNotifier<bool>(false);

  @override
  Stream<AraServer> discover() => Stream.fromIterable(rigs);

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
        {bool chooserRequested = false}) async {
      FlutterSecureStorage.setMockInitialValues({});
      final container = ProviderContainer(overrides: [
        discoveryServiceProvider.overrideWithValue(_FakeDiscovery(rigs)),
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
      final c = await pump(tester, const [rigA, rigB]);
      await settle(tester);
      expect(c.read(selectedServerProvider), isNull);
      expect(await c.read(savedServersProvider.future), isEmpty);
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
