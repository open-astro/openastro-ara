import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/profile_api.dart';

import '../support/fake_section_rig.dart';

/// #2068 a stub rig that records each GET's path and query, and answers
/// with [reply] (a JSON value) or [status].
class _Rig implements HttpClientAdapter {
  _Rig({this.reply, this.status = 200});

  final Object? reply;
  final int status;
  final seen = <(String, Map<String, dynamic>)>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    seen.add((options.path, Map.of(options.queryParameters)));
    return ResponseBody.fromString(
      status == 200 ? jsonEncode(reply) : '',
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

const _plan = {
  'stage': 'fine',
  'direction': 'east',
  'step_deg': 10,
  'turns': 2,
  'max_sweep_deg': 20,
};

const _list = {
  'ready': false,
  'mount_key': 'ZWO_AM5_1',
  'checks': [
    {'id': 'mount_connected', 'status': 'ok', 'detail': 'Connected.'},
    {
      'id': 'clearance_confirmed',
      'status': 'fail',
      'detail': "Confirm this mount's clearance first.",
    },
  ],
  'planned': {
    'stage': 'fine',
    'direction': 'west',
    'turn_step_deg': 10,
    'check_step_deg': 15,
    'turns': 2,
    'max_sweep_deg': 20,
    'reach_deg': 20.5,
    'start_cw_angle_deg': 12.5,
    'min_altitude_deg': null,
  },
  'move_to_start': null,
};

PolarAlignApi _api(_Rig rig) => PolarAlignApi(
  const AraServer(hostname: 'x', port: 80),
  dio: Dio()..httpClientAdapter = rig,
);

void main() {
  test('start-plan sends the stage alone, never hand_turn (#2170)', () async {
    final rig = _Rig(reply: _plan);
    final api = _api(rig);

    await api.startPlan(stage: PolarAlignStages.fine);

    expect(rig.seen.map((s) => s.$1), everyElement(endsWith('/start-plan')));
    expect(rig.seen[0].$2, {
      'stage': 'fine',
    }, reason: 'there is no hand-turn start to ask for');
  });

  test(
    'preflight sends the stage alone, never hand_turn, and parses the whole list (#2170)',
    () async {
      final rig = _Rig(reply: _list);
      final api = _api(rig);

      final list = await api.preflight();
      await api.preflight(stage: PolarAlignStages.check);

      expect(
        rig.seen.map((s) => s.$1),
        everyElement(endsWith('/polaralign/preflight')),
      );
      expect(rig.seen[0].$2, isEmpty, reason: 'no stage, no query');
      expect(rig.seen[1].$2, {
        'stage': 'check',
      }, reason: 'there is no hand-turn list to ask for');
      expect(list, isNotNull);
      expect(list!.ready, isFalse);
      expect(list.mountKey, 'ZWO_AM5_1');
      expect(list.checks.map((c) => (c.id, c.status)), [
        ('mount_connected', PolarAlignPreflightStatuses.ok),
        ('clearance_confirmed', PolarAlignPreflightStatuses.fail),
      ]);
      expect(
        list.check('clearance_confirmed')!.detail,
        "Confirm this mount's clearance first.",
      );
      final p = list.planned;
      expect(
        (
          p.direction,
          p.turnStepDeg,
          p.checkStepDeg,
          p.turns,
          p.maxSweepDeg,
          p.reachDeg,
        ),
        ('west', 10.0, 15.0, 2, 20.0, 20.5),
      );
      expect((p.startCwAngleDeg, p.minAltitudeDeg), (12.5, null));
    },
  );

  test('a preflight list without move_to_start is a FormatException', () async {
    final missing = Map<String, Object?>.of(_list)..remove('move_to_start');
    expect(_api(_Rig(reply: missing)).preflight(), throwsFormatException);
  });

  test('preflight from a rig without the list (404) is null', () async {
    expect(await _api(_Rig(status: 404)).preflight(), isNull);
  });

  test(
    'a preflight reply that is not an object is a FormatException',
    () async {
      expect(
        _api(_Rig(reply: const ['not', 'an', 'object'])).preflight(),
        throwsFormatException,
      );
    },
  );

  test("the polar-align section reads each mount's clearance entry", () async {
    final section = {
      ...rigDefaults['polar-align']!,
      'mount_clearance': {
        'ZWO_AM5_1': {'confirmed_reach_deg': 20.5},
        'Telescope Simulator': {'confirmed_reach_deg': 60},
      },
    };
    final api = ProfileApi(
      const AraServer(hostname: 'x', port: 80),
      dio: Dio()..httpClientAdapter = _Rig(reply: section),
    );

    final got = (await api.getPolarAlignSection()).value.mountClearance;

    expect(got.keys, unorderedEquals(['ZWO_AM5_1', 'Telescope Simulator']));
    expect(got['ZWO_AM5_1']!.confirmedReachDeg, 20.5);
    expect(got['Telescope Simulator']!.confirmedReachDeg, 60.0);
  });

  test(
    'a clearance entry without confirmed_reach_deg fails the load',
    () async {
      final section = {
        ...rigDefaults['polar-align']!,
        'mount_clearance': {
          'ZWO_AM5_1': {'max_cw_angle_deg': 45, 'clearance_confirmed': true},
        },
      };
      final api = ProfileApi(
        const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = _Rig(reply: section),
      );
      expect(api.getPolarAlignSection(), throwsFormatException);
    },
  );
}
