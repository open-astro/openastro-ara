import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../state/night_mode_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../fit_pane.dart';
import 'polar_error_rating.dart';

export 'polar_error_rating.dart';

/// §45.10 dynamic bullseye zoom: the outer ring's radius in arcminutes for the
/// current total error — ~5° while far off, 30′ once under 1°, 5′ once under
/// 5′ (spec says 1′ under 5′; 5′ keeps the dot on-screen while the user
/// overshoots around zero). Pure — unit-tested.
double bullseyeRangeArcmin(double? totalErrorArcmin) {
  final total = totalErrorArcmin;
  if (total == null || total >= 60.0) return 300.0;
  if (total >= 5.0) return 30.0;
  return 5.0;
}

/// Fractional dot offset inside the bullseye for the current error —
/// x = azimuth (east positive → right), y = altitude (above pole → up),
/// clamped to the ring edge so an off-scale dot stays visible. Pure —
/// unit-tested.
Offset bullseyeDotFraction(double? azErrArcmin, double? altErrArcmin, double rangeArcmin) {
  final az = (azErrArcmin ?? 0) / rangeArcmin;
  final alt = (altErrArcmin ?? 0) / rangeArcmin;
  final len = math.sqrt(az * az + alt * alt);
  if (len <= 1.0) return Offset(az, -alt);
  return Offset(az / len, -alt / len);
}

/// Format a signed arcminute value like `+14.2′` / `−23.4′`.
String formatArcmin(double? v) {
  if (v == null) return '—';
  final sign = v >= 0 ? '+' : '−';
  return '$sign${v.abs().toStringAsFixed(1)}′';
}

/// Worst-case declination drift, in arcseconds, that a polar error of
/// [arcmin] causes over [seconds]: error × Earth's rotation rate
/// (7.292e-5 rad/s), i.e. ~0.26″ per minute per arcminute of error.
/// Pure — unit-tested.
double maxDriftArcsec(double arcmin, double seconds) => arcmin.abs() * 60 * 7.2921e-5 * seconds;

/// [arcmin] as a share of the Moon's ~31′ width: `1/39 of the Moon's width`
/// or `1.5× the Moon's width`. Pure — unit-tested.
String moonWidthComparison(double arcmin) {
  const moon = 31.0;
  final a = arcmin.abs();
  if (a <= 0) return "0 × the Moon's width";
  if (a >= moon) return "${(a / moon).toStringAsFixed(1)}× the Moon's width";
  return "1/${(moon / a).round()} of the Moon's width";
}

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
  bool _busy = false;
  String? _status;
  double _toleranceArcmin = 1.0;
  PolarAlignSettings _settings = const PolarAlignSettings();
  final _exposureCtrl = TextEditingController(text: _exposureText(1.0));

  // Repaints the exposing/solving readout while a frame is in progress.
  Timer? _ticker;

  // Live view: the guide camera's latest frame, refetched after every frame.
  Uint8List? _frameJpeg;
  bool _frameLoading = false;
  // A newer frame finished while a fetch was in flight: fetch again after it,
  // or single-frame mode would keep showing the older image.
  bool _frameReloadPending = false;

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

  /// Seed the live view + tolerance from REST on open (the WS resume may have
  /// skipped past routine events fired before this client connected).
  Future<void> _hydrate() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    try {
      final status = await api.getStatus();
      if (status != null) {
        ref.read(polarAlignLiveProvider.notifier).hydrateFromStatus(status);
        if (status.isActive) unawaited(_loadFrame());
      }
      final settings = await api.getSettings();
      if (mounted) {
        setState(() {
          _toleranceArcmin = settings.targetToleranceArcmin;
          _settings = settings;
          _exposureCtrl.text = _exposureText(settings.exposureSeconds);
        });
      }
    } catch (_) {
      // Best-effort: the panel still works purely off the WS stream.
    }
  }

  // [action] is a verb phrase completing "Couldn't <action>".
  Future<void> _run(String action, Future<void> Function() op) async {
    setState(() {
      _busy = true;
      _status = null;
    });
    try {
      await op();
    } catch (e) {
      if (mounted) {
        setState(() => _status = friendlyError(e, action: action));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// Persist [next] to the profile — the running routine re-reads it before
  /// every frame, so exposure and mode changes apply to the next frame.
  /// A failed save rolls the panel back to [previous], so it never shows an
  /// exposure or mode the server is not using.
  void _saveSettings(PolarAlignSettings next) {
    final previous = _settings;
    setState(() => _settings = next);
    _run('save the polar-align settings', () async {
      final api = ref.read(polarAlignApiProvider);
      if (api == null) return;
      try {
        final saved = await api.putSettings(next);
        if (mounted) setState(() => _settings = saved);
      } catch (_) {
        if (mounted) {
          setState(() {
            _settings = previous;
            _exposureCtrl.text = _exposureText(previous.exposureSeconds);
          });
        }
        rethrow;
      }
    });
  }

  void _commitExposure() {
    final value = double.tryParse(_exposureCtrl.text.trim());
    if (value == null || value <= 0 || value > 60) {
      setState(() {
        _status = 'Exposure must be more than 0 and at most 60 seconds.';
        _exposureCtrl.text = _exposureText(_settings.exposureSeconds);
      });
      return;
    }
    if (value == _settings.exposureSeconds) return;
    _saveSettings(_settings.copyWith(exposureSeconds: value));
  }

  /// Best-effort: a missing or failed frame keeps the previous image.
  Future<void> _loadFrame() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    if (_frameLoading) {
      _frameReloadPending = true;
      return;
    }
    _frameLoading = true;
    try {
      final jpeg = await api.getLiveFrame();
      if (mounted && jpeg != null) setState(() => _frameJpeg = jpeg);
    } catch (_) {
      // Keep showing the last frame.
    } finally {
      _frameLoading = false;
    }
    if (_frameReloadPending && mounted) {
      _frameReloadPending = false;
      unawaited(_loadFrame());
    }
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

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(polarAlignLiveProvider);
    final api = ref.watch(polarAlignApiProvider);
    _syncTicker(live.frameInProgress);
    // Each finished frame (solved or not) replaces lastFrame — fetch its image.
    ref.listen(polarAlignLiveProvider, (prev, next) {
      if (next.lastFrame != null && !identical(prev?.lastFrame, next.lastFrame)) {
        _loadFrame();
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
          if (!(_short &&
              const {PolarAlignStates.seeding, PolarAlignStates.adjusting, PolarAlignStates.paused}
                  .contains(live.phase))) ...[
            Row(
              children: [
                const SizedBox.square(dimension: 22, child: CustomPaint(painter: PolarScopeIconPainter())),
                const SizedBox(width: 10),
                Text('Polar Align', style: Theme.of(context).textTheme.titleLarge),
                const Spacer(),
                _statusPill(live),
              ],
            ),
            const SizedBox(height: 12),
          ],
          FitFill(child: _body(live, api == null, color)),
        ],
      ),
    );
  }

  String _headerSummary(PolarAlignLive live) {
    switch (live.phase) {
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
      case PolarAlignStates.seeding:
        return _seedingBody(live);
      case PolarAlignStates.adjusting:
      case PolarAlignStates.paused:
        return _adjustBody(live, color);
      default:
        return _idleBody(live);
    }
  }

  Widget _idleBody(PolarAlignLive live) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text(
          'Roughly point your mount at the celestial pole, make sure the guider '
          'and mount are connected, then start. The routine takes two solved '
          'frames around a small RA slew, then guides your alt/az knob '
          'adjustments live.',
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
              onPressed: _busy
                  ? null
                  : () => _run('start polar alignment', () async {
                        final api = ref.read(polarAlignApiProvider);
                        if (api != null) await api.start();
                      }),
              icon: const Icon(Icons.play_arrow),
              label: const Text('Start Polar Alignment'),
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
          const SizedBox.square(dimension: 28, child: CircularProgressIndicator(strokeWidth: 2.5)),
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
    return FitColumn(
      children: [
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
                        : () => _run('take a frame', () async {
                              final api = ref.read(polarAlignApiProvider);
                              if (api != null) await api.requestCapture();
                            }),
                    icon: const Icon(Icons.camera),
                    label: const Text('Take Frame'),
                  ),
                Tooltip(
                  message: inTolerance
                      ? ''
                      : 'Available once the total error is under ${_toleranceArcmin.toStringAsFixed(1)}′',
                  child: FilledButton.icon(
                    key: const Key('polar-align-done'),
                    onPressed: _busy || !inTolerance
                        ? null
                        : () => _run('complete polar alignment', () async {
                              final api = ref.read(polarAlignApiProvider);
                              if (api != null) await api.complete();
                            }),
                    icon: const Icon(Icons.check),
                    label: const Text('Done'),
                  ),
                ),
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
    final ring = range >= 60 ? '${(range / 60).toStringAsFixed(0)}°' : '${range.toStringAsFixed(0)}′';
    // Square, as large as the spare room allows up to a size that still reads
    // as an instrument rather than a poster.
    final bullseye = ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 560, maxHeight: 560),
      child: AspectRatio(
        aspectRatio: 1,
        child: CustomPaint(
          painter: BullseyePainter(
            dotFraction: bullseyeDotFraction(live.azErrorArcmin, live.altErrorArcmin, range),
            zoneColor: color,
          ),
        ),
      ),
    );
    final body = [
          Row(
            children: [
              _sectionLabel('Alignment'),
              const Spacer(),
              Text('Ring $ring',
                  style: const TextStyle(fontSize: _smallFont, color: AraColors.textSecondary, fontFeatures: _tabular)),
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
              const Divider(height: 1, color: Color(0x1FFFFFFF)),
              const SizedBox(height: 18),
              IntrinsicHeight(
                child: Row(
                  children: [
                    Expanded(child: _axisStat('Altitude', live.altErrorArcmin, _altHint(live.altErrorArcmin), color)),
                    const VerticalDivider(width: 1, color: Color(0x1FFFFFFF)),
                    Expanded(child: _axisStat('Azimuth', live.azErrorArcmin, _azHint(live.azErrorArcmin), color)),
                  ],
                ),
              ),
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
    return _card(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
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
          Expanded(child: _axisStat('Altitude', live.altErrorArcmin, _altHint(live.altErrorArcmin), color)),
          Expanded(child: _axisStat('Azimuth', live.azErrorArcmin, _azHint(live.azErrorArcmin), color)),
        ],
      ),
    );
  }

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
          const Divider(height: 1, color: Color(0x1FFFFFFF)),
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

  /// Decoupled knob directions (§45 design: chasing one coupled 2-D error is
  /// what makes people feel lost). Positive alt = axis above the pole → lower;
  /// positive az = axis east of the pole → move west.
  static (IconData, String) _altHint(double? alt) {
    if (alt == null) return (Icons.remove, '—');
    if (alt.abs() <= 0.05) return (Icons.check, 'On target');
    return alt > 0 ? (Icons.arrow_downward, 'Lower') : (Icons.arrow_upward, 'Raise');
  }

  static (IconData, String) _azHint(double? az) {
    if (az == null) return (Icons.remove, '—');
    if (az.abs() <= 0.05) return (Icons.check, 'On target');
    return az > 0 ? (Icons.arrow_back, 'Move west') : (Icons.arrow_forward, 'Move east');
  }

  /// Exposure box + Loop/Single switch. Saved to the profile; a running
  /// routine picks the change up on its next frame.
  Widget _captureSettings() {
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
          selected: {_settings.loopMode},
          onSelectionChanged: (selection) =>
              _saveSettings(_settings.copyWith(loopMode: selection.first)),
        ),
      ],
    );
  }

  /// The guide camera's latest frame in a card, with the frame status under
  /// it. Tap the image to open it full size with zoom (focus, star shapes).
  /// [imageHeight] null: the image takes the card's spare height (the card
  /// needs a bounded height).
  Widget _cameraCard(PolarAlignLive live, {double? imageHeight}) {
    final jpeg = _frameJpeg;
    final fill = imageHeight == null;
    final body = [
          Row(
            children: [
              _sectionLabel('Guide camera'),
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
                      color: Colors.black26,
                      borderRadius: BorderRadius.circular(8),
                    ),
                    child: const Center(
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(Icons.photo_camera_outlined, size: 28, color: AraColors.textDisabled),
                          SizedBox(height: 8),
                          Text('Waiting for the first frame…',
                              style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary)),
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

  void _abort() => _run('abort polar alignment', () async {
        final api = ref.read(polarAlignApiProvider);
        if (api != null) await api.stop();
      });
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

/// The zooming bullseye: three concentric rings, cross-hairs, and the RA-axis
/// dot at [dotFraction] (unit square, (0,0) = pole).
class BullseyePainter extends CustomPainter {
  final Offset dotFraction;
  final Color zoneColor;

  const BullseyePainter({required this.dotFraction, required this.zoneColor});

  @override
  void paint(Canvas canvas, Size size) {
    final center = Offset(size.width / 2, size.height / 2);
    final radius = math.min(size.width, size.height) / 2 - 4;
    final ring = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1
      ..color = AraColors.textSecondary.withValues(alpha: 0.5);
    for (final f in const [1.0, 2 / 3, 1 / 3]) {
      canvas.drawCircle(center, radius * f, ring);
    }
    canvas.drawLine(center - Offset(radius, 0), center + Offset(radius, 0), ring);
    canvas.drawLine(center - Offset(0, radius), center + Offset(0, radius), ring);

    final dot = Paint()..color = zoneColor;
    final pos = center + Offset(dotFraction.dx * radius, dotFraction.dy * radius);
    canvas.drawCircle(pos, math.max(10.0, radius * 0.06), dot);
    // A subtle line from the dot back to the pole — the direction to drive it.
    final tether = Paint()
      ..strokeWidth = math.max(2.5, radius * 0.015)
      ..color = zoneColor.withValues(alpha: 0.4);
    canvas.drawLine(pos, center, tether);
  }

  @override
  bool shouldRepaint(covariant BullseyePainter old) =>
      old.dotFraction != dotFraction || old.zoneColor != zoneColor;
}
