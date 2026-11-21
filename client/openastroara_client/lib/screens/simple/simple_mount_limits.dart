import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/settings/section_load_state.dart';
import 'simple_device_windows.dart' show simpleWindowRow, simpleWindowStepper;

/// Lowest altitude's range in Simple Mode (Advanced takes 0 to 90).
const int kSimpleLowestMax = 60;

/// Both meridian rows: 30 min either side, in 5 min steps.
const int kSimpleMeridianSpan = 30;

/// One 5-unit step from [v] in direction [dir], onto the 5 grid (a value set
/// in Advanced Mode off the grid, like 23, steps to 25 or 20), kept in
/// [lo]..[hi].
int simpleLimitStep(num v, int dir, int lo, int hi) {
  final q = v / 5;
  final next = (dir > 0 ? q.floor() + 1 : q.ceil() - 1) * 5;
  return next.clamp(lo, hi);
}

/// #2044 — the Mount window's Limits group, under its rows and the pad:
/// Lowest altitude (the site's flat horizon; read-only with a measured one),
/// At the meridian (flip Auto) or Stop before meridian (flip Off), and a sky
/// arc drawn from the same values. Each tap saves at once with a sentence
/// pill, applied to the rig's copy of the section (ADR 0015); a failed save
/// puts the rig's value back. The highest-altitude row waits for its server
/// field (#1589), so it is not built.
class SimpleMountLimits extends ConsumerStatefulWidget {
  const SimpleMountLimits({super.key});

  @override
  ConsumerState<SimpleMountLimits> createState() => _SimpleMountLimitsState();
}

class _SimpleMountLimitsState extends ConsumerState<SimpleMountLimits> {
  /// Saves [write] (a section's changeAndSave): applied to the rig's copy at
  /// once (loaded once more first when it isn't in) and saved whole; the
  /// section chains its saves, so a quick second tap cannot land first.
  /// Success: [pill]. Not loaded, refused, stale or failed: the rig's value
  /// shows again and the couldn't-save pill says why.
  Future<void> _save({
    required String pill,
    required Future<void> Function() write,
  }) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.mount);
    final l10n = context.l10n;
    try {
      await write();
      if (!mounted) return;
      notify(pill);
    } catch (e) {
      if (!mounted) return;
      notify(
        l10n.limitsCouldntSave,
        detail: e is SectionNotLoadedException || e is SectionConflictException
            ? e.toString()
            : failureReason(l10n, e),
        error: true,
      );
    }
  }

  void _setLowest(int deg) => unawaited(
    _save(
      pill: context.l10n.limitsPillLowest(deg),
      write: () => ref
          .read(siteSettingsProvider.notifier)
          .changeAndSave(
            (c) => c.copyWith(defaultHorizonAltitudeDeg: deg.toDouble()),
          ),
    ),
  );

  void _setMeridian(int v) {
    final l10n = context.l10n;
    unawaited(
      _save(
        pill: v < 0
            ? l10n.limitsPillBefore(-v)
            : v == 0
            ? l10n.limitsPillOn
            : l10n.limitsPillAfter(v),
        write: () => ref
            .read(safetyPoliciesProvider.notifier)
            .changeAndSave((p) => withMeridianTimingMin(p, v)),
      ),
    );
  }

  void _setStopBefore(int min) {
    final l10n = context.l10n;
    unawaited(
      _save(
        pill: min > 0 ? l10n.limitsPillStop(min) : l10n.limitsPillStopOff,
        write: () => ref
            .read(safetyPoliciesProvider.notifier)
            .changeAndSave((p) => p.copyWith(stopBeforeMeridianMin: min)),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final siteSection = ref.watch(siteSettingsProvider);
    final site = siteSection.value;
    // Null until the profile's copy is in from the rig (ADR 0015). The
    // Meridian flip row above already says so for the safety section, with
    // Retry; its rows here wait for it.
    final safety = ref.watch(safetyPoliciesProvider).value;
    final low = site?.defaultHorizonAltitudeDeg.round() ?? 0;
    final measured = site?.useCustomHorizon ?? false;
    final children = <Widget>[
      const Divider(height: AraSpace.s12, color: AraColors.border),
      Semantics(
        header: true,
        child: Text(l10n.limitsTitle.toUpperCase(), style: AraText.section),
      ),
    ];
    if (site == null) {
      children.add(
        SectionNotLoaded(
          section: siteSection,
          padding: EdgeInsets.zero,
          onRetry: () {
            final api = ref.read(profileApiProvider);
            if (api != null) {
              ref.read(siteSettingsProvider.notifier).load(api);
            }
          },
        ),
      );
    } else if (measured) {
      children
        ..add(
          simpleWindowRow(
            l10n.limitsLowestAltitude,
            Text(
              l10n.limitsMeasuredHorizon,
              key: const Key('simple-mount-lowest-measured'),
              style: kSimpleText.copyWith(color: AraColors.textSecondary),
            ),
            id: 'Lowest altitude',
          ),
        )
        ..add(Text(l10n.limitsMeasuredHorizonCaption, style: kSimpleCaption));
    } else {
      children.add(
        simpleWindowRow(
          l10n.limitsLowestAltitude,
          simpleWindowStepper(
            id: 'simple-mount-lowest',
            text: l10n.limitsDegrees(low),
            lessLabel: l10n.limitsLowestLower,
            moreLabel: l10n.limitsLowestRaise,
            onLess: low > 0
                ? () =>
                      _setLowest(simpleLimitStep(low, -1, 0, kSimpleLowestMax))
                : null,
            onMore: low < kSimpleLowestMax
                ? () => _setLowest(simpleLimitStep(low, 1, 0, kSimpleLowestMax))
                : null,
          ),
          id: 'Lowest altitude',
        ),
      );
    }
    if (safety == null) {
      // Nothing to show: the flip row above says Not loaded.
    } else if (safety.meridianFlipAuto) {
      final v = meridianTimingMin(safety);
      children
        ..add(
          simpleWindowRow(
            l10n.limitsAtMeridian,
            simpleWindowStepper(
              id: 'simple-mount-meridian',
              text: v < 0
                  ? l10n.limitsMinBefore(-v)
                  : v == 0
                  ? l10n.limitsOnMeridian
                  : l10n.limitsMinAfter(v),
              lessLabel: l10n.limitsAtMeridianEarlier,
              moreLabel: l10n.limitsAtMeridianLater,
              onLess: v > -kSimpleMeridianSpan
                  ? () => _setMeridian(
                      simpleLimitStep(
                        v,
                        -1,
                        -kSimpleMeridianSpan,
                        kSimpleMeridianSpan,
                      ),
                    )
                  : null,
              onMore: v < kSimpleMeridianSpan
                  ? () => _setMeridian(
                      simpleLimitStep(
                        v,
                        1,
                        -kSimpleMeridianSpan,
                        kSimpleMeridianSpan,
                      ),
                    )
                  : null,
            ),
            id: 'At the meridian',
          ),
        )
        ..add(
          Text(
            v < 0
                ? l10n.limitsCaptionBefore(-v)
                : v == 0
                ? l10n.limitsCaptionOn
                : l10n.limitsCaptionAfter(v),
            style: kSimpleCaption,
          ),
        );
    } else {
      final m = safety.stopBeforeMeridianMin;
      children
        ..add(
          simpleWindowRow(
            l10n.limitsStopBefore,
            simpleWindowStepper(
              id: 'simple-mount-stop-before',
              text: m > 0 ? l10n.limitsMinutes(m) : l10n.deviceOff,
              lessLabel: l10n.limitsStopBeforeLower,
              moreLabel: l10n.limitsStopBeforeRaise,
              onLess: m > 0
                  ? () => _setStopBefore(
                      simpleLimitStep(m, -1, 0, kSimpleMeridianSpan),
                    )
                  : null,
              onMore: m < kSimpleMeridianSpan
                  ? () => _setStopBefore(
                      simpleLimitStep(m, 1, 0, kSimpleMeridianSpan),
                    )
                  : null,
            ),
            id: 'Stop before meridian',
          ),
        )
        ..add(
          Text(
            m > 0 ? l10n.limitsStopCaption(m) : l10n.limitsStopCaptionOff,
            style: kSimpleCaption,
          ),
        );
    }
    // A measured horizon depends on the azimuth, which one arc cannot draw.
    if (site != null && !measured) {
      children.add(SimpleLimitsArc(lowestDeg: low));
    }
    return Column(
      key: const Key('simple-mount-limits'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }
}

/// The sky from the horizon (left) to the zenith (top) as a quarter circle:
/// red under the lowest altitude, green where the mount may point.
class SimpleLimitsArc extends StatelessWidget {
  const SimpleLimitsArc({super.key, required this.lowestDeg});

  final int lowestDeg;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    return LayoutBuilder(
      builder: (context, box) {
        // The mock's 360 × 150, narrower when the window is.
        final width = math.min(360.0, box.maxWidth);
        // The top pad keeps the 90° label inside at larger text.
        return Padding(
          padding: const EdgeInsets.only(top: AraSpace.s8),
          child: Center(
            child: Semantics(
              key: const Key('simple-mount-limits-arc'),
              image: true,
              label: l10n.limitsArcLabel(lowestDeg),
              child: ExcludeSemantics(
                child: CustomPaint(
                  size: Size(width, width * 150 / 360),
                  painter: _ArcPainter(
                    lowestDeg: lowestDeg,
                    tooLow: l10n.limitsArcTooLow,
                    ok: l10n.limitsArcOk,
                    horizon: l10n.limitsArcHorizon,
                    scaler: MediaQuery.textScalerOf(context),
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

class _ArcPainter extends CustomPainter {
  _ArcPainter({
    required this.lowestDeg,
    required this.tooLow,
    required this.ok,
    required this.horizon,
    required this.scaler,
  });

  final int lowestDeg;
  final String tooLow;
  final String ok;
  final String horizon;
  final TextScaler scaler;

  @override
  void paint(Canvas canvas, Size size) {
    final s = size.width / 360;
    final c = Offset(40 * s, 138 * s);
    final r = 118 * s;
    Offset at(double deg, double radius) {
      final a = deg * math.pi / 180;
      return c + Offset(radius * math.cos(a), -radius * math.sin(a));
    }

    final disk = Rect.fromCircle(center: c, radius: r);
    void wedge(double from, double to, Color color) => canvas.drawArc(
      disk,
      -from * math.pi / 180,
      -(to - from) * math.pi / 180,
      true,
      Paint()..color = color,
    );
    void label(
      String text,
      Offset p,
      double fontSize,
      Color color, {
      bool centre = false,
    }) {
      final tp = TextPainter(
        text: TextSpan(
          text: text,
          style: TextStyle(fontSize: fontSize, color: color),
        ),
        textDirection: TextDirection.ltr,
        textScaler: scaler,
      )..layout();
      tp.paint(canvas, p - Offset(centre ? tp.width / 2 : 0, tp.height / 2));
      tp.dispose();
    }

    final low = lowestDeg.clamp(0, 90).toDouble();
    if (low > 0) wedge(0, low, AraColors.accentError.withValues(alpha: 0.35));
    wedge(low, 90, AraColors.accentConnected.withValues(alpha: 0.28));
    final tick = Paint()
      ..color = AraColors.textDisabled
      ..strokeWidth = 1;
    for (final a in const [0.0, 30.0, 60.0, 90.0]) {
      canvas.drawLine(at(a, r), at(a, r + 4 * s), tick);
      label(
        '${a.round()}°',
        at(a, r + 14 * s),
        10,
        AraColors.textSecondary,
        centre: a == 90,
      );
    }
    if (low > 0) {
      label(
        tooLow,
        at(math.max(low / 2, 4), r * 0.62),
        11,
        AraColors.textPrimary,
      );
    }
    label(ok, at((low + 90) / 2, r * 0.62), 11, AraColors.textPrimary);
    canvas.drawLine(c, c + Offset(r + 6 * s, 0), tick);
    label(horizon, c + Offset(r + 10 * s, 0), 10, AraColors.textSecondary);
  }

  @override
  bool shouldRepaint(_ArcPainter old) =>
      old.lowestDeg != lowestDeg ||
      old.tooLow != tooLow ||
      old.ok != ok ||
      old.horizon != horizon ||
      old.scaler != scaler;
}
