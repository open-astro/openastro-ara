import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/filter_wheel_labels_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Filter Wheel panel. Auto-connect + per-slot labels editable in
/// 12h.2; focus offsets stay daemon-driven (§37.4.2 measurement wizard).
class EquipmentFilterWheelPanel extends ConsumerWidget {
  const EquipmentFilterWheelPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final connN = ref.read(equipmentConnectionProvider.notifier);
    final labels = ref.watch(filterWheelLabelsProvider);
    final labelsN = ref.read(filterWheelLabelsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          value: connection.autoConnect(EquipmentDeviceType.filterWheel),
          onChanged: (v) =>
              connN.setAutoConnect(EquipmentDeviceType.filterWheel, v),
        ),
        const SettingsSectionHeader('Filters'),
        // ⓘ icon only on slot 1 — the labels are a single conceptual setting
        // (see `eq.filterwheel.slot_labels` in `settings/registry.dart`), so
        // one help affordance per section is enough. Repeating ⓘ on every
        // slot would be visual noise per §69.1.
        for (var slot = 1; slot <= labels.slotCount; slot++)
          EditableTextRow(
            label: 'Slot $slot',
            helpKey: slot == 1 ? 'eq.filterwheel.slot_labels' : null,
            currentValue: labels.labelAt(slot),
            getCanonical: () =>
                ref.read(filterWheelLabelsProvider).labelAt(slot),
            parse: (s) => labelsN.setLabel(slot, s),
            hint: 'Empty = unused',
          ),
        const SettingsSectionHeader('Focus offsets'),
        const SettingsRow(
          label: 'Per-filter offsets',
          value: 'Not measured — run §37.4.2 measurement wizard',
        ),
      ],
    );
  }
}
