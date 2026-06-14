import 'dart:async';
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/data_package.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/data_manager_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
}

class _FakeDataManagerClient implements DataManagerClient {
  _FakeDataManagerClient(this.packages);
  List<DataPackage> packages;
  int lists = 0;
  int downloads = 0;
  String? lastDownloadId;
  bool? lastForce;
  final List<String> cancelled = <String>[];
  final List<String> deleted = <String>[];
  bool throwOnDelete = false;

  @override
  Future<List<DataPackage>> listPackages() async {
    lists++;
    return packages;
  }

  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async {
    downloads++;
    lastDownloadId = packageId;
    lastForce = forceReinstall;
    return 'dl-$packageId';
  }

  @override
  Future<void> cancel(String downloadId) async => cancelled.add(downloadId);

  @override
  Future<bool> delete(String packageId) async {
    deleted.add(packageId);
    if (throwOnDelete) throw StateError('delete failed');
    packages = packages.where((p) => p.id != packageId).toList();
    return true;
  }

  @override
  void close() {}
}

class _FakeSocket {
  final StreamController<dynamic> incoming = StreamController<dynamic>();
  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: (_) {},
    close: () async {
      if (!incoming.isClosed) await incoming.close();
    },
  );
}

class _FakeConnector {
  final List<_FakeSocket> legs = <_FakeSocket>[];
  WsSocket connect(Uri url, Map<String, String> headers) {
    final leg = _FakeSocket();
    legs.add(leg);
    return leg.socket;
  }
}

String _frame(String type, Map<String, dynamic> payload, int seq) =>
    jsonEncode({'type': type, 'ts': '2026-06-14T00:00:00.000Z', 'seq': seq, 'payload': payload});

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, DataManagerClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    dataManagerApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('dataManagerPackagesProvider', () {
    test('no saved server → null catalog', () async {
      final c = _container(const [], _FakeDataManagerClient(const []));
      await c.read(savedServersProvider.future);
      expect(await c.read(dataManagerPackagesProvider.future), isNull);
    });

    test('loads the catalog from the active server', () async {
      final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2', sizeBytes: 10)]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final pkgs = await c.read(dataManagerPackagesProvider.future);
      expect(pkgs, isNotNull);
      expect(pkgs!.single.id, 'tycho-2');
    });

    test('download forwards the id + force flag and returns the download id', () async {
      final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(dataManagerPackagesProvider.future);

      final id = await c.read(dataManagerPackagesProvider.notifier).download('tycho-2', forceReinstall: true);
      expect(api.downloads, 1);
      expect(api.lastDownloadId, 'tycho-2');
      expect(api.lastForce, isTrue);
      expect(id, 'dl-tycho-2');
    });

    test('delete forwards then refreshes the catalog', () async {
      final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2'), DataPackage(id: 'gaia-edr3-bright')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(dataManagerPackagesProvider.future);

      await c.read(dataManagerPackagesProvider.notifier).delete('tycho-2');
      expect(api.deleted, ['tycho-2']);
      final pkgs = c.read(dataManagerPackagesProvider).value!;
      expect(pkgs.map((p) => p.id), ['gaia-edr3-bright'], reason: 'the catalog re-reads after delete');
    });

    test('delete re-reads the catalog even when the delete call throws', () async {
      final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2')])..throwOnDelete = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(dataManagerPackagesProvider.future);
      final listsBefore = api.lists;

      await expectLater(
        c.read(dataManagerPackagesProvider.notifier).delete('tycho-2'),
        throwsA(isA<StateError>()),
      );
      expect(api.lists, greaterThan(listsBefore), reason: 'refresh runs in the finally even on a failed delete');
    });
  });

  group('dataManagerDownloadsProvider', () {
    test('folds download.* WS events into progress keyed by package id', () async {
      final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2')]);
      final conn = _FakeConnector();
      final c = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [_server])),
        dataManagerApiFactoryProvider.overrideWithValue((_) => api),
        wsEventStreamFactoryProvider.overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
      ]);
      addTearDown(c.dispose);

      await c.read(savedServersProvider.future);
      // Subscribe so the notifier (and its WS stream) stay alive.
      final sub = c.listen(dataManagerDownloadsProvider, (_, _) {});
      addTearDown(sub.close);
      c.read(dataManagerDownloadsProvider);
      await pumpEventQueue();

      final leg = conn.legs.single;
      leg.incoming.add(_frame('data_manager.download.progress',
          {'download_id': 'd1', 'package_id': 'tycho-2', 'downloaded_bytes': 512, 'total_bytes': 1024, 'percent_complete': 50.0}, 1));
      await pumpEventQueue();

      final map = c.read(dataManagerDownloadsProvider);
      expect(map['tycho-2'], isNotNull);
      expect(map['tycho-2']!.percentComplete, 50.0);
      expect(map['tycho-2']!.isActive, isTrue);

      // A complete event flips the phase and pokes a catalog refresh.
      final listsBefore = api.lists;
      leg.incoming.add(_frame('data_manager.download.complete',
          {'download_id': 'd1', 'package_id': 'tycho-2', 'downloaded_bytes': 1024, 'total_bytes': 1024, 'percent_complete': 100.0}, 2));
      await pumpEventQueue();

      expect(c.read(dataManagerDownloadsProvider)['tycho-2']!.phase, DownloadPhase.complete);
      expect(api.lists, greaterThan(listsBefore), reason: 'a complete event re-reads the catalog');
    });

    test('a failed event carries the error', () async {
      final conn = _FakeConnector();
      final c = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [_server])),
        dataManagerApiFactoryProvider.overrideWithValue((_) => _FakeDataManagerClient(const [])),
        wsEventStreamFactoryProvider.overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
      ]);
      addTearDown(c.dispose);
      await c.read(savedServersProvider.future);
      final sub = c.listen(dataManagerDownloadsProvider, (_, _) {});
      addTearDown(sub.close);
      c.read(dataManagerDownloadsProvider);
      await pumpEventQueue();

      conn.legs.single.incoming.add(_frame('data_manager.download.failed',
          {'download_id': 'd1', 'package_id': 'gaia-edr3-bright', 'error': 'stalled'}, 1));
      await pumpEventQueue();

      final p = c.read(dataManagerDownloadsProvider)['gaia-edr3-bright']!;
      expect(p.phase, DownloadPhase.failed);
      expect(p.error, 'stalled');
    });
  });
}
