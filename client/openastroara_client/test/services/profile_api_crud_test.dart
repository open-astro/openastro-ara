import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';

/// Records the request it received and replies with a canned body, so the §37
/// multi-profile CRUD methods can be exercised without a live daemon.
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
  group('ProfileList.fromJson', () {
    test('parses active_id + profiles and resolves the active one', () {
      final list = ProfileList.fromJson({
        'active_id': 'id-2',
        'profiles': [
          {'id': 'id-1', 'name': 'Rig A'},
          {'id': 'id-2', 'name': 'Rig B'},
        ],
      });
      expect(list.activeId, 'id-2');
      expect(list.profiles, hasLength(2));
      expect(list.active?.name, 'Rig B');
    });

    test('tolerates a null active_id and missing/garbled profiles', () {
      final list = ProfileList.fromJson({'active_id': null});
      expect(list.activeId, isNull);
      expect(list.profiles, isEmpty);
      expect(list.active, isNull);
    });
  });

  group('ProfileApi multi-profile CRUD', () {
    test('listProfiles GETs /api/v1/profiles and parses the list', () async {
      final a = _RecordingAdapter(body: {
        'active_id': 'id-1',
        'profiles': [
          {'id': 'id-1', 'name': 'Default'}
        ],
      });
      final list = await _api(a).listProfiles();
      expect(a.method, 'GET');
      expect(a.path, '/api/v1/profiles');
      expect(list.active?.name, 'Default');
    });

    test('renameProfile PUTs the new name to /api/v1/profiles/{id}', () async {
      final a = _RecordingAdapter();
      await _api(a).renameProfile('id-9', 'New Name');
      expect(a.method, 'PUT');
      expect(a.path, '/api/v1/profiles/id-9');
      expect((a.requestData as Map)['name'], 'New Name');
    });

    test('selectProfile POSTs to /api/v1/profiles/{id}/select', () async {
      final a = _RecordingAdapter();
      await _api(a).selectProfile('id-9');
      expect(a.method, 'POST');
      expect(a.path, '/api/v1/profiles/id-9/select');
    });

    test('deleteProfile DELETEs /api/v1/profiles/{id}', () async {
      final a = _RecordingAdapter();
      await _api(a).deleteProfile('id-9');
      expect(a.method, 'DELETE');
      expect(a.path, '/api/v1/profiles/id-9');
    });
  });
}
