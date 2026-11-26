import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/polar_align/polar_aligner_state.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../status_indicator.dart';
import 'polar_align_readout.dart' show formatArcmin, formatSigmaArcmin, polarUnconfirmed;

/// #1971 the Polar Aligner's state and controls, the one module both
/// presentations render (ADR 0013): Simple Mode's Polar align panel between
/// Setup and Error, and Advanced Mode's Polar Align panel. Absent with no
/// Polar Aligner on the rig. Mock: `design/preview/simple-polar-aligner-mock.html`.

/// "2" for a 2.0 tolerance, "1.5" for 1.5.
String polarToleranceText(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

/// The motors are working in a running routine: the hand-turn arrows dim and
/// Done steps aside.
bool polarAlignerMotorsBusy(PolarAlignLive live) =>
    live.isActive && (live.correction?.motorsBusy ?? false);

String _axisWord(AppLocalizations l, String? axis) =>
    axis == 'altitude' ? l.polarAlignerAxisAltitude : l.polarAlignerAxisAzimuth;

/// Which way a move of [arcmin] (wire sign) turns the axis. + azimuth is
/// clockwise seen from above: toward east facing the north pole, toward west
/// facing the south pole ([southern], the hand-turn arrows' own flag).
/// + altitude raises it. Pure — unit-tested.
String polarAlignerDirection(AppLocalizations l, String? axis, double arcmin, {required bool southern}) {
  if (axis == 'altitude') return arcmin >= 0 ? l.polarAlignerUp : l.polarAlignerDown;
  return (arcmin >= 0) != southern ? l.polarAlignerEast : l.polarAlignerWest;
}

/// The motors' line while they work ("Moving azimuth 6.1′ east"), or null
/// in the states that have none. Pure — unit-tested.
String? polarAlignerWorkLine(AppLocalizations l, PolarAlignerRun run, {required bool southern}) {
  String move(String Function(String, String, String) words) {
    final a = run.arcmin ?? 0;
    return words(_axisWord(l, run.axis), a.abs().toStringAsFixed(1),
        polarAlignerDirection(l, run.axis, a, southern: southern));
  }

  return switch (run.state) {
    PolarAlignerStates.probing => move(l.polarAlignerTestMove),
    PolarAlignerStates.moving => move(l.polarAlignerMoving),
    PolarAlignerStates.settling =>
      l.polarAlignerSettling(polarToleranceText(run.seconds ?? 0)),
    PolarAlignerStates.measuring => l.polarAlignerMeasuring,
    _ => null,
  };
}

/// Why the motors stopped, in a sentence. A cause this build does not know
/// says so plainly with the device's text when it gave one. Pure — unit-tested.
String polarAlignerStopText(AppLocalizations l, PolarAlignerRun run) {
  final text = run.deviceText;
  final unknown = text == null ? l.polarAlignerStopUnknown : l.polarAlignerStopUnknownText(text);
  return switch (run.cause) {
    PolarAlignerCauses.wrongDirection when run.axis != null =>
      l.polarAlignerStopWrongDirection(_axisWord(l, run.axis)),
    PolarAlignerCauses.wrongScale when run.axis != null =>
      l.polarAlignerStopWrongScale(_axisWord(l, run.axis)),
    PolarAlignerCauses.deviceFault when text != null => l.polarAlignerStopDeviceFault(text),
    PolarAlignerCauses.notCalibrated when text != null => l.polarAlignerStopNotCalibrated(text),
    PolarAlignerCauses.deviceRefused when text != null => l.polarAlignerStopDeviceRefused(text),
    PolarAlignerCauses.diverging => l.polarAlignerStopDiverging,
    PolarAlignerCauses.timeout => l.polarAlignerStopTimeout,
    PolarAlignerCauses.linkLost => l.polarAlignerStopLinkLost,
    PolarAlignerCauses.mountTracking => l.polarAlignerStopMountTracking,
    PolarAlignerCauses.mountSlewing => l.polarAlignerStopMountSlewing,
    PolarAlignerCauses.stoppedByUser => l.polarAlignerStopByUser,
    PolarAlignerCauses.stopNotConfirmed => l.polarAlignerStopNotConfirmed,
    PolarAlignerCauses.stopConfirmed => l.polarAlignerStopConfirmed,
    PolarAlignerCauses.travelLimit => l.polarAlignerStopTravelLimit,
    PolarAlignerCauses.moveLimit => l.polarAlignerStopMoveLimit,
    _ => unknown,
  };
}

/// Why a present Polar Aligner cannot be driven. Pure.
String polarAlignerNotDrivableText(AppLocalizations l, PolarAligner d) =>
    d.notDrivableReason == 'device_runs_its_own_loop'
        ? l.polarAlignerNotDrivableLoop
        : l.polarAlignerNotDrivableInfo;

/// The block is optional content: it shows only on a successful read that
/// found a Polar Aligner, and draws nothing while loading or after a
/// failed read (Settings › Devices says a read failed).
bool polarAlignerShown(AsyncValue<PolarAligner?> read) =>
    switch (read) { AsyncData(value: PolarAligner(present: true)) => true, _ => false };

class PolarAlignerBlock extends ConsumerWidget {
  const PolarAlignerBlock({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final read = ref.watch(polarAlignerProvider);
    if (!polarAlignerShown(read)) return const SizedBox.shrink();
    final device = read.requireValue!;
    final l10n = context.l10n;
    final live = ref.watch(polarAlignLiveProvider);
    // The run belongs to a running routine; its end sends no aligner event.
    final run = live.isActive ? live.correction : null;
    // Null until the profile's copy is in from the rig (ADR 0015): no
    // tolerance or motors choice is made up; the routine's own state decides.
    final settings = ref.watch(polarAlignSettingsProvider).value;
    final southern = (ref.watch(siteSettingsProvider).value?.latitudeDeg ?? 0) < 0;
    final busy = ref.watch(polarAlignActionProvider).busy;
    final aligner = ref.read(polarAlignerProvider.notifier);

    void command(String action, Future<void> Function() op) => ref
        .read(polarAlignActionProvider.notifier)
        .run((_) => op(), describe: (e) => friendlyError(l10n, e, action: action));

    Text line(String text, {Color color = AraColors.textPrimary, Key? key}) =>
        Text(text, key: key, style: kSimpleText.copyWith(color: color));

    final az = device.azimuthPositionArcmin, alt = device.altitudePositionArcmin;
    final position = Text(
      az == null && alt == null
          ? l10n.polarAlignerNoPosition
          : l10n.polarAlignerPosition(formatArcmin(az), formatArcmin(alt)),
      key: const Key('polar-aligner-position'),
      style: AraText.caption,
    );

    // [working] blinks the dot (DESIGN.md status rule); [ring] is changing.
    Widget status(String text, Color color, {bool working = false, bool ring = false}) => Semantics(
          liveRegion: true,
          child: StatusBlink(
            on: working,
            child: Row(
              key: const Key('polar-aligner-status'),
              spacing: AraSpace.s8,
              children: [
                StatusDot(color: color, ring: ring),
                Expanded(child: line(text, color: color)),
              ],
            ),
          ),
        );

    Widget stopMotors(Future<void> Function() op) => FilledButton(
          key: const Key('polar-aligner-stop'),
          style: FilledButton.styleFrom(
            backgroundColor: AraColors.emergencyStop,
            foregroundColor: AraColors.onEmergencyStop,
            minimumSize: const Size(0, 44),
          ),
          // Stop never waits on another call: it halts motors that turn the mount.
          onPressed: () => command(l10n.polarAlignerActionStop, op),
          child: Text(l10n.polarAlignerStopMotors),
        );

    Widget home({required bool enabled}) => OutlinedButton(
          key: const Key('polar-aligner-home'),
          style: OutlinedButton.styleFrom(minimumSize: const Size(0, 44)),
          onPressed: enabled && !busy ? () => command(l10n.polarAlignerActionHome, aligner.home) : null,
          child: Text(l10n.polarAlignerHome),
        );

    final List<Widget> body;
    if (!device.drivable) {
      body = [
        line('${polarAlignerNotDrivableText(l10n, device)} ${l10n.polarAlignerTurnByHand}',
            color: AraColors.textSecondary, key: const Key('polar-aligner-not-drivable')),
      ];
    } else if (settings?.polarAlignerAuto == false || run?.state == PolarAlignerStates.manual) {
      body = [line(l10n.polarAlignerByHand, color: AraColors.textSecondary), position];
    } else if (run?.state == PolarAlignerStates.stopped) {
      final lost = run!.cause == PolarAlignerCauses.linkLost;
      // #2069: Home is a move, and the daemon refuses it while a Stop is owed.
      final unconfirmed = run.cause == PolarAlignerCauses.stopNotConfirmed;
      body = [
        // The one line that must be heard: the motors stopped, and why.
        Semantics(
          liveRegion: true,
          child: line(polarAlignerStopText(l10n, run),
              color: AraColors.accentBusy, key: const Key('polar-aligner-cause')),
        ),
        line(l10n.polarAlignerCarryOn),
        if (!lost) position,
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(child: home(enabled: !lost && !unconfirmed)),
            // #2069: the daemon refuses Resume until a Stop is confirmed, so the
            // person gets Stop to send again in its place.
            if (unconfirmed)
              Expanded(child: stopMotors(aligner.stopInRoutine))
            else
              Expanded(
                child: FilledButton(
                  key: const Key('polar-aligner-resume'),
                  style: FilledButton.styleFrom(
                    backgroundColor: AraColors.accentConnected,
                    foregroundColor: AraColors.bgPrimary,
                    minimumSize: const Size(0, 44),
                  ),
                  // Offered after a lost link too: only the daemon can tell whether the device answers again.
                  onPressed: busy ? null : () => command(l10n.polarAlignerActionResume, aligner.resumeInRoutine),
                  child: Text(l10n.polarAlignerResume),
                ),
              ),
          ],
        ),
        if (lost) Text(l10n.polarAlignerLinkLostCaption, style: AraText.caption),
      ];
    } else if (run != null && run.motorsBusy) {
      final settling = run.state == PolarAlignerStates.settling;
      final measuring = run.state == PolarAlignerStates.measuring;
      body = [
        status(
          polarAlignerWorkLine(l10n, run, southern: southern)!,
          settling ? AraColors.accentInfo : measuring ? AraColors.textSecondary : AraColors.accentConnected,
          working: !settling,
          ring: settling,
        ),
        position,
        stopMotors(aligner.stopInRoutine),
      ];
    } else if (run?.state == PolarAlignerStates.converged) {
      // #2072 inside the tolerance, but not with twice its 1σ: the motors may
      // stop, but the line must not claim the alignment (Joey, 2026-11-21).
      // More moves cannot shrink the fit's uncertainty.
      final unconfirmed = settings != null &&
          polarUnconfirmed(live.totalErrorArcmin, live.totalSigmaArcmin, settings.targetToleranceArcmin);
      body = [
        line(
          settings == null
              ? l10n.polarAlignerConvergedPlain(run!.moves)
              : unconfirmed
              ? l10n.polarAlignerConvergedUnconfirmed(
                  polarToleranceText(settings.targetToleranceArcmin), formatSigmaArcmin(live.totalSigmaArcmin!))
              : l10n.polarAlignerConverged(run!.moves, polarToleranceText(settings.targetToleranceArcmin)),
          color: AraColors.accentConnected,
          key: const Key('polar-aligner-converged'),
        ),
        position,
      ];
    } else if (device.moving) {
      // Outside a correction only Home moves it (Ara has no jog pad).
      body = [
        status(l10n.polarAlignerHoming, AraColors.accentConnected, working: true),
        position,
        stopMotors(aligner.stop),
      ];
    } else if (!live.isActive) {
      body = [
        // Motors or by hand, and the tolerance, are the profile's: unsaid until it loads.
        if (settings != null) line(l10n.polarAlignerIdle(polarToleranceText(settings.targetToleranceArcmin))),
        position,
        Row(children: [Expanded(child: home(enabled: true))]),
      ];
    } else {
      // A routine before its first measurement: nothing moves yet.
      body = [position];
    }

    return Column(
      key: const Key('polar-aligner-block'),
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          spacing: AraSpace.s8,
          children: [
            Flexible(child: Text(l10n.polarAligner, style: AraText.caption)),
            if (device.name case final name?)
              Flexible(child: Text(name, style: AraText.caption, overflow: TextOverflow.ellipsis)),
          ],
        ),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12, vertical: AraSpace.s8),
          decoration: BoxDecoration(
            color: AraColors.tileFill,
            borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: AraSpace.s4,
            children: body,
          ),
        ),
      ],
    );
  }
}

/// The status card's line while the motors work or after they stopped
/// ("Polar Aligner · Moving azimuth 6.1′ east"); null otherwise.
({String text, Color color, bool working, bool ring})? polarAlignerStatusLine(
    AppLocalizations l, PolarAlignLive live, {required bool southern}) {
  final run = live.isActive ? live.correction : null;
  if (run == null) return null;
  if (run.state == PolarAlignerStates.stopped) {
    return (text: l.polarAlignerStopped, color: AraColors.accentBusy, working: false, ring: false);
  }
  final text = polarAlignerWorkLine(l, run, southern: southern);
  if (text == null) return null;
  return switch (run.state) {
    PolarAlignerStates.settling => (text: text, color: AraColors.accentInfo, working: false, ring: true),
    PolarAlignerStates.measuring => (text: text, color: AraColors.textSecondary, working: true, ring: false),
    _ => (text: text, color: AraColors.accentConnected, working: true, ring: false),
  };
}
