import 'package:dio/dio.dart';

import '../models/server.dart';

/// A lat/lng/altitude triple on the §31.3 wire. `alt` is nullable — null
/// means the server doesn't know the altitude (an RMC-only GPS fix).
class TimeSyncLocation {
  final double lat;
  final double lng;
  final double? alt;

  const TimeSyncLocation({required this.lat, required this.lng, this.alt});

  static TimeSyncLocation? fromJson(dynamic json) {
    if (json is! Map<String, dynamic>) return null;
    final lat = json['lat'];
    final lng = json['lng'];
    final alt = json['alt'];
    if (lat is! num || lng is! num) return null;
    return TimeSyncLocation(
      lat: lat.toDouble(),
      lng: lng.toDouble(),
      alt: alt is num ? alt.toDouble() : null,
    );
  }
}

/// Client model for the §31.3 time-sync state. Defensive parse: a
/// missing/odd field reads as unsynced/unknown so the on-connect waterfall
/// pushes (a harmless extra sync, never a skipped one) and the status UI
/// renders placeholders instead of crashing.
class TimeSyncState {
  final bool synced;
  final String source;
  final String trust;
  final double systemTimeOffsetSeconds;
  final TimeSyncLocation? location;
  final bool internetAvailableOnPi;
  final bool internalGpsAvailable;
  final DateTime? syncedAtUtc;

  const TimeSyncState({
    required this.synced,
    this.source = 'none',
    this.trust = 'none',
    this.systemTimeOffsetSeconds = 0.0,
    this.location,
    this.internetAvailableOnPi = false,
    this.internalGpsAvailable = false,
    this.syncedAtUtc,
  });

  factory TimeSyncState.fromJson(Map<String, dynamic> json) => TimeSyncState(
    synced: json['synced'] is bool ? json['synced'] as bool : false,
    source: json['source'] is String ? json['source'] as String : 'none',
    trust: json['trust'] is String ? json['trust'] as String : 'none',
    systemTimeOffsetSeconds: json['system_time_offset_seconds'] is num
        ? (json['system_time_offset_seconds'] as num).toDouble()
        : 0.0,
    location: TimeSyncLocation.fromJson(json['location']),
    internetAvailableOnPi: json['internet_available_on_pi'] is bool
        ? json['internet_available_on_pi'] as bool
        : false,
    internalGpsAvailable: json['internal_gps_available'] is bool
        ? json['internal_gps_available'] as bool
        : false,
    syncedAtUtc: json['synced_at_utc'] is String
        ? DateTime.tryParse(json['synced_at_utc'] as String)
        : null,
  );
}

/// The §31.3 push result the manual-entry modal reports back to the user.
/// `clockSet` false means the daemon lacks CAP_SYS_TIME and is tracking the
/// offset honestly instead of correcting the system clock.
class TimeSyncPushResult {
  final bool locationUpdated;
  final bool clockSet;

  const TimeSyncPushResult({
    required this.locationUpdated,
    required this.clockSet,
  });

  factory TimeSyncPushResult.fromJson(Map<String, dynamic> json) =>
      TimeSyncPushResult(
        locationUpdated: json['location_updated'] is bool
            ? json['location_updated'] as bool
            : false,
        clockSet: json['clock_set'] is bool ? json['clock_set'] as bool : false,
      );
}

/// §31 time-sync operations. An interface so tests can supply a pure fake;
/// [TimeSyncApi] is the Dio-backed implementation.
abstract interface class TimeSyncClient {
  /// `GET /api/v1/server/time-sync` — the daemon's sync state.
  Future<TimeSyncState> getState();

  /// `POST /api/v1/server/time-sync` — push this device's clock as a §31.2
  /// medium-trust `client` sync (modern devices on Wi-Fi/cellular are
  /// NTP-synced). No location: desktop WILMA has no geo fix; the mobile-GPS
  /// source lands with the §31 mobile slice.
  Future<void> pushClientTime(DateTime utcNow);

  /// §31.1 step 5 — push a manually entered time (+ optional position) as a
  /// `manual` sync. The server clamps manual to low trust regardless of what
  /// is requested, so no trust field is sent. Location is all-or-nothing on
  /// lat/lng; altitude alone is never sent.
  Future<TimeSyncPushResult> pushManual({
    required DateTime timeUtc,
    double? lat,
    double? lng,
    double? alt,
  });

  void close();
}

/// Dio wrapper over `/api/v1/server/time-sync`.
class TimeSyncApi implements TimeSyncClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a stub adapter; production passes
  /// nothing and a server-bound Dio is built.
  TimeSyncApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              sendTimeout: const Duration(seconds: 5),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

  @override
  Future<TimeSyncState> getState() async {
    final res = await _dio.get<dynamic>('/api/v1/server/time-sync');
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'server/time-sync returned a non-object body (${data.runtimeType})',
      );
    }
    return TimeSyncState.fromJson(data);
  }

  @override
  Future<void> pushClientTime(DateTime utcNow) async {
    await _dio.post<dynamic>(
      '/api/v1/server/time-sync',
      data: <String, dynamic>{
        'source': 'client',
        'time_utc': utcNow.toUtc().toIso8601String(),
        'trust': 'medium',
      },
    );
  }

  @override
  Future<TimeSyncPushResult> pushManual({
    required DateTime timeUtc,
    double? lat,
    double? lng,
    double? alt,
  }) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/server/time-sync',
      data: <String, dynamic>{
        'source': 'manual',
        'time_utc': timeUtc.toUtc().toIso8601String(),
        if (lat != null && lng != null)
          'location': <String, dynamic>{'lat': lat, 'lng': lng, 'alt': ?alt},
      },
    );
    final data = res.data;
    return data is Map<String, dynamic>
        ? TimeSyncPushResult.fromJson(data)
        : const TimeSyncPushResult(locationUpdated: false, clockSet: false);
  }

  @override
  void close() => _dio.close(force: true);
}
