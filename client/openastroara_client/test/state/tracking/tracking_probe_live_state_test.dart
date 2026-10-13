import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/tracking_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/tracking/tracking_calibration_state.dart';
import 'package:openastroara/state/tracking/tracking_probe_live_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

WsEvent _ev(String type, Map<String, dynamic> payload, {int seq = 1}) =>
    WsEvent(
      type: type,
      ts: DateTime.utc(2026, 10, 12),
      seq: seq,
      payload: payload,
    );

class _PushableRun extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
  void push(SequenceRunStateInfo v) => state = AsyncData(v);
}

void main() {
  _calibrationTests();
  group('foldTrackingProbeEvent', () {
    test(
      'started begins a fresh ladder, rungs append, completed closes it',
      () {
        var live = foldTrackingProbeEvent(
          null,
          _ev('tracking_probe.started', {
            'target': 'M 31',
            'rungs': [10, 30, 60],
            'prior_seconds': 45,
          }),
        );
        expect(live!.target, 'M 31');
        expect(live.ladder, [10, 30, 60]);
        expect(live.priorSeconds, 45);
        expect(live.done, isFalse);

        live = foldTrackingProbeEvent(
          live,
          _ev('tracking_probe.rung_complete', {
            'rung_index': 0,
            'exposure_seconds': 10,
            'eccentricity': 0.2,
            'fwhm_px': 2.1,
            'stars': 120,
            'usable': true,
            'passed': true,
          }),
        );
        live = foldTrackingProbeEvent(
          live,
          _ev('tracking_probe.rung_complete', {
            'rung_index': 1,
            'exposure_seconds': 30,
            'eccentricity': 0.62,
            'fwhm_px': 3.4,
            'stars': 110,
            'usable': true,
            'passed': false,
          }),
        );
        expect(live!.rungs.map((r) => r.passed), [true, false]);
        expect(
          describeTrackingRung(live.rungs.last),
          'Tracking probe 30 s · ecc 0.62 · FWHM 3.4 px · trailed',
        );

        live = foldTrackingProbeEvent(
          live,
          _ev('tracking_probe.completed', {
            'ceiling_seconds': 20,
            'prior_seconds': 45,
            'source': 'measured',
            'usable_rungs': 2,
            'summary': 'ceiling 20 s',
          }),
        );
        expect(live!.done, isTrue);
        expect(live.ceilingSeconds, 20);
        expect(
          describeTrackingCeiling(live),
          'Unguided ceiling 20 s (measured)',
        );
        expect(
          live.rungs,
          hasLength(2),
          reason: 'completed keeps the ladder for the readout',
        );
      },
    );

    test(
      'unrelated events leave the state alone; a second started restarts',
      () {
        final live = foldTrackingProbeEvent(
          null,
          _ev('tracking_probe.started', {
            'rungs': [10],
          }),
        );
        expect(
          foldTrackingProbeEvent(live, _ev('frame.complete', {})),
          same(live),
        );
        final again = foldTrackingProbeEvent(
          foldTrackingProbeEvent(
            live,
            _ev('tracking_probe.rung_complete', {
              'exposure_seconds': 10,
              'usable': true,
              'passed': true,
            }),
          ),
          _ev('tracking_probe.started', {
            'rungs': [10, 30],
          }),
        );
        expect(again!.rungs, isEmpty);
        expect(again.ladder, [10, 30]);
      },
    );

    test('a completed probe that kept the prior says so; no ceiling falls back to the summary', () {
      final kept = foldTrackingProbeEvent(
        null,
        _ev('tracking_probe.completed', {
          'ceiling_seconds': 45,
          'source': 'prior',
          'summary': 's',
        }),
      );
      expect(
        describeTrackingCeiling(kept!),
        'Unguided ceiling 45 s (prior kept)',
      );
      final none = foldTrackingProbeEvent(
        null,
        _ev('tracking_probe.completed', {
          'source': 'prior',
          'summary': 'Too few usable rungs',
        }),
      );
      expect(describeTrackingCeiling(none!), 'Too few usable rungs');
      expect(
        describeTrackingRung(const TrackingProbeRungFixture().unusable),
        'Tracking probe 2.0 min · too few stars',
      );
    });
  });

  group('trackingProbeLiveProvider', () {
    late StreamController<WsEvent> ws;
    late ProviderContainer container;
    late _PushableRun run;

    setUp(() async {
      ws = StreamController<WsEvent>.broadcast();
      run = _PushableRun();
      container = ProviderContainer(
        overrides: [
          wsEventsProvider.overrideWith((ref) => ws.stream),
          sequenceRunStateProvider.overrideWith(() => run),
        ],
      );
      container.listen(trackingProbeLiveProvider, (_, _) {});
      await container.read(sequenceRunStateProvider.future);
    });

    tearDown(() async {
      container.dispose();
      await ws.close();
    });

    test('folds the stream and clears on a new run id', () async {
      run.push(
        const SequenceRunStateInfo(
          runId: 'r1',
          state: SequenceRunState.running,
        ),
      );
      ws.add(
        _ev('tracking_probe.started', {
          'rungs': [10, 30],
        }),
      );
      ws.add(
        _ev('tracking_probe.rung_complete', {
          'exposure_seconds': 10,
          'usable': true,
          'passed': true,
        }, seq: 2),
      );
      await Future<void>.delayed(Duration.zero);
      expect(container.read(trackingProbeLiveProvider)!.rungs, hasLength(1));

      run.push(
        const SequenceRunStateInfo(
          runId: 'r2',
          state: SequenceRunState.running,
        ),
      );
      await Future<void>.delayed(Duration.zero);
      expect(
        container.read(trackingProbeLiveProvider),
        isNull,
        reason: 'run-scoped',
      );
    });
  });
}

/// A TrackingApi that answers from memory and counts reads.
class _CountingApi extends TrackingApi {
  _CountingApi(this.record) : super(const AraServer(hostname: 'x', port: 1));
  TrackingCalibration? record;
  int reads = 0;
  @override
  Future<TrackingCalibration?> read() async {
    reads++;
    return record;
  }
  @override
  void close() {}
}

void _calibrationTests() {
  test('#1264 trackingCalibrationProvider re-reads when a probe completes', () async {
    final ws = StreamController<WsEvent>.broadcast();
    final api = _CountingApi(null);
    final container = ProviderContainer(overrides: [
      wsEventsProvider.overrideWith((ref) => ws.stream),
      activeServerProvider.overrideWithValue(const AraServer(hostname: 'x', port: 1)),
      trackingApiFactoryProvider.overrideWithValue((_) => api),
    ]);
    addTearDown(container.dispose);
    addTearDown(ws.close);
    container.listen(trackingCalibrationProvider, (_, _) {});
    expect(await container.read(trackingCalibrationProvider.future), isNull);
    expect(api.reads, 1);

    api.record = TrackingCalibration(ceilingSeconds: 20, measuredUtc: DateTime.utc(2026, 10, 12), source: 'measured');
    ws.add(_ev('tracking_probe.completed', {'ceiling_seconds': 20, 'source': 'measured'}));
    await Future<void>.delayed(Duration.zero);
    expect((await container.read(trackingCalibrationProvider.future))!.ceilingSeconds, 20);
    expect(api.reads, 2, reason: 'one re-read per completed probe');

    ws.add(_ev('frame.complete', {}, seq: 3));
    await Future<void>.delayed(Duration.zero);
    expect(api.reads, 2, reason: 'other events do not re-read');
  });
}

/// Keeps the unusable-rung wording pinned without a payload round trip.
class TrackingProbeRungFixture {
  const TrackingProbeRungFixture();
  TrackingProbeRung get unusable => const TrackingProbeRung(
    exposureSeconds: 120,
    stars: 2,
    usable: false,
    passed: false,
  );
}
