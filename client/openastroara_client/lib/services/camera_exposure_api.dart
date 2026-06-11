import 'package:dio/dio.dart';

import '../models/server.dart';
import '../state/imaging/exposure_state.dart';

/// Client-side wrapper around §14e `POST /api/v1/equipment/camera/exposure`.
/// Drives a single exposure on the connected Alpaca camera; the daemon
/// exposes, downloads the frame, writes the FITS and registers it in the
/// catalog. Returns the new frame id (202-Accepted body).
class CameraExposureApi {
  final Dio _dio;

  CameraExposureApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          // The POST returns 202 immediately; capture runs in the background.
          receiveTimeout: const Duration(seconds: 10),
        ));

  /// Fire one exposure with the current Imaging-tab params. Returns the
  /// `frame_id` the daemon assigned. Throws `DioException` on HTTP failure —
  /// the caller surfaces the error UI.
  Future<String> takeOne(ExposureParams p) async {
    final res = await _dio.post<Map<String, dynamic>>(
      '/api/v1/equipment/camera/exposure',
      data: <String, dynamic>{
        'exposure_sec': p.exposure.inMilliseconds / 1000.0,
        'gain': p.gain,
        'bin_x': p.bin,
        'bin_y': p.bin,
        // ExposureParams.offset is the electronic pedestal offset, which the
        // daemon takes as camera_offset (distinct from the subframe origin).
        'camera_offset': p.offset,
        'filter_name': p.filterSlot,
      },
    );
    final frameId = res.data?['frame_id'] as String?;
    if (frameId == null || frameId.isEmpty) {
      throw StateError('Exposure response did not include a frame_id.');
    }
    return frameId;
  }
}
