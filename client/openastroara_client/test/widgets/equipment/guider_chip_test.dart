import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/equipment/guider_chip.dart';
import 'package:openastroara/widgets/equipment/guider_dialog.dart';
import 'package:openastroara/widgets/status_indicator.dart';

GuiderStatus _status(GuiderConnectionState c, GuiderRuntimeState r) => GuiderStatus(
      deviceId: 'phd2',
      name: 'PHD2',
      connectionState: c,
      runtimeState: r,
    );

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeGuiderClient implements GuiderClient {
  _FakeGuiderClient(this.status);
  GuiderStatus? status;
  int connectCalls = 0;
  int disconnectCalls = 0;
  @override
  Future<GuiderStatus?> getStatus() async => status;
  @override
  Future<void> connect({String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async =>
      connectCalls++;
  @override
  Future<void> disconnect() async => disconnectCalls++;
  @override
  void close() {}
}

Widget _app(GuiderClient fake) => ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        guiderApiFactoryProvider.overrideWithValue((_) => fake),
      ],
      child: const MaterialApp(home: Scaffold(body: GuiderChip())),
    );

void main() {
  group('guiderStatusLevel', () {
    test('null status → disconnected', () {
      expect(guiderStatusLevel(null), StatusLevel.disconnected);
    });

    test('connecting → info', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connecting, GuiderRuntimeState.stopped)),
        StatusLevel.info,
      );
    });

    test('connected + guiding → connected', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.guiding)),
        StatusLevel.connected,
      );
    });

    test('connected + star lost → error (runtime overrides link)', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.starLost)),
        StatusLevel.error,
      );
    });

    test('connected + calibrating/dithering/paused → busy (not actively guiding)', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.calibrating)),
        StatusLevel.busy,
      );
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.dithering)),
        StatusLevel.busy,
      );
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.paused)),
        StatusLevel.busy,
        reason: 'a paused guider must not read as a healthy green guide',
      );
    });

    test('connected + stopped → connected (idle but healthy)', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.stopped)),
        StatusLevel.connected,
      );
    });

    test('connected + unknown runtime → info (not a misleading green)', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.connected, GuiderRuntimeState.unknown)),
        StatusLevel.info,
      );
    });

    test('error / unknown link → error / disconnected', () {
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.error, GuiderRuntimeState.stopped)),
        StatusLevel.error,
      );
      expect(
        guiderStatusLevel(_status(GuiderConnectionState.unknown, GuiderRuntimeState.unknown)),
        StatusLevel.disconnected,
      );
    });
  });

  group('guiderChipLevel (AsyncValue)', () {
    test('loading → info, error → error, data delegates', () {
      expect(guiderChipLevel(const AsyncValue<GuiderStatus?>.loading()), StatusLevel.info);
      expect(guiderChipLevel(AsyncValue<GuiderStatus?>.error('x', StackTrace.empty)), StatusLevel.error);
      expect(
        guiderChipLevel(AsyncValue<GuiderStatus?>.data(
            _status(GuiderConnectionState.connected, GuiderRuntimeState.guiding))),
        StatusLevel.connected,
      );
    });
  });

  group('labels', () {
    test('connection labels', () {
      expect(guiderConnectionLabel(GuiderConnectionState.connected), 'Connected');
      expect(guiderConnectionLabel(GuiderConnectionState.connecting), 'Connecting…');
      expect(guiderConnectionLabel(GuiderConnectionState.disconnected), 'Disconnected');
    });

    test('runtime labels', () {
      expect(guiderRuntimeLabel(GuiderRuntimeState.guiding), 'Guiding');
      expect(guiderRuntimeLabel(GuiderRuntimeState.starLost), 'Star lost');
    });
  });

  testWidgets('GuiderChip renders the GUIDE label (disconnected by default)', (tester) async {
    await tester.pumpWidget(const ProviderScope(
      child: MaterialApp(home: Scaffold(body: GuiderChip())),
    ));
    await tester.pump();
    expect(find.text('GUIDE'), findsOneWidget);
  });

  testWidgets('tapping the chip opens the guider dialog (no server → not configured)', (tester) async {
    await tester.pumpWidget(const ProviderScope(
      child: MaterialApp(home: Scaffold(body: GuiderChip())),
    ));
    await tester.pump();
    await tester.tap(find.byType(GuiderChip));
    await tester.pumpAndSettle();
    expect(find.text('Guider'), findsOneWidget); // dialog title
    expect(find.text('No guider configured on this server.'), findsOneWidget);
    expect(find.text('Connect'), findsOneWidget);
  });

  testWidgets('tapping Connect on a disconnected guider calls the notifier', (tester) async {
    final fake = _FakeGuiderClient(
        _status(GuiderConnectionState.disconnected, GuiderRuntimeState.stopped));
    await tester.pumpWidget(_app(fake));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(GuiderChip));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Connect'));
    await tester.pumpAndSettle();
    expect(fake.connectCalls, 1);
  });

  testWidgets('tapping Disconnect on a connected guider calls the notifier', (tester) async {
    final fake = _FakeGuiderClient(
        _status(GuiderConnectionState.connected, GuiderRuntimeState.guiding));
    await tester.pumpWidget(_app(fake));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(GuiderChip));
    await tester.pumpAndSettle();
    expect(find.text('Disconnect'), findsOneWidget);
    await tester.tap(find.text('Disconnect'));
    await tester.pumpAndSettle();
    expect(fake.disconnectCalls, 1);
  });
}
