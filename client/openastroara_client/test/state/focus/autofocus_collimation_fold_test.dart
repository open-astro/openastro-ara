import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/collimation_verdict.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// Only the state read matters for this test; the verdict never hits REST.
class _IdleApi implements AutofocusApi {
  @override
  Future<AutofocusRun> state() async => AutofocusRun.idle;
  @override
  Future<AutofocusFrame?> fetchFrame() async => null;
  @override
  void close() {}
  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

WsEvent _ev(String type, Map<String, dynamic> payload, int seq) => WsEvent(
  type: type,
  ts: DateTime.utc(2026, 10, 13, 2, seq),
  seq: seq,
  payload: payload,
);

void main() {
  test('#1139 the collimation verdict folds into the live state and a new run clears it', () async {
    final ws = StreamController<WsEvent>.broadcast();
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'rig', port: 1),
        ),
        autofocusApiProvider.overrideWithValue(_IdleApi()),
        wsEventsProvider.overrideWith((ref) => ws.stream),
      ],
    );
    addTearDown(c.dispose);
    addTearDown(ws.close);
    c.listen(autofocusLiveProvider, (_, _) {});
    await Future<void>.delayed(Duration.zero);

    ws.add(
      _ev('autofocus.collimation_verdict', const {
        'severity': 'slight',
        'offset_percent': 8.5,
        'direction_degrees': 180.0,
        'stars_used': 11,
      }, 1),
    );
    await Future<void>.delayed(Duration.zero);
    final v = c.read(autofocusLiveProvider).collimation;
    expect(v, isNotNull);
    expect(v!.severity, CollimationSeverity.slight);
    expect(v.clockHour, 9);
    expect(
      v.atUtc,
      DateTime.utc(2026, 10, 13, 2, 1),
      reason: 'stamped with the event time',
    );

    ws.add(
      _ev('autofocus.collimation_verdict', const {
        'severity': 'insufficient',
        'offset_percent': 0,
      }, 2),
    );
    await Future<void>.delayed(Duration.zero);
    expect(
      c.read(autofocusLiveProvider).collimation,
      same(v),
      reason: 'no verdict is not "fine"; the last one stays',
    );

    ws.add(_ev('autofocus.started', const {'mode': 'classic'}, 3));
    await Future<void>.delayed(Duration.zero);
    expect(
      c.read(autofocusLiveProvider).collimation,
      isNull,
      reason: 'a new run starts clean',
    );
  });
}
