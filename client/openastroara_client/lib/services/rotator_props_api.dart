import 'package:dio/dio.dart';

import '../models/server.dart';

/// §37 — the connected rotator's wizard-relevant properties, read from
/// `GET /api/v1/equipment/rotator` for the Rotator step's "Refresh from
/// connected rotator" affordance. `stepDeg` is null when the driver doesn't
/// report a usable step size (e.g. the ZWO CAA reports 0). Min/max travel
/// angles are NOT an Alpaca concept — those are the user's mechanical limits.
class RotatorProps {
  final double? stepDeg;
  final bool canReverse;
  final bool reverse;

  const RotatorProps(
      {this.stepDeg, required this.canReverse, required this.reverse});

  /// Parse a `GET /api/v1/equipment/rotator` body, or null when no rotator is
  /// connected.
  static RotatorProps? fromRotatorJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final caps = json['capabilities'];
    if (caps is! Map<String, dynamic>) return null;
    final raw = caps['step_size'];
    final deg = raw is num ? raw.toDouble() : null;
    final runtime = json['runtime'];
    return RotatorProps(
      stepDeg: (deg != null && deg > 0) ? deg : null,
      canReverse: caps['can_reverse'] == true,
      reverse: runtime is Map<String, dynamic> && runtime['reverse'] == true,
    );
  }
}

class RotatorPropsApi {
  final Dio _dio;

  RotatorPropsApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The connected rotator's props, or null when none is connected. Throws
  /// `DioException` on transport failure.
  Future<RotatorProps?> read() async {
    try {
      final res =
          await _dio.get<Map<String, dynamic>>('/api/v1/equipment/rotator');
      final data = res.data;
      return data is Map<String, dynamic>
          ? RotatorProps.fromRotatorJson(data)
          : null;
    } finally {
      // One-shot client: close the Dio so its pool isn't leaked per press.
      _dio.close();
    }
  }
}
