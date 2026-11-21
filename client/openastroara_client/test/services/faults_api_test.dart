import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/fault_row.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/faults_api.dart';

/// Routes canned bodies by request path so list + get-by-id calls can be
/// exercised without a live server (the `sequence_api_test` stub pattern).
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.bodyFor, {this.statusCode = 200});
  final Object? Function(RequestOptions options) bodyFor;
  final int statusCode;

  RequestOptions? lastOptions;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastOptions = options;
    return ResponseBody.fromString(
      jsonEncode(bodyFor(options)),
      statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

(FaultsApi, _StubAdapter) _api(Object? Function(RequestOptions) bodyFor,
    {int statusCode = 200}) {
  final adapter = _StubAdapter(bodyFor, statusCode: statusCode);
  final dio = Dio()..httpClientAdapter = adapter;
  return (
    FaultsApi(const AraServer(hostname: 'x', port: 80), dio: dio),
    adapter
  );
}

Map<String, dynamic> _row({
  String id = 'f1',
  String? actionTaken,
  String? resolvedUtc,
}) =>
    {
      'id': id,
      'session_id': null,
      'detected_utc': '2026-07-10T04:00:00.0000000Z',
      'equipment_type': 'camera',
      'equipment_id': 'dev-1',
      'equipment_name': 'Test Camera',
      'fault_type': 'disconnected',
      'details': '3 probes failed',
      'action_taken': actionTaken,
      'resolved_utc': resolvedUtc,
      'affected_frames': <String>[],
    };

void main() {
  group('FaultRow.fromJson', () {
    test('parses a full row and degrades optionals', () {
      final row = FaultRow.fromJson(_row(
          actionTaken: 'recovered', resolvedUtc: '2026-07-10T04:05:00Z'));
      expect(row.id, 'f1');
      expect(row.sessionId, isNull);
      expect(row.equipmentType, 'camera');
      expect(row.equipmentName, 'Test Camera');
      expect(row.faultType, 'disconnected');
      expect(row.actionTaken, 'recovered');
      expect(row.resolved, isTrue);
      expect(row.affectedFrames, isEmpty);

      final sparse = FaultRow.fromJson(const {'id': 'f2'});
      expect(sparse.equipmentType, 'unknown');
      expect(sparse.resolved, isFalse);
      expect(sparse.detectedUtc,
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true));
    });
  });

  group('FaultsApi.list', () {
    test('parses the cursor envelope and forwards the filters', () async {
      final (api, adapter) = _api((options) => {
            'items': [_row(), _row(id: 'f2')],
            'next_cursor': '2',
            'has_more': true,
          });
      final page = await api.list(
          limit: 2,
          equipmentType: 'camera',
          faultType: 'disconnected',
          unresolvedOnly: true);
      expect(page.items.map((f) => f.id), ['f1', 'f2']);
      expect(page.nextCursor, '2');
      expect(page.hasMore, isTrue);
      final q = adapter.lastOptions!.queryParameters;
      expect(q['limit'], 2);
      expect(q['equipmentType'], 'camera');
      expect(q['faultType'], 'disconnected');
      expect(q['unresolvedOnly'], true);
      expect(q.containsKey('cursor'), isFalse);
      expect(q.containsKey('sessionId'), isFalse);
    });

    test('drops id-less rows and normalizes an absent cursor', () async {
      final (api, _) = _api((options) => {
            'items': [
              _row(),
              {'fault_type': 'op_error'},
            ],
            'next_cursor': null,
            'has_more': false,
          });
      final page = await api.list();
      expect(page.items.map((f) => f.id), ['f1']);
      expect(page.nextCursor, isNull);
      expect(page.hasMore, isFalse);
    });

    test('throws FormatException on an unexpected 2xx body shape', () {
      final (api, _) = _api((options) => {'unexpected': true});
      expect(api.list(), throwsA(isA<FormatException>()));
    });
  });

  group('FaultsApi.getById', () {
    test('parses a row', () async {
      final (api, _) = _api((options) => _row());
      final row = await api.getById('f1');
      expect(row!.id, 'f1');
    });

    test('404 returns null instead of throwing', () async {
      final (api, _) = _api((options) => {'title': 'not found'},
          statusCode: 404);
      expect(await api.getById('missing'), isNull);
    });
  });
}
