import 'package:dio/dio.dart';

import '../models/sequence/nina_sequence_parser.dart';
import '../models/sequence/sequence_node.dart';
import '../models/sequence/sequence_share_export.dart';
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

  /// Import a NINA sequence file (§38.4). [ninaFile] is the parsed NINA JSON
  /// object; the daemon translates it, stores it as a new sequence, and returns
  /// what (if anything) the translation dropped. With [treatWarningsAsErrors] the
  /// daemon answers 422 (a DioException here) if the import would be lossy.
  Future<SequenceImportResult> importNina(
    String newName,
    Map<String, dynamic> ninaFile, {
    bool treatWarningsAsErrors = false,
  });

  /// List the daemon's starting-point sequence templates (§38.6/§38.7). Throws
  /// on transport failure / unexpected body.
  Future<List<SequenceTemplate>> listTemplates();

  /// Create a new sequence from the template [templateName] (§38.7), named
  /// [newName]. Returns the created sequence's id. Throws on transport failure
  /// or an unknown template (the daemon answers 404 → DioException here).
  Future<String> instantiateTemplate(String templateName, String newName);

  /// Create a new sequence directly from a raw [body] (§38.5, `POST ""`), named
  /// [name]. Used by surfaces that build a body locally (e.g. the Planning tab's
  /// "Add to sequence" → a one-slew sequence) rather than starting from a server
  /// template. The daemon schema-validates [body] and answers 422 (a
  /// DioException here) when invalid. Returns the created sequence's id.
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
  });

  /// Fetch a sequence's full detail and parse its body into the editor tree.
  /// Returns the root [SequenceNode] (an empty named root if the body has no
  /// recognizable tree). Throws on transport failure / unknown id (404).
  Future<SequenceNode> getSequence(String id);

  /// Fetch a sequence's full detail INCLUDING the raw body JSON — the source of
  /// truth kept for Save/Export (so they round-trip faithfully). Throws on
  /// transport failure / unknown id (404).
  Future<SequenceDetail> getSequenceDetail(String id);

  /// Update a saved sequence (`PATCH /{id}`, §38.5) — only the supplied fields
  /// change. Pass [body] (the raw NINA/§38.1 DOM) to persist edits; the daemon
  /// schema-validates it and answers 422 (a DioException here) when invalid.
  /// Returns the updated detail. Throws on unknown id (404) or no fields given.
  Future<SequenceDetail> updateSequence(
    String id, {
    String? name,
    String? description,
    Map<String, dynamic>? body,
  });

  /// Delete a saved sequence (`DELETE /{id}`, §38.5). Returns true on success,
  /// false when the daemon doesn't know the id (404 — already gone, which for
  /// a delete is the same outcome the user wanted). Throws on other transport
  /// failures.
  Future<bool> deleteSequence(String id);

  /// Dry-run the raw [body] through the daemon's schema validator (§38.5)
  /// without persisting — `POST /validate`. Returns whether it's valid and the
  /// first problem reason when not. Throws on transport failure / unexpected
  /// body.
  Future<SequenceValidationResult> validate(Map<String, dynamic> body);

  /// Live run state of a sequence, or null when there's no active run (the
  /// daemon answers 404). Polled by the run controls / status line.
  Future<SequenceRunStateInfo?> getRunState(String id);

  Future<String> start(String id);
  Future<String> pause(String id);
  Future<String> resume(String id);

  /// §38 skip-current: cancel whatever the run is executing right now (e.g. a
  /// target that's no longer well-positioned) so the sequence advances to the
  /// next item. A no-op the daemon still 202-accepts when nothing is running.
  Future<String> skipCurrent(String id);
  Future<String> abort(String id);
  Future<String> stop(String id);

  /// §70.5 share-export: fetch the sequence's shareable manifest
  /// (`POST /{id}/share-export`) so the client can write it to a `.araseq.json`
  /// file. Throws on transport failure / unknown id (the daemon answers 404 → a
  /// DioException here) or a manifest-less body ([FormatException]).
  Future<SequenceShareExport> exportShare(String id);

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
  Future<SequenceImportResult> importNina(
    String newName,
    Map<String, dynamic> ninaFile, {
    bool treatWarningsAsErrors = false,
  }) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences/import',
      data: <String, dynamic>{
        'new_name': newName,
        'nina_sequence_file': ninaFile,
        'treat_warnings_as_errors': treatWarningsAsErrors,
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'sequence import returned an unexpected body (${data.runtimeType})');
    }
    return SequenceImportResult.fromJson(data);
  }

  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences/validate',
      data: <String, dynamic>{'body': body},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'sequence validate returned an unexpected body (${data.runtimeType})');
    }
    return SequenceValidationResult.fromJson(data);
  }

  @override
  Future<List<SequenceTemplate>> listTemplates() async {
    final res = await _dio.get<dynamic>('/api/v1/sequences/templates');
    final data = res.data;
    // The endpoint returns a JSON array of templates; a 2xx with a different
    // shape means the wire contract changed — throw rather than silently empty.
    if (data is! List) {
      throw FormatException(
          'sequence templates returned an unexpected body (${data.runtimeType})');
    }
    return data
        .whereType<Map<String, dynamic>>()
        .map(SequenceTemplate.fromJson)
        // Drop a name-less template — the name is the instantiate path segment.
        .where((t) => t.name.isNotEmpty)
        .toList(growable: false);
  }

  @override
  Future<String> instantiateTemplate(String templateName, String newName) async {
    if (templateName.trim().isEmpty) {
      throw ArgumentError.value(
          templateName, 'templateName', 'template name must not be empty');
    }
    if (newName.trim().isEmpty) {
      throw ArgumentError.value(
          newName, 'newName', 'new sequence name must not be empty');
    }
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences/templates/${Uri.encodeComponent(templateName.trim())}/instantiate',
      data: <String, dynamic>{'new_sequence_name': newName.trim()},
    );
    final data = res.data;
    final id = data is Map<String, dynamic> ? data['id'] : null;
    if (id is! String || id.isEmpty) {
      throw FormatException(
          'template instantiate returned no sequence id (${data.runtimeType})');
    }
    return id;
  }

  @override
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
  }) async {
    if (name.trim().isEmpty) {
      throw ArgumentError.value(name, 'name', 'sequence name must not be empty');
    }
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences',
      data: <String, dynamic>{
        'name': name.trim(),
        'description': ?description,
        'body': body,
      },
    );
    final data = res.data;
    final id = data is Map<String, dynamic> ? data['id'] : null;
    if (id is! String || id.isEmpty) {
      throw FormatException(
          'sequence create returned no sequence id (${data.runtimeType})');
    }
    return id;
  }

  @override
  Future<SequenceNode> getSequence(String id) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    final res =
        await _dio.get<dynamic>('/api/v1/sequences/${Uri.encodeComponent(id)}');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'sequence detail returned an unexpected body (${data.runtimeType})');
    }
    final body = data['body'];
    if (body is Map<String, dynamic>) return parseNinaSequenceBody(body);
    // A detail with no usable body object → an empty root named from the DTO.
    final name = data['name'];
    return SequenceNode(
      id: 'root',
      kind: SequenceNodeKind.root,
      displayName: name is String && name.trim().isNotEmpty ? name : 'Sequence',
    );
  }

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    final res =
        await _dio.get<dynamic>('/api/v1/sequences/${Uri.encodeComponent(id)}');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'sequence detail returned an unexpected body (${data.runtimeType})');
    }
    return SequenceDetail.fromJson(data);
  }

  @override
  Future<SequenceDetail> updateSequence(
    String id, {
    String? name,
    String? description,
    Map<String, dynamic>? body,
  }) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    // Null-aware entries: a field omitted (null) isn't sent, so PATCH leaves it
    // unchanged — distinct from sending an explicit null.
    final payload = <String, dynamic>{
      'name': ?name,
      'description': ?description,
      'body': ?body,
    };
    if (payload.isEmpty) {
      throw ArgumentError('updateSequence needs at least one field to change');
    }
    final res = await _dio.patch<dynamic>(
      '/api/v1/sequences/${Uri.encodeComponent(id)}',
      data: payload,
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'sequence update returned an unexpected body (${data.runtimeType})');
    }
    return SequenceDetail.fromJson(data);
  }

  @override
  Future<bool> deleteSequence(String id) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    try {
      await _dio.delete<void>('/api/v1/sequences/${Uri.encodeComponent(id)}');
      return true;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return false; // already gone
      rethrow;
    }
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
  Future<String> skipCurrent(String id) => _lifecycle(id, 'skip-current');

  @override
  Future<String> abort(String id) => _lifecycle(id, 'abort');

  @override
  Future<String> stop(String id) => _lifecycle(id, 'stop');

  @override
  Future<SequenceShareExport> exportShare(String id) async {
    if (id.isEmpty) {
      throw ArgumentError.value(id, 'id', 'sequence id must not be empty');
    }
    final res = await _dio.post<dynamic>(
      '/api/v1/sequences/${Uri.encodeComponent(id)}/share-export',
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
          'share-export returned an unexpected body (${data.runtimeType})');
    }
    return SequenceShareExport.fromJson(data);
  }

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
