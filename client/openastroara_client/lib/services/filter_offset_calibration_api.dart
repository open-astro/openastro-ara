import 'package:dio/dio.dart';

import '../models/filter_offset_calibration.dart';
import '../models/server.dart';

/// #1675 — the filter offset calibration routes under
/// `/api/v1/equipment/filterwheel/offsets`: start one (202 + its batch job; a
/// 409 says why it can't start), read it (404 when there is none), and
/// cancel, save or discard it (204; a 409 when there is nothing to act on).
///
/// An interface so tests can supply a pure fake; [DioFilterOffsetCalibrationApi]
/// is the Dio-backed production implementation.
abstract interface class FilterOffsetCalibrationApi {
  /// Start a calibration over [positions] (the reference is always measured).
  Future<void> start(List<int> positions);

  /// The running calibration or its waiting results; null when there is none.
  Future<FilterOffsetCalibration?> calibration();

  Future<void> cancel();

  /// Write the measured offsets into the profile; clears the results.
  Future<void> save();

  /// Drop the results; the saved offsets stay as they were.
  Future<void> discard();

  void close();
}

class DioFilterOffsetCalibrationApi implements FilterOffsetCalibrationApi {
  final Dio _dio;
  static const _base = '/api/v1/equipment/filterwheel/offsets';

  DioFilterOffsetCalibrationApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
          sendTimeout: const Duration(seconds: 5),
        ));

  @override
  Future<void> start(List<int> positions) async {
    await _dio.post<dynamic>('$_base/calibrate', data: {'positions': positions});
  }

  @override
  Future<FilterOffsetCalibration?> calibration() async {
    final res = await _dio.get<dynamic>(
      '$_base/calibration',
      options: Options(validateStatus: (s) => s != null && (s < 400 || s == 404)),
    );
    final data = res.data;
    if (res.statusCode == 404) return null;
    if (data is! Map<String, dynamic>) {
      throw Exception('unexpected offset calibration response — not a JSON object');
    }
    return FilterOffsetCalibration.fromJson(data);
  }

  // A 409 (nothing to act on) is swallowed: the next read says where it stands.
  static final Options _nothingIsFine =
      Options(validateStatus: (s) => s != null && (s < 400 || s == 409));

  @override
  Future<void> cancel() => _dio.post<void>('$_base/calibration/cancel', options: _nothingIsFine);

  @override
  Future<void> save() => _dio.post<void>('$_base/calibration/save');

  @override
  Future<void> discard() => _dio.post<void>('$_base/calibration/discard', options: _nothingIsFine);

  @override
  void close() => _dio.close(force: true);
}
