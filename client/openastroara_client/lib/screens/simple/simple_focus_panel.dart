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
import 'simple_device_windows.dart' show simpleCelsius;
import 'simple_shell.dart' show SimplePanel, SimpleSegment;

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

/// Whether the focus run's frame, its measured stars circled, fills the screen
/// behind Focus's panels (session only; off to start).
class SimpleFocusStarsShown extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool shown) => state = shown;
}

final simpleFocusStarsShownProvider =
    NotifierProvider<SimpleFocusStarsShown, bool>(SimpleFocusStarsShown.new);

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
    final (headline, headlineColor) = MainFocusCard.headlineFor(
      context.l10n,
      run,
    );
    final hfr = simpleFocusHfr(run);
    final l10n = context.l10n;
    final fit = run.fit;
    final best = run.finalPosition ?? fit?.bestPosition.round();
    final weak = MainFocusCard.weakFitText(l10n, run);
    final starsShown = ref.watch(simpleFocusStarsShownProvider);
    Widget stat(String label, String value) => Expanded(
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
        decoration: BoxDecoration(
          color: AraColors.tileFill,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(label, style: AraText.caption, maxLines: 1),
            Text(value, style: kSimpleText),
          ],
        ),
      ),
    );
    final error =
        live.error ??
        (run.isFailed ? MainFocusCard.failureText(context.l10n, run) : null);
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 360,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            // The method: Smart Focus with the focuser, or by hand with a
            // Bahtinov mask. Locked while a run goes.
            DecoratedBox(
              decoration: BoxDecoration(
                color: AraColors.tileFill,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                children: [
                  for (final (id, m, word) in [
                    ('auto', MainFocusMethod.autofocus, l10n.focusAuto),
                    ('manual', MainFocusMethod.bahtinov, l10n.focusManual),
                  ])
                    Expanded(
                      child: SimpleSegment(
                        key: Key('simple-focus-method-$id'),
                        label: word,
                        selected: simpleFocusMethod(ref) == m,
                        radius: 8,
                        onTap: run.isRunning
                            ? null
                            : () => ref
                                  .read(mainFocusMethodProvider.notifier)
                                  .pick(m),
                      ),
                    ),
                ],
              ),
            ),
            Text(
              headline,
              style: kSimpleText.copyWith(color: headlineColor),
              maxLines: 2,
            ),
            if (connected && focuser != null)
              Text(
                [
                  if (focuser.position != null)
                    l10n.focusFocuserAt('${focuser.position}'),
                  if (focuser.temperature != null)
                    simpleCelsius(focuser.temperature!),
                  if (focuser.name.isNotEmpty) focuser.name,
                ].join(' · '),
                style: AraText.caption,
              ),
            Text(
              context.l10n.focusHfr(hfr?.toStringAsFixed(2) ?? '—'),
              style: kSimpleText.copyWith(
                fontSize: 30,
                fontWeight: FontWeight.w300,
              ),
            ),
            // A finished run's figures, all on view (Simple Mode folds
            // nothing away).
            if (run.isComplete)
              Row(
                spacing: AraSpace.s4,
                children: [
                  stat(l10n.focusBestPosition, best == null ? '—' : '$best'),
                  stat(
                    l10n.focusStars,
                    run.finalStars == null ? '—' : '${run.finalStars}',
                  ),
                  stat(
                    l10n.focusFitR2,
                    fit == null ? '—' : fit.rSquared.toStringAsFixed(2),
                  ),
                ],
              ),
            if (run.probes.isNotEmpty)
              SizedBox(height: 140, child: VCurveChart(run: run)),
            if (weak != null)
              Container(
                padding: const EdgeInsets.all(AraSpace.s8),
                decoration: BoxDecoration(
                  color: AraColors.accentBusy.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Text(weak, style: AraText.caption),
              ),
            // The run's frame with its measured stars circled, full screen
            // behind the panels; off to start.
            if (live.frame != null)
              Row(
                spacing: AraSpace.s12,
                children: [
                  Expanded(
                    child: Text(l10n.focusMeasuredStars, style: kSimpleText),
                  ),
                  DecoratedBox(
                    decoration: BoxDecoration(
                      color: AraColors.tileFill,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        for (final (id, on, word) in [
                          ('on', true, l10n.deviceOn),
                          ('off', false, l10n.deviceOff),
                        ])
                          SimpleSegment(
                            key: Key('simple-focus-stars-$id'),
                            label: word,
                            selected: starsShown == on,
                            radius: 8,
                            onTap: () => ref
                                .read(simpleFocusStarsShownProvider.notifier)
                                .set(on),
                          ),
                      ],
                    ),
                  ),
                ],
              ),
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
