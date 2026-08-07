import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/frame_viewer.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/library_api.dart';

class _RecordingAdapter implements HttpClientAdapter {
  final Future<ResponseBody> Function(RequestOptions options) handler;
  final List<RequestOptions> requests = [];

  _RecordingAdapter(this.handler);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    return handler(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(Object body, {int status = 200}) => ResponseBody.fromString(
  jsonEncode(body),
  status,
  headers: {
    Headers.contentTypeHeader: [Headers.jsonContentType],
  },
);

const _server = AraServer(hostname: 'host', port: 5555);

LibraryApi _api(_RecordingAdapter adapter) => LibraryApi(
  _server,
  dio: Dio(BaseOptions(baseUrl: _server.baseUrl))..httpClientAdapter = adapter,
);

Map<String, dynamic> _metadataJson() => {
  'frame': {
    'id': 'f1',
    'session_id': 's1',
    'target_name': 'M42',
    'frame_type': 'light',
    'exposure_seconds': 120,
    'captured_utc': '2026-08-01T01:00:00Z',
    'file_size_bytes': 42,
    'width': 2,
    'height': 2,
    'bit_depth': 16,
    'rating': 0,
    'tags': [],
  },
  'storage': {'state': 'complete', 'image_format': 'fits'},
  'source_exists': true,
  'analysis_state': 'ready',
  'preview_state': 'ready',
};

Map<String, dynamic> _accepted(String type) => {
  'operation_id': 'job-1',
  'operation_type': type,
  'accepted_utc': '2026-08-01T01:00:00Z',
  'idempotency_key': 'server-echo',
};

void main() {
  test('frameMetadata parses the durable metadata endpoint', () async {
    final adapter = _RecordingAdapter((_) async => _json(_metadataJson()));
    final api = _api(adapter);
    addTearDown(api.close);

    final metadata = await api.frameMetadata('f1');

    expect(adapter.requests.single.path, '/api/v1/frames/f1/metadata');
    expect(metadata.frame.targetName, 'M42');
    expect(metadata.storage?.state, 'complete');
    expect(metadata.sourceExists, isTrue);
  });

  test(
    'fetchPreview posts every option and parses applied response headers',
    () async {
      final adapter = _RecordingAdapter(
        (_) async => ResponseBody.fromBytes(
          [1, 2, 3],
          200,
          headers: {
            Headers.contentTypeHeader: ['image/jpeg'],
            'x-openastro-preview-width': ['1024'],
            'x-openastro-preview-height': ['768'],
            'x-openastro-preview-cache': ['hit'],
            'x-openastro-stretch': ['asinh'],
            'x-openastro-black-point': ['0.02'],
            'x-openastro-mid-point': ['0.4'],
            'x-openastro-white-point': ['0.98'],
            'x-openastro-debayer': ['super_pixel'],
            'x-openastro-channel': ['rgb'],
            'x-openastro-inverted': ['false'],
            'x-openastro-saturation': ['1.4'],
            'x-openastro-annotated': ['true'],
            'x-openastro-annotation-count': ['50'],
          },
        ),
      );
      final api = _api(adapter);
      addTearDown(api.close);
      const options = FramePreviewOptions(
        stretch: FrameStretch.asinh,
        annotateStars: true,
        saturation: 1.4,
      );

      final preview = await api.fetchPreview('f1', options);

      final request = adapter.requests.single;
      expect(request.path, '/api/v1/frames/f1/preview');
      expect(request.method, 'POST');
      expect(
        (request.data as Map<String, dynamic>)['stretch_palette'],
        'asinh',
      );
      expect((request.data as Map<String, dynamic>)['annotate_stars'], isTrue);
      expect(preview.bytes, [1, 2, 3]);
      expect(preview.applied.width, 1024);
      expect(preview.applied.algorithm, 'asinh');
      expect(preview.applied.annotationCount, 50);
    },
  );

  test(
    'mutation retries one transport loss with the same idempotency key',
    () async {
      var attempt = 0;
      final adapter = _RecordingAdapter((options) async {
        attempt++;
        if (attempt == 1) {
          throw DioException(
            requestOptions: options,
            type: DioExceptionType.connectionError,
            error: const SocketException('reset'),
          );
        }
        return _json(_accepted('frames.reanalyze'), status: 202);
      });
      final api = _api(adapter);
      addTearDown(api.close);

      final accepted = await api.reanalyze('f1');

      expect(accepted.operationId, 'job-1');
      expect(adapter.requests, hasLength(2));
      final firstKey = adapter.requests[0].headers['Idempotency-Key'];
      final secondKey = adapter.requests[1].headers['Idempotency-Key'];
      expect(firstKey, isNotEmpty);
      expect(secondKey, firstKey);
    },
  );

  test('response-bearing mutation failure is not retried', () async {
    final adapter = _RecordingAdapter(
      (_) async => _json({'title': 'conflict'}, status: 409),
    );
    final api = _api(adapter);
    addTearDown(api.close);

    await expectLater(
      api.rebuildPreview('f1', const FramePreviewOptions()),
      throwsA(isA<DioException>()),
    );
    expect(adapter.requests, hasLength(1));
  });

  test('concurrent mutations receive distinct idempotency keys', () async {
    final adapter = _RecordingAdapter(
      (_) async => _json(const <String, dynamic>{}, status: 202),
    );
    final api = _api(adapter);
    addTearDown(api.close);

    await Future.wait([
      api.bulkRate(['f1'], 4),
      api.bulkTag(['f1'], addTags: ['keeper']),
    ]);

    final keys = adapter.requests
        .map((request) => request.headers['Idempotency-Key'])
        .toSet();
    expect(keys, hasLength(2));
    expect(keys, isNot(contains(null)));
  });

  test('quarantine sends reversible state and reason', () async {
    final adapter = _RecordingAdapter(
      (_) async => _json(_accepted('frames.bulk-quarantine'), status: 202),
    );
    final api = _api(adapter);
    addTearDown(api.close);

    await api.bulkQuarantine(['f1'], quarantined: true, reason: 'cloud streak');

    final request = adapter.requests.single;
    expect(request.headers['Idempotency-Key'], isNotEmpty);
    expect(request.data, {
      'frame_ids': ['f1'],
      'quarantined': true,
      'reason': 'cloud streak',
    });
  });

  test('download streams original bytes and returns server filename', () async {
    final temp = await Directory.systemTemp.createTemp('ara-frame-download-');
    addTearDown(() => temp.delete(recursive: true));
    final path = '${temp.path}${Platform.pathSeparator}frame.fits';
    final adapter = _RecordingAdapter(
      (_) async => ResponseBody.fromBytes(
        [10, 20, 30, 40],
        200,
        headers: {
          Headers.contentTypeHeader: ['application/fits'],
          'content-disposition': ['attachment; filename="M42_L_120s.fits"'],
        },
      ),
    );
    final api = _api(adapter);
    addTearDown(api.close);

    final name = await api.downloadFrameTo('f1', path);

    expect(name, 'M42_L_120s.fits');
    expect(await File(path).readAsBytes(), [10, 20, 30, 40]);
  });

  test(
    'empty download is rejected and leaves no false-complete file',
    () async {
      final temp = await Directory.systemTemp.createTemp('ara-frame-empty-');
      addTearDown(() => temp.delete(recursive: true));
      final path = '${temp.path}${Platform.pathSeparator}empty.fits';
      final adapter = _RecordingAdapter(
        (_) async => ResponseBody.fromBytes(const [], 200),
      );
      final api = _api(adapter);
      addTearDown(api.close);

      await expectLater(
        api.downloadFrameTo('f1', path),
        throwsA(isA<FormatException>()),
      );
      expect(await File(path).exists(), isFalse);
      expect(await temp.list().toList(), isEmpty);
    },
  );

  test('download refuses to overwrite an existing destination', () async {
    final temp = await Directory.systemTemp.createTemp('ara-frame-existing-');
    addTearDown(() => temp.delete(recursive: true));
    final path = '${temp.path}${Platform.pathSeparator}existing.fits';
    await File(path).writeAsBytes([9, 8, 7]);
    final adapter = _RecordingAdapter(
      (_) async => ResponseBody.fromBytes([1, 2, 3], 200),
    );
    final api = _api(adapter);
    addTearDown(api.close);

    await expectLater(
      api.downloadFrameTo('f1', path),
      throwsA(isA<FileSystemException>()),
    );

    expect(await File(path).readAsBytes(), [9, 8, 7]);
    expect(adapter.requests, isEmpty);
  });

  test('job status and cancellation use the shared jobs surface', () async {
    final adapter = _RecordingAdapter((options) async {
      if (options.method == 'DELETE') return ResponseBody.fromString('', 204);
      return _json({
        'job_id': 'j1',
        'job_type': 'frames.reanalyze:f1',
        'state': 'running',
        'done': 0,
        'total': 1,
        'started_utc': '2026-08-01T01:00:00Z',
      });
    });
    final api = _api(adapter);
    addTearDown(api.close);

    final status = await api.jobStatus('j1');
    await api.cancelJob('j1');

    expect(status.state, 'running');
    expect(adapter.requests.map((request) => request.method), [
      'GET',
      'DELETE',
    ]);
    expect(
      adapter.requests.every((request) => request.path == '/api/v1/jobs/j1'),
      isTrue,
    );
  });
}
