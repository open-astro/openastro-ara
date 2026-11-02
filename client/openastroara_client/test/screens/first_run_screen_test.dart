import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/first_run_screen.dart';
import 'package:openastroara/services/last_rig_service.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/services/server_discovery_service.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/services/multicast_lock.dart';
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

/// In-memory last rig.
class _MemLastRig extends LastRigService {
  _MemLastRig([this.name]);
  String? name;
  @override
  Future<String?> load() async => name;
  @override
  Future<void> save(String name) async => this.name = name;
}

/// ALP-4890: the scan screen with a remembered rig and a handshake that
/// answers with the rig's nickname, without a network.
Future<ProviderContainer> _scan(WidgetTester tester, List<AraServer> rigs, _MemLastRig last,
    {bool chooser = false}) async {
  FlutterSecureStorage.setMockInitialValues({});
  final container = ProviderContainer(overrides: [
    discoveryServiceProvider.overrideWithValue(_FakeDiscovery(rigs)),
    lastRigServiceProvider.overrideWithValue(last),
    serverHandshakeProvider.overrideWith((ref) async {
      final s = ref.watch(selectedServerProvider);
      return s == null ? null : ServerInfo(name: s.mdnsName ?? s.hostname, version: '1', apiVersion: '1');
    }),
  ]);
  addTearDown(container.dispose);
  if (chooser) container.read(serverChooserRequestedProvider.notifier).request();
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: FirstRunScreen()),
  ));
  await tester.pump();
  await tester.pump(const Duration(seconds: 1));
  await tester.pump();
  return container;
}

void main() {
  test('autoPickRig: the last rig by name, in either spelling, first found wins', () {
    const sweep = AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro.lan');
    const mdns = AraServer(hostname: '10.0.0.6', port: 5555, mdnsName: 'astro-lan');
    const other = AraServer(hostname: '10.0.0.7', port: 5555, mdnsName: 'garage');
    expect(autoPickRig([other, sweep], 'astro.lan'), sweep);
    expect(autoPickRig([mdns], 'Astro.lan'), mdns);
    expect(autoPickRig([mdns, sweep], 'astro.lan'), mdns);
    expect(autoPickRig([other], 'astro.lan'), isNull);
    expect(autoPickRig([sweep], null), isNull);
    expect(autoPickRig([const AraServer(hostname: '10.0.0.8', port: 5555)], 'astro.lan'), isNull);
  });

  testWidgets('ALP-4890: the last rig connects on its own when the scan finds it', (tester) async {
    const rig = AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro');
    const other = AraServer(hostname: '10.0.0.7', port: 5555, mdnsName: 'garage');
    final c = await _scan(tester, const [other, rig], _MemLastRig('astro'));
    expect(c.read(selectedServerProvider), rig);
    expect(await c.read(savedServersProvider.future), [rig]);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('ALP-4890: no remembered rig, or another rig, waits for a tap; a tap remembers it', (tester) async {
    const rig = AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro');
    final last = _MemLastRig('garage');
    final c = await _scan(tester, const [rig], last);
    expect(c.read(selectedServerProvider), isNull);
    expect(await c.read(savedServersProvider.future), isEmpty);
    await tester.tap(find.text('astro'));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.text('Continue'));
    await tester.pump();
    expect(last.name, 'astro');
    await tester.pumpWidget(const SizedBox.shrink());
  });

  testWidgets('ALP-4890: Launchpad\'s chooser never connects on its own', (tester) async {
    const rig = AraServer(hostname: '10.0.0.5', port: 5555, mdnsName: 'astro');
    final c = await _scan(tester, const [rig], _MemLastRig('astro'), chooser: true);
    expect(c.read(selectedServerProvider), isNull);
    expect(await c.read(savedServersProvider.future), isEmpty);
    await tester.pumpWidget(const SizedBox.shrink());
  });

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

  testWidgets('pulling the list down rescans, like ⟳', (tester) async {
    final fake = _FakeDiscovery();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [discoveryServiceProvider.overrideWithValue(fake)],
        child: const MaterialApp(home: FirstRunScreen()),
      ),
    );
    await tester.pump();

    await tester.fling(find.textContaining('Looking for servers'), const Offset(0, 300), 1000);
    await tester.pumpAndSettle();
    expect(fake.resets, 1);

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

  // #1129: every launch scans, and the user always taps the rig — a site can
  // have four or five, some not up yet, so even a lone rig is never picked
  // automatically.
  testWidgets('a rig found is listed and waits for the user to pick it', (tester) async {
    FlutterSecureStorage.setMockInitialValues({});
    const rig = AraServer(hostname: 'rig-a.test', port: 5555, mdnsName: 'openastro');
    final container = ProviderContainer(overrides: [
      discoveryServiceProvider.overrideWithValue(_FakeDiscovery(const [rig])),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: FirstRunScreen()),
    ));
    await tester.pump();
    await tester.pump(const Duration(seconds: 30));
    expect(find.text('openastro'), findsOneWidget);
    expect(container.read(selectedServerProvider), isNull);
    expect(await container.read(savedServersProvider.future), isEmpty);
    await tester.pumpWidget(const SizedBox.shrink());
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
