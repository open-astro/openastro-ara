import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/safety_monitor_status.dart';

void main() {
  test('fromJson reads the daemon snake_case SafetyMonitorDto wire shape', () {
    final s = SafetyMonitorStatus.fromJson(const {
      'device_id': 'sm-0',
      'name': 'CloudWatcher',
      'state': 'connected',
      'safe': true,
      'last_transition_at': '2026-06-22T04:00:00Z',
    });
    expect(s.deviceId, 'sm-0');
    expect(s.name, 'CloudWatcher');
    expect(s.connectionState, EquipmentConnectionState.connected);
    expect(s.isConnected, isTrue);
    expect(s.safe, isTrue);
    expect(s.lastTransitionAt, DateTime.utc(2026, 6, 22, 4, 0, 0));
  });

  test('last_transition_at is normalized to UTC so format variants are equal', () {
    final z = SafetyMonitorStatus.fromJson(
        const {'last_transition_at': '2026-06-22T04:00:00Z'});
    final offset = SafetyMonitorStatus.fromJson(
        const {'last_transition_at': '2026-06-22T04:00:00+00:00'});
    expect(z.lastTransitionAt, offset.lastTransitionAt); // same instant
    expect(z.lastTransitionAt!.isUtc, isTrue);
    final unparseable =
        SafetyMonitorStatus.fromJson(const {'last_transition_at': 'nope'});
    expect(unparseable.lastTransitionAt, isNull);
  });

  test('an unrecognized / missing state token degrades to unknown, not a throw', () {
    final s = SafetyMonitorStatus.fromJson(const {'state': 'bogus'});
    expect(s.connectionState, EquipmentConnectionState.unknown);
    expect(s.isConnecting, isFalse);
    expect(s.safe, isFalse); // default when absent
    expect(s.name, '');
  });

  test('connecting maps through and is reported as isConnecting', () {
    final s = SafetyMonitorStatus.fromJson(const {'state': 'connecting'});
    expect(s.connectionState, EquipmentConnectionState.connecting);
    expect(s.isConnecting, isTrue);
    expect(s.isConnected, isFalse);
  });

  test('value equality holds (so a same-content re-read does not rebuild)', () {
    SafetyMonitorStatus mk() => SafetyMonitorStatus.fromJson(const {
          'device_id': 'sm-0',
          'name': 'CloudWatcher',
          'state': 'connected',
          'safe': false,
          'last_transition_at': 't',
        });
    expect(mk(), equals(mk()));
    expect(mk().hashCode, equals(mk().hashCode));
  });
}
