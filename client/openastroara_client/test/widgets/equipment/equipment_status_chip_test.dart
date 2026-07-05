import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';
import 'package:openastroara/widgets/status_indicator.dart';

class _FakeStatus extends EquipmentDeviceStatus {
  _FakeStatus(this.connectionState, {this.isBusy = false});
  @override
  final EquipmentConnectionState connectionState;
  @override
  final bool isBusy;
  @override
  String get name => 'fake';
}

void main() {
  group('equipmentStatusLevel', () {
    test('maps connection state to a dot colour', () {
      expect(equipmentStatusLevel(null), StatusLevel.disconnected);
      expect(equipmentStatusLevel(_FakeStatus(EquipmentConnectionState.connected)),
          StatusLevel.connected);
      expect(
          equipmentStatusLevel(
              _FakeStatus(EquipmentConnectionState.connected, isBusy: true)),
          StatusLevel.busy);
      expect(equipmentStatusLevel(_FakeStatus(EquipmentConnectionState.connecting)),
          StatusLevel.info);
      expect(equipmentStatusLevel(_FakeStatus(EquipmentConnectionState.error)),
          StatusLevel.error);
      expect(equipmentStatusLevel(_FakeStatus(EquipmentConnectionState.disconnected)),
          StatusLevel.disconnected);
      expect(equipmentStatusLevel(_FakeStatus(EquipmentConnectionState.unknown)),
          StatusLevel.disconnected);
    });
  });

  group('equipmentChipLevel (async)', () {
    test('loading → info, error → error, data → mapped', () {
      expect(equipmentChipLevel(const AsyncLoading<_FakeStatus?>()), StatusLevel.info);
      expect(equipmentChipLevel(AsyncError<_FakeStatus?>('x', StackTrace.empty)),
          StatusLevel.error);
      expect(
          equipmentChipLevel(
              AsyncData<_FakeStatus?>(_FakeStatus(EquipmentConnectionState.connected))),
          StatusLevel.connected);
      expect(equipmentChipLevel(const AsyncData<_FakeStatus?>(null)),
          StatusLevel.disconnected);
    });
  });

  group('switchChipLevel (multi-instance)', () {
    SwitchDevice dev(SwitchConnectionState s) => SwitchDevice(
        deviceId: 'sw', alpacaDeviceNumber: 0, name: 'sw', connectionState: s,
        ports: const []);
    test('green if any switch is connected, grey if none', () {
      expect(
          switchChipLevel(AsyncData([
            dev(SwitchConnectionState.disconnected),
            dev(SwitchConnectionState.connected),
          ])),
          StatusLevel.connected);
      expect(switchChipLevel(AsyncData([dev(SwitchConnectionState.disconnected)])),
          StatusLevel.disconnected);
      expect(switchChipLevel(const AsyncData([])), StatusLevel.disconnected);
      expect(switchChipLevel(const AsyncLoading()), StatusLevel.info);
      expect(switchChipLevel(AsyncError('x', StackTrace.empty)), StatusLevel.error);
    });

    test('red when devices errored (failed connect) and none are connected', () {
      // The list fetch succeeded but every device is in an error state (a failed
      // auto-connect): the chip must read red like the single-instance chips do,
      // not a misleading grey. Regression for the SW-stayed-grey report.
      expect(
          switchChipLevel(AsyncData([
            dev(SwitchConnectionState.error),
            dev(SwitchConnectionState.error),
          ])),
          StatusLevel.error);
      // A mix of disconnected + errored (none connected) is still an error.
      expect(
          switchChipLevel(AsyncData([
            dev(SwitchConnectionState.disconnected),
            dev(SwitchConnectionState.error),
          ])),
          StatusLevel.error);
      // But a live connection outranks a sibling's error — green wins.
      expect(
          switchChipLevel(AsyncData([
            dev(SwitchConnectionState.error),
            dev(SwitchConnectionState.connected),
          ])),
          StatusLevel.connected);
      // An in-flight action still outranks the error state (amber).
      expect(
          switchChipLevel(AsyncData([dev(SwitchConnectionState.error)]),
              acting: true),
          StatusLevel.busy);
    });

    test('§25.3 amber while an action is in flight, whatever the list says', () {
      // A port write on a connected switch...
      expect(
          switchChipLevel(AsyncData([dev(SwitchConnectionState.connected)]),
              acting: true),
          StatusLevel.busy);
      // ...and a connect still in flight (nothing connected yet) both read busy.
      expect(
          switchChipLevel(AsyncData([dev(SwitchConnectionState.disconnected)]),
              acting: true),
          StatusLevel.busy);
      // Loading/error outrank the acting signal (an unreadable list is the
      // stronger fact — same precedence as the other chips).
      expect(switchChipLevel(const AsyncLoading(), acting: true), StatusLevel.info);
      expect(switchChipLevel(AsyncError('x', StackTrace.empty), acting: true),
          StatusLevel.error);
    });
  });

  testWidgets('tapping a chip routes to its device Settings panel (Options tab)',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: EquipmentStatusChip(
            icon: Icons.camera_alt,
            label: 'CAM',
            panelId: 'eq.camera',
            watchLevel: (_) => StatusLevel.connected,
          ),
        ),
      ),
    ));

    expect(find.text('CAM'), findsOneWidget);
    await tester.tap(find.text('CAM'));
    await tester.pump();

    expect(container.read(selectedSettingsPanelProvider), 'eq.camera');
    expect(container.read(selectedTabIndexProvider), 3); // Options tab
  });
}
