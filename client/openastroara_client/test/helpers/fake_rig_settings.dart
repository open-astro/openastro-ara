import 'package:dio/dio.dart';
import 'package:openastroara/models/client_settings.dart';
import 'package:openastroara/services/client_settings_api.dart';

/// The rig's client-settings blob as the daemon keeps it: [merge] replaces the
/// given top-level keys (null removes one) and stamps every write. Several
/// devices (containers) can share one instance.
class FakeRigSettings implements ClientSettingsClient {
  FakeRigSettings([Map<String, dynamic>? stored]) : stored = {...?stored};
  Map<String, dynamic> stored;
  int fetches = 0;
  int merges = 0;
  Map<String, dynamic>? lastMerged;

  /// Fail the next writes like a dropped link.
  bool failWrites = false;

  /// Answer writes like a daemon that predates the merge.
  bool oldDaemon = false;
  int _clock = 0;

  @override
  Future<ClientSettings> fetch() async {
    fetches++;
    return _view();
  }

  @override
  Future<ClientSettings> replace(Map<String, dynamic> settings) async {
    stored = {...settings};
    return _stamped();
  }

  @override
  Future<ClientSettings> merge(Map<String, dynamic> patch) async {
    merges++;
    if (oldDaemon) {
      throw DioException(
        requestOptions: RequestOptions(),
        response: Response(requestOptions: RequestOptions(), statusCode: 404),
      );
    }
    if (failWrites) throw StateError('link dropped');
    lastMerged = patch;
    patch.forEach((k, v) => v == null ? stored.remove(k) : stored[k] = v);
    return _stamped();
  }

  ClientSettings _view() =>
      ClientSettings(settings: Map<String, dynamic>.unmodifiable({...stored}));

  ClientSettings _stamped() => ClientSettings(
    settings: Map<String, dynamic>.unmodifiable({...stored}),
    updatedUtc: DateTime.utc(2026, 11, 11, 20).add(Duration(seconds: ++_clock)),
  );

  @override
  void close() {}
}
