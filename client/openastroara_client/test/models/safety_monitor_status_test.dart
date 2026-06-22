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
    expect(s.lastTransitionAt, '2026-06-22T04:00:00Z');
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
