import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/autofocus_run.dart';
import '../../models/equipment_device_status.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../widgets/focus/v_curve_chart.dart';
import 'simple_preview_panel.dart'
    show SimpleShutter, SimpleShutterButton, kSimpleText;
import 'simple_shell.dart' show SimplePanel;

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
String simpleFocusSummary(WidgetRef ref) =>
    simpleFocusMethod(ref) == MainFocusMethod.autofocus
    ? ref.context.l10n.focusAuto
    : ref.context.l10n.focusManual;

/// Autofocus with the focuser connected, the Bahtinov mask without, unless
/// one was picked.
MainFocusMethod simpleFocusMethod(WidgetRef ref) {
  final connected =
      ref.watch(focuserProvider).asData?.value?.connectionState ==
      EquipmentConnectionState.connected;
  return ref.watch(mainFocusMethodProvider) ??
      (connected ? MainFocusMethod.autofocus : MainFocusMethod.bahtinov);
}

/// The rail's shutter in Focus follows the chip: on Auto it starts autofocus
/// and its red square cancels it; on Manual it is Preview's, frames to focus
/// by.
class SimpleFocusShutter extends ConsumerWidget {
  const SimpleFocusShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (simpleFocusMethod(ref) != MainFocusMethod.autofocus) {
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
      label: running
          ? context.l10n.focusCancelAutofocus
          : context.l10n.focusAutofocus,
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
              context.l10n.focusHfr(hfr?.toStringAsFixed(2) ?? '—'),
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
            // The Focuser window's dial moves it; nothing to nudge here.
            if (!connected)
              Text(
                context.l10n.focusNoFocuser,
                style: kSimpleText.copyWith(color: AraColors.textSecondary),
              ),
          ],
        ),
      ),
    );
  }
}
