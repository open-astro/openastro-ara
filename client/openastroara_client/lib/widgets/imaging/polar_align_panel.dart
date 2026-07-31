import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../theme/ara_colors.dart';

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
  bool _expanded = false;
  bool _busy = false;
  String? _status;
  double _toleranceArcmin = 1.0;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
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
      }
      final settings = await api.getSettings();
      if (mounted) setState(() => _toleranceArcmin = settings.targetToleranceArcmin);
    } catch (_) {
      // Best-effort: the panel still works purely off the WS stream.
    }
  }

  Future<void> _run(String label, Future<void> Function() op) async {
    setState(() {
      _busy = true;
      _status = null;
    });
    try {
      await op();
    } catch (e) {
      if (mounted) setState(() => _status = '$label failed: $e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(polarAlignLiveProvider);
    final api = ref.watch(polarAlignApiProvider);
    final color = zoneColor(live.totalErrorArcmin);
    final active = live.phase == PolarAlignStates.seeding ||
        live.phase == PolarAlignStates.adjusting ||
        live.phase == PolarAlignStates.paused;

    return Container(
      margin: const EdgeInsets.only(bottom: 8),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
              child: Row(
                children: [
                  Icon(Icons.adjust, size: 18, color: active ? color : AraColors.textSecondary),
                  const SizedBox(width: 8),
                  const Text('Polar Align', style: TextStyle(fontWeight: FontWeight.w600)),
                  const Spacer(),
                  Text(
                    _headerSummary(live),
                    key: const Key('polar-align-header-summary'),
                    style: TextStyle(color: active ? color : AraColors.textSecondary, fontSize: 12),
                  ),
                  const SizedBox(width: 6),
                  Icon(_expanded ? Icons.expand_less : Icons.expand_more, size: 18),
                ],
              ),
            ),
          ),
          if (_expanded)
            Padding(
              padding: const EdgeInsets.fromLTRB(12, 0, 12, 12),
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
      return const Text('No active server.', style: TextStyle(color: AraColors.textSecondary));
    }
    switch (live.phase) {
      case PolarAlignStates.seeding:
        return _seedingBody();
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
          style: TextStyle(fontSize: 12, color: AraColors.textSecondary),
        ),
        if (live.phase == PolarAlignStates.failed && live.errorReason != null) ...[
          const SizedBox(height: 8),
          Text(
            'Failed (${live.errorReason}): ${live.errorMessage ?? 'see the server log'}',
            key: const Key('polar-align-error-banner'),
            style: const TextStyle(fontSize: 12, color: AraColors.accentError),
          ),
        ],
        if (live.phase == PolarAlignStates.stopped && live.totalErrorArcmin != null) ...[
          const SizedBox(height: 8),
          Text(
            'Last run ended at ${formatArcmin(live.totalErrorArcmin)} total error.',
            style: const TextStyle(fontSize: 12, color: AraColors.textSecondary),
          ),
        ],
        if (_status != null) ...[
          const SizedBox(height: 8),
          Text(_status!, style: const TextStyle(fontSize: 12, color: AraColors.accentError)),
        ],
        const SizedBox(height: 8),
        FilledButton.icon(
          key: const Key('polar-align-start'),
          onPressed: _busy
              ? null
              : () => _run('Start', () async {
                    final api = ref.read(polarAlignApiProvider);
                    if (api != null) await api.start();
                  }),
          icon: const Icon(Icons.play_arrow, size: 16),
          label: const Text('Start Polar Alignment'),
        ),
      ],
    );
  }

  Widget _seedingBody() {
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
            style: TextStyle(fontSize: 12),
          ),
        ),
        TextButton(
          key: const Key('polar-align-abort-seeding'),
          onPressed: _busy ? null : _abort,
          child: const Text('Abort'),
        ),
      ],
    );
  }

  Widget _adjustBody(PolarAlignLive live, Color color) {
    final range = bullseyeRangeArcmin(live.totalErrorArcmin);
    final inTolerance = live.totalErrorArcmin != null && live.totalErrorArcmin! <= _toleranceArcmin;
    return Column(
      children: [
        if (live.phase == PolarAlignStates.paused)
          const Padding(
            padding: EdgeInsets.only(bottom: 8),
            child: Text(
              'No solve — check sky and focus. Retrying…',
              key: Key('polar-align-paused-banner'),
              style: TextStyle(fontSize: 12, color: AraColors.accentBusy),
            ),
          )
        else if (live.consecutiveSolveFailures > 0)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(
              'No solve (${live.consecutiveSolveFailures}) — check sky and focus.',
              key: const Key('polar-align-retry-banner'),
              style: const TextStyle(fontSize: 12, color: AraColors.accentBusy),
            ),
          ),
        SizedBox(
          width: 180,
          height: 180,
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
            style: const TextStyle(fontSize: 10, color: AraColors.textSecondary)),
        const SizedBox(height: 8),
        Text(
          'Az: ${formatArcmin(live.azErrorArcmin)}   '
          'Alt: ${formatArcmin(live.altErrorArcmin)}   '
          'Total: ${formatArcmin(live.totalErrorArcmin)}',
          key: const Key('polar-align-readout'),
          style: TextStyle(fontSize: 14, fontWeight: FontWeight.w600, color: color),
        ),
        const SizedBox(height: 4),
        Text(
          _knobHint(live),
          style: const TextStyle(fontSize: 12, color: AraColors.textSecondary),
        ),
        if (_status != null) ...[
          const SizedBox(height: 8),
          Text(_status!, style: const TextStyle(fontSize: 12, color: AraColors.accentError)),
        ],
        const SizedBox(height: 10),
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            FilledButton.icon(
              key: const Key('polar-align-done'),
              onPressed: _busy || !inTolerance
                  ? null
                  : () => _run('Done', () async {
                        final api = ref.read(polarAlignApiProvider);
                        if (api != null) await api.complete();
                      }),
              icon: const Icon(Icons.check, size: 16),
              label: Text('Done${inTolerance ? '' : ' (< ${_toleranceArcmin.toStringAsFixed(1)}′)'}'),
            ),
            const SizedBox(width: 12),
            OutlinedButton(
              key: const Key('polar-align-abort'),
              onPressed: _busy ? null : _abort,
              child: const Text('Abort'),
            ),
          ],
        ),
      ],
    );
  }

  void _abort() => _run('Abort', () async {
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
    canvas.drawCircle(pos, 6, dot);
    // A subtle line from the dot back to the pole — the direction to drive it.
    final tether = Paint()
      ..strokeWidth = 1.5
      ..color = zoneColor.withValues(alpha: 0.4);
    canvas.drawLine(pos, center, tether);
  }

  @override
  bool shouldRepaint(covariant BullseyePainter old) =>
      old.dotFraction != dotFraction || old.zoneColor != zoneColor;
}
