import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/sequence_api.dart';

/// Routes canned 200 bodies by request path so list + lifecycle calls can be
/// exercised without a live server.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.bodyFor);
  final Object? Function(RequestOptions options) bodyFor;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    return ResponseBody.fromString(
      jsonEncode(bodyFor(options)),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

SequenceApi _api(Object? Function(RequestOptions) bodyFor) {
  final dio = Dio()..httpClientAdapter = _StubAdapter(bodyFor);
  return SequenceApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  group('SequenceRunState.fromWire', () {
    test('parses known states, degrades null/unknown to null', () {
      expect(SequenceRunState.fromWire('running'), SequenceRunState.running);
      expect(SequenceRunState.fromWire('completed'), SequenceRunState.completed);
      expect(SequenceRunState.fromWire(null), isNull);
      expect(SequenceRunState.fromWire('warp_speed'), isNull);
      expect(SequenceRunState.fromWire(42), isNull);
    });

    test('isActive is true only while running/starting', () {
      expect(SequenceRunState.running.isActive, isTrue);
      expect(SequenceRunState.starting.isActive, isTrue);
      expect(SequenceRunState.paused.isActive, isFalse);
      expect(SequenceRunState.idle.isActive, isFalse);
    });
  });

  group('SequenceListItem.fromJson', () {
    test('parses a full row', () {
      final s = SequenceListItem.fromJson({
        'id': 'a1',
        'name': 'M31 LRGB',
        'description': 'Andromeda',
        'created_utc': '2026-06-18T10:00:00Z',
        'modified_utc': '2026-06-18T11:30:00Z',
        'current_run_state': 'running',
        'instruction_count': 12,
        'target_count': 1,
        'template_origin': 'Deep-sky LRGB',
      });
      expect(s.id, 'a1');
      expect(s.name, 'M31 LRGB');
      expect(s.description, 'Andromeda');
      expect(s.createdUtc, DateTime.utc(2026, 6, 18, 10));
      expect(s.currentRunState, SequenceRunState.running);
      expect(s.instructionCount, 12);
      expect(s.targetCount, 1);
      expect(s.templateOrigin, 'Deep-sky LRGB');
    });

    test('degrades missing/wrong-typed fields', () {
      final s = SequenceListItem.fromJson({'id': 'b2'});
      expect(s.id, 'b2');
      expect(s.name, '');
      expect(s.description, isNull);
      expect(s.createdUtc, isNull);
      expect(s.currentRunState, isNull);
      expect(s.instructionCount, 0);
    });
  });

  group('SequenceApi.list', () {
    test('parses the CursorPage envelope and drops id-less rows', () async {
      final api = _api((_) => {
            'items': [
              {'id': 's1', 'name': 'One'},
              {'id': '', 'name': 'malformed — no id'},
              {'id': 's2', 'name': 'Two', 'current_run_state': 'paused'},
            ],
            'next_cursor': null,
            'has_more': false,
          });
      final list = await api.list();
      expect(list.map((s) => s.id), ['s1', 's2']);
      expect(list[1].currentRunState, SequenceRunState.paused);
    });

    test('throws on a non-envelope 200 body', () async {
      final api = _api((_) => [1, 2, 3]); // array, not a CursorPage object
      expect(api.list(), throwsA(isA<FormatException>()));
    });
  });

  group('SequenceApi lifecycle', () {
    test('start returns the accepted operation id', () async {
      final api = _api((_) =>
          {'operation_id': 'op-123', 'operation_type': 'sequence_start'});
      expect(await api.start('seq-1'), 'op-123');
    });

    test('start omits start_from_instruction_index (sends no explicit null)',
        () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'operation_id': 'op-1', 'operation_type': 'sequence_start'};
      });
      await api.start('seq-1');
      final body = captured!.data as Map;
      expect(body.containsKey('start_from_instruction_index'), isFalse);
      expect(body['dry_run'], false);
      expect(body['continue_on_recoverable_errors'], false);
    });

    test('abort throws when no operation_id comes back', () async {
      final api = _api((_) => {'unexpected': true});
      expect(api.abort('seq-1'), throwsA(isA<FormatException>()));
    });
  });
}
