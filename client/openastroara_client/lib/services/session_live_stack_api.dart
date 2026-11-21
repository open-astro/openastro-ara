import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/server.dart';

/// `GET /equipment/camera/live-stack`: the Session Live Stack's status. Every
/// key is optional on the wire (tolerant parse, an absent key reads as its
/// default), so an older or newer daemon never breaks the client.
class SessionLiveStackStatus {
  const SessionLiveStackStatus({
    this.enabled = false,
    this.unsupported,
    this.stackSeq = 0,
    this.lightsStacked = 0,
    this.lightsUnregistered = 0,
    this.lightsSkippedBusy = 0,
    this.lastOutcome,
    this.targetName,
    this.filterName,
  });

  factory SessionLiveStackStatus.fromJson(Map<String, dynamic> j) {
    int n(String k) => (j[k] as num?)?.toInt() ?? 0;
    return SessionLiveStackStatus(
      enabled: j['enabled'] == true,
      unsupported: j['unsupported'] as String?,
      stackSeq: n('stack_seq'),
      lightsStacked: n('lights_stacked'),
      lightsUnregistered: n('lights_unregistered'),
      lightsSkippedBusy: n('lights_skipped_busy'),
      lastOutcome: j['last_outcome'] as String?,
      targetName: j['target_name'] as String?,
      filterName: j['filter_name'] as String?,
    );
  }

  final bool enabled;

  /// Why the daemon cannot stack with this camera: `no_camera`, `not_colour`,
  /// `too_large`, or `not_available` (a 501); null when it can.
  final String? unsupported;
  final int stackSeq;
  final int lightsStacked;
  final int lightsUnregistered;
  final int lightsSkippedBusy;
  final String? lastOutcome;
  final String? targetName;
  final String? filterName;

  bool get supported => unsupported == null;
}

/// The stacked image and the `X-Stack-Seq` / `X-Stack-Lights` it came with.
class StackFrame {
  const StackFrame(this.bytes, this.seq, this.lights);
  final List<int> bytes;
  final int seq;
  final int lights;

  /// [bytes] as the image decoder takes them.
  Uint8List get image =>
      bytes is Uint8List ? bytes as Uint8List : Uint8List.fromList(bytes);
}

/// The Session Live Stack routes. An interface so the state can be tested
/// with a fake instead of real HTTP.
abstract interface class SessionLiveStackClient {
  Future<SessionLiveStackStatus> status();
  Future<void> start();
  Future<void> reset();
  Future<void> stop();

  /// The latest stacked JPEG, or null while no light is stacked (204).
  Future<StackFrame?> frame();
  void close();
}

class SessionLiveStackApi implements SessionLiveStackClient {
  SessionLiveStackApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

  final Dio _dio;
  static const _base = '/api/v1/equipment/camera/live-stack';

  @override
  Future<SessionLiveStackStatus> status() async {
    final res = await _dio.get<Map<String, dynamic>>(
      _base,
      options: Options(validateStatus: (s) => s == 200 || s == 501),
    );
    // 501: a camera service without the capability.
    if (res.statusCode == 501) {
      return const SessionLiveStackStatus(unsupported: 'not_available');
    }
    return SessionLiveStackStatus.fromJson(res.data ?? const {});
  }

  @override
  Future<void> start() => _dio.post<void>('$_base/start');

  @override
  Future<void> reset() => _dio.post<void>('$_base/reset');

  @override
  Future<void> stop() => _dio.post<void>('$_base/stop');

  @override
  Future<StackFrame?> frame() async {
    final res = await _dio.get<List<int>>(
      '$_base/frame',
      options: Options(
        responseType: ResponseType.bytes,
        validateStatus: (s) => s == 200 || s == 204,
      ),
    );
    final bytes = res.data;
    if (res.statusCode == 204 || bytes == null || bytes.isEmpty) return null;
    return StackFrame(
      bytes is Uint8List ? bytes : Uint8List.fromList(bytes),
      int.tryParse(res.headers.value('x-stack-seq') ?? '') ?? 0,
      int.tryParse(res.headers.value('x-stack-lights') ?? '') ?? 0,
    );
  }

  @override
  void close() => _dio.close(force: true);
}
