import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/rotation_assist.dart';
import '../../state/app_shell_state.dart';
import '../../state/rotation/rotation_assist_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../help_icon.dart';
import 'rotation_overlay.dart';
import 'sequencer_toolbar.dart';

/// The by-hand rotation readout under the run band: shown while the daemon's
/// readout runs (a run's Rotate camera by hand step on a rig without a
/// rotator). The delta to the framing angle is the hero number; the advice
/// is relative to the user's last turn, like the guide-camera focus card.
/// Resume goes through the same prompt as the toolbar's Resume.
class RotationAssistCard extends ConsumerWidget {
  const RotationAssistCard({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(rotationAssistProvider);
    final status = live.status;
    if (!status.active && status.state != RotationAssistStates.error) {
      return const SizedBox.shrink();
    }
    final hint = rotationHint(status);
    final latest = status.latest;
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
    return Container(
      key: const Key('rotation-assist-card'),
      padding: const EdgeInsets.fromLTRB(
        AraSpace.s16,
        AraSpace.s12,
        AraSpace.s16,
        AraSpace.s12,
      ),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.center,
            children: [
              SizedBox(
                width: 260,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Flexible(
                          child: Text(
                            'ROTATE THE CAMERA BY HAND',
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
                    const SizedBox(height: 2),
                    Text(
                      delta == null
                          ? '—'
                          : '${delta >= 0 ? '+' : '−'}${deg(delta.abs())}',
                      style: TextStyle(
                        fontSize: 34,
                        fontWeight: FontWeight.w300,
                        height: 1.05,
                        color: onTarget
                            ? AraColors.accentConnected
                            : AraColors.textPrimary,
                        fontFeatures: const [FontFeature.tabularFigures()],
                      ),
                    ),
                    Text(
                      'to go · target ${deg(status.targetPositionAngleDeg)} · solved ${deg(latest?.solvedPositionAngleDeg)} · ±${deg(status.toleranceDeg)}',
                      maxLines: 2,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(width: AraSpace.s16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      children: [
                        Container(
                          padding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 6,
                          ),
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
                    if (live.error != null || status.error != null)
                      Padding(
                        padding: const EdgeInsets.only(top: 4),
                        child: Text(
                          live.error ?? status.error!,
                          style: const TextStyle(
                            fontSize: 12,
                            color: AraColors.accentError,
                          ),
                        ),
                      ),
                  ],
                ),
              ),
              const SizedBox(width: AraSpace.s16),
              Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  FilledButton.icon(
                    key: const Key('rotation-assist-resume'),
                    onPressed: () => promptAndResumeSequence(context, ref),
                    icon: const Icon(Icons.play_arrow_rounded, size: 18),
                    label: const Text('Done — resume'),
                  ),
                  if (latest != null) ...[
                    const SizedBox(height: AraSpace.s8),
                    OutlinedButton.icon(
                      key: const Key('rotation-assist-show-on-sky'),
                      onPressed: () {
                        // The planned (light-blue) box at the planned angle where
                        // the scope points, the amber scope box from the latest
                        // solve beside it: turn until they coincide.
                        ref.read(planetariumCommandProvider.notifier).send({
                          'type': 'goto',
                          'ra': latest.raDeg,
                          'dec': latest.decDeg,
                          'frame': true,
                          'rot': status.targetPositionAngleDeg,
                          'dss': true,
                        });
                        ref
                            .read(selectedTabIndexProvider.notifier)
                            .select(kPlanningTabIndex);
                      },
                      icon: const Icon(Icons.public, size: 18),
                      label: const Text('Show on sky'),
                    ),
                  ],
                ],
              ),
            ],
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
            ),
            const SizedBox(height: 4),
            Text(
              'Frame ${live.frameSeq} · the arrow is north in this picture; turn the camera until the picture\'s edges line up with the ${onTarget ? 'green' : 'amber'} rectangle.',
              style: const TextStyle(
                fontSize: 11.5,
                color: AraColors.textSecondary,
              ),
            ),
          ],
        ],
      ),
    );
  }
}
