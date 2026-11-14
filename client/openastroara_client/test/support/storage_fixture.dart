import 'package:dio/dio.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

/// A loaded Storage section for tests. The product has no client defaults
/// (ADR 0015), so a test that needs a value states one.
const kTestStorage = StorageSettings(
  saveDirectory: '/media/openastroara',
  fileFormat: StorageFileFormat.fits,
  compression: StorageCompression.zstd,
  filenameTemplate: r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s',
  minFreeDiskWarnGb: 10,
  minFreeDiskCriticalGb: 2,
  backupRetentionCount: 20,
  faultLogRetentionDays: 90,
);

/// A rig that answers Storage with [kTestStorage] (or whatever [serve] holds)
/// and an ETag, honours If-Match like the daemon, and records every PUT.
class FakeStorageApi extends ProfileApi {
  FakeStorageApi() : super(const AraServer(hostname: 'test', port: 1));

  StorageSettings serve = kTestStorage;
  String etag = '"v1"';
  Object? failGet;
  Object? failPut;
  final puts = <StorageSettings>[];
  final ifMatches = <String?>[];
  int gets = 0;

  @override
  Future<({StorageSettings value, String? etag})> getStorageSection() async {
    gets++;
    if (failGet != null) throw failGet!;
    return (value: serve, etag: etag);
  }

  @override
  Future<({StorageSettings value, String? etag})> putStorageSection(
    StorageSettings value, {
    String? ifMatch,
  }) async {
    if (failPut != null) throw failPut!;
    ifMatches.add(ifMatch);
    if (ifMatch != null && ifMatch != etag) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/storage'),
        response: Response(
          requestOptions: RequestOptions(path: ''),
          statusCode: 412,
        ),
        type: DioExceptionType.badResponse,
      );
    }
    puts.add(value);
    serve = value;
    etag = '"v${puts.length + 1}"';
    return (value: value, etag: etag);
  }
}
