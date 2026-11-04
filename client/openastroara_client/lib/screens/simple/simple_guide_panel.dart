import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import '../../util/friendly_error.dart';
import 'simple_autopilot.dart'
    show AutopilotStep, autopilotProvider, simpleAutopilotNext;
import 'simple_device_windows.dart'
    show
        SimpleGuideGainSlider,
        simpleSaveGuider,
        simpleWindowRow,
        simpleWindowStepper;
import 'simple_preview_panel.dart'
    show SimplePresetTile, SimpleShutterButton, formatExposure, kSimpleText;
import 'simple_shell.dart' show SimplePanel, kSimpleGoStyle;

/// Guide mode: the guider's routine (loop, pick a star, calibrate when
/// needed, settle, guide) runs from the shutter; its red square stops it.

/// The guider is doing something the shutter's square stops: looping,
/// calibrating, guiding, settling. Pure — unit-tested.
bool simpleGuideActive(GuiderStatus? g) =>
    g != null &&
    g.isConnected &&
    !const {
      GuiderRuntimeState.stopped,
      GuiderRuntimeState.unknown,
    }.contains(g.runtimeState);

/// Guiding, or about to be back at it (a dither's settle): what Guided
/// Run's Guide step waits for. Pure — unit-tested.
bool simpleGuiding(GuiderStatus? g) =>
    g != null &&
    g.isConnected &&
    const {
      GuiderRuntimeState.guiding,
      GuiderRuntimeState.settling,
      GuiderRuntimeState.dithering,
    }.contains(g.runtimeState);

String _arcsec(double v) => '${v.toStringAsFixed(2)}″';

/// A Start (true) or Stop (false) asked for and not shown by the guider yet:
/// said at once, since the guider takes seconds (a stop waits out its frame).
class SimpleGuiderPending extends Notifier<bool?> {
  @override
  bool? build() => null;
  void set(bool? v) => state = v;
}

final simpleGuiderPendingProvider =
    NotifierProvider<SimpleGuiderPending, bool?>(SimpleGuiderPending.new);

/// The guider's dot: the top bar's colour (a lost star red, settling amber),
/// grey without a guider.
Color simpleGuiderColour(GuiderStatus? g) => g == null || !g.isConnected
    ? AraColors.textDisabled
    : guiderStatusLevel(g).color;


/// The rail chip in Guide mode: the RMS while it guides, else Off.
String simpleGuideSummary(WidgetRef ref) {
  final g = ref.watch(guiderStatusProvider).value;
  final rms = g?.rmsTotalArcsec;
  return simpleGuiding(g) && rms != null
      ? _arcsec(rms)
      : ref.context.l10n.deviceOff;
}

class SimpleGuideShutter extends ConsumerWidget {
  const SimpleGuideShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final g = ref.watch(guiderStatusProvider).value;
    final pending = ref.watch(simpleGuiderPendingProvider);
    // An ask in flight shows as done already, and takes no second tap.
    final active = pending ?? simpleGuideActive(g);
    final l10n = context.l10n;
    return SimpleShutterButton(
      start: true,
      running: active,
      label: pending != null
          ? (pending ? l10n.guideStarting : l10n.guideStopping)
          : active
          ? l10n.guideStop
          : l10n.guideStart,
      onPressed: g == null || !g.isConnected || pending != null
          ? null
          : () => simpleGuideRun(context, ref, start: !active),
    );
  }
}

/// Starts (the whole routine) or stops guiding; a refusal says why in a pill.
Future<void> simpleGuideRun(
  BuildContext context,
  WidgetRef ref, {
  required bool start,
}) async {
  final notify = userNotifier(context);
  final l10n = context.l10n;
  final guider = ref.read(guiderStatusProvider.notifier);
  final pending = ref.read(simpleGuiderPendingProvider.notifier);
  pending.set(start);
  try {
    await (start ? guider.startGuiding() : guider.stopGuiding());
    // The guider answers the ask at once and acts a moment later: read again
    // every half second until it shows, 10 s at most.
    for (var i = 0; i < 20; i++) {
      if (simpleGuideActive(ref.read(guiderStatusProvider).value) == start) {
        break;
      }
      await Future<void>.delayed(const Duration(milliseconds: 500));
      if (!context.mounted) return;
      await guider.refresh();
    }
  } catch (e) {
    notify(
      friendlyError(
        l10n,
        e,
        action: start ? l10n.guideActionStart : l10n.guideActionStop,
      ),
      error: true,
      snackBarRed: false,
    );
  } finally {
    if (context.mounted) pending.set(null);
  }
}

/// Guide mode's panel: what the guider is doing, the RMS, and how often
/// the plans dither.
class SimpleGuidePanel extends ConsumerWidget {
  const SimpleGuidePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final g = ref.watch(guiderStatusProvider).value;
    final connected = g != null && g.isConnected;
    final active = simpleGuideActive(g);
    final guiding = simpleGuiding(g);
    final pending = ref.watch(simpleGuiderPendingProvider);
    final colour = pending != null
        ? AraColors.accentBusy
        : simpleGuiderColour(g);
    final phd2 = ref.watch(phd2SettingsProvider);
    final every = phd2.ditherEnabled ? math.max(1, phd2.ditherEveryNFrames) : 0;
    final px = phd2.ditherPixels;
    final total = guiding ? g!.rmsTotalArcsec : null;
    final ra = guiding ? g!.rmsRaArcsec : null;
    final dec = guiding ? g!.rmsDecArcsec : null;
    // Guided Run's Guide step, the last before imaging: its Done starts it.
    final guidedDone =
        guiding &&
        ref.watch(autopilotProvider) == AutopilotStep.guide &&
        simpleAutopilotNext(ref, after: AutopilotStep.guide) == null;
    return SimplePanel(
      key: const Key('simple-guide-panel'),
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s16,
        vertical: AraSpace.s12,
      ),
      child: SizedBox(
        width: 340,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 4,
          children: [
            Row(
              spacing: AraSpace.s8,
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(
                    color: colour,
                    shape: BoxShape.circle,
                  ),
                ),
                Expanded(
                  child: Text(
                    !connected
                        ? l10n.guideNoGuider
                        : pending != null
                        ? (pending ? l10n.guideStarting : l10n.guideStopping)
                        : active
                        ? g.runtimeSummary(l10n)
                        : l10n.guideNotGuiding,
                    style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                  ),
                ),
              ],
            ),
            if (connected && !active && pending == null)
              Text(l10n.guideTapShutter, style: AraText.caption),
            if (!connected)
              Text(l10n.guideConnectFirst, style: AraText.caption),
            simpleWindowRow(
              l10n.deviceRms,
              id: 'RMS',
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    total == null ? '—' : _arcsec(total),
                    style: kSimpleText.copyWith(
                      fontSize: 30,
                      fontWeight: FontWeight.w300,
                    ),
                  ),
                  if (ra != null && dec != null)
                    Text(
                      l10n.deviceRmsRaDec(_arcsec(ra), _arcsec(dec)),
                      style: AraText.caption,
                    ),
                ],
              ),
            ),
            if (guidedDone)
              FilledButton(
                key: const Key('simple-guide-guided-done'),
                style: kSimpleGoStyle,
                onPressed: ref.read(autopilotProvider.notifier).next,
                child: Text(l10n.guidedDoneStartImaging),
              ),
            const Divider(height: AraSpace.s16),
            // The guide camera's exposure, picked as Preview's; set live.
            SimplePresetTile<int>(
              key: const Key('simple-guide-exposure'),
              label: l10n.previewExposure,
              value: phd2.guideExposureMs,
              options: [
                for (final ms in guideExposureChoicesMs)
                  if (ms >= 200 && ms <= 15000) ms,
              ],
              format: (ms) => ms <= 0
                  ? l10n.guideExposureOwn
                  : formatExposure(Duration(milliseconds: ms)),
              onSelected: (ms) => simpleSaveGuider(
                context,
                ref,
                (p) => p.setGuideExposureMs(ms),
                live: true,
              ),
            ),
            const SimpleGuideGainSlider(),
            simpleWindowRow(
              l10n.deviceDither,
              id: 'Dither',
              simpleWindowStepper(
                id: 'simple-guider-dither',
                text: switch (every) {
                  0 => l10n.deviceOff,
                  _ => l10n.deviceDitherEveryFrames(every),
                },
                onLess: () => _setDither(context, ref, every - 1),
                onMore: () => _setDither(context, ref, every + 1),
              ),
            ),
            if (every > 0)
              simpleWindowRow(
                l10n.deviceDitherBy,
                id: 'Dither by',
                simpleWindowStepper(
                  id: 'simple-guider-dither-px',
                  text: '${px == px.roundToDouble() ? px.round() : px} px',
                  onLess: () => px > 1
                      ? _save(context, ref, (p) => p.setDitherPixels(px - 1))
                      : null,
                  onMore: () => px < 30
                      ? _save(context, ref, (p) => p.setDitherPixels(px + 1))
                      : null,
                ),
              ),
          ],
        ),
      ),
    );
  }

  /// 0 turns dithering off; 1 and up dithers every that many frames.
  static void _setDither(BuildContext context, WidgetRef ref, int n) {
    if (n < 0 || n > 20) return;
    _save(context, ref, (p) {
      p.setDitherEnabled(n > 0);
      if (n > 0) p.setDitherEveryNFrames(n);
    });
  }

  static Future<void> _save(
    BuildContext context,
    WidgetRef ref,
    void Function(Phd2SettingsNotifier) change,
  ) => simpleSaveGuider(context, ref, change);
}
