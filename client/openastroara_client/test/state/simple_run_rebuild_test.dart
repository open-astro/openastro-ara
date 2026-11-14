import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

// #1875 slice 1: Simple Mode's queue comes from the rig. A device that did
// not queue the plan (or the same app after a restart) reads it back from
// the daemon: the targets, their rows and each row's own loop count. The
// session's framesCaptured (16 here) is never the plan's progress.

const _seq = 'S-1';

/// The daemon's plan for the running queue, as the wire carries it.
const _plan = {
  'sequence_id': _seq,
  'run_id': 'R-1',
  'targets': [
    {
      'name': 'IC 5148',
      'rows': [
        {
          'filter_name': 'L',
          'exposure_seconds': 60,
          'frames_total': 240,
          'frames_done': 2,
        },
      ],
    },
    {
      'name': 'NGC 253',
      'rows': [
        {
          'filter_name': 'R',
          'exposure_seconds': 120,
          'frames_total': 10,
          'frames_done': 0,
        },
        {
          'filter_name': 'G',
          'exposure_seconds': 120,
          'frames_total': 12,
          'frames_done': 0,
        },
      ],
    },
  ],
};

/// The daemon over HTTP: GET of the plan route answers [_plan], anything else 404.
class _Daemon implements HttpClientAdapter {
  final requests = <String>[];
  @override
  void close({bool force = false}) {}
  @override
  Future<ResponseBody> fetch(
    RequestOptions o,
    Stream<Uint8List>? body,
    Future<void>? cancel,
  ) async {
    requests.add('${o.method} ${o.path}');
    final found = o.method == 'GET' && o.path == '/api/v1/sequences/$_seq/plan';
    return ResponseBody.fromString(
      found ? jsonEncode(_plan) : '{"title":"Not Found","status":404}',
      found ? 200 : 404,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

/// The run another device started: active, on IC 5148, 16 frames filed this session.
class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
    sequenceId: _seq,
    runId: 'R-1',
    state: SequenceRunState.running,
    currentTargetName: 'IC 5148',
    framesCaptured: 16,
  );
}

List<Object> _rig(_Daemon daemon) => [
  sequenceApiProvider.overrideWith(
    (ref) => SequenceApi(
      const AraServer(hostname: '192.0.2.10', port: 1888),
      dio: Dio(BaseOptions(baseUrl: 'http://192.0.2.10:1888'))
        ..httpClientAdapter = daemon,
    ),
  ),
  sequenceRunStateProvider.overrideWith(_Running.new),
];

void main() {
  test(
    'a device that did not queue the plan rebuilds it from the rig',
    () async {
      final daemon = _Daemon();
      final c = ProviderContainer(overrides: _rig(daemon).cast());
      addTearDown(c.dispose);
      c.listen(simpleRunProvider, (_, _) {});
      await c.read(sequenceRunStateProvider.future);
      for (
        var i = 0;
        i < 50 && c.read(simpleRunProvider).targets.isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }

      final run = c.read(simpleRunProvider);
      expect(
        daemon.requests,
        contains('GET /api/v1/sequences/$_seq/plan'),
        reason: 'the plan is read back from the rig',
      );
      expect(run.sequenceId, _seq);
      expect(run.targets.map((t) => t.name), [
        'IC 5148',
        'NGC 253',
      ], reason: 'the queue in the rig\'s order');
      final l = run.targets.first.steps.single;
      expect(
        (l.filterName, l.exposureSeconds, l.frameCount),
        ('L', 60.0, 240),
        reason: "the rig's loop count, not this client's refitted copy",
      );
      expect(run.targets.last.steps.map((s) => (s.filterName, s.frameCount)), [
        ('R', 10),
        ('G', 12),
      ]);
    },
  );

  testWidgets(
    "the plan square counts this plan's lights from the rig, not the session's frames",
    (tester) async {
      final daemon = _Daemon();
      await tester.pumpWidget(
        ProviderScope(
          overrides: _rig(daemon).cast(),
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(
              body: Center(child: SimpleRunTracker(onTap: () {})),
            ),
          ),
        ),
      );
      for (var i = 0; i < 5; i++) {
        await tester.runAsync(
          () => Future<void>.delayed(const Duration(milliseconds: 20)),
        );
        await tester.pump();
      }

      expect(
        find.text('2 / 240'),
        findsOneWidget,
        reason: "IC 5148's L loop is 2 of 240 on the rig",
      );
      expect(
        find.text('16'),
        findsNothing,
        reason: "the session's framesCaptured is not this plan's count",
      );
      final bar = tester.widget<FractionallySizedBox>(
        find.byType(FractionallySizedBox).first,
      );
      expect(
        bar.widthFactor,
        closeTo(2 / 240, 1e-9),
        reason: 'the per-target bar, on a device that did not queue it',
      );
    },
  );
}
