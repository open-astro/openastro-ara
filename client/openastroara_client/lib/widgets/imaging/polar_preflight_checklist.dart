import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_preflight_state.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../adaptive_controls.dart';
import '../adaptive_dialog.dart';
import '../help_icon.dart';
import '../settings/settings_row.dart';

/// #2068 (ARA-18) the Start card's pre-flight list, in the Full UI and in
/// Simple Mode alike: every check the rig refuses a start on, the failing and
/// the warning ones always shown, the rest behind "Show all checks". A failing
/// clearance check offers the clearance dialog ([confirmPolarClearance]).
/// Renders the rig's list as data, loading and error; shows nothing for a rig
/// that predates the list.
class PolarPreflightChecklist extends ConsumerStatefulWidget {
  const PolarPreflightChecklist({super.key});

  @override
  ConsumerState<PolarPreflightChecklist> createState() =>
      _PolarPreflightChecklistState();
}

class _PolarPreflightChecklistState
    extends ConsumerState<PolarPreflightChecklist> {
  bool _all = false;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final preflight = ref.watch(polarAlignPreflightProvider);
    void refresh() => ref.read(polarAlignPreflightProvider.notifier).refresh();
    return switch (preflight) {
      AsyncData(:final value) =>
        value == null ? const SizedBox.shrink() : _list(value, refresh),
      AsyncError() => Row(
        key: const Key('polar-preflight-error'),
        children: [
          const Icon(
            Icons.error_outline,
            size: 16,
            color: AraColors.accentError,
          ),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              l10n.polarPreflightReadFailed,
              style: const TextStyle(
                fontSize: 13,
                color: AraColors.textSecondary,
              ),
            ),
          ),
          TextButton(
            key: const Key('polar-preflight-retry'),
            onPressed: refresh,
            child: Text(l10n.polarPreflightRetry),
          ),
        ],
      ),
      _ => Row(
        key: const Key('polar-preflight-loading'),
        children: [
          const SizedBox.square(
            dimension: 14,
            child: AdaptiveProgressIndicator(strokeWidth: 2),
          ),
          const SizedBox(width: 8),
          Text(
            l10n.polarPreflightChecking,
            style: const TextStyle(
              fontSize: 13,
              color: AraColors.textSecondary,
            ),
          ),
        ],
      ),
    };
  }

  Widget _list(PolarAlignPreflight preflight, VoidCallback refresh) {
    final l10n = context.l10n;
    final shown = [
      for (final c in preflight.checks)
        if (_all ||
            c.status == PolarAlignPreflightStatuses.fail ||
            c.status == PolarAlignPreflightStatuses.warn)
          c,
    ];
    final passed = preflight.checks
        .where((c) => c.status == PolarAlignPreflightStatuses.ok)
        .length;
    final notYet = preflight.checks
        .where((c) => c.status == PolarAlignPreflightStatuses.unknown)
        .length;
    final clearance = preflight.check(
      PolarAlignPreflightChecks.clearanceConfirmed,
    );
    return Column(
      key: const Key('polar-preflight'),
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Text(
              l10n.polarPreflightTitle,
              style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600),
            ),
            const HelpIcon(helpKey: 'setup.polar_align.preflight'),
            const Spacer(),
            IconButton(
              key: const Key('polar-preflight-refresh'),
              tooltip: l10n.polarPreflightRefresh,
              visualDensity: VisualDensity.compact,
              iconSize: 18,
              onPressed: refresh,
              icon: const Icon(Icons.refresh),
            ),
          ],
        ),
        for (final c in shown) _row(c),
        Wrap(
          spacing: 8,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Text(
              l10n.polarPreflightSummary(passed, notYet),
              style: const TextStyle(
                fontSize: 12,
                color: AraColors.textSecondary,
              ),
            ),
            TextButton(
              key: const Key('polar-preflight-show-all'),
              onPressed: () => setState(() => _all = !_all),
              child: Text(
                _all
                    ? l10n.polarPreflightShowFewer
                    : l10n.polarPreflightShowAll,
              ),
            ),
            if (clearance != null && preflight.mountKey != null)
              OutlinedButton(
                key: const Key('polar-preflight-clearance'),
                onPressed: () async {
                  final failure = await confirmPolarClearance(
                    context,
                    ref,
                    preflight,
                  );
                  if (failure != null && mounted) {
                    ref.read(polarAlignActionProvider.notifier).fail(failure);
                  }
                },
                child: Text(
                  clearance.passes
                      ? l10n.polarClearanceChange
                      : l10n.polarClearanceButton,
                ),
              ),
          ],
        ),
      ],
    );
  }

  Widget _row(PolarAlignPreflightCheck c) {
    final l10n = context.l10n;
    final (icon, color, status) = switch (c.status) {
      PolarAlignPreflightStatuses.ok => (
        Icons.check_circle_outline,
        AraColors.accentConnected,
        l10n.polarPreflightStatusOk,
      ),
      PolarAlignPreflightStatuses.warn => (
        Icons.warning_amber_outlined,
        AraColors.accentBusy,
        l10n.polarPreflightStatusWarn,
      ),
      PolarAlignPreflightStatuses.fail => (
        Icons.cancel_outlined,
        AraColors.accentError,
        l10n.polarPreflightStatusFail,
      ),
      _ => (
        Icons.help_outline,
        AraColors.textDisabled,
        l10n.polarPreflightStatusUnknown,
      ),
    };
    return Padding(
      key: Key('polar-preflight-check-${c.id}'),
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Semantics(
        label: '${polarPreflightCheckTitle(l10n, c.id)}: $status',
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(icon, size: 16, color: color),
            const SizedBox(width: 6),
            Expanded(
              child: Text.rich(
                TextSpan(
                  children: [
                    TextSpan(
                      text: polarPreflightCheckTitle(l10n, c.id),
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                    if (c.detail.isNotEmpty) TextSpan(text: ' — ${c.detail}'),
                  ],
                ),
                style: const TextStyle(
                  fontSize: 13,
                  color: AraColors.textSecondary,
                  height: 1.35,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The name of a pre-flight check in the user's language; the rig's id for a
/// check this app does not know. Pure — unit-tested through the list.
String polarPreflightCheckTitle(AppLocalizations l10n, String id) =>
    switch (id) {
      'mount_connected' => l10n.polarPreflightCheckMountConnected,
      'mount_parked' => l10n.polarPreflightCheckMountParked,
      'site_latitude' => l10n.polarPreflightCheckSiteLatitude,
      'site_longitude_lst' => l10n.polarPreflightCheckSiteLongitude,
      'mount_clock_or_site' => l10n.polarPreflightCheckMountClock,
      'pose_verified' => l10n.polarPreflightCheckPoseVerified,
      'clearance_confirmed' => l10n.polarPreflightCheckClearance,
      'start_pointing' => l10n.polarPreflightCheckStartPointing,
      'mount_type' => l10n.polarPreflightCheckMountType,
      'can_move_axis' => l10n.polarPreflightCheckCanMoveAxis,
      'aligner' => l10n.polarPreflightCheckAligner,
      'mount_limits' => l10n.polarPreflightCheckMountLimits,
      _ => id,
    };

/// #2068 (ARA-8) the clearance confirmation: the dialog explains the check,
/// takes the counterweight limit the user measured with the hand controller
/// (pre-filled with the rig's value for this mount), and on Confirm saves it,
/// confirmed for that limit, as this mount's entry of the polar-align section,
/// then reads the pre-flight list again. Take back (a confirmed mount) saves
/// it not confirmed (Joey, 2026-11-21). Nothing is saved on Cancel. The
/// section is loaded from the rig before it is saved (ADR 0015). Returns a
/// failed save's message in the user's words, null otherwise; the caller
/// shows it.
Future<String?> confirmPolarClearance(
  BuildContext context,
  WidgetRef ref,
  PolarAlignPreflight preflight,
) async {
  final key = preflight.mountKey;
  if (key == null) return null;
  final confirmed =
      preflight.check(PolarAlignPreflightChecks.clearanceConfirmed)?.passes ??
      false;
  final result = await showAdaptiveAlert<PolarClearanceResult>(
    context: context,
    builder: (_) => PolarClearanceDialog(
      initialDeg: preflight.planned.maxCwAngleDeg,
      confirmed: confirmed,
    ),
  );
  if (result == null || !context.mounted) return null;
  return _savePolarClearance(
    context,
    ref,
    key,
    result.takeBack
        ? PolarAlignMountClearance.notConfirmed(preflight.planned.maxCwAngleDeg)
        : PolarAlignMountClearance.confirmedAt(result.limitDeg),
  );
}

/// #2068 Take back: this mount's clearance not confirmed any more, its limit
/// kept, so the clearance check fails again for a motor start (Joey,
/// 2026-11-21). Returns a failed save's message, null otherwise.
Future<String?> takeBackPolarClearance(
  BuildContext context,
  WidgetRef ref,
  PolarAlignPreflight preflight,
) async {
  final key = preflight.mountKey;
  if (key == null) return null;
  return _savePolarClearance(
    context,
    ref,
    key,
    PolarAlignMountClearance.notConfirmed(preflight.planned.maxCwAngleDeg),
  );
}

Future<String?> _savePolarClearance(
  BuildContext context,
  WidgetRef ref,
  String key,
  PolarAlignMountClearance entry,
) async {
  final l10n = context.l10n;
  String? failure;
  try {
    await ref
        .read(polarAlignSettingsProvider.notifier)
        .changeAndSave((s) => s.withMountClearance(key, entry));
  } catch (e) {
    failure = e is SectionNotLoadedException || e is SectionConflictException
        ? e.toString()
        : friendlyError(l10n, e, action: l10n.polarActionClearance);
  }
  if (context.mounted) {
    await ref.read(polarAlignPreflightProvider.notifier).refresh();
  }
  return failure;
}

/// #2068 Settings › Imaging › Polar align: the connected mount's counterweight
/// limit and whether its clearance is confirmed, as the rig's pre-flight list
/// has them, with the clearance dialog. Without a mount it says to connect one.
class PolarClearanceSettingsRow extends ConsumerWidget {
  const PolarClearanceSettingsRow({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final preflight = ref.watch(polarAlignPreflightProvider).value;
    final clearance = preflight?.check(
      PolarAlignPreflightChecks.clearanceConfirmed,
    );
    if (preflight == null || preflight.mountKey == null || clearance == null) {
      return SettingsRow(
        key: const Key('polar-clearance-setting'),
        label: l10n.polarKnobClearance,
        value: l10n.polarKnobClearanceNoMount,
        helpKey: 'img.polar.clearance_confirmed',
      );
    }
    final deg = preflight.planned.maxCwAngleDeg.toStringAsFixed(0);
    final messenger = ScaffoldMessenger.of(context);
    return Column(
      key: const Key('polar-clearance-setting'),
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SettingsRow(
          label: l10n.polarKnobClearance,
          value: clearance.passes
              ? l10n.polarKnobClearanceConfirmed(deg)
              : l10n.polarKnobClearanceNotConfirmed(deg),
          hint: l10n.polarKnobClearanceHint,
          helpKey: 'img.polar.clearance_confirmed',
        ),
        Wrap(
          spacing: 8,
          children: [
            OutlinedButton(
              key: const Key('polar-clearance-setting-edit'),
              onPressed: () async {
                final failure = await confirmPolarClearance(
                  context,
                  ref,
                  preflight,
                );
                if (failure != null) {
                  messenger.showSnackBar(SnackBar(content: Text(failure)));
                }
              },
              child: Text(
                clearance.passes
                    ? l10n.polarClearanceChange
                    : l10n.polarClearanceButton,
              ),
            ),
            // Joey, 2026-11-21: a confirmation can be taken back.
            if (clearance.passes)
              TextButton(
                key: const Key('polar-clearance-setting-take-back'),
                onPressed: () async {
                  final failure = await takeBackPolarClearance(
                    context,
                    ref,
                    preflight,
                  );
                  if (failure != null) {
                    messenger.showSnackBar(SnackBar(content: Text(failure)));
                  }
                },
                child: Text(l10n.polarClearanceTakeBack),
              ),
          ],
        ),
      ],
    );
  }
}

/// What the clearance dialog answers: Confirm with the limit, or Take back.
class PolarClearanceResult {
  const PolarClearanceResult.confirm(this.limitDeg) : takeBack = false;
  const PolarClearanceResult.takeBack() : limitDeg = 0, takeBack = true;

  final double limitDeg;
  final bool takeBack;
}

/// The clearance dialog: what to check, the counterweight limit (10–75° from
/// counterweight-down, in 5° steps), and a tick that the user checked it.
/// The tick starts clear and clears again when the limit moves: a
/// confirmation is for one limit (Joey, 2026-11-21). Pops
/// [PolarClearanceResult.confirm] on Confirm (only once ticked),
/// [PolarClearanceResult.takeBack] on Take back (a [confirmed] mount only),
/// null on Cancel.
class PolarClearanceDialog extends StatefulWidget {
  const PolarClearanceDialog({
    super.key,
    required this.initialDeg,
    this.confirmed = false,
  });

  /// The rig's limit for this mount (its default for a mount with none).
  final double initialDeg;

  /// The mount's clearance is confirmed now: the dialog offers Take back.
  final bool confirmed;

  @override
  State<PolarClearanceDialog> createState() => _PolarClearanceDialogState();
}

class _PolarClearanceDialogState extends State<PolarClearanceDialog> {
  late double _deg = widget.initialDeg
      .clamp(
        PolarAlignMountClearance.minMaxCwAngleDeg,
        PolarAlignMountClearance.maxMaxCwAngleDeg,
      )
      .toDouble();
  bool _checked = false;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    const min = PolarAlignMountClearance.minMaxCwAngleDeg;
    const max = PolarAlignMountClearance.maxMaxCwAngleDeg;
    return adaptiveAlertDialog(
      scrollable: true,
      icon: const Icon(Icons.warning_amber_rounded),
      title: Text(l10n.polarClearanceTitle),
      content: Column(
        key: const Key('polar-clearance-dialog'),
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.polarClearanceExplain),
          const SizedBox(height: 8),
          Text(l10n.polarClearanceHow),
          const SizedBox(height: 12),
          Text(
            l10n.polarClearanceLimit(_deg.toStringAsFixed(0)),
            key: const Key('polar-clearance-limit-label'),
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
          Slider.adaptive(
            key: const Key('polar-clearance-limit'),
            min: min,
            max: max,
            divisions: ((max - min) / 5).round(),
            value: _deg,
            label: '${_deg.toStringAsFixed(0)}°',
            onChanged: (v) => setState(() {
              if (v != _deg) _checked = false;
              _deg = v;
            }),
          ),
          CheckboxListTile.adaptive(
            key: const Key('polar-clearance-checked'),
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            value: _checked,
            onChanged: (v) => setState(() => _checked = v ?? false),
            title: Text(l10n.polarClearanceTick),
          ),
        ],
      ),
      actions: [
        adaptiveDialogAction(
          context,
          key: const Key('polar-clearance-cancel'),
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.polarConfirmCancel),
        ),
        if (widget.confirmed)
          adaptiveDialogAction(
            context,
            key: const Key('polar-clearance-take-back'),
            isDestructive: true,
            onPressed: () => Navigator.of(
              context,
            ).pop(const PolarClearanceResult.takeBack()),
            child: Text(l10n.polarClearanceTakeBack),
          ),
        adaptiveDialogAction(
          context,
          key: const Key('polar-clearance-confirm'),
          isDefault: true,
          filled: true,
          onPressed: _checked
              ? () => Navigator.of(context).pop(PolarClearanceResult.confirm(_deg))
              : null,
          child: Text(l10n.polarClearanceConfirm),
        ),
      ],
    );
  }
}
