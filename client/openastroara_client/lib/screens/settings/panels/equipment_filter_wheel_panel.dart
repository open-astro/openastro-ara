import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/filter_wheel_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/filter_wheel_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/filter_wheel_labels_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Filter Wheel panel. Shows the connected wheel's live slots (the device's
/// own names + focus offsets) with a per-slot select, via the shared connection
/// card. Slot names are the device's, not local labels (§37.4 hydration).
class EquipmentFilterWheelPanel extends ConsumerWidget {
  const EquipmentFilterWheelPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final connN = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(filterWheelProvider);
    final notifier = ref.read(filterWheelProvider.notifier);
    final labels = ref.watch(filterWheelLabelsProvider);
    final labelsN = ref.read(filterWheelLabelsProvider.notifier);
    // While a wheel is connected its own driver slot names are authoritative and
    // shown live above, so the local "Slot labels" section is pure duplication —
    // only surface it for offline sequence authoring. Show it ONLY on a resolved
    // status that is genuinely absent/disconnected; loading and (transient) error
    // keep it hidden so a single failed poll can't flash the editor in over a
    // still-registered wheel.
    final showSlotLabels = status.maybeWhen(
      data: (s) => s == null || !s.isConnected,
      orElse: () => false,
    );

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FilterWheelStatus>(
          status: status,
          deviceType: EquipmentDeviceType.filterWheel,
          deviceTypeLabel: 'filter wheel',
          emptyLabel: 'No filter wheel connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FilterWheelBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.filterWheel),
          onChanged: (v) =>
              connN.setAutoConnect(EquipmentDeviceType.filterWheel, v),
        ),
        // Local slot labels — the user's filter names used when authoring
        // sequences offline (the §38 editor reads `filterWheelLabelsProvider`),
        // independent of the connected wheel's own names shown live above. Hidden
        // while a wheel is connected (its driver names take over) to avoid duplication.
        if (showSlotLabels) ...[
          const SettingsSectionHeader('Slot labels (for sequences)'),
          for (var slot = 1; slot <= labels.slotCount; slot++)
            EditableTextRow(
              label: 'Slot $slot',
              helpKey: slot == 1 ? 'eq.filterwheel.slot_labels' : null,
              currentValue: labels.labelAt(slot),
              getCanonical: () =>
                  ref.read(filterWheelLabelsProvider).labelAt(slot),
              // Each committed row persists to the daemon (12h.2b round-trip);
              // a failure keeps the local edit and says so — offline authoring
              // still works, the labels just won't survive a daemon-side reload.
              parse: (s) {
                labelsN.setLabel(slot, s);
                unawaited(_persistLabels(context, ref));
              },
              hint: 'Empty = unused',
            ),
        ],
      ],
    );
  }
}

/// The connected wheel's live body: the current filter + the slot list, each slot
/// selectable (disabled while moving / for the active slot).
class _FilterWheelBody extends ConsumerWidget {
  final FilterWheelStatus status;
  const _FilterWheelBody({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (status.isConnecting) return const Text('Reading…');
    if (status.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Filter wheel read failed — check the device.')),
      ]);
    }
    final current = status.current;
    final currentText = current != null
        ? '${current.name.isEmpty ? 'Slot ${current.position}' : current.name} '
            '(slot ${current.position})'
        : (status.isMoving ? 'Changing…' : '—');
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(children: [
          const Expanded(child: Text('Current filter')),
          Text(currentText),
        ]),
        const Divider(height: 20, color: AraColors.border),
        if (status.slots.isEmpty)
          const Text('This filter wheel reports no slots.')
        else
          // Focus offsets are only meaningful when the driver actually reports
          // them; most wheels (e.g. ZWO EFW) report 0 for every slot, so hide the
          // column entirely rather than show a row of "focus offset 0".
          for (final slot in status.slots)
            _SlotRow(
              slot: slot,
              isCurrent: slot.position == status.currentSlot,
              disabled: status.isMoving,
              showOffset: status.slots.any((s) => s.focusOffset != 0),
              onSelect: () => _select(context, ref, slot),
            ),
      ],
    );
  }

  Future<void> _select(BuildContext context, WidgetRef ref, FilterSlot slot) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed =
          await ref.read(filterWheelProvider.notifier).changeFilter(slot.position);
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another action is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't change filter: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}

class _SlotRow extends StatelessWidget {
  final FilterSlot slot;
  final bool isCurrent;
  final bool disabled;
  // Whether to show the focus-offset column (any slot has a non-zero offset).
  final bool showOffset;
  final VoidCallback onSelect;
  const _SlotRow({
    required this.slot,
    required this.isCurrent,
    required this.disabled,
    required this.showOffset,
    required this.onSelect,
  });

  @override
  Widget build(BuildContext context) {
    final secondary = Theme.of(context)
        .textTheme
        .bodySmall
        ?.copyWith(color: AraColors.textSecondary);
    // Same empty-name fallback the "Current filter" header uses, so an unnamed
    // slot reads consistently in both places.
    final name = slot.name.isEmpty ? 'Slot ${slot.position}' : slot.name;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          // The driver's own slot number (0-indexed, matching "Current filter
          // (slot N)") — highlighted for the active slot.
          SizedBox(
            width: 26,
            child: Text(
              '${slot.position}',
              textAlign: TextAlign.center,
              style: secondary?.copyWith(
                  color: isCurrent
                      ? AraColors.accentConnected
                      : AraColors.textSecondary),
            ),
          ),
          const SizedBox(width: 6),
          // Fixed-width check slot so the name column starts at the same x on
          // EVERY row — reserving the space whether or not this slot is active.
          SizedBox(
            width: 20,
            child: isCurrent
                ? const Icon(Icons.check,
                    size: 16, color: AraColors.accentConnected)
                : null,
          ),
          Expanded(child: Text(name)),
          if (showOffset)
            Text('focus offset ${slot.focusOffset}', style: secondary),
          const SizedBox(width: 12),
          TextButton(
            onPressed: (disabled || isCurrent) ? null : onSelect,
            child: Text(isCurrent ? 'Active' : 'Select'),
          ),
        ],
      ),
    );
  }
}

/// Persist the slot labels to the daemon, surfacing a failure as a SnackBar
/// (the local edit is kept either way — offline authoring keeps working).
Future<void> _persistLabels(BuildContext context, WidgetRef ref) async {
  try {
    await ref.read(filterWheelLabelsProvider.notifier).persistToServer();
  } catch (e) {
    if (context.mounted) {
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Slot labels not saved: $e')));
    }
  }
}
