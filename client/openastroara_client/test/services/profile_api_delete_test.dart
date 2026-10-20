import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_twin.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody, {this.status = 200});
  final Object? jsonBody;
  final int status;
  RequestOptions? lastRequest;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options,
      Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    lastRequest = options;
    return ResponseBody.fromString(
      jsonBody == null ? '' : jsonEncode(jsonBody),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

ProfileApi _api(_StubAdapter adapter) => ProfileApi(
      const AraServer(hostname: 'rig', port: 5555),
      dio: Dio(BaseOptions(
        baseUrl: 'http://rig:5555',
        receiveTimeout: const Duration(seconds: 5),
      ))
        ..httpClientAdapter = adapter,
    );

void main() {
  test('deleteProfile with the guider waits longer than the daemon\'s 15 s bound',
      () async {
    // §63.4: the daemon releases and reconnects the guider's equipment inside
    // the DELETE and allows itself 15 s; a 5 s receive timeout reports
    // "Couldn't delete profile" for a profile that WAS deleted.
    final adapter = _StubAdapter(const {'guider_profile': 'deleted'});
    final result =
        await _api(adapter).deleteProfile('id-1', deleteGuiderProfile: true);
    expect(result.guiderProfile, GuiderTwinOutcome.deleted);
    expect(adapter.lastRequest!.queryParameters['guider_profile'], 'delete');
    expect(adapter.lastRequest!.receiveTimeout,
        greaterThanOrEqualTo(const Duration(seconds: 45)));
  });

  test('deleteProfile defaults to keep and reads an empty 204 as kept', () async {
    final adapter = _StubAdapter(null, status: 204);
    final result = await _api(adapter).deleteProfile('id-1');
    expect(result.guiderProfile, GuiderTwinOutcome.kept);
    expect(adapter.lastRequest!.queryParameters['guider_profile'], 'keep');
  });
}
