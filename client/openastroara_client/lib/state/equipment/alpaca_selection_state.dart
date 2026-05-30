import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/discovered_device.dart';
import '../settings/equipment_connection_state.dart';

/// Tracks the user's currently-selected Alpaca device per equipment type.
/// In-memory only for now; 12h.5b layer 2 wires `/api/v1/profile/equipment`
/// to persist the choice across daemon restarts.

class AlpacaSelectionNotifier
    extends Notifier<Map<EquipmentDeviceType, DiscoveredDevice>> {
  @override
  Map<EquipmentDeviceType, DiscoveredDevice> build() => const {};

  void select(EquipmentDeviceType type, DiscoveredDevice device) {
    state = {...state, type: device};
  }

  void clear(EquipmentDeviceType type) {
    final next = {...state}..remove(type);
    state = next;
  }

  DiscoveredDevice? selectedFor(EquipmentDeviceType type) => state[type];
}

final alpacaSelectionProvider = NotifierProvider<AlpacaSelectionNotifier,
    Map<EquipmentDeviceType, DiscoveredDevice>>(AlpacaSelectionNotifier.new);
