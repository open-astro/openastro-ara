import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../settings/filter_wheel_labels_state.dart';
import 'filter_wheel_state.dart';

/// One pickable filter: its name and 0-based wheel position.
typedef FilterChoice = ({String name, int position});

/// The filters the Imaging tab and the sequence editor's Switch Filter offer.
///
/// The connected wheel's names are the truth: AlpacaBridge reports the names
/// the user set on the bridge (else "Filter N"), and those are the names a run
/// matches and writes to FITS. The profile's slot labels (which the daemon
/// mirrors from the wheel on connect) cover offline authoring when no wheel is
/// connected, and name a slot the driver left unnamed. Empty names are skipped
/// and duplicates keep the first slot, so a name-keyed dropdown gets each name
/// once.
final filterChoicesProvider = Provider<List<FilterChoice>>((ref) {
  final wheel = ref
      .watch(filterWheelProvider)
      .maybeWhen(data: (s) => s, orElse: () => null);
  final labels = ref.watch(filterWheelLabelsProvider);
  final seen = <String>{};
  if (wheel != null &&
      wheel.connectionState == EquipmentConnectionState.connected &&
      wheel.slots.any((s) => s.name.trim().isNotEmpty)) {
    return [
      for (final s in wheel.slots)
        if ((s.name.trim().isNotEmpty ? s.name : labels.labelAt(s.position + 1))
            case final name when name.isNotEmpty && seen.add(name))
          (name: name, position: s.position),
    ];
  }
  return [
    for (var slot = 1; slot <= labels.slotCount; slot++)
      if (labels.labelAt(slot).isNotEmpty && seen.add(labels.labelAt(slot)))
        (name: labels.labelAt(slot), position: slot - 1),
  ];
});
