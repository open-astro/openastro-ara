import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

WsEvent _limit({bool stopped = true, int paused = 1}) => WsEvent(
  type: 'telescope.limit_reached',
  ts: DateTime.utc(2026, 11, 20, 11),
  seq: 1,
  payload: {
    'limit': 'stop_before_meridian',
    'minutes': 10,
    'tracking_stopped': stopped,
    'runs_paused': paused,
    'detail': 'Tracking stopped 10 min before the meridian (meridian flip off). Plan paused. Ara didn\'t move the mount.',
  },
);

/// #2044 — the daemon's meridian stop (flip off) as Simple Mode shows it: an
/// error pill, and the reason under a paused run.
void main() {
  late StreamController<WsEvent> ws;
  late ProviderContainer c;
  setUp(() {
    ws = StreamController<WsEvent>();
    c = ProviderContainer(
      overrides: [wsEventsProvider.overrideWith((ref) => ws.stream)],
    );
    c.listen(simpleMountLimitProvider, (_, _) {});
  });
  tearDown(() {
    c.dispose();
    ws.close();
  });

  test('a meridian stop that paused the plan is an error pill and the run reason', () async {
    ws.add(_limit());
    await Future<void>.delayed(Duration.zero);
    const text =
        "Tracking stopped 10 min before the meridian. Plan paused. Ara didn't move the mount.";
    final pill = c.read(simpleMessagesProvider).single;
    expect(
      (pill.kind, pill.title, pill.origin),
      (SimpleMessageKind.error, text, SimpleStatusOrigin.mount),
    );
    expect(c.read(simpleMountLimitProvider), text);
  });

  test('with no plan running only the pill shows; a mount that kept tracking says to stop it', () async {
    ws.add(_limit(stopped: false, paused: 0));
    await Future<void>.delayed(Duration.zero);
    expect(
      c.read(simpleMessagesProvider).single.title,
      "The mount didn't stop tracking 10 min before the meridian. Stop it by hand. Ara didn't move the mount.",
    );
    expect(c.read(simpleMountLimitProvider), isNull);
  });
}
