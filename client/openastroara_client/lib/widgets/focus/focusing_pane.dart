import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/autofocus_run.dart';
import '../../models/equipment_device_status.dart';
import '../../models/focuser_status.dart';
import '../../models/guide_focus.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/focus/guide_focus_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../fit_pane.dart';
import 'bahtinov_focus_card.dart';
import 'collimation_dial.dart';
import 'focus_frame_view.dart';
import 'focus_section.dart';
import 'guide_focus_card.dart';
import 'v_curve_chart.dart';
import '../../util/friendly_error.dart';

/// Setup → Smart Focus: two tabs, Main telescope then Guide camera — the order is
/// the order of work (an OAG only focuses once the main scope has), and each
/// instrument gets the whole pane. The main tab carries a check once the scope
/// is focused this session.
class FocusingPane extends ConsumerStatefulWidget {
  const FocusingPane({super.key});

  @override
  ConsumerState<FocusingPane> createState() => _FocusingPaneState();
}

class _FocusingPaneState extends ConsumerState<FocusingPane>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 2, vsync: this);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      ref.read(autofocusLiveProvider.notifier).refresh();
    });
  }

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final focused = ref.watch(
        autofocusLiveProvider.select((s) => s.focusedThisSession));
    final running = ref.watch(autofocusLiveProvider.select((s) => s.run.isRunning));
    final guideLive = ref.watch(guideFocusProvider.select((s) => s.status.active));
    final guideFocused = ref.watch(guideFocusProvider.select((s) => s.status.focusedThisSession));
    // A short window puts the title beside the tabs, drops the subtitle and
    // tightens the padding so the instruments keep their height.
    final short = AraBreakpoints.isShort(context);
    final title = Text('Smart Focus', style: theme.textTheme.titleLarge);
    final tabs = TabBar(
      controller: _tabs,
      isScrollable: true,
      tabAlignment: TabAlignment.start,
      dividerColor: AraColors.border,
      tabs: [
        _StepTab(
          label: 'Main telescope',
          done: focused,
          busy: running,
          height: short ? 40 : null,
        ),
        _StepTab(label: 'Guide camera', busy: guideLive, done: guideFocused, height: short ? 40 : null),
      ],
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Padding(
          padding: short
              ? const EdgeInsets.fromLTRB(20, 4, 20, 0)
              : const EdgeInsets.fromLTRB(28, 24, 28, 0),
          child: short
              ? Row(children: [title, const SizedBox(width: 24), Expanded(child: tabs)])
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    title,
                    const SizedBox(height: 4),
                    Text(
                      'Main telescope first, then the guide camera.',
                      style: theme.textTheme.bodyMedium
                          ?.copyWith(color: AraColors.textSecondary),
                    ),
                    const SizedBox(height: 12),
                    tabs,
                  ],
                ),
        ),
        Expanded(
          child: TabBarView(
            controller: _tabs,
            children: const [
              _TabPage(child: MainFocusCard()),
              _TabPage(child: GuideFocusCard()),
            ],
          ),
        ),
      ],
    );
  }
}

/// A tab with a step glyph: check once done, a pulse while busy.
class _StepTab extends StatelessWidget {
  final String label;
  final bool done;
  final bool busy;
  final double? height;
  const _StepTab({required this.label, this.done = false, this.busy = false, this.height});

  @override
  Widget build(BuildContext context) {
    return Tab(
      height: height,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (busy)
            const SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          else
            Icon(
              done ? Icons.check_circle : Icons.circle_outlined,
              size: 16,
              color: done ? AraColors.accentConnected : AraColors.textDisabled,
            ),
          const SizedBox(width: 8),
          Text(label),
        ],
      ),
    );
  }
}

class _TabPage extends StatelessWidget {
  final Widget child;
  const _TabPage({required this.child});

  @override
  Widget build(BuildContext context) {
    // No width cap: the pane fills whatever the window gives it, and the
    // instruments take the height the card leaves them.
    return FitPane(
      padding: AraBreakpoints.isShort(context)
          ? const EdgeInsets.fromLTRB(20, 12, 20, 12)
          : const EdgeInsets.fromLTRB(28, 20, 28, 28),
      child: child,
    );
  }
}

/// Main telescope: run / cancel autofocus and watch the run.
/// #1542 — move the focuser by hand from the pane: nudge in or out by a chosen
/// step, and stop a move in progress.
class ManualFocusControl extends ConsumerStatefulWidget {
  const ManualFocusControl({super.key, required this.focuser});

  final FocuserStatus focuser;

  @override
  ConsumerState<ManualFocusControl> createState() => _ManualFocusControlState();
}

class _ManualFocusControlState extends ConsumerState<ManualFocusControl> {
  int _step = 50;

  Future<void> _command(Future<bool> Function(FocuserNotifier) send, String failure) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    try {
      await send(ref.read(focuserProvider.notifier));
    } catch (e) {
      messenger.showSnackBar(SnackBar(content: Text('$failure: ${failureReason(l10n, e)}')));
    }
  }

  void _nudge(int direction) {
    final caps = widget.focuser.capabilities;
    var target = widget.focuser.position! + direction * _step;
    if (target < (caps?.minPosition ?? 0)) target = caps?.minPosition ?? 0;
    // A device that reports no travel has a max of 0: no upper clamp then.
    if (caps != null && caps.maxPosition > 0 && target > caps.maxPosition) target = caps.maxPosition;
    _command((n) => n.move(target), context.l10n.focusMoveFailed);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final moving = widget.focuser.isMoving;
    final planRunning = ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final autofocusRunning = ref.watch(autofocusLiveProvider.select((l) => l.run.isRunning));
    final canNudge = !moving && !planRunning && !autofocusRunning && widget.focuser.position != null;
    return Wrap(
      spacing: 8,
      runSpacing: 8,
      crossAxisAlignment: WrapCrossAlignment.center,
      children: [
        SegmentedButton<int>(
          segments: [
            for (final steps in const [10, 50, 200])
              ButtonSegment(value: steps, label: Text('$steps'), tooltip: l10n.focusNudgeSteps(steps)),
          ],
          selected: {_step},
          showSelectedIcon: false,
          onSelectionChanged: (s) => setState(() => _step = s.single),
        ),
        IconButton.outlined(
          key: const Key('manual-focus-in'),
          tooltip: l10n.focusNudgeIn,
          onPressed: canNudge ? () => _nudge(-1) : null,
          icon: const Icon(Icons.remove),
        ),
        IconButton.outlined(
          key: const Key('manual-focus-out'),
          tooltip: l10n.focusNudgeOut,
          onPressed: canNudge ? () => _nudge(1) : null,
          icon: const Icon(Icons.add),
        ),
        FilledButton.tonalIcon(
          key: const Key('manual-focus-stop'),
          onPressed: moving ? () => _command((n) => n.halt(), l10n.focusStopFailed) : null,
          icon: const Icon(Icons.stop_rounded, size: 18),
          label: Text(l10n.focusNudgeStop),
        ),
        if (planRunning || autofocusRunning)
          Text(
            planRunning ? l10n.focusNudgePlanRunning : l10n.focusNudgeAutofocusRunning,
            style: Theme.of(context).textTheme.bodySmall,
          ),
      ],
    );
  }
}

class MainFocusCard extends ConsumerWidget {
  const MainFocusCard({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(autofocusLiveProvider);
    final focuser = ref.watch(focuserProvider).asData?.value;
    final focuserConnected =
        focuser?.connectionState == EquipmentConnectionState.connected;
    // #1299 — Autofocus or Bahtinov mask: the user's pick, else the rig's
    // (no focuser to drive means a sweep can't run). A readout already running
    // (another client, a reconnect) shows itself. Simple Mode reads the same.
    final method = ref.watch(effectiveFocusMethodProvider);
    final selector = MainFocusMethodSelector(method: method);
    if (method == MainFocusMethod.bahtinov) {
      return BahtinovFocusCard(
        methodSelector: selector,
        manualFocus: focuserConnected ? ManualFocusControl(focuser: focuser!) : null,
      );
    }
    final run = live.run;
    final apiAvailable = ref.watch(autofocusApiProvider) != null;
    final hasData = run.probes.any((p) => p.kept) || run.fit != null || live.frame != null;

    final subheadParts = <String>[
      if (focuser != null && focuserConnected) ...[
        'Focuser at ${focuser.position ?? '—'}',
        if (focuser.temperature != null && focuser.temperature!.isFinite)
          '${focuser.temperature!.toStringAsFixed(1)} °C',
        focuser.name,
      ] else
        'No focuser connected',
    ];

    final action = run.isRunning
        ? FilledButton.tonalIcon(
            onPressed: live.busy
                ? null
                : () => ref.read(autofocusLiveProvider.notifier).cancel(),
            icon: const Icon(Icons.stop_rounded, size: 18),
            label: const Text('Cancel'),
          )
        : FilledButton.icon(
            onPressed: focuserConnected && apiAvailable && !live.busy
                ? () => ref.read(autofocusLiveProvider.notifier).start()
                : null,
            icon: live.busy
                ? const SizedBox(
                    width: 14, height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.center_focus_strong, size: 18),
            label: Text(run.isComplete ? 'Run again' : 'Run autofocus'),
          );

    final l10n = context.l10n;
    final (headline, headlineColor) = headlineFor(l10n, run);

    return FocusSection(
      title: 'Main telescope',
      helpKey: 'setup.focusing.main',
      titleTrailing: selector,
      headline: headline,
      headlineColor: headlineColor,
      subhead: subheadParts.join(' · '),
      action: action,
      secondaryAction: IconButton(
        tooltip: 'Autofocus settings',
        onPressed: () => openSettingsPanel(ref, 'img.autofocus'),
        icon: const Icon(Icons.tune, size: 20),
      ),
      child: FitColumn(
        children: [
          if (live.error != null) ...[
            InlineNotice.error(failureReason(context.l10n, live.error!)),
            const SizedBox(height: 16),
          ],
          if (run.isFailed) ...[
            InlineNotice.error(failureText(l10n, run)),
            const SizedBox(height: 16),
          ],
          if (weakFitText(context.l10n, run) case final weak?) ...[
            InlineNotice.warning(weak),
            const SizedBox(height: 16),
          ],
          if (focuserConnected && !run.isRunning) ...[
            ManualFocusControl(focuser: focuser!),
            const SizedBox(height: 16),
          ],
          if (run.isCancelled) ...[
            InlineNotice.info(
              'Autofocus was cancelled.'
              '${run.restoredPosition != null ? ' The focuser is back at ${run.restoredPosition}.' : ''}',
            ),
            const SizedBox(height: 16),
          ],
          if (run.isRunning) ...[
            ClipRRect(
              borderRadius: BorderRadius.circular(2),
              child: LinearProgressIndicator(
                value: run.phase == 'coarse' || run.phase == 'smart' || run.totalSteps == 0
                    ? null
                    : (run.completedSteps / run.totalSteps).clamp(0.0, 1.0),
                minHeight: 3,
              ),
            ),
            const SizedBox(height: 16),
          ],
          if (!hasData && !run.isRunning) ...[
            FitFill(child: EmptyState(
              icon: Icons.center_focus_weak,
              title: run.isFailed || run.isCancelled ? 'Nothing measured' : 'No autofocus yet',
              message: run.isFailed || run.isCancelled
                  ? 'No probe found enough stars to plot. Check the sky and the exposure, rough-focus by hand, then run again.'
                  : focuserConnected
                      ? 'Run autofocus to see the V-curve, the statistics and the focused field.'
                      : 'Connect a focuser, then run autofocus to see the V-curve, the statistics and the focused field.',
            )),
            if (run.state != AutofocusRunStates.idle)
              DetailsDisclosure(rows: detailsFor(run)),
          ] else ...[
            StatRow(tiles: tilesFor(run)),
            // §59.10 / #1139 — the collimation verdict the sweep produced, as a
            // donut picture; obstructed scopes only, nothing until one lands.
            if (live.collimation case final verdict? when !run.isRunning) ...[
              SizedBox(height: AraBreakpoints.isShort(context) ? 8 : 12),
              CollimationCard(verdict: verdict),
            ],
            SizedBox(height: AraBreakpoints.isShort(context) ? 12 : 16),
            FitFill(child: FitBand(builder: (context, width, height) {
              final chart = VCurveChart(run: run);
              Widget frame(double h) => FocusFrameView(
                frame: live.frame,
                height: h,
                badge: run.isComplete ? 'FOCUSED' : run.isRunning ? 'PROBE' : null,
                badgeColor: run.isComplete
                    ? AraColors.accentConnected
                    : AraColors.accentInfo,
                caption: run.framePosition == null
                    ? null
                    : 'at ${run.framePosition}${run.frameHfr != null && run.frameHfr! > 0 ? ' · HFR ${run.frameHfr!.toStringAsFixed(2)}' : ''}',
                emptyText: run.isRunning
                    ? 'The first measurable probe appears here.'
                    : 'No frame from this run.',
              );
              if (width >= AraBreakpoints.sideBySide(context)) {
                // The band takes the height the window leaves. The frame stays
                // 4:3 and takes up to half the width; past that the band stops
                // growing rather than turn into a billboard.
                final band = math.max(height, 160.0);
                final frameWidth = math.min(band * 4 / 3, (width - 16) / 2);
                final h = math.min(band, frameWidth * 0.75);
                return Align(
                  alignment: Alignment.topCenter,
                  child: SizedBox(
                    height: h,
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Expanded(child: chart),
                        const SizedBox(width: 16),
                        SizedBox(width: frameWidth, child: frame(h)),
                      ],
                    ),
                  ),
                );
              }
              // Narrow: stacked, each half the band; the frame no taller than
              // its 4:3 at full width.
              final each = math.max((height - 12) / 2, 180.0);
              return Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  SizedBox(height: each, child: chart),
                  const SizedBox(height: 12),
                  frame(math.min(each, width * 0.75)),
                ],
              );
            })),
            const SizedBox(height: 8),
            DetailsDisclosure(rows: detailsFor(run)),
          ],
        ],
      ),
    );
  }

  /// The headline in words + its colour. Pure — unit-tested.
  static (String, Color?) headlineFor(AppLocalizations l, AutofocusRun run) {
    switch (run.state) {
      case AutofocusRunStates.running:
        return (phaseText(l, run), AraColors.accentInfo);
      case AutofocusRunStates.complete:
        final hfr = run.finalHfr;
        return (
          hfr != null && hfr > 0 ? l.focusCardInFocusHfr(hfr.toStringAsFixed(2)) : l.focusCardInFocus,
          AraColors.accentConnected,
        );
      case AutofocusRunStates.failed:
        return (l.focusCardFailed, AraColors.accentError);
      case AutofocusRunStates.cancelled:
        return (l.focusCardCancelled, null);
      default:
        return (l.focusCardNotFocused, null);
    }
  }

  /// A completed run whose fit explained the probes poorly. Seen on the rig:
  /// a narrow V with flat wings — the outer probes sit on a plateau around
  /// HFR 4 while the bottom reaches 1.2 — fits with R² 0.57 and predicts an
  /// HFR well above what the best probe measured. The measured confirmation
  /// frame already tells the truth; this tells the user what to change. Pure —
  /// unit-tested.
  static String? weakFitText(AppLocalizations l, AutofocusRun run) {
    final fit = run.fit;
    if (!run.isComplete || fit == null || fit.rSquared >= 0.8) return null;
    final kept = run.sweepProbes.where((p) => p.kept && p.hfr > 0).toList();
    if (kept.length < 5) return null;
    final minHfr = kept.map((p) => p.hfr).reduce((a, b) => a < b ? a : b);
    final sorted = [...kept]..sort((a, b) => a.position.compareTo(b.position));
    final edge = (sorted.first.hfr + sorted.last.hfr) / 2;
    final plateau = edge > minHfr * 2 && (sorted.first.hfr - sorted.last.hfr).abs() < edge * 0.3;
    final r2 = fit.rSquared.toStringAsFixed(2);
    if (!plateau) {
      return l.focusCardWeakFit(r2);
    }
    // §59.8 — an automatic step size learns the V's width from this very run,
    // so the advice is "run again", not "go change a setting".
    final auto = run.stepSizeSource != null && run.stepSizeSource != 'manual';
    return auto ? l.focusCardWeakFitPlateauAuto(r2) : l.focusCardWeakFitPlateau(r2);
  }

  /// §59.8 — the step size row: the number, then where it came from, in words.
  /// Pure — unit-tested.
  static String stepSizeText(AutofocusRun run) {
    final size = run.stepSize;
    if (size == null) return '—';
    final source = switch (run.stepSizeSource) {
      'measured' => 'auto, from the last V-curve',
      'cfz' => 'auto, from the focuser step size and optics',
      'default' => 'auto, first sweep at the profile value',
      'manual' => 'manual',
      _ => null,
    };
    return source == null ? '$size' : '$size · $source';
  }

  /// The failure banner text: the daemon's reason as a sentence, then where the
  /// focuser was left. Pure — unit-tested.
  static String failureText(AppLocalizations l, AutofocusRun run) {
    var reason = (run.reason ?? l.focusCardFailedSeeLogs).trim();
    if (reason.isNotEmpty) {
      reason = reason[0].toUpperCase() + reason.substring(1);
      if (!RegExp(r'[.!?]$').hasMatch(reason)) reason = '$reason.';
    }
    return run.restoredPosition != null
        ? '$reason ${l.focusCardBackAt('${run.restoredPosition}')}'
        : reason;
  }

  /// The one-line phase while running. Pure — unit-tested.
  static String phaseText(AppLocalizations l, AutofocusRun run) {
    final restored =
        run.restoredPosition != null ? ' — ${l.focusCardRestoredPosition('${run.restoredPosition}')}' : '';
    switch (run.state) {
      case AutofocusRunStates.running:
        return switch (run.phase) {
          'smart' => l.focusCardSmart('${run.sweepProbes.length}', '${run.totalSteps > 0 ? run.totalSteps : 5}'),
          'coarse' => l.focusCardCoarse,
          'sweep' => l.focusCardSweeping('${run.completedSteps}', '${run.totalSteps}') +
              (run.sweepAttempt > 1 ? ' ${l.focusCardPass('${run.sweepAttempt}')}' : ''),
          'fitting' => l.focusCardFitting,
          'moving' => l.focusCardMoving,
          'confirming' => l.focusCardConfirming,
          'backlash' => l.focusCardBacklash,
          _ => l.focusCardRunning,
        };
      case AutofocusRunStates.complete:
        final hfr = run.finalHfr;
        return l.focusCardInFocusAt('${run.finalPosition}') +
            (hfr != null && hfr > 0 ? ' — HFR ${hfr.toStringAsFixed(2)}' : '') +
            (run.durationSeconds != null ? ' ${l.focusCardTook(durationText(run.durationSeconds!))}' : '');
      case AutofocusRunStates.failed:
        return l.focusCardFailedReason(run.reason ?? l.focusCardSeeLogs) + restored;
      case AutofocusRunStates.cancelled:
        return l.focusCardCancelled + restored;
      default:
        return l.focusCardNoRunYet;
    }
  }

  static String durationText(double seconds) {
    if (seconds < 90) return '${seconds.round()} s';
    final m = (seconds / 60).floor();
    final s = (seconds - m * 60).round();
    return '${m}m ${s.toString().padLeft(2, '0')}s';
  }

  /// The four stat tiles. While running they follow the latest probe; after a
  /// run they are the result. Pure — unit-tested.
  static List<Widget> tilesFor(AutofocusRun run) {
    final fit = run.fit;
    final kept = run.sweepProbes.where((p) => p.kept).toList(growable: false);
    final latest = kept.isEmpty ? null : kept.last;
    String n(double? v, [int d = 2]) =>
        v == null || !v.isFinite || v <= 0 ? '—' : v.toStringAsFixed(d);
    if (run.isRunning) {
      return [
        StatTile(caption: 'Latest HFR', value: n(latest?.hfr), unit: 'px'),
        StatTile(caption: 'Probe position', value: latest?.position.toString() ?? '—'),
        StatTile(caption: 'Stars', value: latest?.stars.toString() ?? '—'),
        StatTile(
          caption: 'Progress',
          value: run.totalSteps > 0 && run.phase != 'coarse' ? '${run.completedSteps} / ${run.totalSteps}' : '…',
        ),
      ];
    }
    final measured = run.isComplete && run.finalHfr != null && run.finalHfr! > 0;
    return [
      StatTile(
        caption: measured ? 'HFR at focus' : 'HFR at focus (predicted)',
        value: n(measured ? run.finalHfr : fit?.predictedHfr),
        unit: 'px',
        color: run.isComplete ? AraColors.accentConnected : null,
      ),
      StatTile(
        caption: 'Best position',
        value: run.finalPosition?.toString() ??
            (fit != null && fit.bestPosition > 0 ? fit.bestPosition.round().toString() : '—'),
      ),
      StatTile(
        caption: 'Stars',
        value: run.finalStars?.toString() ?? (latest?.stars.toString() ?? '—'),
      ),
      // A Smart run confirmed by a bracket carries the calibration's curve, not a fit: no R² to show.
      StatTile(caption: 'Fit R²', value: fit == null || fit.algorithm == 'calibration' ? '—' : n(fit.rSquared, 3)),
    ];
  }

  /// The secondary facts behind Details. Pure — unit-tested.
  static List<(String, String)> detailsFor(AutofocusRun run) {
    final fit = run.fit;
    final sweep = run.sweepProbes;
    final kept = sweep.where((p) => p.kept).length;
    String n(double? v, [int d = 2]) => v == null || !v.isFinite ? '—' : v.toStringAsFixed(d);
    return [
      ('Probes kept', sweep.isEmpty ? '—' : '$kept of ${sweep.length}${run.coarseProbes.isNotEmpty ? ' (+${run.coarseProbes.length} coarse)' : ''}'),
      if (run.stepSize != null) ('Step size', stepSizeText(run)),
      ('Fit', fit == null ? '—' : '${fit.algorithm}${fit.withinSampledRange ? '' : ' · outside the sweep'}'),
      ('Predicted HFR', n(fit?.predictedHfr)),
      ('Start → final', run.startPosition == null
          ? '—'
          : run.finalPosition == null
              ? '${run.startPosition}'
              : '${run.startPosition} → ${run.finalPosition} (${_signed(run.finalPosition! - run.startPosition!)})'),
      ('Focuser temperature', run.focuserTemperatureC == null ? '—' : '${n(run.focuserTemperatureC, 1)} °C'),
      ('Filter', run.filter == null || run.filter!.isEmpty ? '—' : run.filter!),
      ('Mode · trigger', run.mode == null ? '—' : '${run.mode}${run.trigger != null ? ' · ${run.trigger}' : ''}'),
      ('Duration', run.durationSeconds == null ? '—' : durationText(run.durationSeconds!)),
      if (run.startedUtc != null) ('Started', _clock(run.startedUtc!.toLocal())),
    ];
  }

  static String _signed(int v) => v > 0 ? '+$v' : '$v';

  static String _clock(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
}
