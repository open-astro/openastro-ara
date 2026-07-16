import 'package:dio/dio.dart';

import '../models/server.dart';

/// §37 — the connected focuser's wizard-relevant properties, read from
/// `GET /api/v1/equipment/focuser` for the Focuser step's "Refresh from
/// connected focuser" affordance. `stepSizeUm` is null when the driver
/// doesn't report it (most don't — e.g. the ZWO EAF reports 0).
class FocuserProps {
  final double? stepSizeUm;
  final bool canTempComp;

  const FocuserProps({this.stepSizeUm, required this.canTempComp});

  /// Parse a `GET /api/v1/equipment/focuser` body, or null when no focuser is
  /// connected. A connected focuser that reports no step size yields
  /// `stepSizeUm == null` so the caller can explain instead of writing a 0.
  static FocuserProps? fromFocuserJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final caps = json['capabilities'];
    if (caps is! Map<String, dynamic>) return null;
    final raw = caps['step_size_um'];
    final um = raw is num ? raw.toDouble() : null;
    return FocuserProps(
      stepSizeUm: (um != null && um > 0) ? um : null,
      canTempComp: caps['can_temp_comp'] == true,
    );
  }
}

class FocuserPropsApi {
  final Dio _dio;

  FocuserPropsApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The connected focuser's props, or null when none is connected. Throws
  /// `DioException` on transport failure.
  Future<FocuserProps?> read() async {
    try {
      final res =
          await _dio.get<Map<String, dynamic>>('/api/v1/equipment/focuser');
      final data = res.data;
      return data is Map<String, dynamic>
          ? FocuserProps.fromFocuserJson(data)
          : null;
    } finally {
      // One-shot client: close the Dio so its pool isn't leaked per press.
      _dio.close();
    }
  }
}
