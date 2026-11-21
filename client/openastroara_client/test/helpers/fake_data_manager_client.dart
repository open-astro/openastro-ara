import 'package:openastroara/models/catalog_object.dart';
import 'package:openastroara/models/data_package.dart';
import 'package:openastroara/models/solver_recommendation.dart';
import 'package:openastroara/services/data_manager_api.dart';

/// A [DataManagerClient] for wizard tests: answers the §36.10 recommendation
/// with [recommendation] and records every queued download.
class FakeDataManagerClient implements DataManagerClient {
  FakeDataManagerClient({this.recommendation = const SolverRecommendation(), this.throwOnRecommend = false});
  SolverRecommendation recommendation;
  bool throwOnRecommend;
  SolverRecommendationRequest? lastRequest;
  final List<String> downloaded = <String>[];

  @override
  Future<List<DataPackage>> listPackages() async => const <DataPackage>[];

  @override
  Future<SolverRecommendation> solverRecommendation(SolverRecommendationRequest request) async {
    lastRequest = request;
    if (throwOnRecommend) throw StateError('rig unreachable');
    return recommendation;
  }

  @override
  Future<String> download(String packageId, {bool forceReinstall = false}) async {
    downloaded.add(packageId);
    return 'dl-$packageId';
  }

  @override
  Future<void> cancel(String downloadId) async {}
  @override
  Future<bool> delete(String packageId) async => true;
  @override
  Future<List<CatalogObject>?> getCatalog(String packageId, {double? maxMag, int? limit}) async =>
      const <CatalogObject>[];
  @override
  void close() {}
}
