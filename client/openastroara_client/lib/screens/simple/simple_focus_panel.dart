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
import '../../models/focuser_status.dart';
import '../../widgets/focus/bahtinov_focus_card.dart'
    show BahtinovFocusCard, bahtinovHint, formatOffset;
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../state/focus/guide_focus_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../widgets/focus/guide_focus_card.dart'
    show GuideFocusCard, TurnAdvice, guideFocusBlocker, guideFocusHint;
import '../../widgets/focus/v_curve_chart.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/simple_mode_state.dart';
import 'simple_autopilot.dart'
    show AutopilotStep, autopilotNext, autopilotProvider;
import 'simple_preview_panel.dart' show SimpleShutterButton, kSimpleText;
import 'simple_device_windows.dart' show simpleCelsius;
import 'simple_shell.dart' show SimplePanel, SimpleSegment, kSimpleGoStyle;

/// The HFR to show big: the confirmed one after a run, the latest kept probe
/// while one runs, else nothing. Pure — unit-tested.
double? simpleFocusHfr(AutofocusRun run) {
  if (run.isComplete && (run.finalHfr ?? 0) > 0) return run.finalHfr;
  final kept = run.sweepProbes.where((p) => p.kept && p.hfr > 0);
  return kept.isEmpty ? null : kept.last.hfr;
}

/// "Auto", "Manual" or "Guide": the rail chip's caption in Focus mode. For
/// the main telescope the method is the one Smart Focus would use: the user's
/// pick, else autofocus with a focuser connected, by hand without (as
/// `MainFocusCard` decides).
String simpleFocusSummary(WidgetRef ref) =>
    ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide
    ? ref.context.l10n.focusGuide
    : simpleFocusMethod(ref) == MainFocusMethod.autofocus
    ? ref.context.l10n.focusAuto
    : ref.context.l10n.focusManual;

/// Which camera Focus focuses: the main telescope's, or the guide camera's
/// (always by hand, watching its HFR). Session only; the main one to start.
enum SimpleFocusScope { main, guide }

class SimpleFocusScopeNotifier extends Notifier<SimpleFocusScope> {
  @override
  SimpleFocusScope build() => SimpleFocusScope.main;

  void set(SimpleFocusScope scope) => state = scope;
}

final simpleFocusScopeProvider =
    NotifierProvider<SimpleFocusScopeNotifier, SimpleFocusScope>(
      SimpleFocusScopeNotifier.new,
    );

/// The guide camera's live-focus exposures, and the one picked (2 s to
/// start), as the full UI's card offers them.
const kSimpleGuideExposures = [1.0, 2.0, 3.0, 5.0];

class SimpleGuideExposure extends Notifier<double> {
  @override
  double build() => 2;

  void set(double seconds) => state = seconds;
}

final simpleGuideExposureProvider =
    NotifierProvider<SimpleGuideExposure, double>(SimpleGuideExposure.new);

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

/// The Bahtinov readout's exposures in Simple Mode, and the one picked
/// (shared by the panel and the shutter; 2 s to start).
const kSimpleBahtinovExposures = [1.0, 2.0, 4.0, 8.0];

class SimpleBahtinovExposure extends Notifier<double> {
  @override
  double build() => 2;

  void set(double seconds) => state = seconds;
}

final simpleBahtinovExposureProvider =
    NotifierProvider<SimpleBahtinovExposure, double>(
      SimpleBahtinovExposure.new,
    );

/// The rail's shutter in Focus follows the chip: on Auto it starts autofocus
/// and its red square cancels it; on Manual it starts the Bahtinov readout
/// and its square stops it.
class SimpleFocusShutter extends ConsumerWidget {
  const SimpleFocusShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide) {
      // The guide camera: starts and stops its live focus, once it can.
      final guide = ref.watch(guideFocusProvider);
      final active = guide.status.active;
      final notifier = ref.read(guideFocusProvider.notifier);
      final blocked =
          guideFocusBlocker(
            context.l10n,
            ref.watch(guiderStatusProvider).asData?.value,
            active: active,
          ) !=
          null;
      final gated =
          ref.watch(
            phd2SettingsProvider.select((p) => p.guiderSetupType == 'oag'),
          ) &&
          !ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
      return SimpleShutterButton(
        start: true,
        running: active,
        label: active
            ? context.l10n.focusStopGuide
            : context.l10n.focusStartGuide,
        onPressed: guide.busy || (!active && (blocked || gated))
            ? null
            : active
            ? notifier.stop
            : () => notifier.start(
                exposureSec: ref.read(simpleGuideExposureProvider),
              ),
      );
    }
    if (simpleFocusMethod(ref) != MainFocusMethod.autofocus) {
      // Manual: starts and stops the Bahtinov readout.
      final bahtinov = ref.watch(bahtinovFocusProvider);
      final active = bahtinov.status.active;
      final notifier = ref.read(bahtinovFocusProvider.notifier);
      return SimpleShutterButton(
        start: true,
        running: active,
        label: active
            ? context.l10n.focusStopBahtinov
            : context.l10n.focusStartBahtinov,
        onPressed: bahtinov.busy
            ? null
            : active
            ? notifier.stop
            : () => notifier.start(
                exposureSec: ref.read(simpleBahtinovExposureProvider),
              ),
      );
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
class SimpleFocusPanel extends ConsumerStatefulWidget {
  const SimpleFocusPanel({super.key});

  @override
  ConsumerState<SimpleFocusPanel> createState() => _SimpleFocusPanelState();
}

class _SimpleFocusPanelState extends ConsumerState<SimpleFocusPanel> {
  // The mask-off question, asked in the panel (Simple Mode has no popups):
  // by Finish, or by leaving Manual before finishing ([_thenAuto]).
  bool _askMask = false;
  bool _thenAuto = false;

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(autofocusLiveProvider);
    final bahtinov = ref.watch(bahtinovFocusProvider);
    final method = simpleFocusMethod(ref);
    final guide = ref.watch(guideFocusProvider);
    final scope = ref.watch(simpleFocusScopeProvider);
    final locked =
        live.run.isRunning || bahtinov.status.active || bahtinov.busy;
    final anyRunning = locked || guide.status.active || guide.busy;
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
    // The full UI's verdict, cut to what to do (the panel has no room for
    // the why).
    final weak = MainFocusCard.weakFitText(l10n, run) == null
        ? null
        : l10n.focusWeakFitShort(fit!.rSquared.toStringAsFixed(2));
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
        width: 400,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 6,
          children: [
            // Which camera, then how to focus the main one, on one line
            // (the panel has no room for two rows).
            Row(
              spacing: AraSpace.s8,
              children: [
                Expanded(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      color: AraColors.tileFill,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Row(
                      children: [
                        for (final (id, sc, word) in [
                          ('main', SimpleFocusScope.main, l10n.focusScopeMain),
                          (
                            'guide',
                            SimpleFocusScope.guide,
                            l10n.focusScopeGuide,
                          ),
                        ])
                          Expanded(
                            child: SimpleSegment(
                              key: Key('simple-focus-scope-$id'),
                              label: word,
                              selected: scope == sc,
                              radius: 8,
                              onTap: anyRunning || scope == sc
                                  ? null
                                  : () => ref
                                        .read(simpleFocusScopeProvider.notifier)
                                        .set(sc),
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
                // The method: Smart Focus with the focuser, or by hand with a
                // Bahtinov mask. Locked while a run goes.
                if (scope == SimpleFocusScope.main)
                  Expanded(
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: AraColors.tileFill,
                        borderRadius: BorderRadius.circular(8),
                      ),
                      child: Row(
                        children: [
                          for (final (id, m, word) in [
                            ('auto', MainFocusMethod.autofocus, l10n.focusAuto),
                            (
                              'manual',
                              MainFocusMethod.bahtinov,
                              l10n.focusManual,
                            ),
                          ])
                            Expanded(
                              child: SimpleSegment(
                                key: Key('simple-focus-method-$id'),
                                label: word,
                                selected: method == m,
                                radius: 8,
                                onTap: locked || method == m
                                    ? null
                                    : () {
                                        if (m == MainFocusMethod.autofocus &&
                                            bahtinov.status.hasMeasurement &&
                                            !bahtinov.finished) {
                                          setState(
                                            () => _askMask = _thenAuto = true,
                                          );
                                          return;
                                        }
                                        ref
                                            .read(
                                              mainFocusMethodProvider.notifier,
                                            )
                                            .pick(m);
                                      },
                              ),
                            ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
            if (scope == SimpleFocusScope.guide)
              ..._guide(l10n, guide)
            else if (_askMask)
              ..._maskQuestion(l10n)
            else if (method == MainFocusMethod.bahtinov)
              ..._bahtinov(l10n, bahtinov, connected ? focuser : null)
            else ...[
              Text(
                headline,
                style: kSimpleText.copyWith(color: headlineColor),
                maxLines: 2,
              ),
              // A finished run's Best position tile says where it is.
              if (connected && focuser != null && !run.isComplete)
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
              // A finished run's headline already says its HFR.
              if (!run.isComplete)
                Text(
                  context.l10n.focusHfr(hfr?.toStringAsFixed(2) ?? '—'),
                  style: kSimpleText.copyWith(
                    fontSize: 30,
                    fontWeight: FontWeight.w300,
                  ),
                ),
              // Guided Run with focus the last step (a permanent pier, or
              // aligned already): its Done starts imaging, never the run's
              // end on its own.
              if (ref.watch(autopilotProvider) == AutopilotStep.focus &&
                  ref.watch(setupFocusStateProvider) == SetupStepState.done &&
                  autopilotNext(
                        after: AutopilotStep.focus,
                        focused: true,
                        aligned:
                            ref.watch(setupPolarAlignStateProvider) ==
                            SetupStepState.done,
                        permanentPier:
                            ref.watch(simplePermanentPierProvider).value ??
                            false,
                      ) ==
                      null)
                FilledButton(
                  key: const Key('simple-focus-guided-done'),
                  style: kSimpleGoStyle,
                  onPressed: ref.read(autopilotProvider.notifier).next,
                  child: Text(l10n.guidedDoneStartImaging),
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
                SizedBox(height: 90, child: VCurveChart(run: run)),
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
          ],
        ),
      ),
    );
  }

  List<Widget> _maskQuestion(AppLocalizations l10n) => [
    Text(l10n.bahtinovMaskQuestion, style: kSimpleText),
    Text(l10n.bahtinovMaskQuestionDetail, style: AraText.caption),
    Row(
      spacing: AraSpace.s8,
      children: [
        Expanded(
          child: OutlinedButton(
            onPressed: () => setState(() => _askMask = _thenAuto = false),
            child: Text(l10n.bahtinovNotYet),
          ),
        ),
        Expanded(
          child: FilledButton(
            style: kSimpleGoStyle,
            onPressed: () async {
              final thenAuto = _thenAuto;
              setState(() => _askMask = _thenAuto = false);
              await ref.read(bahtinovFocusProvider.notifier).finish();
              if (thenAuto && mounted) {
                ref
                    .read(mainFocusMethodProvider.notifier)
                    .pick(MainFocusMethod.autofocus);
              }
            },
            child: Text(l10n.bahtinovMaskRemoved),
          ),
        ),
      ],
    ),
  ];

  /// Manual: the full UI's Bahtinov readout, flat. The shutter starts and
  /// stops it; the Focuser window's dial (or the hand) turns the focuser.
  List<Widget> _bahtinov(
    AppLocalizations l10n,
    BahtinovFocusLive live,
    FocuserStatus? focuser,
  ) {
    final status = live.status;
    final (headline, colour) = BahtinovFocusCard.headlineFor(l10n, live);
    final steady = status.steady;
    final reading = status.active && steady != null && steady.detected;
    final hint = status.active ? bahtinovHint(l10n, status) : null;
    final exposure = ref.watch(simpleBahtinovExposureProvider);
    final measured = status.hasMeasurement && !live.finished;
    return [
      Text(headline, style: kSimpleText.copyWith(color: colour), maxLines: 2),
      if (focuser != null)
        Text(
          [
            if (focuser.position != null)
              l10n.focusFocuserAt('${focuser.position}'),
            if (focuser.name.isNotEmpty) focuser.name,
          ].join(' · '),
          style: AraText.caption,
        ),
      if (!status.active && !status.hasMeasurement && !live.finished)
        Text(l10n.bahtinovFitMaskDetail, style: AraText.caption),
      if (reading) ...[
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.baseline,
          textBaseline: TextBaseline.alphabetic,
          spacing: AraSpace.s8,
          children: [
            Text(
              formatOffset(steady.offsetPx!.abs()),
              style: kSimpleText.copyWith(
                fontSize: 40,
                fontWeight: FontWeight.w300,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
            Text(l10n.bahtinovPxFromFocus, style: AraText.caption),
          ],
        ),
      ],
      if (hint != null) ...[
        Text(
          hint.title,
          textAlign: TextAlign.center,
          style: kSimpleText.copyWith(
            fontWeight: FontWeight.w600,
            color: switch (hint.advice) {
              TurnAdvice.atBest => AraColors.accentConnected,
              TurnAdvice.turnBack => AraColors.accentBusy,
              _ => AraColors.textPrimary,
            },
          ),
        ),
        Text(hint.detail, textAlign: TextAlign.center, style: AraText.caption),
      ],
      if (live.error != null)
        Text(
          live.error!,
          style: kSimpleText.copyWith(color: AraColors.errorText),
          maxLines: 3,
        ),
      if (!live.finished)
        Row(
          spacing: AraSpace.s12,
          children: [
            Expanded(child: Text(l10n.bahtinovExposure, style: kSimpleText)),
            DecoratedBox(
              decoration: BoxDecoration(
                color: AraColors.tileFill,
                borderRadius: BorderRadius.circular(8),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  for (final s in kSimpleBahtinovExposures)
                    SimpleSegment(
                      key: Key('simple-bahtinov-exposure-${s.round()}'),
                      label: '${s.round()} s',
                      selected: exposure == s,
                      radius: 8,
                      // A running readout keeps its exposure; Start again
                      // takes the new one.
                      onTap: status.active
                          ? null
                          : () => ref
                                .read(simpleBahtinovExposureProvider.notifier)
                                .set(s),
                    ),
                ],
              ),
            ),
          ],
        ),
      if (measured)
        FilledButton(
          key: const Key('simple-bahtinov-finish'),
          style: steady?.withinZone == true ? kSimpleGoStyle : null,
          onPressed: live.busy ? null : () => setState(() => _askMask = true),
          child: Text(l10n.bahtinovFinish),
        ),
    ];
  }

  /// The guide camera, focused by hand: the full UI's guide-focus readout,
  /// flat. The shutter starts and stops it; its frames fill the screen.
  List<Widget> _guide(AppLocalizations l10n, GuideFocusLive live) {
    final status = live.status;
    final guider = ref.watch(guiderStatusProvider).asData?.value;
    final oag = ref.watch(
      phd2SettingsProvider.select((p) => p.guiderSetupType == 'oag'),
    );
    final gated =
        oag &&
        !ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
    final blocker = guideFocusBlocker(l10n, guider, active: status.active);
    final (headline, colour) = GuideFocusCard.headlineFor(
      l10n,
      status,
      gated: gated,
      blocked: blocker != null,
    );
    final latest = status.latest;
    final hint = status.active ? guideFocusHint(l10n, status) : null;
    final exposure = ref.watch(simpleGuideExposureProvider);
    Widget fact(String label, String value) => Expanded(
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
    return [
      Text(headline, style: kSimpleText.copyWith(color: colour), maxLines: 2),
      Text(
        oag ? l10n.focusGuideOag : l10n.focusGuideScope,
        style: AraText.caption,
      ),
      if (gated && !status.active) ...[
        Text(l10n.focusGuideMainFirst, style: AraText.caption),
        OutlinedButton(
          key: const Key('simple-guide-already-focused'),
          onPressed: () =>
              ref.read(autofocusLiveProvider.notifier).markFocusedManually(),
          child: Text(l10n.focusAlreadyInFocus),
        ),
      ],
      if (blocker != null) Text(blocker, style: AraText.caption),
      if (latest != null)
        Row(
          spacing: AraSpace.s4,
          children: [
            fact(l10n.focusStars, '${latest.stars}'),
            fact(l10n.focusFrames, '${status.seq}'),
          ],
        ),
      if (hint != null) ...[
        Text(
          hint.title,
          textAlign: TextAlign.center,
          style: kSimpleText.copyWith(
            fontWeight: FontWeight.w600,
            color: switch (hint.advice) {
              TurnAdvice.atBest => AraColors.accentConnected,
              TurnAdvice.turnBack => AraColors.accentBusy,
              _ => AraColors.textPrimary,
            },
          ),
        ),
        Text(hint.detail, textAlign: TextAlign.center, style: AraText.caption),
      ],
      if (live.error ?? status.error case final error?)
        Text(
          error,
          style: kSimpleText.copyWith(color: AraColors.errorText),
          maxLines: 3,
        ),
      Row(
        spacing: AraSpace.s12,
        children: [
          Expanded(child: Text(l10n.bahtinovExposure, style: kSimpleText)),
          DecoratedBox(
            decoration: BoxDecoration(
              color: AraColors.tileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                for (final s in kSimpleGuideExposures)
                  SimpleSegment(
                    key: Key('simple-guide-exposure-${s.round()}'),
                    label: '${s.round()} s',
                    selected: exposure == s,
                    radius: 8,
                    onTap: status.active
                        ? null
                        : () => ref
                              .read(simpleGuideExposureProvider.notifier)
                              .set(s),
                  ),
              ],
            ),
          ),
        ],
      ),
    ];
  }
}
