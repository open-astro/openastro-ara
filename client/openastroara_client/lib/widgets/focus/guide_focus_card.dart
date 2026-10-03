import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guide_focus.dart';
import '../../models/guider_status.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/guide_focus_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import 'focus_frame_view.dart';
import 'focus_section.dart';
import 'hfr_trend_chart.dart';

/// Guide camera: a live readout for the hand-turned helical focuser on a guide
/// scope or off-axis guider. Frames come through the guider (Ara never opens
/// the guide camera itself), so the guider must be connected and not guiding.
/// An OAG shares the main scope's focus, so it waits for the main telescope to
/// be focused this session — or the user's say-so.
class GuideFocusCard extends ConsumerStatefulWidget {
  const GuideFocusCard({super.key});

  @override
  ConsumerState<GuideFocusCard> createState() => _GuideFocusCardState();

  /// The headline in words. Pure — unit-tested.
  static (String, Color?) headlineFor(GuideFocusStatus status, {required bool gated, required bool blocked}) {
    final latest = status.latest;
    if (status.active) {
      if (latest == null) return ('Starting…', AraColors.accentBusy);
      if (latest.stars == 0) return ('Live · no stars measured', AraColors.accentBusy);
      final best = status.bestHfr;
      final atBest = best != null && latest.hfr > 0 && latest.hfr <= best + 1e-9;
      return (
        'Live · HFR ${latest.hfr.toStringAsFixed(2)}${atBest ? ' — best so far' : ''}',
        atBest ? AraColors.accentConnected : AraColors.accentBusy,
      );
    }
    if (status.state == GuideFocusStates.error) return ('Stopped on an error', AraColors.accentError);
    if (latest != null) {
      final best = status.bestHfr;
      return ('Stopped${best != null ? ' · best HFR ${best.toStringAsFixed(2)}' : ''}', null);
    }
    if (gated) return ('Waiting for the main telescope', null);
    if (blocked) return ('Not ready', null);
    return ('Ready to focus', null);
  }
}

class _GuideFocusCardState extends ConsumerState<GuideFocusCard> {
  double _exposureSec = 2;
  static const _exposures = [1.0, 2.0, 3.0, 5.0];

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(guideFocusProvider);
    final guider = ref.watch(guiderStatusProvider).asData?.value;
    final phd2 = ref.watch(phd2SettingsProvider);
    final isOag = phd2.guiderSetupType == 'oag';
    final mainFocused =
        ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
    final status = live.status;
    final active = status.active;
    final guiderConnected =
        guider?.connectionState == GuiderConnectionState.connected;
    final guiderBusy = guider != null &&
        const {
          GuiderRuntimeState.guiding,
          GuiderRuntimeState.calibrating,
          GuiderRuntimeState.dithering,
          GuiderRuntimeState.starLost,
          GuiderRuntimeState.paused,
        }.contains(guider.runtimeState);
    final gated = isOag && !mainFocused;

    // The daemon is the authority on a running loop; the blockers only gate a
    // Start from here.
    String? blocker;
    if (!active) {
      if (!guiderConnected) {
        blocker = 'Connect the guider first — the frames come through it.';
      } else if (guiderBusy) {
        blocker = 'Stop guiding first; the guider owns its camera while it guides or calibrates.';
      }
    }

    final latest = status.latest;
    final hasData = latest != null || live.frame != null;
    final (headline, color) = GuideFocusCard.headlineFor(status, gated: gated, blocked: blocker != null);

    final action = active
        ? FilledButton.tonalIcon(
            onPressed: live.busy ? null : () => ref.read(guideFocusProvider.notifier).stop(),
            icon: live.busy
                ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.stop_rounded, size: 18),
            label: const Text('Stop'),
          )
        : FilledButton.icon(
            onPressed: live.busy || gated || blocker != null
                ? null
                : () => ref.read(guideFocusProvider.notifier).start(exposureSec: _exposureSec),
            icon: live.busy
                ? const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.play_arrow_rounded, size: 18),
            label: Text(hasData ? 'Start again' : 'Start live focus'),
          );

    return FocusSection(
      title: 'Guide camera',
      helpKey: 'setup.focusing.guide',
      headline: headline,
      headlineColor: color,
      subhead: isOag
          ? 'Off-axis guider — it shares the main scope\'s focus. Turn the OAG\'s helical focuser in small moves and watch the number fall.'
          : 'Guide scope — slide the camera in its draw tube, or turn the focuser, in small moves and watch the number fall.',
      action: action,
      secondaryAction: SegmentedButton<double>(
        segments: [
          for (final e in _exposures)
            ButtonSegment(value: e, label: Text('${e.round()} s')),
        ],
        selected: {_exposureSec},
        showSelectedIcon: false,
        style: const ButtonStyle(visualDensity: VisualDensity.compact),
        onSelectionChanged: active ? null : (s) => setState(() => _exposureSec = s.first),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (gated) ...[
            InlineNotice.warning(
              'Focus the main telescope first — an off-axis guider sees the same focal plane, so its HFR only means something once the main scope is in focus.',
              action: TextButton(
                onPressed: () => ref.read(autofocusLiveProvider.notifier).markFocusedManually(),
                child: const Text('Already in focus'),
              ),
            ),
            const SizedBox(height: 16),
          ],
          if (blocker != null) ...[
            InlineNotice.info(blocker),
            const SizedBox(height: 16),
          ],
          if (live.error != null || status.error != null) ...[
            InlineNotice.error(live.error ?? status.error!),
            const SizedBox(height: 16),
          ],
          if (!hasData)
            EmptyState(
              icon: Icons.filter_center_focus,
              title: 'No live focus yet',
              message: 'Start live focus to see the guide camera, its HFR and the trend while you turn the focuser.',
            )
          else ...[
            LayoutBuilder(builder: (context, c) {
              final wide = c.maxWidth >= 760;
              // The live view keeps the guide camera's 4:3 at the width of its
              // 5/8 column, bounded for small and very large windows.
              // …and budgeted against the window height so the live view, the
              // readout AND the trend fit a 1080-tall window without scrolling
              // (chrome + card header ≈ 330, trend ≈ 180, paddings ≈ 90).
              final viewport = MediaQuery.sizeOf(context).height;
              final budget = (viewport - 600).clamp(200.0, 900.0);
              final band = (wide
                      ? ((c.maxWidth - 16) * 5 / 8 * 0.75).clamp(300.0, 900.0)
                      : (c.maxWidth * 0.75).clamp(220.0, 480.0))
                  .clamp(200.0, budget);
              final frame = FocusFrameView(
                frame: live.frame,
                badge: active ? 'LIVE' : 'LAST',
                badgeColor: active ? AraColors.accentBusy : AraColors.textSecondary,
                caption: latest == null ? null : 'frame ${latest.seq}',
                emptyText: 'Waiting for the first guide frame…',
                height: band,
              );
              final hero = SizedBox(
                height: wide ? band : null,
                child: _Hero(status: status),
              );
              if (wide) {
                return Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(flex: 5, child: frame),
                    const SizedBox(width: 16),
                    Expanded(flex: 3, child: hero),
                  ],
                );
              }
              return Column(children: [frame, const SizedBox(height: 12), hero]);
            }),
            const SizedBox(height: 16),
            LayoutBuilder(builder: (context, c) => SizedBox(
              height: (c.maxWidth * 0.12).clamp(150.0, 220.0),
              child: HfrTrendChart(samples: status.recent, bestHfr: status.bestHfr),
            )),
          ],
        ],
      ),
    );
  }

}

// Type scale shared with the polar-align panel: read from an arm's length or
// more while a hand is on the focuser.
const double _heroFont = 88;
const double _hintFont = 28;
const double _statFont = 24;
const double _textFont = 14;
const _tabular = [FontFeature.tabularFigures()];

/// The HFR as the polar-align total-error readout: a light 88-pt figure that
/// scales down to fit, the best-so-far under it, the advice as a bold tinted
/// capsule, then the per-frame facts at 24 pt.
class _Hero extends StatelessWidget {
  final GuideFocusStatus status;
  const _Hero({required this.status});

  @override
  Widget build(BuildContext context) {
    final latest = status.latest;
    final best = status.bestHfr;
    final hfr = latest?.hfr ?? 0.0;
    final atBest = best != null && hfr > 0 && hfr <= best + 1e-9;
    final hint = guideFocusHint(status);
    final color = atBest ? AraColors.accentConnected : AraColors.textPrimary;
    String f(double v, [int d = 2]) => v > 0 ? v.toStringAsFixed(d) : '—';
    return Container(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Text('HFR', textAlign: TextAlign.center,
              style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary, letterSpacing: 1.1)),
          const SizedBox(height: 2),
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(
              f(hfr),
              style: TextStyle(
                fontSize: _heroFont,
                fontWeight: FontWeight.w300,
                height: 1.05,
                color: color,
                fontFeatures: _tabular,
              ),
            ),
          ),
          const SizedBox(height: 4),
          Text(
            best == null ? 'px · best so far —' : 'px · best so far ${f(best)}',
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 16, color: AraColors.textSecondary, fontFeatures: _tabular),
          ),
          if (status.active) ...[
            const SizedBox(height: 14),
            _TurnHint(hint: hint),
          ],
          const SizedBox(height: 18),
          const Divider(height: 1, color: Color(0x1FFFFFFF)),
          const SizedBox(height: 14),
          Row(
            children: [
              Expanded(child: _Fact(label: 'Stars', value: latest == null ? '—' : '${latest.stars}')),
              Expanded(child: _Fact(label: 'Peak', value: latest == null || latest.peakAdu <= 0 ? '—' : '${(latest.peakAdu / 1000).toStringAsFixed(1)}k')),
              Expanded(child: _Fact(label: 'FWHM', value: latest == null ? '—' : f(latest.fwhm, 1))),
              Expanded(child: _Fact(label: 'Frames', value: '${status.seq}')),
            ],
          ),
        ],
      ),
    );
  }
}

class _Fact extends StatelessWidget {
  final String label;
  final String value;
  const _Fact({required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        Text(label, style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
        const SizedBox(height: 2),
        FittedBox(
          fit: BoxFit.scaleDown,
          child: Text(value,
              style: const TextStyle(fontSize: _statFont, fontWeight: FontWeight.w600, fontFeatures: _tabular)),
        ),
      ],
    );
  }
}

/// What to do with the focuser next, read from the HFR trend. Whether it is an
/// OAG\'s helical focuser or a camera sliding in a guide scope\'s draw tube, the
/// app cannot know which way "in" is — so the advice is relative to the user\'s
/// LAST move: keep going, go back, or hold. The size of the move follows how
/// far the HFR sits above the best seen.
enum TurnAdvice { wait, keepGoing, turnBack, hold, atBest, noStars }

class GuideFocusHint {
  final TurnAdvice advice;
  /// Short, for the capsule ("Keep going").
  final String title;
  /// One line under it ("Same way you just moved — small moves").
  final String detail;
  const GuideFocusHint(this.advice, this.title, this.detail);
}

/// Pure — unit-tested. Compares the latest measurable HFR with the one
/// [lookback] frames earlier (a short window rides out single-frame noise).
GuideFocusHint guideFocusHint(GuideFocusStatus status, {int lookback = 4}) {
  final latest = status.latest;
  if (latest == null) {
    return const GuideFocusHint(TurnAdvice.wait, 'Waiting', 'The first frame is on its way.');
  }
  if (latest.stars == 0 || latest.hfr <= 0) {
    return const GuideFocusHint(TurnAdvice.noStars, 'No stars',
        'Point at a star field or lengthen the exposure. Far out of focus? Make big moves until stars appear.');
  }
  final best = status.bestHfr;
  final hfr = latest.hfr;
  final measured = status.recent.where((s) => s.hfr > 0).toList(growable: false);
  final far = best != null && hfr > best * 1.5;
  final size = far ? 'big moves' : 'small moves';
  if (best != null && hfr <= best * 1.04) {
    return const GuideFocusHint(TurnAdvice.atBest, 'Sharpest so far', 'Hold here, or nudge a touch either way to confirm.');
  }
  if (measured.length <= lookback) {
    return GuideFocusHint(TurnAdvice.hold, 'Make a move', 'In or out, $size — then watch which way the number goes.');
  }
  final earlier = measured[measured.length - 1 - lookback].hfr;
  final delta = hfr - earlier;
  final tolerance = (earlier * 0.03).clamp(0.03, 0.3);
  if (delta < -tolerance) {
    return GuideFocusHint(TurnAdvice.keepGoing, 'Keep going', 'Same way you just moved — $size.');
  }
  if (delta > tolerance) {
    return GuideFocusHint(TurnAdvice.turnBack, 'Go back', 'The other way from your last move — $size.');
  }
  return GuideFocusHint(TurnAdvice.hold, 'Holding',
      far ? 'Still well off focus — make a bigger move and watch the number.' : 'Try a small move either way.');
}

/// The advice as a bold tinted capsule (the polar-align knob hint), with the
/// one-line detail under it.
class _TurnHint extends StatelessWidget {
  final GuideFocusHint hint;
  const _TurnHint({required this.hint});

  @override
  Widget build(BuildContext context) {
    final (icon, color) = switch (hint.advice) {
      TurnAdvice.keepGoing => (Icons.arrow_forward_rounded, AraColors.accentConnected),
      TurnAdvice.turnBack => (Icons.u_turn_left_rounded, AraColors.accentWarning),
      TurnAdvice.atBest => (Icons.check_circle_outline, AraColors.accentConnected),
      TurnAdvice.noStars => (Icons.visibility_off_outlined, AraColors.accentWarning),
      TurnAdvice.wait => (Icons.hourglass_empty_rounded, AraColors.textSecondary),
      TurnAdvice.hold => (Icons.pause_circle_outline, AraColors.accentInfo),
    };
    return Column(
      children: [
        FittedBox(
          fit: BoxFit.scaleDown,
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            decoration: BoxDecoration(
              color: color.withValues(alpha: 0.16),
              borderRadius: BorderRadius.circular(14),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(icon, size: _hintFont + 4, color: color),
                const SizedBox(width: 8),
                Text(hint.title, style: TextStyle(fontSize: _hintFont, fontWeight: FontWeight.w700, color: color)),
              ],
            ),
          ),
        ),
        const SizedBox(height: 8),
        Text(hint.detail, textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 16, color: AraColors.textSecondary, height: 1.3)),
      ],
    );
  }
}
