import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

/// Records the request and replies with a canned body, so the storage-settings
/// JSON mapping can be exercised without a live daemon.
class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({this.body = const <String, dynamic>{}});
  final Object body;

  String? method;
  String? path;
  Object? requestData;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    requestData = options.data;
    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

ProfileApi _api(_RecordingAdapter adapter) {
  final dio = Dio()..httpClientAdapter = adapter;
  return ProfileApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  group('storage settings JSON (#1145 fault_log_retention_days)', () {
    test(
      'getStorageSettings reads the wire key with a non-default value',
      () async {
        final a = _RecordingAdapter(
          body: {
            'save_directory': '/media/x',
            'file_format': 'fits',
            'compression': 'rice',
            'filename_template': 't',
            'min_free_disk_warn_gb': 10,
            'min_free_disk_critical_gb': 2,
            'backup_retention_count': 20,
            'fault_log_retention_days': 365,
          },
        );
        final s = await _api(a).getStorageSettings();
        expect(a.method, 'GET');
        expect(a.path, '/api/v1/profile/storage');
        expect(s.faultLogRetentionDays, 365);
      },
    );

    test('a missing key falls back to the 90-day default', () async {
      final a = _RecordingAdapter(body: {'save_directory': '/media/x'});
      final s = await _api(a).getStorageSettings();
      expect(s.faultLogRetentionDays, 90);
    });

    test('putStorageSettings writes the wire key and reads it back', () async {
      const sent = StorageSettings(faultLogRetentionDays: 0);
      final a = _RecordingAdapter(body: {'fault_log_retention_days': 0});
      final echoed = await _api(a).putStorageSettings(sent);
      expect(a.method, 'PUT');
      expect(a.path, '/api/v1/profile/storage');
      expect((a.requestData as Map)['fault_log_retention_days'], 0);
      expect(echoed.faultLogRetentionDays, 0);
    });
  });
}
