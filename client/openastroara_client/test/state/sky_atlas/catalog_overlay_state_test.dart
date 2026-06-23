import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/catalog_object.dart';
import 'package:openastroara/models/data_package.dart';
import 'package:openastroara/services/data_manager_api.dart';
import 'package:openastroara/state/sky_atlas/catalog_overlay_state.dart';
import 'package:openastroara/state/sky_atlas/data_manager_state.dart';

// A fake that records which catalogs were requested and returns a fixed set per
// id (null → the daemon's 404 "no catalog" case).
class _FakeClient implements DataManagerClient {
  _FakeClient(this.catalogs);
  final Map<String, List<CatalogObject>?> catalogs;
  final List<String> requested = <String>[];

  @override
  Future<List<CatalogObject>?> getCatalog(String packageId, {double? maxMag, int? limit}) async {
    requested.add(packageId);
    return catalogs[packageId];
  }

  @override
  Future<List<DataPackage>> listPackages() async => const <DataPackage>[];
  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async => 'x';
  @override
  Future<void> cancel(String downloadId) async {}
  @override
  Future<bool> delete(String packageId) async => true;
  @override
  void close() {}
}

DataPackage _pkg(String id, {required bool installed}) =>
    DataPackage(id: id, isInstalled: installed);

ProviderContainer _container({
  required List<DataPackage> packages,
  required _FakeClient client,
}) {
  final c = ProviderContainer(overrides: [
    // Bind the fake client directly (skip the saved-server → factory chain).
    dataManagerApiProvider.overrideWithValue(client),
    dataManagerPackagesProvider.overrideWith(() => _StubPackages(packages)),
  ]);
  addTearDown(c.dispose);
  return c;
}

class _StubPackages extends DataManagerPackagesNotifier {
  _StubPackages(this._packages);
  final List<DataPackage> _packages;
  @override
  Future<List<DataPackage>?> build() async => _packages;
}

void main() {
  test('combines installed catalog packages and skips uninstalled / unknown ids', () async {
    final client = _FakeClient({
      'hyg-stars': const [CatalogObject(name: 'Sol', raDeg: 1, decDeg: 2)],
      'openngc-dso': const [CatalogObject(name: 'M31', raDeg: 10, decDeg: 41)],
    });
    final c = _container(
      packages: [
        _pkg('hyg-stars', installed: true),
        _pkg('openngc-dso', installed: false), // not installed → not fetched
        _pkg('horizon-default', installed: true), // not a known catalog → ignored
      ],
      client: client,
    );

    await c.read(dataManagerPackagesProvider.future); // resolve the dependency first
    final objects = await c.read(skyAtlasCatalogProvider.future);

    expect(client.requested, ['hyg-stars'], reason: 'only the installed, known catalog is fetched');
    expect(objects.map((o) => o.name), ['Sol']);
  });

  test('flattens multiple installed catalogs and drops a 404 (null) result', () async {
    final client = _FakeClient({
      'hyg-stars': const [
        CatalogObject(name: 'Sol', raDeg: 0, decDeg: 0),
        CatalogObject(name: 'Vega', raDeg: 279, decDeg: 38),
      ],
      'openngc-dso': null, // vanished between list + fetch → dropped, not an error
    });
    final c = _container(
      packages: [
        _pkg('hyg-stars', installed: true),
        _pkg('openngc-dso', installed: true),
      ],
      client: client,
    );

    await c.read(dataManagerPackagesProvider.future); // resolve the dependency first
    final objects = await c.read(skyAtlasCatalogProvider.future);

    expect(client.requested.toSet(), {'hyg-stars', 'openngc-dso'});
    expect(objects.map((o) => o.name), ['Sol', 'Vega'], reason: 'the null catalog contributes nothing');
  });

  test('no installed catalog → empty overlay, no fetch', () async {
    final client = _FakeClient(const {});
    final c = _container(
      packages: [_pkg('hyg-stars', installed: false)],
      client: client,
    );

    await c.read(dataManagerPackagesProvider.future); // resolve the dependency first
    final objects = await c.read(skyAtlasCatalogProvider.future);

    expect(objects, isEmpty);
    expect(client.requested, isEmpty);
  });
}
