import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/rotation_assist.dart';
import '../../state/rotation/rotation_assist_state.dart';
import '../../state/sky_atlas/plan_framing_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../help_icon.dart';
import 'rotation_overlay.dart';

/// The by-hand rotation readout, docked beside the planetarium on the Plan
/// screen. The planned angle is the framing dial's; the daemon plate-solves
/// the main camera against it — one frame at a time (**Single**: take a frame,
/// turn, take another) or continuously (**Loop**) — and the panel shows the
/// delta still to turn, advice relative to the last move, and the solved frame
/// with north and the planned framing drawn over it. Every solve also lands on
/// the sky as the amber scope box beside the planned (blue) framing box.
class RotationAssistPanel extends ConsumerStatefulWidget {
  static const double width = 360;

  const RotationAssistPanel({super.key});

  @override
  ConsumerState<RotationAssistPanel> createState() =>
      _RotationAssistPanelState();
}

class _RotationAssistPanelState extends ConsumerState<RotationAssistPanel> {
  final _exposureCtrl = TextEditingController();
  String _mode = RotationAssistModes.loop;
  // Set once from the daemon's default so a user's own value is never clobbered
  // by a later status poll.
  bool _exposureSeeded = false;

  @override
  void dispose() {
    _exposureCtrl.dispose();
    super.dispose();
  }

  void _seedExposure(RotationAssistStatus status) {
    if (_exposureSeeded) return;
    final s = status.exposureSeconds > 0
        ? status.exposureSeconds
        : status.defaultExposureSeconds;
    if (s <= 0) return;
    _exposureSeeded = true;
    _exposureCtrl.text = _fmtSeconds(s);
    if (status.mode == RotationAssistModes.single) {
      _mode = RotationAssistModes.single;
    }
  }

  static String _fmtSeconds(double s) =>
      s == s.roundToDouble() ? s.toStringAsFixed(0) : s.toStringAsFixed(2);

  /// The exposure field as seconds, or null when it is not a usable number
  /// (the daemon then falls back to the profile's plate-solve exposure).
  double? get _exposureSeconds {
    final v = double.tryParse(_exposureCtrl.text.trim().replaceAll(',', '.'));
    if (v == null || !v.isFinite || v <= 0 || v > 60) return null;
    return v;
  }

  Future<void> _start(double positionAngleDeg) => ref
      .read(rotationAssistProvider.notifier)
      .start(
        positionAngleDeg: positionAngleDeg,
        exposureSeconds: _exposureSeconds,
        mode: _mode,
      );

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(rotationAssistProvider);
    final status = live.status;
    final framing = ref.watch(planFramingProvider);
    _seedExposure(status);
    final hasTarget = framing.hasTarget;
    final exposureOk = _exposureSeconds != null || _exposureCtrl.text.isEmpty;
    final canStart = hasTarget && !live.busy && !status.active && exposureOk;
    final single = _mode == RotationAssistModes.single;

    return Material(
      color: AraColors.bgPanel,
      child: SizedBox(
        width: RotationAssistPanel.width,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _header(context),
            Expanded(
              child: ListView(
                key: const Key('rotation-assist-panel'),
                padding: const EdgeInsets.fromLTRB(
                  AraSpace.s16,
                  AraSpace.s8,
                  AraSpace.s16,
                  AraSpace.s16,
                ),
                children: [
                  _targetBlock(framing),
                  const SizedBox(height: AraSpace.s12),
                  _captureControls(single),
                  const SizedBox(height: AraSpace.s12),
                  Row(
                    children: [
                      Expanded(
                        child: single
                            ? FilledButton.icon(
                                key: const Key('rotation-assist-take-frame'),
                                onPressed: canStart
                                    ? () =>
                                          unawaited(_start(framing.rotationDeg))
                                    : null,
                                icon: status.active
                                    ? const SizedBox(
                                        width: 16,
                                        height: 16,
                                        child: CircularProgressIndicator(
                                          strokeWidth: 2,
                                        ),
                                      )
                                    : const Icon(
                                        Icons.camera_alt_outlined,
                                        size: 18,
                                      ),
                                label: Text(
                                  status.active
                                      ? 'Exposing & solving…'
                                      : 'Take frame',
                                ),
                              )
                            : status.active
                            ? FilledButton.tonalIcon(
                                key: const Key('rotation-assist-stop'),
                                onPressed: live.busy
                                    ? null
                                    : () => unawaited(
                                        ref
                                            .read(
                                              rotationAssistProvider.notifier,
                                            )
                                            .stop(),
                                      ),
                                icon: const Icon(Icons.stop_rounded, size: 18),
                                label: const Text('Stop loop'),
                              )
                            : FilledButton.icon(
                                key: const Key('rotation-assist-start'),
                                onPressed: canStart
                                    ? () =>
                                          unawaited(_start(framing.rotationDeg))
                                    : null,
                                icon: const Icon(Icons.loop, size: 18),
                                label: const Text('Start loop'),
                              ),
                      ),
                    ],
                  ),
                  if (!hasTarget)
                    const Padding(
                      padding: EdgeInsets.only(top: AraSpace.s8),
                      child: Text(
                        'Switch on Framing on the sky and put the box on your '
                        'target — the dial\'s angle is what the camera is '
                        'measured against.',
                        style: TextStyle(
                          fontSize: 12,
                          color: AraColors.textSecondary,
                          height: 1.3,
                        ),
                      ),
                    ),
                  if (!exposureOk)
                    const Padding(
                      padding: EdgeInsets.only(top: AraSpace.s8),
                      child: Text(
                        'Exposure must be between 0.01 and 60 seconds.',
                        style: TextStyle(
                          fontSize: 12,
                          color: AraColors.accentError,
                        ),
                      ),
                    ),
                  const SizedBox(height: AraSpace.s16),
                  _readout(live),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _header(BuildContext context) => Padding(
    padding: const EdgeInsets.fromLTRB(
      AraSpace.s16,
      AraSpace.s12,
      AraSpace.s8,
      0,
    ),
    child: Row(
      children: [
        const Expanded(
          child: Row(
            children: [
              Flexible(
                child: Text(
                  'ROTATE CAMERA BY HAND',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(
                    fontSize: 11,
                    letterSpacing: 1.1,
                    color: AraColors.textSecondary,
                  ),
                ),
              ),
              HelpIcon(helpKey: 'session.rotate_by_hand'),
            ],
          ),
        ),
        IconButton(
          key: const Key('rotation-assist-close'),
          tooltip: 'Close',
          icon: const Icon(Icons.close, size: 18),
          onPressed: () => ref
              .read(skyAtlasModeProvider.notifier)
              .set(SkyAtlasMode.catalogView),
        ),
      ],
    ),
  );

  Widget _targetBlock(PlanFraming framing) {
    final name = framing.name.isEmpty ? 'Framed target' : framing.name;
    return Container(
      padding: const EdgeInsets.all(AraSpace.s12),
      decoration: BoxDecoration(
        color: AraColors.bgPrimary,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AraColors.border),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  framing.hasTarget ? name : 'No framing on the sky',
                  key: const Key('rotation-assist-target'),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: AraColors.textPrimary,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  framing.hasTarget
                      ? 'Planned framing from the dial'
                      : 'Framing is off',
                  style: const TextStyle(
                    fontSize: 12,
                    color: AraColors.textSecondary,
                  ),
                ),
              ],
            ),
          ),
          Column(
            crossAxisAlignment: CrossAxisAlignment.end,
            children: [
              Text(
                framing.hasTarget
                    ? '${framing.rotationDeg.toStringAsFixed(0)}°'
                    : '—',
                key: const Key('rotation-assist-planned-angle'),
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w300,
                  color: AraColors.textPrimary,
                  fontFeatures: [FontFeature.tabularFigures()],
                ),
              ),
              const Text(
                'planned angle',
                style: TextStyle(fontSize: 11, color: AraColors.textSecondary),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _captureControls(bool single) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    children: [
      Row(
        children: [
          const Text(
            'Exposure',
            style: TextStyle(fontSize: 13, color: AraColors.textSecondary),
          ),
          const SizedBox(width: AraSpace.s8),
          SizedBox(
            width: 84,
            child: TextField(
              key: const Key('rotation-assist-exposure'),
              controller: _exposureCtrl,
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              style: const TextStyle(fontSize: 13),
              decoration: const InputDecoration(
                isDense: true,
                suffixText: 's',
                border: OutlineInputBorder(),
              ),
              onChanged: (_) => setState(() {}),
              onTapOutside: (_) => FocusScope.of(context).unfocus(),
            ),
          ),
          const SizedBox(width: AraSpace.s8),
          const Expanded(
            child: Text(
              'per frame; the solve follows',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(fontSize: 11.5, color: AraColors.textSecondary),
            ),
          ),
        ],
      ),
      const SizedBox(height: AraSpace.s8),
      SegmentedButton<String>(
        key: const Key('rotation-assist-mode'),
        showSelectedIcon: false,
        style: const ButtonStyle(
          visualDensity: VisualDensity.compact,
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        ),
        segments: const [
          ButtonSegment(
            value: RotationAssistModes.loop,
            label: Text('Loop'),
            icon: Icon(Icons.loop, size: 16),
          ),
          ButtonSegment(
            value: RotationAssistModes.single,
            label: Text('Single'),
            icon: Icon(Icons.camera, size: 16),
          ),
        ],
        selected: {_mode},
        onSelectionChanged: (sel) => setState(() => _mode = sel.first),
      ),
    ],
  );

  Widget _readout(RotationAssistLive live) {
    final status = live.status;
    final latest = status.latest;
    final hasSomething =
        latest != null ||
        status.active ||
        status.state == RotationAssistStates.error ||
        live.error != null;
    if (!hasSomething) {
      return const Text(
        'Point the mount at the target first (GoTo on the sky), loosen the '
        'camera, then take a frame. Each solve shows how far to turn and draws '
        'where the camera really points on the sky.',
        key: Key('rotation-assist-intro'),
        style: TextStyle(
          fontSize: 12.5,
          color: AraColors.textSecondary,
          height: 1.35,
        ),
      );
    }
    final hint = rotationHint(status);
    final delta = latest?.deltaDeg;
    final onTarget = hint.advice == RotateAdvice.onTarget;
    final (icon, color) = switch (hint.advice) {
      RotateAdvice.keepGoing => (
        Icons.arrow_forward_rounded,
        AraColors.accentConnected,
      ),
      RotateAdvice.goBack => (
        Icons.u_turn_left_rounded,
        AraColors.accentWarning,
      ),
      RotateAdvice.onTarget => (
        Icons.check_circle_outline,
        AraColors.accentConnected,
      ),
      RotateAdvice.noSolve => (
        Icons.visibility_off_outlined,
        AraColors.accentWarning,
      ),
      RotateAdvice.wait => (
        Icons.hourglass_empty_rounded,
        AraColors.textSecondary,
      ),
      RotateAdvice.makeAMove => (
        Icons.rotate_90_degrees_ccw_outlined,
        AraColors.accentInfo,
      ),
    };
    String deg(double? v, [int d = 1]) =>
        v == null ? '—' : '${v.toStringAsFixed(d)}°';
    final error = live.error ?? status.error;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          delta == null ? '—' : '${delta >= 0 ? '+' : '−'}${deg(delta.abs())}',
          key: const Key('rotation-assist-delta'),
          style: TextStyle(
            fontSize: 40,
            fontWeight: FontWeight.w300,
            height: 1.05,
            color: onTarget ? AraColors.accentConnected : AraColors.textPrimary,
            fontFeatures: const [FontFeature.tabularFigures()],
          ),
        ),
        Text(
          'to go · target ${deg(status.targetPositionAngleDeg)} · solved ${deg(latest?.solvedPositionAngleDeg)} · ±${deg(status.toleranceDeg)}',
          style: const TextStyle(fontSize: 12, color: AraColors.textSecondary),
        ),
        const SizedBox(height: AraSpace.s8),
        Row(
          children: [
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              decoration: BoxDecoration(
                color: color.withValues(alpha: 0.16),
                borderRadius: BorderRadius.circular(12),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(icon, size: 18, color: color),
                  const SizedBox(width: 6),
                  Text(
                    hint.title,
                    key: const Key('rotation-assist-advice'),
                    style: TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w700,
                      color: color,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        Text(
          hint.detail,
          style: const TextStyle(
            fontSize: 13,
            color: AraColors.textSecondary,
            height: 1.3,
          ),
        ),
        if (error != null)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(
              error,
              key: const Key('rotation-assist-error'),
              style: const TextStyle(
                fontSize: 12,
                color: AraColors.accentError,
              ),
            ),
          ),
        if (live.frame != null && latest != null) ...[
          const SizedBox(height: AraSpace.s12),
          RotationFrameView(
            key: const Key('rotation-assist-frame'),
            frame: live.frame!,
            frameWidth: latest.frameWidth,
            frameHeight: latest.frameHeight,
            solvedPositionAngleDeg: latest.solvedPositionAngleDeg,
            targetPositionAngleDeg: status.targetPositionAngleDeg,
            deltaDeg: latest.deltaDeg,
            flipped: latest.flipped,
            onTarget: onTarget,
            height: 220,
          ),
          const SizedBox(height: 4),
          Text(
            'Frame ${live.frameSeq} · the arrow is north in this picture; turn the camera until the picture\'s edges line up with the ${onTarget ? 'green' : 'amber'} rectangle. On the sky, the amber scope box shows where the camera points right now beside the planned framing.',
            style: const TextStyle(
              fontSize: 11.5,
              color: AraColors.textSecondary,
              height: 1.3,
            ),
          ),
        ],
      ],
    );
  }
}
