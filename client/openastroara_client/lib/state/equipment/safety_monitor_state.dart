import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/safety_monitor_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the SafetyMonitor on a server.
/// Overridable in tests so a pure fake can be injected.
final safetyMonitorApiFactoryProvider =
    Provider<EquipmentDeviceClient<SafetyMonitorStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<SafetyMonitorStatus>(
            server,
            path: 'safetymonitor',
            fromJson: SafetyMonitorStatus.fromJson,
          ),
    );

/// SafetyMonitor client bound to the **active** server (`savedServers.last`), or
/// `null` when no server is saved.
final safetyMonitorApiProvider =
    Provider<EquipmentDeviceClient<SafetyMonitorStatus>?>((ref) {
      final server = ref.watch(
        savedServersProvider.select(
          (async) => async.maybeWhen(
            data: (list) => list.isEmpty ? null : list.last,
            orElse: () => null,
          ),
        ),
      );
      if (server == null) return null;
      // A fresh Dio-backed client is built per active-server change and torn down via
      // onDispose; fine for the low connection churn here. (Pattern the other
      // single-instance device panels reuse.)
      final api = ref.watch(safetyMonitorApiFactoryProvider)(server);
      ref.onDispose(api.close);
      return api;
    });

/// Live SafetyMonitor status for the active server (or `null` when none is
/// connected). Exposes connect/disconnect; the generic engine polls a mid-connect
/// device to settlement.
class SafetyMonitorNotifier
    extends EquipmentDeviceNotifier<SafetyMonitorStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.safetyMonitor;

  @override
  EquipmentDeviceClient<SafetyMonitorStatus>? watchClient() =>
      ref.watch(safetyMonitorApiProvider);

  @override
  EquipmentDeviceClient<SafetyMonitorStatus>? readClient() =>
      ref.read(safetyMonitorApiProvider);
}

final safetyMonitorProvider =
    AsyncNotifierProvider<SafetyMonitorNotifier, SafetyMonitorStatus?>(
      SafetyMonitorNotifier.new,
    );
