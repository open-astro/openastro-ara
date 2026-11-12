import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart' show PolarAlignClient;
import '../../state/night_mode_state.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../l10n/l10n.dart';
import '../fit_pane.dart';
import '../help_icon.dart';
import 'polar_align_readout.dart';
import '../adaptive_controls.dart';

export 'polar_align_readout.dart';

// Type scale: body text matches the other Setup panes; the alignment
// readout is sized to read at arm's length or more from the mount.
const double _textFont = 14;
const double _smallFont = 12;
const double _heroFont = 88;
const double _axisFont = 60;
const double _hintFont = 28;
// The "Alignment quality" headline figure; the axis readout above is larger.
const double _qualityFont = 56;
const _tabular = [FontFeature.tabularFigures()];
// Side-by-side cards from here; stacked below (tablet portrait, phone). A short
// window keeps them side by side down to the narrower width.
const double _wideBreakpoint = AraBreakpoints.sideBySideWidth;
const double _shortWideBreakpoint = AraBreakpoints.sideBySideWidthShort;
// A short window (AraBreakpoints.isShort) scales the readout down so the cards
// fit a laptop screen.
const double _heroFontShort = 60;
const double _axisFontShort = 40;
const double _qualityFontShort = 40;
const double _hintFontShort = 22;
// The camera card around its image: padding, the label row, the frame status.
const double _cameraChrome = 80;

/// RA in degrees as `09h14m40s`. Pure — unit-tested.
String formatRaHms(double raDeg) {
  final totalSeconds = ((raDeg % 360) / 15 * 3600).round() % (24 * 3600);
  final h = totalSeconds ~/ 3600;
  final m = (totalSeconds % 3600) ~/ 60;
  final sec = totalSeconds % 60;
  String two(int v) => v.toString().padLeft(2, '0');
  return '${two(h)}h${two(m)}m${two(sec)}s';
}

/// Dec in degrees as `+87°10′56″`. Pure — unit-tested.
String formatDecDms(double decDeg) {
  final sign = decDeg < 0 ? '−' : '+';
  final totalSeconds = (decDeg.abs() * 3600).round();
  final d = totalSeconds ~/ 3600;
  final m = (totalSeconds % 3600) ~/ 60;
  final sec = totalSeconds % 60;
  String two(int v) => v.toString().padLeft(2, '0');
  return '$sign${two(d)}°${two(m)}′${two(sec)}″';
}

/// Milliseconds as seconds with one decimal: `1.6 s`.
String _secondsLabel(num ms) => '${(ms / 1000).toStringAsFixed(1)} s';

/// §45 polar-alignment panel — collapsible Imaging-tab section driving the
/// server routine: Start (2-point seed → live adjust), the zooming bullseye
/// with decoupled Az/Alt readouts fed by `polar_align.progress` WS events,
/// the §45.11 no-solve retry banner, and Done/Abort. [Done] enables inside the
/// profile's target tolerance (§45.12).
class PolarAlignPanel extends ConsumerStatefulWidget {
  const PolarAlignPanel({super.key});

  @override
  ConsumerState<PolarAlignPanel> createState() => _PolarAlignPanelState();
}

class _PolarAlignPanelState extends ConsumerState<PolarAlignPanel> {
  // The routine's command state, settings and frame are the shared module's
  // (polar_align_routine_state.dart); this panel renders them.
  bool get _busy => ref.watch(polarAlignActionProvider).busy;
  String? get _status => ref.watch(polarAlignActionProvider).error;
  PolarAlignSettings get _settings =>
      ref.watch(polarAlignSettingsProvider).value ?? const PolarAlignSettings();
  double get _toleranceArcmin => _settings.targetToleranceArcmin;

  // §45.16 bolt-direction nudge: the previous frame's errors, the iteration
  // they came from, the nudge in force and the iteration it expires after.
  final _bolt = BoltNudgeTracker();
  final _exposureCtrl = TextEditingController(text: _exposureText(1.0));

  // Repaints the exposing/solving readout while a frame is in progress.
  Timer? _ticker;

  // Night mode (red filter) as of the last build: blue accents nearly vanish
  // under it, so the in-routine Abort switches to full-brightness text.
  bool _night = false;

  // A short window as of the last build: the readout figures scale down.
  bool _short = false;

  static String _exposureText(double seconds) =>
      seconds == seconds.roundToDouble() ? seconds.toStringAsFixed(1) : '$seconds';

  @override
  void initState() {
    super.initState();
    _bolt.track(ref.read(polarAlignLiveProvider)); // the first frame is the nudge baseline
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _exposureCtrl.dispose();
    super.dispose();
  }

  void _syncTicker(bool frameInProgress) {
    if (frameInProgress && _ticker == null) {
      _ticker = Timer.periodic(const Duration(milliseconds: 200), (_) {
        if (mounted) setState(() {});
      });
    } else if (!frameInProgress && _ticker != null) {
      _ticker!.cancel();
      _ticker = null;
    }
  }

  /// Seed the live view and settings from REST on open (the WS resume may
  /// have skipped past routine events fired before this client connected).
  Future<void> _hydrate() {
    ref.invalidate(polarAlignSettingsProvider);
    return ref.read(polarAlignFrameProvider.notifier).hydrate();
  }

  // [action] is a verb phrase completing "Couldn't <action>".
  Future<void> _run(String action, Future<void> Function(PolarAlignClient api) op) {
    final l10n = context.l10n;
    return ref
        .read(polarAlignActionProvider.notifier)
        .run(op, describe: (e) => friendlyError(l10n, e, action: action));
  }

  /// Persist [next] to the profile; the module rolls a failed save back.
  void _saveSettings(PolarAlignSettings next) => _run(
        'save the polar-align settings',
        (_) => ref.read(polarAlignSettingsProvider.notifier).save(next),
      );

  void _commitExposure() {
    final settings = ref.read(polarAlignSettingsProvider).value ?? const PolarAlignSettings();
    final value = double.tryParse(_exposureCtrl.text.trim());
    if (!polarExposureValid(value)) {
      ref.read(polarAlignActionProvider.notifier).fail('Exposure must be more than 0 and at most 60 seconds.');
      _exposureCtrl.text = _exposureText(settings.exposureSeconds);
      return;
    }
    if (value == settings.exposureSeconds) return;
    _saveSettings(settings.copyWith(exposureSeconds: value));
  }

  void _showFullFrame(Uint8List jpeg) {
    showDialog<void>(
      context: context,
      builder: (context) => Dialog(
        insetPadding: const EdgeInsets.all(16),
        child: InteractiveViewer(
          maxScale: 8,
          child: Image.memory(jpeg, gaplessPlayback: true),
        ),
      ),
    );
  }

  /// Facing the south celestial pole: east and west swap sides on screen.
  bool get _southern => ref.watch(siteSettingsProvider).latitudeDeg < 0;

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(polarAlignLiveProvider);
    final api = ref.watch(polarAlignApiProvider);
    _syncTicker(live.frameInProgress);
    // §45.16 the bolt-direction nudge is tracked on each state change, not
    // in build: it mutates the baseline fields.
    ref.listen(polarAlignLiveProvider, (prev, next) => setState(() => _bolt.track(next)));
    // Kept alive while the panel is open, so every finished frame is fetched.
    ref.watch(polarAlignFrameProvider);
    // The field follows what the rig uses: the first read, and a failed save's rollback.
    ref.listen(polarAlignSettingsProvider, (_, next) {
      if (next.value case final settings?) {
        _exposureCtrl.text = _exposureText(settings.exposureSeconds);
      }
    });
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    // Night mode's red filter keeps only luminance, so the zone hues (and
    // their tints) would be the dimmest things on screen. Draw them at full
    // brightness instead; the words (Excellent, Raise, …) carry the meaning.
    final color = night ? AraColors.textPrimary : zoneColor(live.totalErrorArcmin);
    _night = night;
    _short = AraBreakpoints.isShort(context);

    // Always-on panel: the live bullseye + Az/Alt/Total readout stay visible
    // on the page (like the equipment chips) instead of hiding behind a
    // collapse — polar alignment is a hands-on, eyes-on process.
    // The body fills the height the pane leaves (FitPane), so the camera and
    // the bullseye grow with the window.
    return Container(
      padding: EdgeInsets.all(_short ? 12 : 16),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(12),
      ),
      child: FitColumn(
        children: [
          // A short window drops the title while the routine runs (the Setup
          // checklist names the step) so the camera and bullseye keep the room.
          if (!(_short && live.isActive)) ...[
            Row(
              children: [
                const SizedBox.square(dimension: 22, child: CustomPaint(painter: PolarScopeIconPainter())),
                const SizedBox(width: 10),
                Text('Polar Align', style: Theme.of(context).textTheme.titleLarge),
                const SizedBox(width: 4),
                const HelpIcon(helpKey: 'setup.polar_align.stages'),
                const Spacer(),
                _statusPill(live),
              ],
            ),
            const SizedBox(height: 12),
          ],
          if (live.isActive || live.phase == PolarAlignStates.checked) ...[
            _stageStrip(live),
            SizedBox(height: _short ? 8 : 12),
          ],
          FitFill(child: _body(live, api == null, color)),
        ],
      ),
    );
  }

  /// The three stages as chips — the one running lit, the ones done ticked.
  Widget _stageStrip(PolarAlignLive live) {
    // A null stage is an older daemon's one-stage routine — the fine routine,
    // as the fold and the Simple panel read it.
    final current = PolarAlignStages.indexOf(live.stage) ?? 1;
    final checkedDone = live.phase == PolarAlignStates.checked;
    const labels = ['1 · Rough', '2 · Fine', '3 · Check'];
    return Row(
      key: const Key('polar-align-stages'),
      children: [
        for (var i = 0; i < labels.length; i++) ...[
          if (i > 0) const Expanded(child: Divider(height: 1, color: AraColors.frameDivider)),
          Builder(builder: (context) {
            final done = i < current || (checkedDone && i == current);
            final active = i == current && !checkedDone;
            final tint = active
                ? AraColors.accentInfo
                : done
                    ? AraColors.accentConnected
                    : AraColors.textDisabled;
            return Container(
              key: Key('polar-align-stage-${PolarAlignStages.all[i]}'),
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
              decoration: BoxDecoration(
                color: tint.withValues(alpha: active ? 0.18 : 0.08),
                borderRadius: BorderRadius.circular(999),
                border: active ? Border.all(color: tint, width: 1) : null,
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  if (done) Icon(Icons.check, size: 14, color: tint),
                  if (done) const SizedBox(width: 4),
                  Text(labels[i],
                      style: TextStyle(fontSize: _smallFont, fontWeight: FontWeight.w600, color: tint)),
                ],
              ),
            );
          }),
        ],
      ],
    );
  }

  String _headerSummary(PolarAlignLive live) {
    switch (live.phase) {
      case PolarAlignStates.homing:
        return live.waitingForHome ? 'Put the mount at Home' : 'Going Home';
      case PolarAlignStates.checking:
        return 'Checking ${live.checkSolvesDone}/${live.checkSolvesTotal}';
      case PolarAlignStates.checked:
        return 'Checked';
      case PolarAlignStates.seeding:
        return 'Measuring axis';
      // Adjusting: no pill — the alignment card already shows the total.
      case PolarAlignStates.paused:
        return 'Paused — no solve';
      case PolarAlignStates.failed:
        return 'Failed';
      default:
        return '';
    }
  }

  /// Capsule in the header: a status dot + the routine's phase.
  Widget _statusPill(PolarAlignLive live) {
    final text = _headerSummary(live);
    if (text.isEmpty) return const SizedBox.shrink();
    final tint = switch (live.phase) {
      PolarAlignStates.failed => AraColors.accentError,
      PolarAlignStates.paused => AraColors.accentBusy,
      PolarAlignStates.homing when live.waitingForHome => AraColors.accentBusy,
      PolarAlignStates.checked => AraColors.accentConnected,
      _ => AraColors.accentInfo,
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: tint.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 6,
            height: 6,
            decoration: BoxDecoration(color: tint, shape: BoxShape.circle),
          ),
          const SizedBox(width: 6),
          Text(
            text,
            key: const Key('polar-align-header-summary'),
            style: TextStyle(
                fontSize: _smallFont, fontWeight: FontWeight.w600, color: tint, fontFeatures: _tabular),
          ),
        ],
      ),
    );
  }

  Widget _body(PolarAlignLive live, bool noServer, Color color) {
    if (noServer) {
      return const Text('Connect to your rig first.', style: TextStyle(color: AraColors.textSecondary));
    }
    switch (live.phase) {
      case PolarAlignStates.homing:
        return _homingBody(live);
      case PolarAlignStates.seeding:
        return _seedingBody(live);
      case PolarAlignStates.adjusting:
      case PolarAlignStates.paused:
        return _adjustBody(live, color);
      case PolarAlignStates.checking:
        return _checkingBody(live);
      case PolarAlignStates.checked:
        return _checkedBody(live, color);
      default:
        return _idleBody(live);
    }
  }

  Future<void> _start({String? stage}) => _run('start polar alignment', (api) => api.start(stage: stage));

  Future<void> _next(String action) => _run(action, (api) => api.next());

  /// Coarse/check: the mount is going Home, or waits for the user to put it there.
  Widget _homingBody(PolarAlignLive live) {
    final waiting = live.waitingForHome;
    final card = _card(
      child: Column(
        children: [
          Align(alignment: Alignment.centerLeft, child: _sectionLabel('Home position')),
          const SizedBox(height: 28),
          if (waiting)
            const Icon(Icons.home_outlined, size: 32, color: AraColors.accentBusy)
          else
            const SizedBox.square(dimension: 28, child: AdaptiveProgressIndicator(strokeWidth: 2.5)),
          const SizedBox(height: 20),
          Text(
            waiting ? 'Put the mount in its home position' : 'Sending the mount Home',
            key: const Key('polar-align-homing-title'),
            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 6),
          Text(
            waiting
                ? 'This mount cannot find Home by itself. Counterweights down, tube pointing at '
                    'the pole, then tap Continue.'
                : 'Counterweights down, tube along the RA axis. Leave the mount alone until it stops.',
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary, height: 1.4),
          ),
          if (_status != null) ...[
            const SizedBox(height: 12),
            _callout(_status!, AraColors.accentError, Icons.error_outline),
          ],
          const SizedBox(height: 20),
          Wrap(
            spacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              TextButton(
                key: const Key('polar-align-abort-homing'),
                onPressed: _busy ? null : _abort,
                child: const Text('Abort'),
              ),
              if (waiting)
                FilledButton.icon(
                  key: const Key('polar-align-continue'),
                  onPressed: _busy ? null : () => _next('continue polar alignment'),
                  icon: const Icon(Icons.arrow_forward),
                  label: const Text('Continue'),
                ),
            ],
          ),
        ],
      ),
    );
    return Align(alignment: Alignment.topCenter, child: ConstrainedBox(constraints: const BoxConstraints(maxWidth: 520), child: card));
  }

  /// Check stage: three solves around two RA turns, no adjusting.
  Widget _checkingBody(PolarAlignLive live) {
    final card = _card(
      child: Column(
        children: [
          Align(alignment: Alignment.centerLeft, child: _sectionLabel('Three-point check')),
          const SizedBox(height: 28),
          const SizedBox.square(dimension: 28, child: AdaptiveProgressIndicator(strokeWidth: 2.5)),
          const SizedBox(height: 20),
          Text(
            'Check solve ${live.checkSolvesDone} of ${live.checkSolvesTotal}',
            key: const Key('polar-align-check-progress'),
            style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 6),
          const Text(
            'Three solved frames around two RA turns measure the axis exactly. '
            'Leave the mount alone — this only measures.',
            textAlign: TextAlign.center,
            style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary, height: 1.4),
          ),
          const SizedBox(height: 20),
          TextButton(
            key: const Key('polar-align-abort-checking'),
            onPressed: _busy ? null : _abort,
            child: const Text('Abort'),
          ),
        ],
      ),
    );
    return FitBand(builder: (context, width, height) {
      if (width < (_short ? _shortWideBreakpoint : _wideBreakpoint)) {
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [_cameraCard(live, imageHeight: 240), const SizedBox(height: 12), card],
        );
      }
      final cameraWidth = (width - 12) * 11 / 20;
      return Align(
        alignment: Alignment.topCenter,
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: math.min(height, cameraWidth * 0.75 + _cameraChrome)),
          child: FitRow(flex: const [11, 9], spacing: 12, children: [_cameraCard(live), card]),
        ),
      );
    });
  }

  /// The check's result: the verdict, the measured error, and how it compares
  /// with what the fine stage said.
  Widget _checkedBody(PolarAlignLive live, Color color) {
    final r = live.checkResult;
    final total = r?.totalErrorArcmin ?? live.totalErrorArcmin;
    // The daemon's verdict when it sent one; the local bands only for a token this build does not know.
    final rating = (r == null ? null : polarRatingForVerdict(r.verdict)) ??
        (total == null ? null : polarErrorRating(total));
    final delta = r?.fineDeltaArcmin;
    final alt = r?.altErrorArcmin ?? live.altErrorArcmin;
    final az = r?.azErrorArcmin ?? live.azErrorArcmin;
    return SingleChildScrollView(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _card(
            key: const Key('polar-align-check-result'),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _sectionLabel('Three-point check'),
                const SizedBox(height: 12),
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  crossAxisAlignment: CrossAxisAlignment.center,
                  children: [
                    Flexible(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          _sectionLabel('Measured error'),
                          const SizedBox(height: 2),
                          FittedBox(
                            fit: BoxFit.scaleDown,
                            child: Text(
                              formatArcmin(total),
                              key: const Key('polar-align-check-total'),
                              style: TextStyle(
                                fontSize: _short ? _heroFontShort : _heroFont,
                                fontWeight: FontWeight.w300,
                                height: 1.05,
                                color: color,
                                fontFeatures: _tabular,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                    if (rating != null && total != null) ...[
                      const SizedBox(width: 16),
                      Flexible(
                        child: FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: Alignment.centerRight,
                          child: Container(
                            padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 10),
                            decoration: BoxDecoration(
                              color: color.withValues(alpha: 0.16),
                              borderRadius: BorderRadius.circular(16),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Icon(polarErrorRatingIcon(total), size: 34, color: color),
                                const SizedBox(width: 10),
                                Text(rating.$1,
                                    key: const Key('polar-align-check-verdict'),
                                    style: TextStyle(fontSize: 30, fontWeight: FontWeight.w700, color: color)),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
                if (rating != null) ...[
                  const SizedBox(height: 14),
                  Text(rating.$2, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w500)),
                ],
                const SizedBox(height: 18),
                const Divider(height: 1, color: AraColors.frameDivider),
                const SizedBox(height: 16),
                IntrinsicHeight(
                  child: Row(
                    children: [
                      Expanded(child: _axisStat('Altitude', alt, polarAltHint(context.l10n, alt), color)),
                      const VerticalDivider(width: 1, color: AraColors.frameDivider),
                      Expanded(child: _axisStat('Azimuth', az, polarAzHint(context.l10n, az, southern: _southern), color)),
                    ],
                  ),
                ),
                const SizedBox(height: 16),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: _fact(
                        'Fine stage said',
                        r?.fineErrorArcmin == null ? '—' : formatArcmin(r!.fineErrorArcmin),
                      ),
                    ),
                    const SizedBox(width: 16),
                    Expanded(
                      child: _fact('Disagreement', delta == null ? '—' : formatArcmin(delta)),
                    ),
                  ],
                ),
                if (delta != null && delta > 2.0) ...[
                  const SizedBox(height: 12),
                  _callout(
                    'The check and the fine readout disagree by ${formatArcmin(delta)}. Trust the check; '
                    'run the fine stage again if it says you are not where you want to be.',
                    AraColors.accentBusy,
                    Icons.warning_amber_outlined,
                    key: const Key('polar-align-check-disagree'),
                  ),
                ],
                if (r != null) ...[
                  const SizedBox(height: 14),
                  Text(
                    'Pointings ${r.coneHalfAngleDeg.toStringAsFixed(1)}° off the axis · solved turns within '
                    "${r.turnMismatchDeg.toStringAsFixed(2)}° of the mount's own",
                    style: const TextStyle(fontSize: 13, color: AraColors.textSecondary),
                  ),
                ],
              ],
            ),
          ),
          if (_status != null) ...[
            const SizedBox(height: 12),
            _callout(_status!, AraColors.accentError, Icons.error_outline),
          ],
          const SizedBox(height: 16),
          Wrap(
            spacing: 8,
            runSpacing: 12,
            alignment: WrapAlignment.end,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              TextButton.icon(
                key: const Key('polar-align-adjust-again'),
                onPressed: _busy ? null : () => _start(stage: PolarAlignStages.fine),
                icon: const Icon(Icons.tune),
                label: const Text('Adjust again'),
              ),
              FilledButton.icon(
                key: const Key('polar-align-check-done'),
                onPressed: _busy
                    ? null
                    : () => _run('complete polar alignment', (api) => api.complete()),
                icon: const Icon(Icons.check),
                label: const Text('Done'),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _idleBody(PolarAlignLive live) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'Three steps. Rough: the mount goes Home and the main camera shows how far '
          'from the pole you are and on which side, while you make the big moves. '
          'Fine: the guide camera measures the axis around an RA turn and guides the '
          'knobs live. Check: back Home, three solves measure the result exactly.',
          style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary, height: 1.4),
        ),
        if (live.phase == PolarAlignStates.failed && live.errorReason != null) ...[
          const SizedBox(height: 12),
          _callout(
            'Failed (${live.errorReason}): ${live.errorMessage ?? 'see the server log'}',
            AraColors.accentError,
            Icons.error_outline,
            key: const Key('polar-align-error-banner'),
          ),
        ],
        if (live.phase == PolarAlignStates.stopped && live.totalErrorArcmin != null) ...[
          const SizedBox(height: 12),
          _callout(
            'Last run ended at ${formatArcmin(live.totalErrorArcmin)} total error.',
            AraColors.textSecondary,
            Icons.history,
          ),
        ],
        if (_status != null) ...[
          const SizedBox(height: 12),
          _callout(_status!, AraColors.accentError, Icons.error_outline),
        ],
        const SizedBox(height: 16),
        Wrap(
          spacing: 16,
          runSpacing: 12,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            _captureSettings(),
            FilledButton.icon(
              key: const Key('polar-align-start'),
              onPressed: _busy ? null : () => _start(),
              icon: const Icon(Icons.play_arrow),
              label: const Text('Start Polar Alignment'),
            ),
            TextButton(
              key: const Key('polar-align-start-fine'),
              onPressed: _busy ? null : () => _start(stage: PolarAlignStages.fine),
              child: const Text('Skip the rough step'),
            ),
            TextButton(
              key: const Key('polar-align-start-check'),
              onPressed: _busy ? null : () => _start(stage: PolarAlignStages.check),
              child: const Text('Check only'),
            ),
          ],
        ),
      ],
    );
  }


  Widget _seedingBody(PolarAlignLive live) {
    final measuring = _card(
      child: Column(
        children: [
          Align(alignment: Alignment.centerLeft, child: _sectionLabel('RA axis')),
          const SizedBox(height: 28),
          const SizedBox.square(dimension: 28, child: AdaptiveProgressIndicator(strokeWidth: 2.5)),
          const SizedBox(height: 20),
          const Text(
            'Measuring the RA axis',
            style: TextStyle(fontSize: 16, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 6),
          const Text(
            'Two solved frames around a small RA slew. Leave the mount alone '
            'until the bullseye appears.',
            textAlign: TextAlign.center,
            style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary, height: 1.4),
          ),
          const SizedBox(height: 20),
          TextButton(
            key: const Key('polar-align-abort-seeding'),
            onPressed: _busy ? null : _abort,
            child: const Text('Abort'),
          ),
        ],
      ),
    );
    return FitBand(builder: (context, width, height) {
      if (width < (_short ? _shortWideBreakpoint : _wideBreakpoint)) {
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [_cameraCard(live, imageHeight: 240), const SizedBox(height: 12), measuring],
        );
      }
      // Camera card left with ~55% of the width, the measuring card beside it.
      // The camera takes the height the window leaves, up to a 4:3 frame.
      final cameraWidth = (width - 12) * 11 / 20;
      return Align(
        alignment: Alignment.topCenter,
        child: ConstrainedBox(
          constraints: BoxConstraints(minHeight: math.min(height, cameraWidth * 0.75 + _cameraChrome)),
          child: FitRow(flex: const [11, 9], spacing: 12, children: [_cameraCard(live), measuring]),
        ),
      );
    });
  }

  Widget _adjustBody(PolarAlignLive live, Color color) {
    final inTolerance = live.totalErrorArcmin != null && live.totalErrorArcmin! <= _toleranceArcmin;
    final single = _settings.loopMode == PolarAlignLoopModes.single;
    final coarse = live.stage == PolarAlignStages.coarse;
    final side = coarse ? polarSideOfPoleText(context.l10n, live.altErrorArcmin, live.azErrorArcmin) : null;
    return FitColumn(
      children: [
        if (coarse) ...[
          _callout(
            side == null
                ? 'Rough step — the main camera measures the mount axis with a turn in RA, then follows your knob moves. Waiting for the first solve…'
                : '$side Make the big moves now; the fine step takes over from a few degrees in.',
            AraColors.accentInfo,
            Icons.explore_outlined,
            key: const Key('polar-align-coarse-banner'),
          ),
          SizedBox(height: _short ? 8 : 12),
        ],
        if (live.phase == PolarAlignStates.paused) ...[
          _callout(
            'No solve — check sky and focus. Retrying…',
            AraColors.accentBusy,
            Icons.cloud_outlined,
            key: const Key('polar-align-paused-banner'),
          ),
          SizedBox(height: _short ? 8 : 12),
        ] else if (live.consecutiveSolveFailures > 0) ...[
          _callout(
            'No solve (${live.consecutiveSolveFailures}) — check sky and focus.',
            AraColors.accentBusy,
            Icons.cloud_outlined,
            key: const Key('polar-align-retry-banner'),
          ),
          SizedBox(height: _short ? 8 : 12),
        ],
        FitFill(child: FitBand(builder: (context, width, height) {
          if (_short && width >= _shortWideBreakpoint) {
            // A laptop screen: camera and bullseye fill the band, the numbers
            // run in one strip beneath them.
            return FitColumn(children: [
              FitFill(
                child: FitRow(
                  flex: const [11, 9],
                  spacing: 12,
                  children: [
                    _cameraCard(live),
                    _alignmentCard(live, color, single, fill: true, readout: false),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              _readoutStrip(live, color),
            ]);
          }
          if (width >= _wideBreakpoint) {
            // Both columns share the height the window leaves, so the card
            // edges line up: the camera image and the bullseye take what the
            // readouts don't need.
            return FitRow(
              flex: const [11, 9],
              spacing: 12,
              children: [
                FitColumn(children: [
                  FitFill(child: _cameraCard(live)),
                  const SizedBox(height: 12),
                  _meaningCard(live, color),
                ]),
                _alignmentCard(live, color, single, fill: true),
              ],
            );
          }
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: [
              _cameraCard(live, imageHeight: 240),
              const SizedBox(height: 12),
              _alignmentCard(live, color, single),
              const SizedBox(height: 12),
              _meaningCard(live, color),
            ],
          );
        })),
        if (_status != null) ...[
          const SizedBox(height: 12),
          _callout(_status!, AraColors.accentError, Icons.error_outline),
        ],
        const SizedBox(height: 16),
        Wrap(
          alignment: WrapAlignment.spaceBetween,
          crossAxisAlignment: WrapCrossAlignment.center,
          runSpacing: 12,
          children: [
            _captureSettings(),
            Wrap(
              spacing: 8,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                TextButton(
                  key: const Key('polar-align-abort'),
                  style: _night ? TextButton.styleFrom(foregroundColor: AraColors.textPrimary) : null,
                  onPressed: _busy ? null : _abort,
                  child: const Text('Abort'),
                ),
                if (single)
                  FilledButton.tonalIcon(
                    key: const Key('polar-align-take-frame'),
                    onPressed: _busy || live.frameInProgress
                        ? null
                        : () => _run('take a frame', (api) => api.requestCapture()),
                    icon: const Icon(Icons.camera),
                    label: const Text('Take Frame'),
                  ),
                if (coarse)
                  FilledButton.icon(
                    key: const Key('polar-align-next-fine'),
                    onPressed: _busy || live.totalErrorArcmin == null
                        ? null
                        : () => _next('move on to the fine step'),
                    icon: const Icon(Icons.arrow_forward),
                    label: const Text('Next: fine'),
                  )
                else ...[
                  Tooltip(
                    message: inTolerance
                        ? ''
                        : 'Available once the total error is under ${_toleranceArcmin.toStringAsFixed(1)}′',
                    child: FilledButton.tonalIcon(
                      key: const Key('polar-align-done'),
                      onPressed: _busy || !inTolerance
                          ? null
                          : () => _run('complete polar alignment', (api) => api.complete()),
                      icon: const Icon(Icons.check),
                      label: const Text('Done'),
                    ),
                  ),
                  Tooltip(
                    message: inTolerance
                        ? ''
                        : 'Available once the total error is under ${_toleranceArcmin.toStringAsFixed(1)}′',
                    child: FilledButton.icon(
                      key: const Key('polar-align-next-check'),
                      onPressed: _busy || !inTolerance ? null : () => _next('start the check'),
                      icon: const Icon(Icons.fact_check_outlined),
                      label: const Text('Check it'),
                    ),
                  ),
                ],
              ],
            ),
          ],
        ),
      ],
    );
  }

  /// Bullseye, the total error as the one large figure, then Altitude and
  /// Azimuth side by side, each with the knob direction that closes it.
  /// [fill]: the card is stretched to its row's height and the bullseye
  /// centers in the spare room. [readout] false leaves the numbers to
  /// [_readoutStrip].
  Widget _alignmentCard(PolarAlignLive live, Color color, bool single, {bool fill = false, bool readout = true}) {
    final range = bullseyeRangeArcmin(live.totalErrorArcmin);
    final reticle = nearPoleReticle(live.totalErrorArcmin);
    final ring = reticle
        ? 'Reticle ${range.toStringAsFixed(0)}′ · rings 30″'
        : 'Ring ${range >= 60 ? '${(range / 60).toStringAsFixed(0)}°' : '${range.toStringAsFixed(0)}′'}';
    // Square, as large as the spare room allows up to a size that still reads
    // as an instrument rather than a poster.
    final bullseye = ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 560, maxHeight: 560),
      child: AspectRatio(
        aspectRatio: 1,
        child: CustomPaint(
          painter: BullseyePainter(
            dotFraction: bullseyeDotFraction(live.azErrorArcmin, live.altErrorArcmin, range, southern: _southern),
            zoneColor: color,
            reticle: reticle,
          ),
        ),
      ),
    );
    final body = [
          Row(
            children: [
              _sectionLabel('Alignment'),
              const Spacer(),
              // The reticle label is the long one: scale it down in a narrow
              // column rather than overflow the header.
              Flexible(
                child: FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(ring,
                      key: const Key('polar-align-ring'),
                      style: const TextStyle(fontSize: _smallFont, color: AraColors.textSecondary, fontFeatures: _tabular)),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          if (fill)
            FitFill(
              child: FitBand(
                builder: (context, width, height) => SizedBox(
                  height: math.max(height, _short ? 120.0 : 160.0),
                  child: Center(child: bullseye),
                ),
              ),
            )
          else
            bullseye,
          if (readout) ...[
          const SizedBox(height: 16),
          Column(
            key: const Key('polar-align-readout'),
            children: [
              _sectionLabel('Total error'),
              const SizedBox(height: 2),
              FittedBox(
                fit: BoxFit.scaleDown,
                child: Text(
                  formatArcmin(live.totalErrorArcmin),
                  style: TextStyle(
                    fontSize: _short ? _heroFontShort : _heroFont,
                    fontWeight: FontWeight.w300,
                    height: 1.05,
                    color: color,
                    fontFeatures: _tabular,
                  ),
                ),
              ),
              const SizedBox(height: 20),
              const Divider(height: 1, color: AraColors.frameDivider),
              const SizedBox(height: 18),
              IntrinsicHeight(
                child: Row(
                  children: [
                    Expanded(child: _axisStat('Altitude', live.altErrorArcmin, polarAltHint(context.l10n, live.altErrorArcmin), color)),
                    const VerticalDivider(width: 1, color: AraColors.frameDivider),
                    Expanded(child: _axisStat('Azimuth', live.azErrorArcmin, polarAzHint(context.l10n, live.azErrorArcmin, southern: _southern), color)),
                  ],
                ),
              ),
              if (_bolt.nudge case final nudge?) ...[
                const SizedBox(height: 12),
                _nudgeLine(nudge),
              ],
            ],
          ),
          ],
          if (single) ...[
            const SizedBox(height: 14),
            const Center(
              child: Text(
                'Turn a knob, then Take Frame.',
                style: TextStyle(fontSize: _smallFont, color: AraColors.textSecondary),
              ),
            ),
          ],
    ];
    // Filling: the bullseye takes the card's spare height (FitRow/FitPane).
    return _card(child: fill ? FitColumn(children: body) : Column(children: body));
  }

  /// A short window's readout: the total error with its verdict, then the two
  /// axes with their knob directions, in one row.
  Widget _readoutStrip(PolarAlignLive live, Color color) {
    final total = live.totalErrorArcmin;
    final rating = total == null ? null : polarErrorRating(total);
    final row = Row(
        key: const Key('polar-align-readout'),
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Column(
              children: [
                _sectionLabel('Total error'),
                const SizedBox(height: 2),
                FittedBox(
                  fit: BoxFit.scaleDown,
                  child: Text(
                    formatArcmin(total),
                    style: TextStyle(
                      fontSize: _axisFontShort,
                      fontWeight: FontWeight.w300,
                      height: 1.1,
                      color: color,
                      fontFeatures: _tabular,
                    ),
                  ),
                ),
                if (rating != null) ...[
                  const SizedBox(height: 10),
                  Text(rating.$1, style: TextStyle(fontSize: _hintFontShort, fontWeight: FontWeight.w700, color: color)),
                ],
              ],
            ),
          ),
          Expanded(child: _axisStat('Altitude', live.altErrorArcmin, polarAltHint(context.l10n, live.altErrorArcmin), color)),
          Expanded(child: _axisStat('Azimuth', live.azErrorArcmin, polarAzHint(context.l10n, live.azErrorArcmin, southern: _southern), color)),
        ],
      );
    return _card(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Column(
        children: [
          row,
          if (_bolt.nudge case final nudge?) ...[
            const SizedBox(height: 8),
            _nudgeLine(nudge),
          ],
        ],
      ),
    );
  }

  /// §45.16 the bolt-direction nudge, amber, under the knob directions.
  Widget _nudgeLine(bool altitude) => Row(
        key: const Key('polar-align-nudge'),
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.u_turn_left, size: 18, color: AraColors.accentBusy),
          const SizedBox(width: 8),
          Expanded(
            child: Text(altitude ? context.l10n.polarHintAltitudeWorse : context.l10n.polarHintAzimuthWorse,
                style: const TextStyle(fontSize: _smallFont, color: AraColors.accentBusy, height: 1.3)),
          ),
        ],
      );

  /// The total error translated for people who don't think in arcminutes:
  /// arcseconds, a plain-English verdict, worst-case star drift over a
  /// 5-minute exposure, and a Moon-width comparison.
  Widget _meaningCard(PolarAlignLive live, Color color) {
    final total = live.totalErrorArcmin;
    final offset = total == null ? null : formatPoleOffset(total);
    final rating = total == null ? null : polarErrorRating(total);
    final drift = total == null ? null : maxDriftArcsec(total, 300);
    return _card(
      key: const Key('polar-align-meaning'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _sectionLabel('Alignment quality'),
          const SizedBox(height: 12),
          // Distance from the pole on the left, the verdict on the right.
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Flexible(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    FittedBox(
                      fit: BoxFit.scaleDown,
                      child: Text(
                        offset?.$1 ?? '—',
                        style: TextStyle(
                            fontSize: _short ? _qualityFontShort : _qualityFont, fontWeight: FontWeight.w600, height: 1.1, fontFeatures: _tabular),
                      ),
                    ),
                    Text(
                      offset?.$2 ?? 'waiting for a solve',
                      style: const TextStyle(fontSize: 16, color: AraColors.textSecondary),
                    ),
                  ],
                ),
              ),
              if (rating != null) ...[
                const SizedBox(width: 16),
                Flexible(
                  child: FittedBox(
                    fit: BoxFit.scaleDown,
                    alignment: Alignment.centerRight,
                    child: Container(
                      padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 10),
                      decoration: BoxDecoration(
                        color: color.withValues(alpha: 0.16),
                        borderRadius: BorderRadius.circular(16),
                      ),
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(polarErrorRatingIcon(total!), size: 34, color: color),
                          const SizedBox(width: 10),
                          Text(rating.$1,
                              style: TextStyle(fontSize: 30, fontWeight: FontWeight.w700, color: color)),
                        ],
                      ),
                    ),
                  ),
                ),
              ],
            ],
          ),
          if (rating != null) ...[
            const SizedBox(height: 14),
            Text(rating.$2, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w500)),
          ],
          const SizedBox(height: 18),
          const Divider(height: 1, color: AraColors.frameDivider),
          const SizedBox(height: 16),
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: _fact(
                  'Star drift in a 5-min exposure',
                  drift == null
                      ? '—'
                      : 'up to ${drift < 10 ? drift.toStringAsFixed(1) : drift.round().toString()}″',
                ),
              ),
              const SizedBox(width: 16),
              Expanded(
                child: _fact('Compared with the Moon', total == null ? '—' : moonWidthComparison(total)),
              ),
            ],
          ),
          const SizedBox(height: 14),
          const Text(
            '1° = 60′ (arcminutes)  ·  1′ = 60″ (arcseconds)  ·  the Moon is about 30′ across',
            style: TextStyle(fontSize: 13, color: AraColors.textSecondary),
          ),
        ],
      ),
    );
  }

  Widget _fact(String label, String value) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
        const SizedBox(height: 4),
        Text(value,
            style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w600, fontFeatures: _tabular)),
      ],
    );
  }

  Widget _axisStat(String label, double? value, (IconData, String) hint, Color color) {
    return Column(
      children: [
        Text(label, style: const TextStyle(fontSize: 16, color: AraColors.textSecondary)),
        const SizedBox(height: 2),
        // Scale down rather than overflow in a narrow (phone/tablet) column.
        FittedBox(
          fit: BoxFit.scaleDown,
          child: Text(
            formatArcmin(value),
            style: TextStyle(
                fontSize: _short ? _axisFontShort : _axisFont, fontWeight: FontWeight.w600, height: 1.1, fontFeatures: _tabular),
          ),
        ),
        const SizedBox(height: 10),
        // The knob direction as a bold tinted capsule — the thing to act on,
        // readable from the mount with less-than-perfect eyesight.
        FittedBox(
          fit: BoxFit.scaleDown,
          child: Container(
            padding: EdgeInsets.symmetric(horizontal: 16, vertical: _short ? 4 : 8),
            decoration: BoxDecoration(
              color: color.withValues(alpha: 0.16),
              borderRadius: BorderRadius.circular(14),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(hint.$1, size: (_short ? _hintFontShort : _hintFont) + 4, color: color),
                const SizedBox(width: 8),
                Text(hint.$2, style: TextStyle(fontSize: _short ? _hintFontShort : _hintFont, fontWeight: FontWeight.w700, color: color)),
              ],
            ),
          ),
        ),
      ],
    );
  }



  /// Exposure box + Loop/Single switch. Saved to the profile; a running
  /// routine picks the change up on its next frame.
  Widget _captureSettings() {
    final settings = _settings;
    return Wrap(
      crossAxisAlignment: WrapCrossAlignment.center,
      spacing: 12,
      runSpacing: 8,
      children: [
        Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Text('Exposure',
                style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
            const SizedBox(width: 8),
            SizedBox(
              width: 96,
              child: TextField(
                key: const Key('polar-align-exposure'),
                controller: _exposureCtrl,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: const InputDecoration(
                  isDense: true,
                  suffixText: 's',
                  border: OutlineInputBorder(),
                ),
                onSubmitted: (_) => _commitExposure(),
                onTapOutside: (_) {
                  FocusScope.of(context).unfocus();
                  _commitExposure();
                },
              ),
            ),
          ],
        ),
        SegmentedButton<String>(
          key: const Key('polar-align-mode'),
          showSelectedIcon: false,
          segments: const [
            ButtonSegment(
              value: PolarAlignLoopModes.loop,
              label: Text('Loop'),
              icon: Icon(Icons.loop),
            ),
            ButtonSegment(
              value: PolarAlignLoopModes.single,
              label: Text('Single'),
              icon: Icon(Icons.camera),
            ),
          ],
          selected: {settings.loopMode},
          onSelectionChanged: (selection) =>
              _saveSettings(settings.copyWith(loopMode: selection.first)),
        ),
      ],
    );
  }

  /// The guide camera's latest frame in a card, with the frame status under
  /// it. Tap the image to open it full size with zoom (focus, star shapes).
  /// [imageHeight] null: the image takes the card's spare height (the card
  /// needs a bounded height).
  Widget _cameraCard(PolarAlignLive live, {double? imageHeight}) {
    final coarse = live.stage == PolarAlignStages.coarse;
    // The main camera's snapshot has no preview endpoint: the card shows the solve readout only.
    final jpeg = coarse ? null : ref.watch(polarAlignFrameProvider);
    final fill = imageHeight == null;
    final body = [
          Row(
            children: [
              _sectionLabel(coarse ? 'Main camera' : 'Guide camera'),
              const Spacer(),
              if (jpeg != null)
                const Text('Tap to enlarge',
                    style: TextStyle(fontSize: _smallFont, color: AraColors.textSecondary)),
            ],
          ),
          const SizedBox(height: 10),
          _imageSlot(
            imageHeight,
            jpeg == null
                ? DecoratedBox(
                    decoration: BoxDecoration(
                      color: AraColors.frameDim,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: Center(
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          const Icon(Icons.photo_camera_outlined, size: 28, color: AraColors.textDisabled),
                          const SizedBox(height: 8),
                          Text(coarse ? 'Main-camera solves (no preview)' : 'Waiting for the first frame…',
                              style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
                        ],
                      ),
                    ),
                  )
                // Rounded on the image itself, so a frame narrower than the
                // card is not boxed in black bars. FittedBox, not Center: the
                // frame scales UP to the card too (it sat at its own pixel
                // size in a big empty card at 4K).
                : FittedBox(
                    child: GestureDetector(
                      key: const Key('polar-align-live-view'),
                      onTap: () => _showFullFrame(jpeg),
                      child: ClipRRect(
                        borderRadius: BorderRadius.circular(8),
                        child: Image.memory(jpeg, gaplessPlayback: true, fit: BoxFit.contain),
                      ),
                    ),
                  ),
          ),
          _frameStatus(live),
    ];
    return _card(
      padding: const EdgeInsets.all(12),
      child: fill
          ? FitColumn(children: body)
          : Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: body),
    );
  }

  /// The camera image at [height], or taking the card's spare height (never
  /// under a size that still shows stars).
  Widget _imageSlot(double? height, Widget image) => height != null
      ? SizedBox(height: height, child: image)
      : FitFill(
          child: FitBand(
            builder: (context, width, h) =>
                SizedBox(height: math.max(h, _short ? 120.0 : 150.0), child: image),
          ),
        );

  /// The frame in progress (exposing → downloading/solving, with elapsed
  /// time) and the last finished frame's timings + solved pointing.
  Widget _frameStatus(PolarAlignLive live) {
    const style = TextStyle(fontSize: _smallFont, color: AraColors.textSecondary, fontFeatures: _tabular);
    final children = <Widget>[];
    final started = live.frameStartedAt;
    if (started != null) {
      final elapsed = DateTime.now().difference(started).inMilliseconds / 1000.0;
      final exposure = live.frameExposureSeconds ?? 0;
      final exposing = elapsed < exposure;
      children.add(ClipRRect(
        borderRadius: BorderRadius.circular(2),
        child: LinearProgressIndicator(
          value: exposing && exposure > 0 ? elapsed / exposure : null,
          minHeight: 2,
        ),
      ));
      children.add(const SizedBox(height: 6));
      children.add(Text(
        exposing
            ? 'Exposing ${elapsed.toStringAsFixed(1)} / ${exposure.toStringAsFixed(1)} s…'
            : 'Downloading and solving… ${elapsed.toStringAsFixed(1)} s',
        key: const Key('polar-align-frame-progress'),
        style: style,
      ));
    }
    final last = live.lastFrame;
    if (last != null && _short) {
      // A short window: timings and pointing on one line, so the image keeps
      // the card's height.
      children.add(Text(
        '${last.exposureSeconds.toStringAsFixed(1)} s · capture ${_secondsLabel(last.captureMs)} · '
        'solve ${last.solveMs == null ? '—' : _secondsLabel(last.solveMs!)} · '
        '${last.solved && last.raDeg != null && last.decDeg != null ? 'RA ${formatRaHms(last.raDeg!)}  Dec ${formatDecDms(last.decDeg!)}' : 'did not solve'}',
        key: const Key('polar-align-last-frame'),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: last.solved ? style : style.copyWith(color: AraColors.accentBusy),
      ));
    } else if (last != null) {
      children.add(Text(
        'Last frame: ${last.exposureSeconds.toStringAsFixed(1)} s exposure · '
        'capture ${_secondsLabel(last.captureMs)} · '
        'solve ${last.solveMs == null ? '—' : _secondsLabel(last.solveMs!)}',
        key: const Key('polar-align-last-frame'),
        style: style,
      ));
      children.add(Text(
        last.solved && last.raDeg != null && last.decDeg != null
            ? 'Solved: RA ${formatRaHms(last.raDeg!)}  Dec ${formatDecDms(last.decDeg!)}'
            : 'Did not solve',
        key: const Key('polar-align-last-solve'),
        style: last.solved ? style : style.copyWith(color: AraColors.accentBusy),
      ));
    }
    if (children.isEmpty) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: children,
      ),
    );
  }

  Widget _card({Key? key, required Widget child, EdgeInsets padding = const EdgeInsets.all(16)}) {
    return Container(
      key: key,
      padding: padding,
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(12),
      ),
      child: child,
    );
  }

  Widget _sectionLabel(String text) {
    return Text(
      text.toUpperCase(),
      style: const TextStyle(
        fontSize: 11,
        fontWeight: FontWeight.w600,
        letterSpacing: 0.8,
        color: AraColors.textSecondary,
      ),
    );
  }

  /// Tinted inline notice: icon + message on a soft background.
  Widget _callout(String text, Color tint, IconData icon, {Key? key}) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
      decoration: BoxDecoration(
        color: tint.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        children: [
          Icon(icon, size: 18, color: tint),
          const SizedBox(width: 10),
          Expanded(
            child: Text(text, key: key, style: TextStyle(fontSize: _textFont, color: tint)),
          ),
        ],
      ),
    );
  }

  void _abort() => _run('abort polar alignment', (api) => api.stop());
}

/// Header icon: a polar-scope reticle (three rings + crosshair) with a green
/// dot on the pole.
class PolarScopeIconPainter extends CustomPainter {
  const PolarScopeIconPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = math.min(size.width, size.height) / 2 - 0.5;
    final line = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1
      ..color = AraColors.textSecondary;
    for (final f in const [1.0, 2 / 3, 1 / 3]) {
      canvas.drawCircle(center, radius * f, line);
    }
    canvas.drawLine(center - Offset(radius, 0), center + Offset(radius, 0), line);
    canvas.drawLine(center - Offset(0, radius), center + Offset(0, radius), line);
    canvas.drawCircle(center, radius * 0.16, Paint()..color = AraColors.accentConnected);
  }

  @override
  bool shouldRepaint(PolarScopeIconPainter oldDelegate) => false;
}

