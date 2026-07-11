import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/stop_mount_overlay.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async =>
      const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeMountApi implements EquipmentDeviceClient<MountStatus> {
  final List<String> commands = [];
  bool failCommands = false;
  MountStatus? status;

  @override
  Future<MountStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    if (failCommands) throw Exception('unreachable');
    commands.add(subpath);
  }

  @override
  void close() {}
}

class _FakeSeqClient implements SequenceClient {
  final List<String> lifecycle = [];

  @override
  Future<String> resume(String id) async {
    lifecycle.add('resume:$id');
    return 'op';
  }

  @override
  Future<String> skipCurrent(String id) async {
    lifecycle.add('skip:$id');
    return 'op';
  }

  @override
  Future<String> stop(String id) async {
    lifecycle.add('stop:$id');
    return 'op';
  }

  @override
  void close() {}

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

WsEvent _event(String type, [Map<String, dynamic> payload = const {}]) =>
    WsEvent(type: type, ts: DateTime.utc(2026, 7, 11), seq: 1, payload: payload);

class _LinkUpNotifier extends Notifier<bool> {
  @override
  bool build() => true;
  void set(bool value) => state = value;
}

final _linkUp = NotifierProvider<_LinkUpNotifier, bool>(_LinkUpNotifier.new);

void main() {
  late StreamController<WsEvent> ws;
  late _FakeMountApi mountApi;
  late _FakeSeqClient seqApi;
  late ProviderContainer container;

  Future<void> pump(WidgetTester tester) async {
    ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    mountApi = _FakeMountApi();
    seqApi = _FakeSeqClient();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServerServiceProvider
              .overrideWithValue(_FakeSavedServerService()),
          wsEventsProvider.overrideWith((ref) => ws.stream),
          serverLinkUpProvider.overrideWith((ref) => ref.watch(_linkUp)),
          mountApiFactoryProvider.overrideWithValue((_) => mountApi),
          sequenceApiFactoryProvider.overrideWithValue((_) => seqApi),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: StopMountListener(child: Text('tab content')),
          ),
        ),
      ),
    );
    // Resolve the saved-servers chain so the device/sequence APIs bind —
    // nothing in this minimal tree watches it before the first action.
    container = ProviderScope.containerOf(
      tester.element(find.byType(StopMountListener)),
    );
    container.read(_linkUp.notifier).set(true);
    await container.read(savedServersProvider.future);
    await tester.pumpAndSettle();
  }

  Future<void> fire(WidgetTester tester, WsEvent event) async {
    ws.add(event);
    await tester.pumpAndSettle();
  }

  testWidgets('the button surfaces on slew_started and hides on complete', (
    tester,
  ) async {
    await pump(tester);
    expect(find.byKey(const ValueKey('stop_mount_button')), findsNothing);

    await fire(
      tester,
      _event(SlewWsEvents.started, {
        'target_ra_hours': 9.92,
        'target_dec_degrees': 69.1,
      }),
    );
    expect(find.byKey(const ValueKey('stop_mount_button')), findsOneWidget);
    expect(find.textContaining('RA 9.92h'), findsOneWidget);

    await fire(tester, _event(SlewWsEvents.complete));
    expect(
      find.byKey(const ValueKey('stop_mount_button')),
      findsNothing,
      reason: 'the button only exists while a slew is in progress (§57.2)',
    );
  });

  testWidgets('tapping the button issues the panic stop', (tester) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));

    await tester.tap(find.byKey(const ValueKey('stop_mount_button')));
    await tester.pumpAndSettle();

    expect(mountApi.commands, contains('abort'));
    expect(
      find.byKey(const ValueKey('stop_mount_button')),
      findsOneWidget,
      reason: 'the overlay stays until the daemon confirms via slew_aborted',
    );
  });

  testWidgets('slew_aborted closes the overlay and opens the modal', (
    tester,
  ) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));

    await fire(
      tester,
      _event(SlewWsEvents.aborted, {
        'halted_ra_hours': 9.57,
        'halted_dec_degrees': 61.0,
        'reason': 'user_request',
      }),
    );

    expect(find.byKey(const ValueKey('stop_mount_button')), findsNothing);
    expect(find.text('Mount stopped at user request'), findsOneWidget);
    expect(find.textContaining('RA 9.57h'), findsOneWidget);
  });

  testWidgets('resume acts on the run the daemon paused for this stop', (
    tester,
  ) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    await tester.tap(find.byKey(const ValueKey('stop_mount_button')));
    await tester.pumpAndSettle();
    await fire(tester, _event(SlewWsEvents.aborted));
    // The §57.4 pause lands after the modal opens (gate-arm semantics).
    await fire(
      tester,
      _event(SequenceWsEvents.paused, {'sequence_id': 'run-7'}),
    );

    await tester.tap(find.byKey(const ValueKey('stop_modal_resume')));
    await tester.pumpAndSettle();

    expect(seqApi.lifecycle, ['resume:run-7']);
  });

  testWidgets('skip target skips the current instruction then resumes', (
    tester,
  ) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    await tester.tap(find.byKey(const ValueKey('stop_mount_button')));
    await tester.pumpAndSettle();
    await fire(tester, _event(SlewWsEvents.aborted));
    await fire(
      tester,
      _event(SequenceWsEvents.paused, {'sequence_id': 'run-7'}),
    );

    await tester.tap(find.byKey(const ValueKey('stop_modal_skip')));
    await tester.pumpAndSettle();

    expect(seqApi.lifecycle, ['skip:run-7', 'resume:run-7']);
  });

  testWidgets('with no paused run the modal actions explain instead of throwing', (
    tester,
  ) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    await fire(tester, _event(SlewWsEvents.aborted));

    await tester.tap(find.byKey(const ValueKey('stop_modal_end')));
    await tester.pumpAndSettle();

    expect(seqApi.lifecycle, isEmpty);
    expect(find.textContaining('No paused run to act on'), findsOneWidget);
  });

  testWidgets('a dropped server link clears a stale overlay', (tester) async {
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    expect(find.byKey(const ValueKey('stop_mount_button')), findsOneWidget);

    container.read(_linkUp.notifier).set(false);
    await tester.pumpAndSettle();

    expect(
      find.byKey(const ValueKey('stop_mount_button')),
      findsNothing,
      reason: 'the slew state is unknown when the link drops — no stale panic button',
    );
  });

  testWidgets('a slew already in progress at reconnect re-arms the button', (
    tester,
  ) async {
    // #837 r2 — the event stream only carries transitions: a link drop
    // mid-slew clears the overlay and no fresh slew_started ever fires, so
    // the polled mount status must re-arm the panic button.
    await pump(tester);
    expect(find.byKey(const ValueKey('stop_mount_button')), findsNothing);

    mountApi.status = MountStatus(
      deviceId: 'm1',
      name: 'Bench Mount',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      runtimeState: 'slewing',
      rightAscensionHours: 5.5,
      declinationDegrees: 20.0,
      tracking: true,
      parked: false,
      atHome: false,
    );
    // Reconnect: link down (clears any state), back up → the widget does a
    // one-shot status read and re-arms from runtimeState == 'slewing'.
    container.read(_linkUp.notifier).set(false);
    await tester.pumpAndSettle();
    container.read(_linkUp.notifier).set(true);
    await tester.pumpAndSettle();

    expect(
      find.byKey(const ValueKey('stop_mount_button')),
      findsOneWidget,
      reason: 'the polled status says slewing — the panic button must exist',
    );

    // The slew's END happens after reconnect, so the event stream clears it.
    await fire(tester, _event(SlewWsEvents.complete));
    expect(find.byKey(const ValueKey('stop_mount_button')), findsNothing);
  });

  testWidgets('a REMOTE abort still binds the paused run for the modal', (
    tester,
  ) async {
    // #837 r1 — the abort may come from another client or the server itself:
    // no local button press, so the paused-run capture must arm on the
    // aborted event, not in the tap handler.
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    await fire(tester, _event(SlewWsEvents.aborted)); // no tap
    await fire(
      tester,
      _event(SequenceWsEvents.paused, {'sequence_id': 'run-9'}),
    );

    await tester.tap(find.byKey(const ValueKey('stop_modal_resume')));
    await tester.pumpAndSettle();

    expect(seqApi.lifecycle, ['resume:run-9']);
  });

  testWidgets('a second stop cycle never acts on the previous cycle\'s run', (
    tester,
  ) async {
    // #837 r1 — the previous cycle's paused id must not leak: cycle 2's pause
    // hasn't landed yet, so the action explains rather than resuming run-7.
    await pump(tester);
    await fire(tester, _event(SlewWsEvents.started));
    await fire(tester, _event(SlewWsEvents.aborted));
    await fire(
      tester,
      _event(SequenceWsEvents.paused, {'sequence_id': 'run-7'}),
    );
    await tester.tap(find.byKey(const ValueKey('stop_modal_close')));
    await tester.pumpAndSettle();

    await fire(tester, _event(SlewWsEvents.started));
    await fire(tester, _event(SlewWsEvents.aborted)); // cycle 2, pause not landed
    await tester.tap(find.byKey(const ValueKey('stop_modal_resume')));
    await tester.pumpAndSettle();

    expect(seqApi.lifecycle, isEmpty,
        reason: 'acting on stale run-7 would resume the wrong run');
    expect(find.textContaining('No paused run to act on'), findsOneWidget);
  });
}
