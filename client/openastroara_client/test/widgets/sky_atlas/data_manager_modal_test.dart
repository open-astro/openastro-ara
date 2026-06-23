import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/catalog_object.dart';
import 'package:openastroara/models/data_package.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/data_manager_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';
import 'package:openastroara/widgets/sky_atlas/data_manager_modal.dart';

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
  final List<String> downloaded = <String>[];
  final List<String> cancelled = <String>[];
  final List<String> deleted = <String>[];

  @override
  Future<List<DataPackage>> listPackages() async => packages;
  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async {
    downloaded.add(packageId);
    return 'dl-$packageId';
  }

  @override
  Future<void> cancel(String downloadId) async => cancelled.add(downloadId);
  @override
  Future<bool> delete(String packageId) async {
    deleted.add(packageId);
    return true;
  }

  @override
  Future<List<CatalogObject>?> getCatalog(String packageId, {double? maxMag, int? limit}) async =>
      const <CatalogObject>[];

  @override
  void close() {}
}

// Supplies a fixed download-progress map so row states can be tested without a WS.
class _FakeDownloads extends DataManagerDownloadsNotifier {
  _FakeDownloads(this._fixed);
  final Map<String, DownloadProgress> _fixed;
  @override
  Map<String, DownloadProgress> build() => _fixed;
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _host(
  _FakeDataManagerClient api, {
  Map<String, DownloadProgress> downloads = const {},
}) =>
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [_server])),
        dataManagerApiFactoryProvider.overrideWithValue((_) => api),
        dataManagerDownloadsProvider.overrideWith(() => _FakeDownloads(downloads)),
      ],
      child: const MaterialApp(home: DataManagerModal()),
    );

void main() {
  testWidgets('lists catalog packages grouped, with installed total', (tester) async {
    final api = _FakeDataManagerClient(const [
      DataPackage(id: 'tycho-2', name: 'Tycho-2', category: 'catalog', sizeBytes: 2 * 1024 * 1024, isInstalled: true),
      DataPackage(id: 'horizon-default', name: 'Default horizon', category: 'horizon', sizeBytes: 4096),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.text('Tycho-2'), findsOneWidget);
    expect(find.text('Default horizon'), findsOneWidget);
    expect(find.text('Star catalogs'), findsOneWidget, reason: 'category headers render');
    expect(find.text('Horizon profiles'), findsOneWidget);
    expect(find.text('2.0 MB installed'), findsOneWidget, reason: 'header sums installed sizes');
  });

  testWidgets('not-installed → Download, installed → Remove', (tester) async {
    final api = _FakeDataManagerClient(const [
      DataPackage(id: 'tycho-2', name: 'Tycho-2', category: 'catalog', isInstalled: true),
      DataPackage(id: 'gaia-edr3-bright', name: 'Gaia', category: 'catalog'),
    ]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(TextButton, 'Remove'), findsOneWidget, reason: 'installed package');
    expect(find.widgetWithText(FilledButton, 'Download'), findsOneWidget, reason: 'not-installed package');
  });

  testWidgets('tapping Download calls the client', (tester) async {
    final api = _FakeDataManagerClient(const [DataPackage(id: 'gaia-edr3-bright', name: 'Gaia', category: 'catalog')]);
    await tester.pumpWidget(_host(api));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Download'));
    await tester.pump();
    expect(api.downloaded, ['gaia-edr3-bright']);
  });

  testWidgets('an active download shows a progress bar + Cancel', (tester) async {
    final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2', name: 'Tycho-2', category: 'catalog')]);
    await tester.pumpWidget(_host(api, downloads: const {
      'tycho-2': DownloadProgress(
          downloadId: 'd1', packageId: 'tycho-2', downloadedBytes: 512, totalBytes: 1024, percentComplete: 50),
    }));
    await tester.pumpAndSettle();

    expect(find.byType(LinearProgressIndicator), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Cancel'), findsOneWidget);

    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pump();
    expect(api.cancelled, ['d1']);
  });

  testWidgets('a failed download shows the error', (tester) async {
    final api = _FakeDataManagerClient(const [DataPackage(id: 'tycho-2', name: 'Tycho-2', category: 'catalog')]);
    await tester.pumpWidget(_host(api, downloads: const {
      'tycho-2': DownloadProgress(
          downloadId: 'd1', packageId: 'tycho-2', phase: DownloadPhase.failed, error: 'stalled'),
    }));
    await tester.pumpAndSettle();

    expect(find.text('Download stalled'), findsOneWidget);
  });

  test('formatBytes renders binary units', () {
    expect(formatBytes(0), '0 B');
    expect(formatBytes(512), '512 B');
    expect(formatBytes(2 * 1024 * 1024), '2.0 MB');
    expect(formatBytes(187654321), '179 MB', reason: 'values >= 100 drop the decimal');
  });
}
