import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';

/// One write that reached the rig's store.
typedef LandedPut = ({String section, Object? body, String? ifMatch});

/// A rig at the HTTP level for any profile section (ADR 0015): it serves each
/// section under `/api/v1/profile/<section>` with an `ETag`, refuses a PUT
/// whose `If-Match` names another copy with a 412 (nothing written), and still
/// takes a PUT with no `If-Match`, as the daemon does until a version is
/// required. The tests assert on [puts] only, so they hold whatever shape the
/// client's section notifiers take.
class FakeSectionRig implements HttpClientAdapter {
  FakeSectionRig(Map<String, Object?> sections) : _sections = {...sections};

  static const _prefix = '/api/v1/profile/';

  final Map<String, Object?> _sections;
  final Map<String, int> _versions = {};

  /// Sections whose GET fails as an unreachable rig does.
  final Set<String> failGet = {};

  /// Every write that landed, in order.
  final List<LandedPut> puts = [];

  /// Every `If-Match` sent with a PUT (null when none), landed or refused.
  final List<String?> ifMatches = [];

  String etagOf(String section) => '"$section-v${_versions[section] ?? 1}"';

  /// Another device saved [section]: the rig's copy, and so its tag, moved on.
  void savedElsewhere(String section) =>
      _versions[section] = (_versions[section] ?? 1) + 1;

  /// A [ProfileApi] whose requests reach this rig and nothing else.
  ProfileApi api({String hostname = 'rig.test'}) => ProfileApi(
    AraServer(hostname: hostname, port: 1),
    dio: Dio(BaseOptions(baseUrl: 'http://$hostname:1'))
      ..httpClientAdapter = this,
  );

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final section = options.path.startsWith(_prefix)
        ? options.path.substring(_prefix.length)
        : options.path;
    if (!_sections.containsKey(section)) {
      return _json({'title': 'no such section'}, 404);
    }
    switch (options.method.toUpperCase()) {
      case 'GET':
        if (failGet.contains(section)) {
          throw DioException(
            requestOptions: options,
            type: DioExceptionType.connectionError,
            error: 'rig unreachable',
          );
        }
        return _json(_sections[section], 200, etag: etagOf(section));
      case 'PUT':
        final ifMatch = _header(options, 'if-match');
        ifMatches.add(ifMatch);
        if (ifMatch != null && ifMatch != '*' && ifMatch != etagOf(section)) {
          return _json({'title': 'Precondition Failed'}, 412);
        }
        puts.add((section: section, body: options.data, ifMatch: ifMatch));
        _sections[section] = options.data;
        savedElsewhere(section);
        return _json(options.data, 200, etag: etagOf(section));
      default:
        return _json({'title': 'method not allowed'}, 405);
    }
  }

  static String? _header(RequestOptions o, String name) {
    for (final e in o.headers.entries) {
      if (e.key.toLowerCase() == name && e.value != null) return '${e.value}';
    }
    return null;
  }

  static ResponseBody _json(Object? body, int status, {String? etag}) =>
      ResponseBody.fromString(
        jsonEncode(body),
        status,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
          if (etag != null) 'etag': [etag],
        },
      );
}
