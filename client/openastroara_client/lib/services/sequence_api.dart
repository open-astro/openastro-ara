import 'package:dio/dio.dart';

import '../models/sequence/sequence_summary.dart';
import '../models/server.dart';

/// The §38 sequencer operations the state layer depends on. An interface so
/// tests can supply a pure fake (no Dio); [SequenceApi] is the Dio-backed
/// production implementation.
///
/// Lifecycle ops (start/pause/resume/abort/stop) are 202-Accepted — the daemon
/// runs the sequence in the background and reports progress over the
/// `sequence.*` WS stream — so they return the accepted operation id, not a
/// terminal result.
abstract interface class SequenceClient {
  /// First page of the sequence list (newest-first per the daemon). Pagination
  /// beyond the first page is a later slice; [limit] caps the page size.
  Future<List<SequenceListItem>> list({int limit});

  Future<String> start(String id);
  Future<String> pause(String id);
  Future<String> resume(String id);
  Future<String> abort(String id);
  Future<String> stop(String id);

  void close();
}

/// Dio wrapper over `/api/v1/sequences/*`.
class SequenceApi implements SequenceClient {
  final Dio _dio;

  SequenceApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 12),
            ));

  @override
  Future<List<SequenceListItem>> list({int limit = 50}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/sequences',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    final data = res.data;
    // The endpoint returns a CursorPage envelope: { items: [...], next_cursor,
    // has_more }. A 2xx with a different shape means the wire contract changed —
    // throw so the AsyncNotifier surfaces an error rather than a silently-empty
    // list. (Dio already throws on 4xx/5xx.)
    if (data is! Map<String, dynamic> || data['items'] is! List) {
      throw FormatException(
          'sequences list returned an unexpected body (${data.runtimeType})');
    }
    return (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(SequenceListItem.fromJson)
        // Drop an id-less (malformed) row — id is the path segment every
        // lifecycle call needs, so an empty one is unusable.
        .where((s) => s.id.isNotEmpty)
        .toList(growable: false);
  }

  @override
  Future<String> start(String id) => _lifecycle(id, 'start', body: const {
        // start_from_instruction_index is omitted, not sent as null: the daemon's
        // SequenceStartRequestDto field is `int?`, so a missing key deserializes
        // to null ("start from the beginning") — the intent here — without
        // relying on the server accepting an explicit JSON null for an int field.
        'dry_run': false,
        'continue_on_recoverable_errors': false,
      });

  @override
  Future<String> pause(String id) => _lifecycle(id, 'pause');

  @override
  Future<String> resume(String id) => _lifecycle(id, 'resume');

  @override
  Future<String> abort(String id) => _lifecycle(id, 'abort');

  @override
  Future<String> stop(String id) => _lifecycle(id, 'stop');

  /// POST a lifecycle transition and return the accepted operation id. The
  /// daemon answers 202 with an OperationAccepted envelope ({ operation_id, … });
  /// a 409 (illegal transition for the current state) propagates as a DioException
  /// the caller surfaces.
  Future<String> _lifecycle(String id, String action,
      {Map<String, dynamic>? body}) async {
    // Hard guard (not assert): an empty id would POST to `/api/v1/sequences//$action`
    // — a malformed path that 404s or hits the wrong route. asserts are stripped
    // in release, so a direct caller with an empty id must fail loudly here.
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences/${Uri.encodeComponent(id)}/$action',
      data: body,
    );
    final data = res.data;
    final opId = data is Map<String, dynamic> ? data['operation_id'] : null;
    if (opId is! String) {
      throw FormatException('$action accepted but no operation_id was returned');
    }
    return opId;
  }

  @override
  void close() => _dio.close(force: true);
}
