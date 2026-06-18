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
  /// First page of the sequence list (newest-first per the daemon). Returns the
  /// full [SequencePage] (items + hasMore + nextCursor) so a long list isn't
  /// silently truncated; [limit] caps the page size. Paging further with
  /// nextCursor is a later slice.
  Future<SequencePage> list({int limit});

  /// Live run state of a sequence, or null when there's no active run (the
  /// daemon answers 404). Polled by the run controls / status line.
  Future<SequenceRunStateInfo?> getRunState(String id);

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

  /// [receiveTimeout] is widenable by callers without forking the class — the
  /// list endpoint can be slow on a cold daemon / NFS-backed storage. It applies
  /// only to the internally-constructed Dio; when a [dio] is injected (tests),
  /// its own options govern and [receiveTimeout] is ignored.
  SequenceApi(AraServer server,
      {Dio? dio, Duration receiveTimeout = const Duration(seconds: 12)})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: receiveTimeout,
            ));

  @override
  Future<SequencePage> list({int limit = 50}) async {
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
    final items = (data['items'] as List)
        .whereType<Map<String, dynamic>>()
        .map(SequenceListItem.fromJson)
        // Drop an id-less (malformed) row — id is the path segment every
        // lifecycle call needs, so an empty one is unusable.
        .where((s) => s.id.isNotEmpty)
        .toList(growable: false);
    final nextCursor = data['next_cursor'];
    return SequencePage(
      items: items,
      hasMore: data['has_more'] == true,
      nextCursor: nextCursor is String ? nextCursor : null,
    );
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    try {
      final res = await _dio.get<dynamic>(
          '/api/v1/sequences/${Uri.encodeComponent(id)}/state');
      final data = res.data;
      if (data is! Map<String, dynamic>) {
        throw FormatException(
            'sequence state returned an unexpected body (${data.runtimeType})');
      }
      return SequenceRunStateInfo.fromJson(data);
    } on DioException catch (e) {
      // 404 = no active run for this sequence (idle); not an error — return null.
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  Future<String> start(String id) => _lifecycle(id, 'start', body: const {
        // TODO(sequencer-ui-slice): promote dry_run / continue_on_recoverable_errors
        // (and start_from_instruction_index) to start() params on SequenceClient
        // when the toolbar needs a dry-run / continue-on-error / resume-from mode —
        // extend the interface rather than having callers build the request body.
        //
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

  /// force: true cancels in-flight requests immediately — intentional on a
  /// server change so a stale response can't land against the switched-away
  /// server. The trade-off: a lifecycle call (start/pause/abort) in flight when
  /// the provider disposes throws a DioException with an ambiguous outcome (the
  /// daemon may or may not have received it). The UI-wiring slice handles that
  /// DioException on lifecycle calls; the operation's true state is the WS
  /// `sequence.*` stream, not this Future.
  @override
  void close() => _dio.close(force: true);
}
