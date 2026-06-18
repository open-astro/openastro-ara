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

    test('active is null when active_id matches no listed profile', () {
      // A stale/unknown active_id must not crash or mis-resolve — `active` does a
      // membership check, so it returns null rather than a wrong profile.
      final list = ProfileList.fromJson({
        'active_id': 'ghost',
        'profiles': [
          {'id': 'id-1', 'name': 'Rig A'},
        ],
      });
      expect(list.activeId, 'ghost');
      expect(list.active, isNull);
    });

    test('coerces a non-String-keyed profile map (the fromJson Map branch)', () {
      // A JSON decoder can hand back Map<dynamic, dynamic> for nested objects;
      // fromJson copies those into Map<String, dynamic> rather than dropping them.
      final list = ProfileList.fromJson({
        'active_id': 'id-1',
        'profiles': <dynamic>[
          <dynamic, dynamic>{'id': 'id-1', 'name': 'Coerced'},
        ],
      });
      expect(list.profiles, hasLength(1));
      expect(list.active?.name, 'Coerced');
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

  group('ProfileApi §70 share import', () {
    test('importPreview POSTs the manifest and parses the preview', () async {
      final a = _RecordingAdapter(body: {
        'import_token': 'tok-1',
        'profile_name': 'Imported profile',
        'warnings': ['This is a template, not a complete profile'],
        'dropped_fields': ['Site location', 'PHD2 host / port / profile'],
        'expires_utc': '2026-06-18T04:00:00Z',
      });
      final manifest = {'schema_version': 'profile-share-v1', 'settings': {}};
      final preview = await _api(a).importPreview(manifest);
      expect(a.method, 'POST');
      expect(a.path, '/api/v1/profiles/share-import');
      // The raw manifest is the request body.
      expect((a.requestData as Map)['schema_version'], 'profile-share-v1');
      expect(preview.importToken, 'tok-1');
      expect(preview.profileName, 'Imported profile');
      expect(preview.droppedFields, hasLength(2));
      expect(preview.expiresUtc, isNotNull);
    });

    test('importCommit POSTs the token in the body and returns the new id',
        () async {
      // The daemon returns the new Guid as a bare JSON string (201 Created).
      final a = _RecordingAdapter(body: 'new-profile-id');
      final id = await _api(a).importCommit('tok-1');
      expect(a.method, 'POST');
      expect(a.path, '/api/v1/profiles/share-import/commit');
      expect((a.requestData as Map)['import_token'], 'tok-1');
      expect(id, 'new-profile-id');
    });
  });
}
