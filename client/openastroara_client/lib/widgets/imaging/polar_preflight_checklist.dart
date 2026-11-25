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
import '../../util/polar_reach_text.dart';
import '../adaptive_controls.dart';
import '../adaptive_dialog.dart';
import '../help_icon.dart';
import '../settings/settings_row.dart';

/// #2068 (ARA-18) the Start card's pre-flight list, in the Full UI and in
/// Simple Mode alike: every check the rig refuses a start on, the failing and
/// the warning ones always shown, the rest behind "Show all checks". A failing
/// clearance check offers the clearance dialog ([confirmPolarClearance]).
/// #2165: with a mount connected it always says how far the run turns RA
/// each way (the rig's reach), so the limit is never hidden.
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
        if (preflight.mountKey != null)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 3),
            child: Text(
              l10n.polarPreflightReach(
                polarReachText(preflight.planned.reachDeg, l10n.localeName),
              ),
              key: const Key('polar-preflight-reach'),
              style: const TextStyle(
                fontSize: 13,
                color: AraColors.textSecondary,
              ),
            ),
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

/// #2068 (ARA-8) the clearance confirmation. #2165: the dialog asks one
/// question, whether there is room for the run's reach (the rig's
/// [PolarAlignPreflightPlanned.reachDeg]: its turns plus the 0.5° direction
/// test when owed), and on "Yes, there is room" saves this mount's entry
/// confirmed for that reach, then reads the pre-flight list again. Take back
/// (a mount with an entry, confirmed for this reach or a smaller one) removes
/// the entry (Joey, 2026-11-21). Nothing is saved
/// on Cancel. The section is loaded from the rig before it is saved (ADR
/// 0015). Returns a failed save's message in the user's words, null
/// otherwise; the caller shows it.
Future<String?> confirmPolarClearance(
  BuildContext context,
  WidgetRef ref,
  PolarAlignPreflight preflight,
) async {
  final key = preflight.mountKey;
  if (key == null) return null;
  final result = await _askPolarClearance(
    context,
    key,
    preflight.planned.reachDeg,
    hasEntry: polarMountHasClearanceEntry(ref, preflight),
  );
  if (result == null || !context.mounted) return null;
  return _savePolarClearance(context, ref, result.change(key));
}

/// #2165 the confirm step's clearance question: a motor start the rig refused
/// for clearance asks about [reachDeg], the rig's reach for that start's stage
/// (the refusal's `reach_deg`: a Check turns by the Check step, which can be
/// bigger than the turn size the Start card's list shows), and "Yes, there is
/// room" saves this mount's entry for it. True once that is saved; false on
/// Cancel and on Take back. Null when it cannot ask (the pre-flight list is
/// not loaded or failed to read, or names no mount): the caller shows the
/// rig's refusal. A failed save throws its message in the user's words (a
/// [StateError]).
Future<bool?> confirmPolarClearanceForStart(
  BuildContext context,
  WidgetRef ref,
  double reachDeg,
) async {
  final preflight = ref.read(polarAlignPreflightProvider).value;
  final key = preflight?.mountKey;
  if (preflight == null || key == null) return null;
  final result = await _askPolarClearance(
    context,
    key,
    reachDeg,
    hasEntry: polarMountHasClearanceEntry(ref, preflight),
  );
  if (result == null || !context.mounted) return false;
  final failure = await _savePolarClearance(context, ref, result.change(key));
  if (failure != null) throw StateError(failure);
  return result.saved != null;
}

/// #2165 the mount has a clearance entry (Take back has something to remove),
/// confirmed for this run's reach or not; the clearance row's pass while the
/// settings are not loaded yet.
bool polarMountHasClearanceEntry(
  WidgetRef ref,
  PolarAlignPreflight preflight,
) {
  final key = preflight.mountKey;
  final settings = ref.read(polarAlignSettingsProvider).value;
  if (key != null && settings != null) {
    return settings.mountClearance.containsKey(key);
  }
  return preflight.check(PolarAlignPreflightChecks.clearanceConfirmed)?.passes ??
      false;
}

/// The dialog's answer for [reachDeg]: the change it makes to the settings,
/// and the entry it saves (null for Take back). Null on Cancel.
Future<({PolarAlignMountClearance? saved})?> _askPolarClearance(
  BuildContext context,
  String key,
  double reachDeg, {
  required bool hasEntry,
}) async {
  final result = await showAdaptiveAlert<PolarClearanceResult>(
    context: context,
    builder: (_) =>
        PolarClearanceDialog(reachDeg: reachDeg, confirmed: hasEntry),
  );
  return switch (result) {
    null => null,
    PolarClearanceResult.takeBack => (saved: null),
    PolarClearanceResult.confirm => (
      saved: PolarAlignMountClearance(confirmedReachDeg: reachDeg),
    ),
  };
}

extension on ({PolarAlignMountClearance? saved}) {
  PolarAlignSettings Function(PolarAlignSettings) change(String key) =>
      (s) => switch (saved) {
        null => s.withoutMountClearance(key),
        final entry => s.withMountClearance(key, entry),
      };
}

/// #2068 Take back: this mount's entry removed, so the clearance check fails
/// again for a motor start (Joey, 2026-11-21). Returns a failed save's
/// message, null otherwise.
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
    (s) => s.withoutMountClearance(key),
  );
}

Future<String?> _savePolarClearance(
  BuildContext context,
  WidgetRef ref,
  PolarAlignSettings Function(PolarAlignSettings) change,
) async {
  final l10n = context.l10n;
  String? failure;
  try {
    await ref
        .read(polarAlignSettingsProvider.notifier)
        .changeAndSave(change);
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

/// #2068 Settings › Imaging › Polar align: how far the run turns the
/// connected mount each way (#2165, the rig's reach) and whether the room for
/// it is confirmed, as the rig's pre-flight list has them, with the clearance
/// dialog. Without a mount it says to connect one.
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
    final deg = polarReachText(preflight.planned.reachDeg, l10n.localeName);
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

/// What the clearance dialog answers: there is room, or Take back.
enum PolarClearanceResult { confirm, takeBack }

/// #2165 the clearance dialog: one question, whether there is room for the
/// tube, counterweight and cables to turn [reachDeg] each way, and one
/// button, "Yes, there is room" (Joey, 2026-11-21). No slider, no angle to
/// measure. Pops [PolarClearanceResult.confirm] on it,
/// [PolarClearanceResult.takeBack] on Take back (a [confirmed] mount only),
/// null on Cancel.
class PolarClearanceDialog extends StatelessWidget {
  const PolarClearanceDialog({
    super.key,
    required this.reachDeg,
    required this.confirmed,
  });

  /// How far this run turns RA each way: the rig's plan.
  final double reachDeg;

  /// The mount has a clearance entry, for this reach or a smaller one: the
  /// dialog offers Take back.
  final bool confirmed;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return adaptiveAlertDialog(
      scrollable: true,
      icon: const Icon(Icons.warning_amber_rounded),
      title: Text(l10n.polarClearanceTitle),
      content: Text(
        l10n.polarClearanceQuestion(polarReachText(reachDeg, l10n.localeName)),
        key: const Key('polar-clearance-dialog'),
      ),
      actions: [
        adaptiveDialogAction(
          context,
          key: const Key('polar-clearance-cancel'),
          onPressed: () => Navigator.of(context).pop(),
          child: Text(l10n.polarConfirmCancel),
        ),
        if (confirmed)
          adaptiveDialogAction(
            context,
            key: const Key('polar-clearance-take-back'),
            isDestructive: true,
            onPressed: () =>
                Navigator.of(context).pop(PolarClearanceResult.takeBack),
            child: Text(l10n.polarClearanceTakeBack),
          ),
        adaptiveDialogAction(
          context,
          key: const Key('polar-clearance-confirm'),
          isDefault: true,
          filled: true,
          onPressed: () =>
              Navigator.of(context).pop(PolarClearanceResult.confirm),
          child: Text(l10n.polarClearanceYes),
        ),
      ],
    );
  }
}
