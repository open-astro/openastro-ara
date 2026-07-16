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
  _StubAdapter(this.bodyFor, {this.statusCode = 200});
  final Object? Function(RequestOptions options) bodyFor;
  final int statusCode;

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
      statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

SequenceApi _api(Object? Function(RequestOptions) bodyFor,
    {int statusCode = 200}) {
  final dio = Dio()
    ..httpClientAdapter = _StubAdapter(bodyFor, statusCode: statusCode);
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

    test('isActive is true for every in-progress state, false when idle/terminal',
        () {
      for (final s in [
        SequenceRunState.starting,
        SequenceRunState.running,
        SequenceRunState.paused,
        SequenceRunState.aborting,
      ]) {
        expect(s.isActive, isTrue, reason: '$s should be active');
      }
      for (final s in [
        SequenceRunState.idle,
        SequenceRunState.completed,
        SequenceRunState.stopped,
        SequenceRunState.failed,
      ]) {
        expect(s.isActive, isFalse, reason: '$s should be inactive');
      }
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
      final page = await api.list();
      expect(page.items.map((s) => s.id), ['s1', 's2']);
      expect(page.items[1].currentRunState, SequenceRunState.paused);
      expect(page.hasMore, isFalse);
      expect(page.nextCursor, isNull);
    });

    test('surfaces has_more + next_cursor so a long list is not truncated',
        () async {
      final api = _api((_) => {
            'items': [
              {'id': 's1', 'name': 'One'},
            ],
            'next_cursor': 'eyJvIjoxfQ==',
            'has_more': true,
          });
      final page = await api.list();
      expect(page.hasMore, isTrue);
      expect(page.nextCursor, 'eyJvIjoxfQ==');
    });

    test('throws on a non-envelope 200 body', () async {
      final api = _api((_) => [1, 2, 3]); // array, not a CursorPage object
      expect(api.list(), throwsA(isA<FormatException>()));
    });


  });

  group('SequenceApi templates', () {
    test('listTemplates parses the array and drops name-less rows', () async {
      final api = _api((_) => [
            {
              'name': 'Deep-sky LRGB',
              'category': 'Deep sky',
              'description': 'L/R/G/B with dithering',
              'is_built_in': true,
            },
            {'name': '', 'category': 'broken'}, // dropped — no name
            {'name': 'Quick test', 'category': 'Utility'},
          ]);
      final templates = await api.listTemplates();
      expect(templates.map((t) => t.name), ['Deep-sky LRGB', 'Quick test']);
      expect(templates.first.isBuiltIn, isTrue);
      expect(templates.first.description, 'L/R/G/B with dithering');
    });

    test('listTemplates throws on a non-array body', () async {
      final api = _api((_) => {'not': 'an array'});
      expect(api.listTemplates(), throwsA(isA<FormatException>()));
    });

    test('instantiateTemplate posts the new name and returns the created id',
        () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'id': 'seq-new', 'name': 'My LRGB'};
      });
      final id = await api.instantiateTemplate('  Deep-sky LRGB  ', '  My LRGB  ');
      expect(id, 'seq-new');
      // Both the path name and the body name are trimmed before sending.
      expect(captured!.path, contains('/templates/Deep-sky%20LRGB/instantiate'));
      expect((captured!.data as Map)['new_sequence_name'], 'My LRGB');
    });

    test('instantiateTemplate rejects empty template name / new name', () async {
      final api = _api((_) => const {});
      expect(() => api.instantiateTemplate('', 'x'), throwsA(isA<ArgumentError>()));
      expect(() => api.instantiateTemplate('   ', 'x'), // whitespace-only name
          throwsA(isA<ArgumentError>()));
      expect(() => api.instantiateTemplate('t', '   '),
          throwsA(isA<ArgumentError>()));
    });

    test('instantiateTemplate throws when no id comes back', () async {
      final api = _api((_) => {'name': 'no id here'});
      expect(api.instantiateTemplate('t', 'n'), throwsA(isA<FormatException>()));
    });

    test('an unknown template (404) propagates as DioException', () async {
      final api = _api((_) => {'error': 'no such template'}, statusCode: 404);
      await expectLater(
        api.instantiateTemplate('nope', 'n'),
        throwsA(isA<DioException>()
            .having((e) => e.response?.statusCode, 'statusCode', 404)),
      );
    });
  });

  group('SequenceApi.create', () {
    test('posts name + body to /sequences and returns the created id', () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'id': 'seq-42', 'name': 'M31'};
      });
      final body = {'schemaVersion': 'openastroara-sequence-v1', 'x': 1};
      final id = await api.create('  M31  ', body, description: 'planned');
      expect(id, 'seq-42');
      expect(captured!.path, endsWith('/api/v1/sequences'));
      final sent = captured!.data as Map;
      expect(sent['name'], 'M31'); // trimmed
      expect(sent['description'], 'planned');
      expect(sent['body'], body);
    });

    test('omits description when not supplied', () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'id': 'seq-1'};
      });
      await api.create('T', const {'schemaVersion': 'openastroara-sequence-v1'});
      expect((captured!.data as Map).containsKey('description'), isFalse);
    });

    test('rejects an empty name', () {
      final api = _api((_) => const {});
      expect(() => api.create('   ', const {}), throwsA(isA<ArgumentError>()));
    });

    test('throws when no id comes back', () async {
      final api = _api((_) => {'name': 'no id'});
      expect(api.create('n', const {}), throwsA(isA<FormatException>()));
    });

    test('a rejected body (422) propagates as DioException', () async {
      final api = _api((_) => {'error': 'bad body'}, statusCode: 422);
      await expectLater(
        api.create('n', const {}),
        throwsA(isA<DioException>()
            .having((e) => e.response?.statusCode, 'statusCode', 422)),
      );
    });
  });

  group('SequenceTemplate.fromJson', () {
    test('degrades missing/wrong-typed fields', () {
      final t = SequenceTemplate.fromJson({'name': 'T'});
      expect(t.name, 'T');
      expect(t.category, '');
      expect(t.description, isNull);
      expect(t.isBuiltIn, isFalse);
    });
  });

  group('SequenceApi.getSequence', () {
    test('parses the NINA body into a tree', () async {
      final api = _api((_) => {
            'id': 's1',
            'name': 'M42',
            'body': {
              r'$type':
                  'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
              'Name': 'M42',
              'Items': {
                r'$values': [
                  {
                    r'$type':
                        'NINA.Sequencer.Container.StartAreaContainer, NINA.Sequencer',
                    'Name': 'Start',
                    'Items': {r'$values': []}
                  }
                ]
              }
            }
          });
      final root = await api.getSequence('s1');
      expect(root.displayName, 'M42');
      expect(root.children.single.displayName, 'Start');
    });

    test('a detail with no body object → empty root named from the DTO', () async {
      final api = _api((_) => {'id': 's1', 'name': 'Bare'});
      final root = await api.getSequence('s1');
      expect(root.displayName, 'Bare');
      expect(root.children, isEmpty);
    });

    test('rejects an empty id before any request', () async {
      final api = _api((_) => const {});
      expect(() => api.getSequence(''), throwsA(isA<ArgumentError>()));
    });
  });

  group('SequenceRunStateInfo.applyWsProgress', () {
    test('folds live WS fields on, preserving the slower REST-only fields', () {
      final base = SequenceRunStateInfo(
        sequenceId: 'seq-1',
        runId: 'run-1',
        state: SequenceRunState.running,
        currentInstructionIndex: 2,
        currentTargetName: 'M31',
        startedUtc: DateTime.utc(2026, 6, 18, 9),
        instructionsCompleted: 5,
        instructionsTotal: 60,
        currentInstructionDescription: 'Take exposure',
      );
      final next = base.applyWsProgress(const {
        'sequence_id': 'seq-1',
        'run_id': 'run-1',
        'state': 'paused',
        'current_instruction_index': 4,
        'instructions_completed': 18,
        'instructions_total': 60,
      });
      // Fast fields updated from the WS frame…
      expect(next.state, SequenceRunState.paused);
      expect(next.currentInstructionIndex, 4);
      expect(next.instructionsCompleted, 18);
      // …slower fields the WS frame doesn't carry are preserved.
      expect(next.currentTargetName, 'M31');
      expect(next.startedUtc, DateTime.utc(2026, 6, 18, 9));
      expect(next.currentInstructionDescription, 'Take exposure');
    });

    test('an absent/unknown state keeps the current state, not null', () {
      const base = SequenceRunStateInfo(state: SequenceRunState.running);
      expect(base.applyWsProgress(const {'instructions_completed': 1}).state,
          SequenceRunState.running);
      expect(base.applyWsProgress(const {'state': 'warp_speed'}).state,
          SequenceRunState.running);
    });
  });

  group('SequenceApi.getSequenceDetail', () {
    test('parses id/name/description/body/template_origin', () async {
      final api = _api((_) => {
            'id': 's1',
            'name': 'M42',
            'description': 'Orion',
            'body': {r'$type': 'NINA...Root', 'Name': 'M42'},
            'template_origin': 'Deep-sky LRGB',
          });
      final d = await api.getSequenceDetail('s1');
      expect(d.id, 's1');
      expect(d.name, 'M42');
      expect(d.description, 'Orion');
      expect(d.body['Name'], 'M42'); // raw body kept verbatim
      expect(d.templateOrigin, 'Deep-sky LRGB');
    });

    test('a detail with no body object → empty body map', () async {
      final api = _api((_) => {'id': 's1', 'name': 'Bare'});
      final d = await api.getSequenceDetail('s1');
      expect(d.body, isEmpty);
    });

    test('value equality is deep — two parses of the same nested body are ==',
        () {
      Map<String, dynamic> json() => {
            'id': 's1',
            'name': 'M42',
            'body': {
              r'$type': 'Root',
              'Items': {
                r'$values': [
                  {'Name': 'Start', 'Exposure': 60}
                ]
              }
            },
          };
      final a = SequenceDetail.fromJson(json());
      final b = SequenceDetail.fromJson(json());
      expect(a, b); // deep — distinct nested-Map instances, equal content
      expect(a.hashCode, b.hashCode);
      // A nested change breaks equality.
      final changed = json()..['body']['Items']['\$values'][0]['Exposure'] = 120;
      expect(SequenceDetail.fromJson(changed), isNot(a));
    });

    test('rejects an empty id before any request', () async {
      final api = _api((_) => const {});
      expect(() => api.getSequenceDetail(''), throwsA(isA<ArgumentError>()));
    });

    test('copyWith updates supplied fields, keeps the rest (null = keep)', () {
      final base = SequenceDetail(
          id: 's1',
          name: 'A',
          description: 'desc',
          body: const {'x': 1},
          templateOrigin: 'T');
      final renamed = base.copyWith(name: 'B');
      expect(renamed.name, 'B');
      expect(renamed.description, 'desc'); // kept
      expect(renamed.body, {'x': 1}); // kept
      // Body unchanged → reuse the same already-unmodifiable map (no re-wrap).
      expect(identical(renamed.body, base.body), isTrue);
      expect(renamed.id, 's1'); // not copyable
      expect(renamed.templateOrigin, 'T');
      // null description does NOT clear it (documented limitation).
      expect(base.copyWith(description: null).description, 'desc');
      // a supplied body replaces.
      expect(base.copyWith(body: const {'y': 2}).body, const {'y': 2});
    });

    test('body is DEEPLY unmodifiable — mutation throws at any depth', () {
      final d = SequenceDetail(id: 's1', body: {
        'x': 1,
        'Items': {
          r'$values': [
            {'Exposure': 60}
          ]
        }
      });
      expect(() => d.body['x'] = 2, throwsUnsupportedError); // top level
      expect(() => (d.body['Items'] as Map)['k'] = 'v',
          throwsUnsupportedError); // nested map
      final values = (d.body['Items'] as Map)[r'$values'] as List;
      expect(() => values.add({}), throwsUnsupportedError); // nested list
      expect(() => (values.first as Map)['Exposure'] = 120,
          throwsUnsupportedError); // map inside list
      // Nested maps stay Map<String, dynamic> so save-b can cast without throwing.
      expect(d.body['Items'], isA<Map<String, dynamic>>());
      expect(values.first, isA<Map<String, dynamic>>());
    });

    test('a pathologically deep body throws instead of overflowing the stack',
        () {
      // Build a > _maxBodyDepth (512) nested chain.
      Map<String, dynamic> deep = {'leaf': 1};
      for (var i = 0; i < 600; i++) {
        deep = {'n': deep};
      }
      expect(() => SequenceDetail(id: 's1', body: deep),
          throwsA(isA<FormatException>()));
    });
  });

  group('SequenceApi.updateSequence', () {
    test('PATCHes only the supplied fields and returns the updated detail',
        () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'id': 's1', 'name': 'Renamed', 'body': {'Name': 'Renamed'}};
      });
      final d = await api.updateSequence('s1',
          name: 'Renamed', body: {'Name': 'Renamed'});
      expect(d.name, 'Renamed');
      expect(captured!.method, 'PATCH');
      final sent = captured!.data as Map;
      expect(sent['name'], 'Renamed');
      expect((sent['body'] as Map)['Name'], 'Renamed');
      expect(sent.containsKey('description'), isFalse); // omitted, not null
    });

    test('rejects an empty id and an empty change set', () async {
      final api = _api((_) => const {});
      expect(() => api.updateSequence('', name: 'x'),
          throwsA(isA<ArgumentError>()));
      expect(() => api.updateSequence('s1'), throwsA(isA<ArgumentError>()));
    });

    test('a 422 (schema-invalid body) propagates as DioException(422)',
        () async {
      final api = _api((_) => {'error': 'bad body'}, statusCode: 422);
      await expectLater(
        api.updateSequence('s1', body: const {'bad': true}),
        throwsA(isA<DioException>()
            .having((e) => e.response?.statusCode, 'statusCode', 422)),
      );
    });

    test('an unknown id (404) propagates as DioException', () async {
      final api = _api((_) => {'error': 'no seq'}, statusCode: 404);
      expect(api.updateSequence('s1', name: 'x'),
          throwsA(isA<DioException>()));
    });

    test('throws on a non-object 200 body', () async {
      final api = _api((_) => [1, 2, 3]); // array, not the updated detail
      expect(api.updateSequence('s1', name: 'x'),
          throwsA(isA<FormatException>()));
    });
  });

  group('SequenceApi.getRunState', () {
    test('parses an active run state', () async {
      final api = _api((_) => {
            'sequence_id': 'seq-1',
            'run_id': 'run-9',
            'state': 'running',
            'current_instruction_index': 4,
            'current_target_name': 'M31',
            'started_utc': '2026-06-18T09:00:00Z',
            'completed_utc': null,
            'instructions_completed': 12,
            'instructions_total': 60,
            'current_instruction_description': 'Take exposure',
          });
      final info = await api.getRunState('seq-1');
      expect(info, isNotNull);
      expect(info!.state, SequenceRunState.running);
      expect(info.currentInstructionIndex, 4);
      expect(info.currentTargetName, 'M31');
      expect(info.startedUtc, DateTime.utc(2026, 6, 18, 9));
      expect(info.completedUtc, isNull);
      expect(info.instructionsCompleted, 12);
      expect(info.instructionsTotal, 60);
    });

    test('an unknown/absent state surfaces as null (not idle)', () async {
      final api = _api((_) => {'run_id': 'r', 'state': 'warp_speed'});
      final info = await api.getRunState('seq-1');
      expect(info!.state, isNull);
    });

    test('returns null when there is no active run (404)', () async {
      final api = _api((_) => {'error': 'no run'}, statusCode: 404);
      expect(await api.getRunState('seq-1'), isNull);
    });

    test('rejects an empty id before any request', () async {
      final api = _api((_) => const {});
      expect(() => api.getRunState(''), throwsA(isA<ArgumentError>()));
    });

    test('throws on a non-object 200 body', () async {
      final api = _api((_) => [1, 2, 3]); // array, not the state object
      expect(api.getRunState('seq-1'), throwsA(isA<FormatException>()));
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

    test('skipCurrent posts to /skip-current and returns the op id', () async {
      RequestOptions? captured;
      final api = _api((opts) {
        captured = opts;
        return {'operation_id': 'op-skip', 'operation_type': 'sequence_skip'};
      });
      expect(await api.skipCurrent('seq-1'), 'op-skip');
      expect(captured!.path, endsWith('/sequences/seq-1/skip-current'));
    });

    test('skipCurrent rejects an empty id before any request', () async {
      final api = _api((_) => {'operation_id': 'op'});
      expect(() => api.skipCurrent(''), throwsA(isA<ArgumentError>()));
    });

    test('a 409 (illegal transition) propagates as DioException', () async {
      final api = _api((_) => {'error': 'not running'}, statusCode: 409);
      expect(api.pause('seq-1'), throwsA(isA<DioException>()));
    });

    test('an empty id is rejected before any request', () async {
      final api = _api((_) => {'operation_id': 'op'});
      expect(() => api.start(''), throwsA(isA<ArgumentError>()));
    });
  });
}
