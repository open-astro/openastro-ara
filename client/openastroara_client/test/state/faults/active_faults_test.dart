import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/faults/faults_state.dart';
import 'package:openastroara/widgets/status_indicator.dart';

WsEvent _event(String type, Map<String, dynamic> payload,
        {int seq = 1, DateTime? ts}) =>
    WsEvent(
        type: type,
        ts: ts ?? DateTime.utc(2026, 7, 10, 4),
        seq: seq,
        payload: payload);

WsEvent _fault(String deviceType, String kind, {DateTime? ts}) =>
    _event(FaultWsEvents.fault, {
      'device_type': deviceType,
      'device_id': 'dev-1',
      'device_name': 'Test Device',
      'kind': kind,
      'details': '3 probes failed',
    }, ts: ts);

void main() {
  group('faultKindLevel', () {
    test('recovery-tracked kinds are red, everything else amber', () {
      expect(faultKindLevel('disconnected'), StatusLevel.error);
      expect(faultKindLevel('tracking_lost'), StatusLevel.error);
      expect(faultKindLevel('stall_timeout'), StatusLevel.busy);
      expect(faultKindLevel('value_mismatch'), StatusLevel.busy);
      expect(faultKindLevel('op_error'), StatusLevel.busy);
      expect(faultKindLevel('cooling_drift'), StatusLevel.busy);
      expect(faultKindLevel('some_future_kind'), StatusLevel.busy);
    });
  });

  group('blendFaultLevel', () {
    test('worst-of: a fault shows through a healthier base, never softens it',
        () {
      expect(blendFaultLevel(StatusLevel.connected, StatusLevel.error),
          StatusLevel.error);
      expect(blendFaultLevel(StatusLevel.connected, StatusLevel.busy),
          StatusLevel.busy);
      expect(blendFaultLevel(StatusLevel.error, StatusLevel.busy),
          StatusLevel.error);
      expect(blendFaultLevel(StatusLevel.connected, null),
          StatusLevel.connected);
      expect(blendFaultLevel(StatusLevel.disconnected, StatusLevel.error),
          StatusLevel.error);
    });
  });

  group('ActiveFaultsAccumulator', () {
    test('a fault event enters the standing set with its kind level', () {
      final acc = ActiveFaultsAccumulator();
      final snap = acc.apply(_fault('camera', 'disconnected'));
      expect(snap, isNotNull);
      final fault = snap!.byDeviceType['camera'];
      expect(fault, isNotNull);
      expect(fault!.level, StatusLevel.error);
      expect(fault.kind, 'disconnected');
      expect(fault.deviceName, 'Test Device');
      expect(fault.details, '3 probes failed');
    });

    test('non-fault and malformed events do not fold', () {
      final acc = ActiveFaultsAccumulator();
      expect(acc.apply(_event('sequence.progress', {})), isNull);
      expect(acc.apply(_event(FaultWsEvents.fault, {'kind': 'op_error'})),
          isNull, reason: 'device_type-less fault is unattributable');
      expect(acc.apply(_event(FaultWsEvents.fault, {'device_type': 'camera'})),
          isNull, reason: 'kind-less fault is unclassifiable');
    });

    test('an advisory never softens a standing red fault', () {
      final acc = ActiveFaultsAccumulator();
      acc.apply(_fault('camera', 'disconnected'));
      expect(acc.apply(_fault('camera', 'op_error')), isNull);
      expect(acc.snapshot.byDeviceType['camera']!.level, StatusLevel.error);
    });

    test('a red fault replaces a standing advisory', () {
      final acc = ActiveFaultsAccumulator();
      acc.apply(_fault('camera', 'op_error'));
      final snap = acc.apply(_fault('camera', 'disconnected'));
      expect(snap!.byDeviceType['camera']!.level, StatusLevel.error);
    });

    test('only the recovered action clears; in-flight and gave_up keep it', () {
      final acc = ActiveFaultsAccumulator();
      acc.apply(_fault('camera', 'disconnected'));
      for (final action in ['sequence_paused', 'reconnecting', 'gave_up']) {
        expect(
            acc.apply(_event(FaultWsEvents.actionTaken,
                {'device_type': 'camera', 'action': action})),
            isNull,
            reason: '$action must not clear the standing fault');
      }
      final snap = acc.apply(_event(FaultWsEvents.actionTaken,
          {'device_type': 'camera', 'action': 'recovered'}));
      expect(snap, isNotNull);
      expect(snap!.byDeviceType, isEmpty);
    });

    test('equipment.connected heals a red fault but leaves an advisory', () {
      final acc = ActiveFaultsAccumulator();
      acc.apply(_fault('camera', 'disconnected'));
      acc.apply(_fault('switch', 'value_mismatch'));
      final snap = acc.apply(_event(
          FaultWsEvents.connected, {'device_type': 'camera', 'state': 'connected'}));
      expect(snap, isNotNull);
      expect(snap!.byDeviceType.containsKey('camera'), isFalse);
      expect(
          acc.apply(_event(FaultWsEvents.connected,
              {'device_type': 'switch', 'state': 'connected'})),
          isNull,
          reason: 'a reconnect does not retract a port read-back mismatch');
      expect(acc.snapshot.byDeviceType.containsKey('switch'), isTrue);
    });

    test('prune ages out advisories, never red faults', () {
      final t0 = DateTime.utc(2026, 7, 10, 4);
      final acc = ActiveFaultsAccumulator(advisoryTtl: const Duration(minutes: 10));
      acc.apply(_fault('camera', 'disconnected', ts: t0));
      acc.apply(_fault('switch', 'value_mismatch', ts: t0));
      expect(acc.prune(t0.add(const Duration(minutes: 5))), isNull,
          reason: 'nothing old enough yet');
      final snap = acc.prune(t0.add(const Duration(minutes: 11)));
      expect(snap, isNotNull);
      expect(snap!.byDeviceType.containsKey('switch'), isFalse,
          reason: 'the stale advisory ages out');
      expect(snap.byDeviceType.containsKey('camera'), isTrue,
          reason: 'a red fault holds until its clear signal');
    });

    test('worstFor spans multiple wire tokens and returns null when clean', () {
      final acc = ActiveFaultsAccumulator();
      acc.apply(_fault('flatdevice', 'op_error'));
      final snap = acc.snapshot;
      expect(snap.worstFor(const {'covercalibrator', 'flatdevice'}),
          StatusLevel.busy);
      expect(snap.worstFor(const {'camera'}), isNull);
    });
  });
}
