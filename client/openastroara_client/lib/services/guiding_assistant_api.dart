import 'package:dio/dio.dart';

import '../models/guiding_assistant.dart';
import '../models/server.dart';

/// #1364 item 4 — the Guiding Assistant's client surface. An interface so the
/// dialog can be tested with a fake.
abstract interface class GuidingAssistantClient {
  Future<GuidingAssistantStatus> getStatus();

  /// Start the measurement: `durationSec` null = the guider's default
  /// (two minutes); the status returned is the run's first. A slow guider
  /// can take longer than the other calls to answer (it checks the star and
  /// the calibration, then switches guide output off), so the client allows
  /// it [GuidingAssistantApi.startTimeout] rather than the usual 10 s.
  Future<GuidingAssistantStatus> start({
    double? durationSec,
    required bool measureBacklash,
  });
  Future<GuidingAssistantStatus> stop();

  /// Write the named recommendations; returns the names the guider applied.
  Future<List<String>> apply(List<String> which);
  void close();
}

/// Dio-backed client for `/api/v1/equipment/guider/live/assistant*`.
class GuidingAssistantApi implements GuidingAssistantClient {
  final Dio _dio;

  GuidingAssistantApi(AraServer server)
    : _dio = Dio(
        BaseOptions(
          baseUrl: server.baseUrl,
          connectTimeout: const Duration(seconds: 3),
          receiveTimeout: const Duration(seconds: 10),
        ),
      );

  static const _base = '/api/v1/equipment/guider/live/assistant';

  /// Review of #1387: a Start that passed the 10 s receive timeout showed an
  /// error while the run had in fact begun.
  static const startTimeout = Duration(seconds: 30);

  static GuidingAssistantStatus _parse(dynamic data) =>
      data is Map<String, dynamic>
      ? GuidingAssistantStatus.fromJson(data)
      : GuidingAssistantStatus.disconnected;

  @override
  Future<GuidingAssistantStatus> getStatus() async =>
      _parse((await _dio.get<dynamic>(_base)).data);

  @override
  Future<GuidingAssistantStatus> start({
    double? durationSec,
    required bool measureBacklash,
  }) async => _parse(
    (await _dio.post<dynamic>(
      '$_base/start',
      data: {'duration_sec': durationSec, 'measure_backlash': measureBacklash},
      options: Options(receiveTimeout: startTimeout),
    )).data,
  );

  @override
  Future<GuidingAssistantStatus> stop() async =>
      _parse((await _dio.post<dynamic>('$_base/stop')).data);

  @override
  Future<List<String>> apply(List<String> which) async {
    final res = await _dio.post<dynamic>(
      '$_base/apply',
      data: {'which': which},
    );
    final data = res.data;
    final applied = data is Map<String, dynamic> ? data['applied'] : null;
    return [
      if (applied is List)
        for (final a in applied)
          if (a is String) a,
    ];
  }

  @override
  void close() => _dio.close(force: true);
}
