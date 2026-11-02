import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/autofocus_run.dart';
import '../../models/equipment_device_status.dart';
import '../../models/focuser_status.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../state/equipment/focuser_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../widgets/focus/v_curve_chart.dart';
import 'simple_preview_panel.dart'
    show SimpleShutter, SimpleShutterButton, kSimpleText;
import 'simple_shell.dart' show SimplePanel;
import '../../state/simple_messages_state.dart';

/// The HFR to show big: the confirmed one after a run, the latest kept probe
/// while one runs, else nothing. Pure — unit-tested.
double? simpleFocusHfr(AutofocusRun run) {
  if (run.isComplete && (run.finalHfr ?? 0) > 0) return run.finalHfr;
  final kept = run.sweepProbes.where((p) => p.kept && p.hfr > 0);
  return kept.isEmpty ? null : kept.last.hfr;
}

/// "Auto" or "Manual": the rail chip's caption in Focus mode. The method is
/// the one Smart Focus would use: the user's pick, else autofocus with a
/// focuser connected, by hand without (as `MainFocusCard` decides).
String simpleFocusSummary(WidgetRef ref) {
  final connected =
      ref.watch(focuserProvider).asData?.value?.connectionState ==
      EquipmentConnectionState.connected;
  final method =
      ref.watch(mainFocusMethodProvider) ??
      (connected ? MainFocusMethod.autofocus : MainFocusMethod.bahtinov);
  return method == MainFocusMethod.autofocus ? 'Auto' : 'Manual';
}

/// The target a nudge of [delta] steps sends: an absolute focuser goes to
/// position + delta (clamped when the range is known), a relative one moves
/// by delta. Null when an absolute focuser has no position yet. Pure —
/// unit-tested.
int? simpleNudgeTarget(FocuserStatus focuser, int delta) {
  final caps = focuser.capabilities;
  if (caps != null && !caps.absoluteFocuser) return delta;
  final at = focuser.position;
  if (at == null) return null;
  final to = at + delta;
  return caps != null && caps.maxPosition > caps.minPosition
      ? to.clamp(caps.minPosition, caps.maxPosition)
      : to;
}

/// The rail's shutter in Focus follows the chip: on Auto it starts autofocus
/// and its red square cancels it; on Manual it is Preview's, frames to focus
/// by.
class SimpleFocusShutter extends ConsumerWidget {
  const SimpleFocusShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (simpleFocusSummary(ref) != 'Auto') {
      return const SimpleShutter(start: true);
    }
    final live = ref.watch(autofocusLiveProvider);
    final connected =
        ref.watch(focuserProvider).asData?.value?.connectionState ==
        EquipmentConnectionState.connected;
    final running = live.run.isRunning;
    final notifier = ref.read(autofocusLiveProvider.notifier);
    return SimpleShutterButton(
      start: true,
      running: running,
      label: running ? 'Cancel autofocus' : 'Autofocus',
      onPressed: live.busy || !(running || connected)
          ? null
          : running
          ? notifier.cancel
          : notifier.start,
    );
  }
}

/// Focus mode's panel beside the right rail: where the run stands, the HFR,
/// the V-curve once there are probes, the focuser position and four nudges.
/// The shutter starts autofocus.
class SimpleFocusPanel extends ConsumerWidget {
  const SimpleFocusPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(autofocusLiveProvider);
    final run = live.run;
    final focuser = ref.watch(focuserProvider).asData?.value;
    final connected =
        focuser?.connectionState == EquipmentConnectionState.connected;
    final (headline, headlineColor) = MainFocusCard.headlineFor(run);
    final hfr = simpleFocusHfr(run);
    final error =
        live.error ?? (run.isFailed ? MainFocusCard.failureText(run) : null);
    // The last run's step size (measured when automatic), else the profile's.
    final step = run.stepSize ?? ref.watch(autofocusSettingsProvider).stepSize;
    final canNudge =
        connected && !run.isRunning && !(focuser?.isMoving ?? false);

    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 248,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Text(
              headline,
              style: kSimpleText.copyWith(color: headlineColor),
              maxLines: 2,
            ),
            Text(
              hfr == null ? 'HFR —' : 'HFR ${hfr.toStringAsFixed(2)}',
              style: kSimpleText.copyWith(
                fontSize: 30,
                fontWeight: FontWeight.w300,
              ),
            ),
            if (run.probes.isNotEmpty)
              SizedBox(height: 110, child: VCurveChart(run: run)),
            if (error != null)
              Text(
                error,
                style: kSimpleText.copyWith(color: AraColors.errorText),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
            Text(
              connected
                  ? 'Position ${focuser!.position ?? '—'}'
                  : 'No focuser connected',
              style: kSimpleText.copyWith(color: AraColors.textSecondary),
            ),
            Row(
              spacing: 4,
              children: [
                for (final (icon, delta, label) in [
                  (Icons.keyboard_double_arrow_left, -5 * step, 'In, large'),
                  (Icons.chevron_left, -step, 'In, small'),
                  (Icons.chevron_right, step, 'Out, small'),
                  (Icons.keyboard_double_arrow_right, 5 * step, 'Out, large'),
                ])
                  Expanded(
                    child: _Nudge(
                      icon: icon,
                      label: '$label ($delta steps)',
                      onTap: canNudge
                          ? () => _nudge(context, ref, focuser!, delta)
                          : null,
                    ),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _nudge(
    BuildContext context,
    WidgetRef ref,
    FocuserStatus focuser,
    int delta,
  ) async {
    final target = simpleNudgeTarget(focuser, delta);
    if (target == null) return;
    final notify = userNotifier(context);
    try {
      await ref.read(focuserProvider.notifier).move(target);
    } catch (e) {
      notify("Couldn't move", detail: describeEquipmentError(e), error: true);
    }
  }
}

class _Nudge extends StatelessWidget {
  const _Nudge({required this.icon, required this.label, required this.onTap});

  final IconData icon;
  final String label;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) => Tooltip(
    message: label,
    child: Material(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(8),
        child: SizedBox(
          height: 44,
          child: Icon(
            icon,
            size: 20,
            color: onTap == null
                ? AraColors.textDisabled
                : AraColors.textPrimary,
          ),
        ),
      ),
    ),
  );
}
