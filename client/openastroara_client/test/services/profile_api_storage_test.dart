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
            'compression': 'zstd',
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

  // #1148 — the format is fits | xisf and compression off | zstd on the wire;
  // the retired fits_rice/fits_gzip formats read as FITS and the legacy
  // rice/gzip compression values (which meant "compress") as Zstandard.
  group('storage file format + compression wire tokens (#1148)', () {
    Future<StorageSettings> read(Object? format, Object? compression) =>
        _api(_RecordingAdapter(body: {
          'save_directory': '/media/x',
          'file_format': ?format,
          'compression': ?compression,
        })).getStorageSettings();

    test('current tokens parse', () async {
      final s = await read('xisf', 'off');
      expect(s.fileFormat, StorageFileFormat.xisf);
      expect(s.compression, StorageCompression.off);
      final t = await read('fits', 'zstd');
      expect(t.fileFormat, StorageFileFormat.fits);
      expect(t.compression, StorageCompression.zstd);
    });

    test('legacy formats read as plain FITS', () async {
      expect((await read('fits_rice', 'rice')).fileFormat, StorageFileFormat.fits);
      expect((await read('fits_gzip', 'gzip')).fileFormat, StorageFileFormat.fits);
      expect((await read('XISF', null)).fileFormat, StorageFileFormat.xisf);
      expect((await read(null, null)).fileFormat, StorageFileFormat.fits);
    });

    test('legacy compression values read as Zstandard; only off is off', () async {
      expect((await read('xisf', 'rice')).compression, StorageCompression.zstd);
      expect((await read('xisf', 'gzip')).compression, StorageCompression.zstd);
      expect((await read('xisf', null)).compression, StorageCompression.zstd);
      expect((await read('xisf', 'OFF')).compression, StorageCompression.off);
    });

    for (final format in StorageFileFormat.values) {
      for (final compression in StorageCompression.values) {
        test('${format.name}/${compression.name} round-trips', () async {
          final sent = StorageSettings(
              fileFormat: format, compression: compression);
          final a = _RecordingAdapter(body: {
            'file_format': format.wire,
            'compression': compression.wire,
          });
          final echoed = await _api(a).putStorageSettings(sent);
          final body = a.requestData as Map;
          expect(body['file_format'], anyOf('fits', 'xisf'));
          expect(body['compression'], anyOf('off', 'zstd'));
          expect(StorageFileFormat.fromWire(body['file_format'] as String),
              format);
          expect(StorageCompression.fromWire(body['compression'] as String),
              compression);
          expect(echoed.fileFormat, format);
          expect(echoed.compression, compression);
        });
      }
    }
  });
}
