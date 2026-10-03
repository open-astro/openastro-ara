import 'dart:async';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';

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

/// §45.10 color zones: red > 1°, yellow 10′–1°, green < 10′. Pure — unit-tested.
Color zoneColor(double? totalErrorArcmin) {
  final total = totalErrorArcmin;
  if (total == null) return AraColors.textSecondary;
  if (total >= 60.0) return AraColors.accentError;
  if (total >= 10.0) return AraColors.accentBusy;
  return AraColors.accentConnected;
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

// Sized to read from a laptop or tablet beside the mount while turning knobs.
const double _textFont = 20;
const double _smallFont = 16;
const double _readoutFont = 44;
const double _hintFont = 30;
const _bigButton = ButtonStyle(
  textStyle: WidgetStatePropertyAll(TextStyle(fontSize: 22, fontWeight: FontWeight.w600)),
  padding: WidgetStatePropertyAll(EdgeInsets.symmetric(horizontal: 24, vertical: 16)),
  iconSize: WidgetStatePropertyAll(26),
);

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
  void _saveSettings(PolarAlignSettings next) {
    setState(() => _settings = next);
    _run('save the polar-align settings', () async {
      final api = ref.read(polarAlignApiProvider);
      if (api == null) return;
      final saved = await api.putSettings(next);
      if (mounted) setState(() => _settings = saved);
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
    if (api == null || _frameLoading) return;
    _frameLoading = true;
    try {
      final jpeg = await api.getLiveFrame();
      if (mounted && jpeg != null) setState(() => _frameJpeg = jpeg);
    } catch (_) {
      // Keep showing the last frame.
    } finally {
      _frameLoading = false;
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
    final color = zoneColor(live.totalErrorArcmin);
    final active = live.phase == PolarAlignStates.seeding ||
        live.phase == PolarAlignStates.adjusting ||
        live.phase == PolarAlignStates.paused;

    // Always-on panel: the live bullseye + Az/Alt/Total readout stay visible
    // on the page (like the equipment chips) instead of hiding behind a
    // collapse — polar alignment is a hands-on, eyes-on process.
    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 10, 12, 0),
            child: Row(
              children: [
                Icon(Icons.adjust, size: 30, color: active ? color : AraColors.textSecondary),
                const SizedBox(width: 8),
                const Text('Polar Align', style: TextStyle(fontSize: 26, fontWeight: FontWeight.w600)),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    _headerSummary(live),
                    key: const Key('polar-align-header-summary'),
                    textAlign: TextAlign.right,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(color: active ? color : AraColors.textSecondary, fontSize: _textFont),
                  ),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 8, 12, 12),
            child: _body(live, api == null, color),
          ),
        ],
      ),
    );
  }

  String _headerSummary(PolarAlignLive live) {
    switch (live.phase) {
      case PolarAlignStates.seeding:
        return 'measuring axis…';
      case PolarAlignStates.adjusting:
        return 'Total ${formatArcmin(live.totalErrorArcmin)}';
      case PolarAlignStates.paused:
        return 'paused — no solve';
      case PolarAlignStates.failed:
        return 'failed';
      default:
        return '';
    }
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
          style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary),
        ),
        if (live.phase == PolarAlignStates.failed && live.errorReason != null) ...[
          const SizedBox(height: 8),
          Text(
            'Failed (${live.errorReason}): ${live.errorMessage ?? 'see the server log'}',
            key: const Key('polar-align-error-banner'),
            style: const TextStyle(fontSize: _textFont, color: AraColors.accentError),
          ),
        ],
        if (live.phase == PolarAlignStates.stopped && live.totalErrorArcmin != null) ...[
          const SizedBox(height: 8),
          Text(
            'Last run ended at ${formatArcmin(live.totalErrorArcmin)} total error.',
            style: const TextStyle(fontSize: _textFont, color: AraColors.textSecondary),
          ),
        ],
        if (_status != null) ...[
          const SizedBox(height: 8),
          Text(_status!, style: const TextStyle(fontSize: _textFont, color: AraColors.accentError)),
        ],
        const SizedBox(height: 8),
        _captureSettings(),
        const SizedBox(height: 8),
        FilledButton.icon(
          key: const Key('polar-align-start'),
          style: _bigButton,
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
    );
  }

  Widget _seedingBody(PolarAlignLive live) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _seedingRow(),
        _liveView(live),
        _frameStatus(live),
      ],
    );
  }

  Widget _seedingRow() {
    return Row(
      children: [
        const SizedBox(
          width: 14,
          height: 14,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
        const SizedBox(width: 10),
        const Expanded(
          child: Text(
            'Measuring the RA axis — two solved frames around a small RA slew…',
            style: TextStyle(fontSize: _textFont),
          ),
        ),
        TextButton(
          key: const Key('polar-align-abort-seeding'),
          style: _bigButton,
          onPressed: _busy ? null : _abort,
          child: const Text('Abort'),
        ),
      ],
    );
  }

  Widget _adjustBody(PolarAlignLive live, Color color) {
    final range = bullseyeRangeArcmin(live.totalErrorArcmin);
    final inTolerance = live.totalErrorArcmin != null && live.totalErrorArcmin! <= _toleranceArcmin;
    final single = _settings.loopMode == PolarAlignLoopModes.single;
    return Column(
      children: [
        if (live.phase == PolarAlignStates.paused)
          const Padding(
            padding: EdgeInsets.only(bottom: 8),
            child: Text(
              'No solve — check sky and focus. Retrying…',
              key: Key('polar-align-paused-banner'),
              style: TextStyle(fontSize: 22, fontWeight: FontWeight.w600, color: AraColors.accentBusy),
            ),
          )
        else if (live.consecutiveSolveFailures > 0)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(
              'No solve (${live.consecutiveSolveFailures}) — check sky and focus.',
              key: const Key('polar-align-retry-banner'),
              style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w600, color: AraColors.accentBusy),
            ),
          ),
        // Wide window: camera image left, bullseye + readout right, so both
        // stay on screen together. Narrow (tablet portrait): stacked.
        LayoutBuilder(builder: (context, constraints) {
          final aim = Column(
            children: [
              SizedBox(
                width: 320,
                height: 320,
                child: CustomPaint(
                  painter: BullseyePainter(
                    dotFraction: bullseyeDotFraction(
                        live.azErrorArcmin, live.altErrorArcmin, range),
                    zoneColor: color,
                  ),
                ),
              ),
              const SizedBox(height: 4),
              Text('ring: ${range >= 60 ? '${(range / 60).toStringAsFixed(0)}°' : '${range.toStringAsFixed(0)}′'}',
                  style: const TextStyle(fontSize: _smallFont, color: AraColors.textSecondary)),
              const SizedBox(height: 8),
              Wrap(
                key: const Key('polar-align-readout'),
                alignment: WrapAlignment.center,
                spacing: 24,
                runSpacing: 4,
                children: [
                  for (final value in [
                    'Alt: ${formatArcmin(live.altErrorArcmin)}',
                    'Az: ${formatArcmin(live.azErrorArcmin)}',
                    'Total: ${formatArcmin(live.totalErrorArcmin)}',
                  ])
                    Text(value,
                        style: TextStyle(fontSize: _readoutFont, fontWeight: FontWeight.w700, color: color)),
                ],
              ),
              const SizedBox(height: 6),
              Text(
                _knobHint(live),
                textAlign: TextAlign.center,
                style: const TextStyle(fontSize: _hintFont, fontWeight: FontWeight.w600),
              ),
              if (single)
                const Text(
                  'Single frame: turn a knob, then Take Frame.',
                  style: TextStyle(fontSize: _textFont, color: AraColors.textSecondary),
                ),
            ],
          );
          if (_frameJpeg != null && constraints.maxWidth >= 680) {
            return Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(child: _liveView(live, maxHeight: 520)),
                const SizedBox(width: 16),
                SizedBox(width: 360, child: aim),
              ],
            );
          }
          return Column(
            children: [
              _liveView(live),
              const SizedBox(height: 8),
              aim,
            ],
          );
        }),
        const SizedBox(height: 8),
        _captureSettings(),
        _frameStatus(live),
        if (_status != null) ...[
          const SizedBox(height: 8),
          Text(_status!, style: const TextStyle(fontSize: _textFont, color: AraColors.accentError)),
        ],
        const SizedBox(height: 10),
        Wrap(
          alignment: WrapAlignment.center,
          spacing: 12,
          runSpacing: 8,
          children: [
            if (single)
              FilledButton.tonalIcon(
                key: const Key('polar-align-take-frame'),
                style: _bigButton,
                onPressed: _busy || live.frameInProgress
                    ? null
                    : () => _run('take a frame', () async {
                          final api = ref.read(polarAlignApiProvider);
                          if (api != null) await api.requestCapture();
                        }),
                icon: const Icon(Icons.camera),
                label: const Text('Take Frame'),
              ),
            FilledButton.icon(
              key: const Key('polar-align-done'),
              style: _bigButton,
              onPressed: _busy || !inTolerance
                  ? null
                  : () => _run('complete polar alignment', () async {
                        final api = ref.read(polarAlignApiProvider);
                        if (api != null) await api.complete();
                      }),
              icon: const Icon(Icons.check),
              label: Text('Done${inTolerance ? '' : ' (< ${_toleranceArcmin.toStringAsFixed(1)}′)'}'),
            ),
            OutlinedButton(
              key: const Key('polar-align-abort'),
              style: _bigButton,
              onPressed: _busy ? null : _abort,
              child: const Text('Abort'),
            ),
          ],
        ),
      ],
    );
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
            const SizedBox(width: 6),
            SizedBox(
              width: 120,
              child: TextField(
                key: const Key('polar-align-exposure'),
                controller: _exposureCtrl,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                style: const TextStyle(fontSize: 22),
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
          style: const ButtonStyle(
            textStyle: WidgetStatePropertyAll(TextStyle(fontSize: _textFont)),
            iconSize: WidgetStatePropertyAll(24),
          ),
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

  /// The guide camera's latest frame. Tap to open it full size with zoom
  /// (check focus and star shapes).
  Widget _liveView(PolarAlignLive live, {double maxHeight = 300}) {
    final jpeg = _frameJpeg;
    if (jpeg == null) return const SizedBox.shrink();
    final last = live.lastFrame;
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          GestureDetector(
            key: const Key('polar-align-live-view'),
            onTap: () => _showFullFrame(jpeg),
            child: ConstrainedBox(
              constraints: BoxConstraints(maxHeight: maxHeight),
              child: ClipRRect(
                borderRadius: BorderRadius.circular(4),
                child: Image.memory(jpeg, gaplessPlayback: true, fit: BoxFit.contain),
              ),
            ),
          ),
          const SizedBox(height: 2),
          Text(
            'Guide camera${last == null ? '' : ' · ${last.frameId}'} — tap to enlarge',
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: _smallFont, color: AraColors.textSecondary),
          ),
        ],
      ),
    );
  }

  /// The frame in progress (exposing → downloading/solving, with elapsed
  /// time) and the last finished frame's timings + solved pointing.
  Widget _frameStatus(PolarAlignLive live) {
    const style = TextStyle(fontSize: _textFont, color: AraColors.textSecondary);
    final children = <Widget>[];
    final started = live.frameStartedAt;
    if (started != null) {
      final elapsed = DateTime.now().difference(started).inMilliseconds / 1000.0;
      final exposure = live.frameExposureSeconds ?? 0;
      final exposing = elapsed < exposure;
      children.add(Text(
        exposing
            ? 'Exposing ${elapsed.toStringAsFixed(1)} / ${exposure.toStringAsFixed(1)} s…'
            : 'Downloading and solving… ${elapsed.toStringAsFixed(1)} s',
        key: const Key('polar-align-frame-progress'),
        style: style,
      ));
      children.add(Padding(
        padding: const EdgeInsets.only(top: 4),
        child: LinearProgressIndicator(
          value: exposing && exposure > 0 ? elapsed / exposure : null,
          minHeight: 3,
        ),
      ));
    }
    final last = live.lastFrame;
    if (last != null) {
      if (children.isNotEmpty) children.add(const SizedBox(height: 6));
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
        style: TextStyle(
          fontSize: _textFont,
          color: last.solved ? AraColors.textSecondary : AraColors.accentBusy,
        ),
      ));
    }
    if (children.isEmpty) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: children,
      ),
    );
  }

  void _abort() => _run('abort polar alignment', () async {
        final api = ref.read(polarAlignApiProvider);
        if (api != null) await api.stop();
      });

  /// Decoupled knob directions (§45 design: chasing one coupled 2-D error is
  /// what makes people feel lost). Positive alt = axis above the pole → lower;
  /// positive az = axis east of the pole → move west.
  String _knobHint(PolarAlignLive live) {
    final parts = <String>[];
    final alt = live.altErrorArcmin;
    final az = live.azErrorArcmin;
    if (alt != null && alt.abs() > 0.05) {
      parts.add('Alt: ${alt > 0 ? 'lower ▼' : 'raise ▲'}');
    }
    if (az != null && az.abs() > 0.05) {
      parts.add('Az: ${az > 0 ? 'move west ◀' : 'move east ▶'}');
    }
    return parts.isEmpty ? 'On the pole — nice.' : parts.join('    ');
  }
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
    canvas.drawCircle(pos, 10, dot);
    // A subtle line from the dot back to the pole — the direction to drive it.
    final tether = Paint()
      ..strokeWidth = 2.5
      ..color = zoneColor.withValues(alpha: 0.4);
    canvas.drawLine(pos, center, tether);
  }

  @override
  bool shouldRepaint(covariant BullseyePainter old) =>
      old.dotFraction != dotFraction || old.zoneColor != zoneColor;
}
