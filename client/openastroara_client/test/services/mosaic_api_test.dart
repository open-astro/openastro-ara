import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/mosaic.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/mosaic_api.dart';

/// §47 / #1146 — the exact wire shape `MosaicApi` sends and reads, against a
/// stub adapter: misspell a key and the daemon would quietly fall back.
class _Daemon implements HttpClientAdapter {
  final requests = <RequestOptions>[];
  final Map<String, Object?> routes; // path → body (null = 500)
  _Daemon(this.routes);

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    final body = routes[options.path];
    if (body == null) {
      return ResponseBody.fromString(
        '{"title":"boom"}',
        500,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      options.method == 'POST' ? 201 : 200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

MosaicApi _api(_Daemon daemon) {
  final dio = Dio()..httpClientAdapter = daemon;
  return MosaicApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  test(
    'create sends the daemon\'s field names and the panel centres',
    () async {
      final daemon = _Daemon({
        '/api/v1/mosaics': {'id': 'm1', 'name': 'Veil', 'total_panels': 2},
      });
      final created = await _api(daemon).create(
        name: 'Veil',
        centerRaDeg: 312.75,
        centerDecDeg: 30.5,
        cols: 2,
        rows: 1,
        overlapPercent: 15,
        positionAngleDeg: 20,
        panels: const [
          (raDeg: 311.5, decDeg: 30.5),
          (raDeg: 314.0, decDeg: 30.5),
        ],
        framesPerPanel: 120,
      );
      expect(created.id, 'm1');
      final req = daemon.requests.single;
      expect(req.method, 'POST');
      final sent = req.data as Map<String, dynamic>;
      expect(sent.keys.toSet(), {
        'name',
        'center_ra_degrees',
        'center_dec_degrees',
        'panel_count_x',
        'panel_count_y',
        'overlap_percent',
        'position_angle_degrees',
        'panels',
        'frames_per_panel',
      });
      expect(sent['panel_count_x'], 2);
      expect(sent['panel_count_y'], 1);
      expect(sent['frames_per_panel'], 120);
      expect(sent['panels'], [
        {'ra_degrees': 311.5, 'dec_degrees': 30.5},
        {'ra_degrees': 314.0, 'dec_degrees': 30.5},
      ]);
    },
  );

  test('fetchProjects reads the list, each progress and each panel list; one broken project keeps its row', () async {
    final daemon = _Daemon({
      '/api/v1/mosaics': {
        'items': [
          {
            'id': 'a',
            'name': 'Veil',
            'panel_count_x': 2,
            'panel_count_y': 1,
            'total_panels': 2,
          },
          {
            'id': 'b',
            'name': 'Heart',
            'panel_count_x': 1,
            'panel_count_y': 1,
            'total_panels': 1,
          },
          {'name': 'no id — dropped'},
        ],
        'next_cursor': null,
        'has_more': false,
      },
      '/api/v1/mosaics/a/progress': {
        'mosaic_id': 'a',
        'completed_panels': 1,
        'total_panels': 2,
        'current_panel_index': 1,
        'frames_per_panel_target': 3,
        'frames_captured_total': 4,
      },
      '/api/v1/mosaics/a/panels': [
        {'panel_index': 0, 'status': 'done', 'frames_captured': 3},
        {'panel_index': 1, 'status': 'in_progress', 'frames_captured': 1},
      ],
      // project b: progress 500s, panels fine
      '/api/v1/mosaics/b/panels': [
        {'panel_index': 0, 'status': 'pending', 'frames_captured': 0},
      ],
    });

    final projects = await _api(daemon).fetchProjects();

    expect(projects.map((p) => p.mosaic.id), [
      'a',
      'b',
    ], reason: 'the unkeyed row is dropped');
    expect(projects[0].progress.completedPanels, 1);
    expect(projects[0].progress.framesPerPanelTarget, 3);
    expect(projects[0].panels.map((p) => p.status), [
      MosaicPanelStatus.done,
      MosaicPanelStatus.inProgress,
    ]);
    expect(
      projects[1].progress.totalPanels,
      0,
      reason: 'its progress read failed: unknown, not missing',
    );
    expect(
      projects[1].panels,
      isEmpty,
      reason:
          'the panels read follows the failed progress read in the same try',
    );
    expect(daemon.requests.first.queryParameters['limit'], 100);
  });
}
