import 'package:flutter/material.dart';

import '../../models/equipment_device_status.dart';
import '../../theme/ara_colors.dart';

/// Connection-state pill for the single-instance equipment panels, keyed on the
/// shared [EquipmentConnectionState]. (Switch keeps its own chip — it predates
/// this shared enum and uses a distinct multi-instance state type.)
class EquipmentStateChip extends StatelessWidget {
  final EquipmentConnectionState state;
  const EquipmentStateChip({super.key, required this.state});

  @override
  Widget build(BuildContext context) {
    final (color, text) = switch (state) {
      EquipmentConnectionState.connected => (AraColors.accentConnected, 'Connected'),
      EquipmentConnectionState.connecting => (AraColors.accentBusy, 'Connecting'),
      EquipmentConnectionState.error => (AraColors.accentError, 'Error'),
      EquipmentConnectionState.disconnected =>
        (AraColors.textSecondary, 'Disconnected'),
      EquipmentConnectionState.unknown => (AraColors.textSecondary, 'Unknown'),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(text, style: TextStyle(color: color, fontSize: 12)),
    );
  }
}
