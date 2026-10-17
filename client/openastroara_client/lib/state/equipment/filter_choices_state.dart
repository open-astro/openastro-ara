import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../settings/filter_set_state.dart';
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

/// The wheel name a run must write for planning filter [filter]: the choice
/// with the same name (case-insensitive), else the one choice of the filter's
/// kind (set 'Hα' → wheel 'Ha', set 'R' → wheel 'Red'), else the set's own
/// name (no wheel to disagree, or an ambiguous / unknown kind — the daemon's
/// Switch Filter does the same kind match again at run time). A Switch Filter
/// matches the wheel's names, so a plan that wrote the set's spelling instead
/// would be skipped with "No Filter was selected".
String wheelNameFor(List<FilterChoice> choices, PlanningFilter filter) {
  final key = filter.name.trim().toLowerCase();
  for (final c in choices) {
    if (c.name.trim().toLowerCase() == key) return c.name;
  }
  final sameKind = [
    for (final c in choices)
      if (FilterSetNotifier.recognizedKind(c.name) == filter.kind) c.name,
  ];
  return sameKind.length == 1 ? sameKind.single : filter.name;
}
