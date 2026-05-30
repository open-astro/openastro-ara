import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/equipment/alpaca_selection_state.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../theme/ara_colors.dart';
import 'alpaca_chooser_dialog.dart';

/// Shared "Alpaca device" row used by every §25.5.5 equipment panel.
/// Shows the currently-selected device (or "Not selected" greyed) plus a
/// "Choose…" trailing button that opens the §52.2 chooser dialog for the
/// matching device type.
class AlpacaDeviceRow extends ConsumerWidget {
  final EquipmentDeviceType deviceType;
  final String deviceTypeLabel;

  const AlpacaDeviceRow({
    super.key,
    required this.deviceType,
    required this.deviceTypeLabel,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selected = ref.watch(alpacaSelectionProvider)[deviceType];
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text('Alpaca device',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Expanded(
          child: Text(
            selected?.name ?? 'Not selected',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: selected == null ? AraColors.textDisabled : null,
                ),
          ),
        ),
        TextButton.icon(
          onPressed: () => showAlpacaChooserDialog(
            context,
            deviceType,
            deviceTypeLabel: deviceTypeLabel,
          ),
          icon: const Icon(Icons.search, size: 16),
          label: const Text('Choose…'),
        ),
      ]),
    );
  }
}
