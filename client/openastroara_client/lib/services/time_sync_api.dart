import 'package:dio/dio.dart';

import '../models/server.dart';

/// Client model for the §31.3 time-sync state — only the fields the on-connect
/// waterfall branches on. Defensive parse: a missing/odd field reads as
/// unsynced so the client pushes (a harmless extra sync, never a skipped one).
class TimeSyncState {
  final bool synced;
  final String source;
  final String trust;

  const TimeSyncState({
    required this.synced,
    this.source = 'none',
    this.trust = 'none',
  });

  factory TimeSyncState.fromJson(Map<String, dynamic> json) => TimeSyncState(
    synced: json['synced'] is bool ? json['synced'] as bool : false,
    source: json['source'] is String ? json['source'] as String : 'none',
    trust: json['trust'] is String ? json['trust'] as String : 'none',
  );
}

/// §31 time-sync operations the on-connect push depends on. An interface so
/// tests can supply a pure fake; [TimeSyncApi] is the Dio-backed implementation.
abstract interface class TimeSyncClient {
  /// `GET /api/v1/server/time-sync` — the daemon's sync state.
  Future<TimeSyncState> getState();

  /// `POST /api/v1/server/time-sync` — push this device's clock as a §31.2
  /// medium-trust `client` sync (modern devices on Wi-Fi/cellular are
  /// NTP-synced). No location: desktop WILMA has no geo fix; the mobile-GPS
  /// source lands with the §31 mobile slice.
  Future<void> pushClientTime(DateTime utcNow);

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
  void close() => _dio.close(force: true);
}
