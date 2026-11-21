import 'dart:typed_data';

import 'package:dio/dio.dart';

import '../models/polar_align.dart';
import '../models/server.dart';
import 'refusal.dart';

/// The §45 polar-align operations the state layer depends on. An interface so
/// tests supply a pure fake; [PolarAlignApi] is the Dio-backed implementation.
abstract interface class PolarAlignClient {
  Future<PolarAlignStatus?> getStatus();

  /// #2048 — what a start at [stage] would do, for the confirm step the user
  /// answers before the mount moves.
  /// [handTurn]: the plan of a Turn RA by hand start, which the rig does not
  /// refuse for an unconfirmed clearance (it moves nothing).
  Future<PolarAlignStartPlan> startPlan({String? stage, bool handTurn = false});

  /// #2068 the pre-flight list for a start at [stage]: every check the rig
  /// refuses a start on. Null from a rig that predates it (a 404).
  /// [handTurn]: the list of a Turn RA by hand start, whose clearance check is
  /// a warn while the clearance is not confirmed.
  Future<PolarAlignPreflight?> preflight({String? stage, bool handTurn = false});

  /// Begin the [plan] the user confirmed: from where the mount points, one
  /// measurement at its stage, turning its direction (#2082: a measurement
  /// never leads into another; a check started from the adjust loop ends the
  /// loop first).
  Future<void> start(PolarAlignStartPlan plan);
  Future<void> stop();
  Future<void> complete();
  Future<void> requestCapture();
  Future<Uint8List?> getLiveFrame();
  void close();
}

/// Client wrapper around the §45 surface: the routine
/// (`/api/v1/equipment/polaralign/{status,start,stop,complete}` — the POSTs are
/// 202-Accepted; live progress arrives on the `polar_align.*` WS stream). The
/// §45.12 profile section (`/api/v1/profile/polar-align`) is a profile section
/// like the others: `ProfileApi` and `polarAlignSettingsProvider` (ADR 0015).
class PolarAlignApi implements PolarAlignClient {
  final Dio _dio;

  PolarAlignApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  /// Current routine snapshot, or `null` on a `404` (a server predating §45).
  /// Other HTTP failures throw `DioException` for the caller to surface.
  @override
  Future<PolarAlignStatus?> getStatus() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/equipment/polaralign/status');
      final data = res.data;
      if (data is! Map<String, dynamic>) return null;
      return PolarAlignStatus.fromJson(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  // `hand_turn` only when true: a rig from before it plans the motor start.
  static Map<String, dynamic>? _planQuery(String? stage, bool handTurn) {
    final q = <String, dynamic>{'stage': ?stage, if (handTurn) 'hand_turn': true};
    return q.isEmpty ? null : q;
  }

  /// #2048 — the confirm step's plan: the RA turn direction from where the
  /// mount points now, the step, and the most the run turns. A parked mount is
  /// the `polar-align-mount-parked` [Refusal].
  @override
  Future<PolarAlignStartPlan> startPlan({
    String? stage,
    bool handTurn = false,
  }) async {
    final Response<dynamic> res;
    try {
      res = await _dio.get<dynamic>(
        '/api/v1/equipment/polaralign/start-plan',
        queryParameters: _planQuery(stage, handTurn),
      );
    } on DioException catch (e) {
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('start-plan returned a non-object body');
    }
    return PolarAlignStartPlan.fromJson(data, stage: stage);
  }

  /// #2068 (ARA-18) the Start card's pre-flight list: always 200, a check
  /// that fails is a row. Null on a 404 (an older rig): the card then shows no
  /// list and the rig's own start refusals stand.
  @override
  Future<PolarAlignPreflight?> preflight({
    String? stage,
    bool handTurn = false,
  }) async {
    final Response<dynamic> res;
    try {
      res = await _dio.get<dynamic>(
        '/api/v1/equipment/polaralign/preflight',
        queryParameters: _planQuery(stage, handTurn),
      );
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw const FormatException('preflight returned a non-object body');
    }
    return PolarAlignPreflight.fromJson(data);
  }

  /// Begin the routine (preflight: connected guider + mount, configured site)
  /// at the [plan]'s stage, confirmed by the user with the plan's direction
  /// (#2048). 202-Accepted; a failed preflight surfaces as a 409/422 — a
  /// parked mount, a direction that changed since, a scope within 3° of the
  /// pole or turns past the sweep limit is a [Refusal]. `/polaralign/next` is
  /// retired (#2082): this client never calls it. A check waits [endTimeout]:
  /// from the adjust screen the daemon ends the loop (a confirmed stop) first.
  @override
  Future<void> start(PolarAlignStartPlan plan) async {
    try {
      await _dio.post<void>(
        '/api/v1/equipment/polaralign/start',
        data: <String, dynamic>{
          if (plan.stage != null) 'stage': plan.stage,
          'confirmed': true,
          'direction': plan.direction,
          'new_origin': plan.newOrigin,
          // #2083 — only when the user chose it: the rig then moves nothing.
          if (plan.handTurn) 'hand_turn': true,
          // #2081 — the user saw the 0.5° motor-direction test (a motor run).
          if (!plan.handTurn && plan.directionTestDeg > 0)
            'direction_test': true,
        },
        options: plan.stage == PolarAlignStages.check ? Options(receiveTimeout: endTimeout) : null,
      );
    } on DioException catch (e) {
      final refusal = Refusal.fromDio(e);
      if (refusal != null) throw refusal;
      rethrow;
    }
  }

  /// Abort — the mount stops where it is (#2048: AbortSlew and both motors
  /// stopped, confirmed on the daemon, tracking left off); logged as
  /// `aborted`. 202-Accepted.
  @override
  Future<void> stop() async {
    await _dio.post<void>(
      '/api/v1/equipment/polaralign/stop',
      options: Options(receiveTimeout: endTimeout),
    );
  }

  /// The user is satisfied: same unwind as [stop] but the §45.13 session row
  /// records `complete` with the achieved error. 202-Accepted.
  @override
  Future<void> complete() async {
    await _dio.post<void>(
      '/api/v1/equipment/polaralign/complete',
      options: Options(receiveTimeout: endTimeout),
    );
  }

  /// #2048 — Stop and Done answer once the daemon has ended the run and
  /// confirmed the mount stopped. Its slowest end (`EndWorstCase` in
  /// PolarAlignService.MountStop.cs) is a stuck run: one round of stop
  /// commands (3 × 3 s), the 15 s unwind grace, then the stop it confirms
  /// itself (8 s window, plus the poll, two 3 s reads and one more 9 s round):
  /// 47.25 s. The margin covers tracking off, the session row and the camera
  /// release, so the app never reports a failed Stop while the daemon is still
  /// stopping the mount. PolarAlignServiceTest reads this value and checks it
  /// against the daemon's bound.
  static const endTimeout = Duration(seconds: 60);

  /// Single-frame mode: take the next adjust frame. 202-Accepted; a 409
  /// outside the adjust phase.
  @override
  Future<void> requestCapture() async {
    await _dio.post<void>('/api/v1/equipment/polaralign/capture');
  }

  /// The guide camera's latest frame as JPEG bytes (the live view), or
  /// `null` when the server has none to serve (`404`).
  @override
  Future<Uint8List?> getLiveFrame() async {
    try {
      final res = await _dio.get<List<int>>(
        '/api/v1/equipment/polaralign/frame.jpg',
        options: Options(responseType: ResponseType.bytes),
      );
      final data = res.data;
      return data == null ? null : Uint8List.fromList(data);
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Releases the underlying Dio's connection pool. Call when the API is
  /// replaced (e.g. the active server changed) so sockets don't leak.
  @override
  void close() => _dio.close(force: true);
}

/// #1971 the rig's Polar Aligner: the device (`/api/v1/equipment/polaraligner`,
/// its stop and home) and its motors in a running routine
/// (`/api/v1/equipment/polaralign/correction/{stop,resume}`). The commands are
/// 202-Accepted; a refusal is a 409 whose detail is a sentence for people.
abstract interface class PolarAlignerClient {
  /// Null on a 404: a daemon that predates the Polar Aligner.
  Future<PolarAligner?> get();
  Future<void> stop();
  Future<void> home();

  /// Stop the motors; the routine carries on by hand.
  Future<void> stopInRoutine();

  /// After a stop: correct with the motors again, from a new test move.
  Future<void> resumeInRoutine();
  void close();
}

class PolarAlignerApi implements PolarAlignerClient {
  final Dio _dio;

  PolarAlignerApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  @override
  Future<PolarAligner?> get() async {
    try {
      final res = await _dio.get<dynamic>('/api/v1/equipment/polaraligner');
      final data = res.data;
      return data is Map<String, dynamic> ? PolarAligner.fromJson(data) : null;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  Future<void> stop() => _dio.post<void>('/api/v1/equipment/polaraligner/stop');

  @override
  Future<void> home() => _dio.post<void>('/api/v1/equipment/polaraligner/home');

  @override
  Future<void> stopInRoutine() => _dio.post<void>('/api/v1/equipment/polaralign/correction/stop');

  @override
  Future<void> resumeInRoutine() => _dio.post<void>('/api/v1/equipment/polaralign/correction/resume');

  @override
  void close() => _dio.close(force: true);
}

/// #2081 the RA motor direction Polar Align saved for this profile's mount
/// (`/api/v1/equipment/polaralign/motor-direction`) and "Test direction
/// again", which forgets it so the next start tests it with a 0.5° turn.
abstract interface class PolarAlignMotorDirectionClient {
  /// Null on a 404: a rig that predates the saved direction.
  Future<PolarAlignMotorDirectionState?> get();
  Future<void> retest();
  void close();
}

class PolarAlignMotorDirectionApi implements PolarAlignMotorDirectionClient {
  final Dio _dio;

  PolarAlignMotorDirectionApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 5),
            ),
          );

  @override
  Future<PolarAlignMotorDirectionState?> get() async {
    try {
      final res = await _dio.get<dynamic>(
        '/api/v1/equipment/polaralign/motor-direction',
      );
      final data = res.data;
      return data is Map<String, dynamic>
          ? PolarAlignMotorDirectionState.fromJson(data)
          : null;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  Future<void> retest() =>
      _dio.post<void>('/api/v1/equipment/polaralign/motor-direction/retest');

  @override
  void close() => _dio.close(force: true);
}
