import 'package:dio/dio.dart';

import '../models/library/mosaic.dart';
import '../models/server.dart';

/// The §47 mosaic-project calls the state layer depends on (#1146). An
/// interface so tests can supply a pure fake (no Dio); [MosaicApi] is the
/// Dio-backed production implementation.
abstract interface class MosaicClient {
  /// Every project, newest first, each with its progress and panels.
  Future<List<MosaicProject>> fetchProjects();

  /// Register a project the planetarium framed. [panels] are the per-panel
  /// J2000 centres (row-major, `cols × rows` of them); [framesPerPanel] is the
  /// run's planned frame count, which is when a panel counts as done.
  Future<Mosaic> create({
    required String name,
    required double centerRaDeg,
    required double centerDecDeg,
    required int cols,
    required int rows,
    required double overlapPercent,
    required double positionAngleDeg,
    required List<({double raDeg, double decDeg})> panels,
    required int framesPerPanel,
  });

  Future<void> delete(String id);

  void close();
}

/// Dio wrapper over `/api/v1/mosaics`.
class MosaicApi implements MosaicClient {
  final Dio _dio;

  /// [dio] is injectable so tests can supply a mock adapter; production passes
  /// nothing and a server-bound Dio is built.
  MosaicApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );

  @override
  Future<List<MosaicProject>> fetchProjects() async {
    final res = await _dio.get<dynamic>(
      '/api/v1/mosaics',
      queryParameters: {'limit': 100},
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'mosaics returned a non-object body (${data.runtimeType})',
      );
    }
    final raw = data['items'];
    if (raw is! List) return const [];
    final projects = <MosaicProject>[];
    for (final e in raw) {
      if (e is! Map<String, dynamic>) continue;
      final mosaic = Mosaic.fromJson(e);
      if (mosaic.id.isEmpty) continue;
      // Progress and panels are per-project reads; a failure on one project
      // must not hide the others, so each degrades to "unknown" on its own.
      var progress = const MosaicProgress();
      var panels = const <MosaicPanel>[];
      try {
        final p = await _dio.get<dynamic>(
          '/api/v1/mosaics/${mosaic.id}/progress',
        );
        if (p.data is Map<String, dynamic>) {
          progress = MosaicProgress.fromJson(p.data as Map<String, dynamic>);
        }
        final q = await _dio.get<dynamic>(
          '/api/v1/mosaics/${mosaic.id}/panels',
        );
        if (q.data is List) {
          panels = [
            for (final x in q.data as List)
              if (x is Map<String, dynamic>) MosaicPanel.fromJson(x),
          ];
        }
      } on DioException {
        // keep the project row with whatever was read
      }
      projects.add(
        MosaicProject(mosaic: mosaic, progress: progress, panels: panels),
      );
    }
    return projects;
  }

  @override
  Future<Mosaic> create({
    required String name,
    required double centerRaDeg,
    required double centerDecDeg,
    required int cols,
    required int rows,
    required double overlapPercent,
    required double positionAngleDeg,
    required List<({double raDeg, double decDeg})> panels,
    required int framesPerPanel,
  }) async {
    final res = await _dio.post<dynamic>(
      '/api/v1/mosaics',
      data: {
        'name': name,
        'center_ra_degrees': centerRaDeg,
        'center_dec_degrees': centerDecDeg,
        'panel_count_x': cols,
        'panel_count_y': rows,
        'overlap_percent': overlapPercent,
        'position_angle_degrees': positionAngleDeg,
        'panels': [
          for (final p in panels)
            {'ra_degrees': p.raDeg, 'dec_degrees': p.decDeg},
        ],
        'frames_per_panel': framesPerPanel,
      },
    );
    final data = res.data;
    if (data is! Map<String, dynamic>) {
      throw FormatException(
        'mosaics create returned a non-object body (${data.runtimeType})',
      );
    }
    return Mosaic.fromJson(data);
  }

  @override
  Future<void> delete(String id) => _dio.delete<void>('/api/v1/mosaics/$id');

  @override
  void close() => _dio.close(force: true);
}
