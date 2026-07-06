import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/flat_panel_status.dart';
import '../settings/equipment_connection_state.dart';
import '../../models/server.dart';
import '../../services/equipment_device_api.dart';
import '../saved_server_state.dart';
import 'equipment_device_state.dart';

/// Builds an [EquipmentDeviceClient] for the flat (cover/calibrator) panel on a
/// server. Overridable in tests so a pure fake can be injected.
final flatPanelApiFactoryProvider =
    Provider<EquipmentDeviceClient<FlatPanelStatus> Function(AraServer)>(
      (ref) =>
          (server) => EquipmentDeviceApi<FlatPanelStatus>(
            server,
            path: 'flatdevice',
            fromJson: FlatPanelStatus.fromJson,
          ),
    );

/// Flat-panel client bound to the **active** server, or `null` when none is saved.
final flatPanelApiProvider = Provider<EquipmentDeviceClient<FlatPanelStatus>?>((
  ref,
) {
  final server = ref.watch(
    savedServersProvider.select(
      (async) => async.maybeWhen(
        data: (list) => list.isEmpty ? null : list.last,
        orElse: () => null,
      ),
    ),
  );
  if (server == null) return null;
  final api = ref.watch(flatPanelApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live flat-panel status for the active server (or `null` when none is
/// connected). Connect/disconnect + the liveness/busy poll come from the generic
/// core; this device's cover/light controls live in its Settings panel.
class FlatPanelNotifier extends EquipmentDeviceNotifier<FlatPanelStatus> {
  @override
  EquipmentDeviceType get deviceType => EquipmentDeviceType.flatPanel;

  @override
  EquipmentDeviceClient<FlatPanelStatus>? watchClient() =>
      ref.watch(flatPanelApiProvider);

  @override
  EquipmentDeviceClient<FlatPanelStatus>? readClient() =>
      ref.read(flatPanelApiProvider);
}

final flatPanelProvider =
    AsyncNotifierProvider<FlatPanelNotifier, FlatPanelStatus?>(
      FlatPanelNotifier.new,
    );
