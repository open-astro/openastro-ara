import 'package:dio/dio.dart';

import '../models/server.dart';

/// One filter slot read off a connected wheel: its [name] and per-filter
/// autofocus [focusOffset] (steps).
class FilterWheelSlot {
  final String name;
  final int focusOffset;
  const FilterWheelSlot({required this.name, required this.focusOffset});
}

/// §37/§25.5.5 — the connected filter wheel's slots, read from
/// `GET /api/v1/equipment/filterwheel` for the wizard's "Refresh from connected
/// filter wheel" affordance, so the user doesn't hand-type the names + offsets
/// the daemon already reads off the device.
class FilterWheelSlots {
  final List<FilterWheelSlot> slots;
  const FilterWheelSlots(this.slots);

  /// Parse a `GET /api/v1/equipment/filterwheel` body, or null when no wheel is
  /// connected. A connected wheel with no slots yields an empty list (the caller
  /// shows "the wheel reported no slots") rather than null.
  static FilterWheelSlots? fromFilterWheelJson(Map<String, dynamic> json) {
    if (json['state'] != 'connected') return null;
    final raw = json['slots'];
    if (raw is! List) return null;
    final slots = raw
        .whereType<Map<String, dynamic>>()
        .map((s) => FilterWheelSlot(
              name: (s['name'] ?? '').toString(),
              focusOffset: s['focus_offset'] is num
                  ? (s['focus_offset'] as num).toInt()
                  : 0,
            ))
        .toList();
    return FilterWheelSlots(slots);
  }
}

class FilterWheelNamesApi {
  final Dio _dio;

  FilterWheelNamesApi(AraServer server)
      : _dio = Dio(BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 5),
        ));

  /// The connected wheel's slots, or null when none is connected. Throws
  /// `DioException` on transport failure.
  Future<FilterWheelSlots?> read() async {
    try {
      final res =
          await _dio.get<Map<String, dynamic>>('/api/v1/equipment/filterwheel');
      final data = res.data;
      return data is Map<String, dynamic>
          ? FilterWheelSlots.fromFilterWheelJson(data)
          : null;
    } finally {
      _dio.close();
    }
  }
}
