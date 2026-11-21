import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/backup_snapshot.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/backup_api.dart';

/// Minimal canned-response adapter so the Dio paths are exercised without a network.
class _FakeAdapter implements HttpClientAdapter {
  _FakeAdapter(this.handler);
  final ResponseBody Function(RequestOptions options) handler;
  RequestOptions? lastRequest;

  @override
  Future<ResponseBody> fetch(
      RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    lastRequest = options;
    return handler(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(Object body, {int status = 200}) => ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );

const _server = AraServer(hostname: 'host', port: 5555);

BackupApi _api(_FakeAdapter adapter) => BackupApi(_server, dio: Dio()..httpClientAdapter = adapter);

void main() {
  group('BackupApi.listSnapshots', () {
    test('parses an array body and drops unusable entries (no id or no download url)', () async {
      final api = _api(_FakeAdapter((_) => _json(<dynamic>[
            {'backup_id': 'b1', 'download_url': '/dl/b1', 'size_bytes': 10, 'included_areas': ['profiles']},
            {'size_bytes': 99}, // id-less → dropped
            {'backup_id': 'b2'}, // no download_url → dropped
          ])));
      final list = await api.listSnapshots();
      expect(list, hasLength(1));
      expect(list.single.backupId, 'b1');
    });

    test('throws on a non-array 2xx body', () async {
      final api = _api(_FakeAdapter((_) => _json(<String, dynamic>{'oops': true})));
      await expectLater(api.listSnapshots(), throwsA(isA<FormatException>()));
    });
  });

  group('BackupApi.createBackup', () {
    test('returns the operation_id', () async {
      final api = _api(_FakeAdapter((_) => _json(<String, dynamic>{'operation_id': 'op-1'}, status: 202)));
      expect(await api.createBackup(), 'op-1');
    });

    test('throws when no operation_id is returned', () async {
      final api = _api(_FakeAdapter((_) => _json(<String, dynamic>{}, status: 202)));
      await expectLater(api.createBackup(), throwsA(isA<FormatException>()));
    });
  });

  group('BackupApi.restore', () {
    test('posts the source url + area flags and returns the operation_id', () async {
      final adapter = _FakeAdapter((_) => _json(<String, dynamic>{'operation_id': 'op-r'}, status: 202));
      final api = _api(adapter);
      final id = await api.restore(sourceUrl: '/api/v1/backup/snapshot/b1/download', profiles: true, sequences: false, frameMetadata: true);
      expect(id, 'op-r');
      final body = adapter.lastRequest!.data as Map<String, dynamic>;
      expect(body['backup_source_url'], '/api/v1/backup/snapshot/b1/download');
      expect(body['restore_profiles'], isTrue);
      expect(body['restore_sequences'], isFalse);
      expect(body['restore_frame_metadata'], isTrue);
      expect(body['restore_logs'], isFalse);
    });

    test('rejects an empty source url before hitting the network', () async {
      final api = _api(_FakeAdapter((_) => _json(<String, dynamic>{'operation_id': 'unused'})));
      await expectLater(
        api.restore(sourceUrl: '', profiles: true, sequences: true, frameMetadata: false),
        throwsA(isA<ArgumentError>()),
      );
    });
  });

  group('BackupApi.absoluteDownloadUrl', () {
    test('resolves a server-relative download path against the base origin', () {
      final api = BackupApi(_server);
      const snap = BackupSnapshot(backupId: 'b1', downloadUrl: '/api/v1/backup/snapshot/b1/download');
      final url = api.absoluteDownloadUrl(snap);
      expect(url, startsWith(Uri.parse(_server.baseUrl).origin));
      expect(url, endsWith('/api/v1/backup/snapshot/b1/download'));
      api.close();
    });
  });
}
