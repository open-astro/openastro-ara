import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../util/friendly_error.dart';
import '../../state/profile_management_state.dart';
import 'editable_field.dart';
import 'section_load_state.dart';

/// The "Auto-connect on boot" switch of an equipment panel. It saves on
/// change (ADR 0015): disabled until the rig's copy is loaded. A refusal
/// (not loaded, unreachable) shows in a snackbar; a copy changed on the rig
/// (412) keeps the edit and shows the conflict banner, whose Reload loads the
/// rig's copy.
class AutoConnectRow extends ConsumerWidget {
  const AutoConnectRow(this.type, {super.key, this.hint});

  final EquipmentDeviceType type;
  final String? hint;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final section = ref.watch(equipmentConnectionProvider);
    final settings = section.value;
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    final row = SettingsSwitchRow(
      label: 'Auto-connect on boot',
      helpKey: 'eq.auto_connect_on_boot',
      value: settings?.autoConnect(type) ?? false,
      // Not loaded: say why the switch is dead rather than show it Off.
      hint: settings == null
          ? (ref.watch(profileApiProvider) == null
                ? l10n.shellRigNotConnected
                : section.lastFailure == null
                ? l10n.settingsSectionLoading
                : l10n.settingsSectionNotLoaded)
          : hint,
      onChanged: settings == null
          ? null
          : (v) => unawaited(() async {
              try {
                await ref
                    .read(equipmentConnectionProvider.notifier)
                    .setAutoConnect(type, v);
              } catch (e) {
                messenger.showSnackBar(
                  SnackBar(
                    content: Text(
                      e is SectionNotLoadedException ||
                              e is SectionConflictException
                          ? e.toString()
                          : friendlyError(l10n, e, action: l10n.actionSave),
                    ),
                  ),
                );
              }
            }()),
    );
    if (!section.conflict) return row;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SectionConflictBanner(
          onReload: () {
            final api = ref.read(profileApiProvider);
            if (api != null) {
              ref.read(equipmentConnectionProvider.notifier).load(api);
            }
          },
        ),
        row,
      ],
    );
  }
}
