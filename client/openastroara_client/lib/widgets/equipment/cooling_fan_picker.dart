import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/switch_device.dart';
import '../../state/equipment/switch_state.dart';
import '../../state/settings/cooling_fan_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';

/// The Cooling fan choice (#1700): None, then each writable port of the
/// connected switches as "Device › Port", with what the choice does under it.
class CoolingFanPicker extends ConsumerWidget {
  const CoolingFanPicker({
    super.key,
    required this.value,
    required this.onChanged,
    this.style,
  });

  final CoolingFan value;
  final ValueChanged<CoolingFan> onChanged;

  /// Simple Mode's text; the theme's elsewhere.
  final TextStyle? style;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final switches =
        ref.watch(switchListProvider).value ?? const <SwitchDevice>[];
    final options = <CoolingFan, String>{
      CoolingFan.none: l10n.coolingFanNone,
      for (final d in switches)
        if (d.isConnected)
          for (final p in d.ports)
            if (p.canWrite)
              CoolingFan(deviceId: d.deviceId, portId: p.id):
                  '${d.name} › ${p.label}',
    };
    // The chosen switch may be off right now: it stays chosen, and says so.
    options.putIfAbsent(value, () => l10n.coolingFanNotConnected);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: [
        Text(
          l10n.coolingFan,
          style: (style ?? const TextStyle()).copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        MergeSemantics(
          child: Semantics(
            label: l10n.coolingFan,
            child: DropdownButton<CoolingFan>(
              key: const Key('cooling-fan-picker'),
              isExpanded: true,
              value: value,
              // 48 is Flutter's own floor for this control: over Simple Mode's 44.
              itemHeight: kMinInteractiveDimension,
              style: style,
              onChanged: (v) {
                if (v != null && v != value) onChanged(v);
              },
              items: [
                for (final o in options.entries)
                  DropdownMenuItem(
                    value: o.key,
                    child: Text(o.value, overflow: TextOverflow.ellipsis),
                  ),
              ],
            ),
          ),
        ),
        Text(
          value.isNone ? l10n.coolingFanNoneNote : l10n.coolingFanChosenNote,
          style: AraText.caption,
        ),
      ],
    );
  }
}

/// The picker on the active profile's fan: Simple Mode's Camera window and the
/// full UI's camera panel. Nothing until the daemon's answer is in.
class ProfileCoolingFanPicker extends ConsumerWidget {
  const ProfileCoolingFanPicker({
    super.key,
    this.style,
  });

  final TextStyle? style;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final fan = ref.watch(coolingFanProvider).value;
    if (fan == null) return const SizedBox.shrink();
    return CoolingFanPicker(
      value: fan,
      style: style,
      onChanged: (v) => _choose(context, ref, v),
    );
  }

  Future<void> _choose(
    BuildContext context,
    WidgetRef ref,
    CoolingFan fan,
  ) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.camera);
    final title = context.l10n.coolingFanCouldntSave;
    final l10n = context.l10n;
    try {
      await ref.read(coolingFanProvider.notifier).choose(fan);
    } catch (e) {
      notify(title, detail: failureReason(l10n, e), error: true);
    }
  }
}
