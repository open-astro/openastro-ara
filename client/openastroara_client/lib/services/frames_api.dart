import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/server.dart';

/// Client-side wrapper around the §65 frame endpoints. The preview is a POST
/// (it carries an optional stretch-options body), so `Image.network` (GET-only)
/// can't be used directly — we fetch the JPEG bytes here and render them with
/// `Image.memory`.
class FramesApi {
  final Dio _dio;

  FramesApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 15),
        ));

  /// True once the frame is registered in the catalog — i.e. the background
  /// capture finished writing the FITS. Until then GET returns 404.
  Future<bool> isRegistered(String id) async {
    try {
      await _dio.get<Map<String, dynamic>>('/api/v1/frames/$id');
      return true;
    } on DioException catch (e) {
      // 404 = not registered yet (still capturing) — the only "keep polling"
      // case. Everything else (5xx, connection drop, timeout) is a real failure
      // and must propagate, so the caller surfaces it instead of spinning the
      // poll loop to a bogus timeout.
      if (e.response?.statusCode == 404) return false;
      rethrow;
    }
  }

  /// Fetch the stretched preview JPEG bytes for a frame.
  Future<Uint8List> preview(String id) async {
    final res = await _dio.post<List<int>>(
      '/api/v1/frames/$id/preview',
      data: const <String, dynamic>{},
      options: Options(responseType: ResponseType.bytes),
    );
    final bytes = Uint8List.fromList(res.data ?? const <int>[]);
    if (bytes.isEmpty) {
      // An empty body would make Image.memory throw an opaque "Invalid image
      // data" codec error downstream — surface a clear message instead.
      throw StateError('Server returned an empty preview for frame $id.');
    }
    return bytes;
  }
}
