import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/polar_align/polar_corrector_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../status_indicator.dart';
import 'polar_align_readout.dart' show formatArcmin;

/// #1971 the Polar Corrector's state and controls, the one module both
/// presentations render (ADR 0013): Simple Mode's Polar align panel between
/// Setup and Error, and Advanced Mode's Polar Align panel. Absent with no
/// Polar Corrector on the rig. Mock: `design/preview/simple-polar-corrector-mock.html`.

/// "2" for a 2.0 tolerance, "1.5" for 1.5.
String polarToleranceText(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

/// The motors are working in a running routine: the hand-turn arrows dim and
/// Verify and Done step aside.
bool polarCorrectorMotorsBusy(PolarAlignLive live) =>
    live.isActive && (live.corrector?.motorsBusy ?? false);

String _axisWord(AppLocalizations l, String? axis) =>
    axis == 'altitude' ? l.polarCorrectorAxisAltitude : l.polarCorrectorAxisAzimuth;

/// Which way a move of [arcmin] (wire sign) turns the axis. + azimuth is
/// clockwise seen from above: toward east facing the north pole, toward west
/// facing the south pole ([southern], the hand-turn arrows' own flag).
/// + altitude raises it. Pure — unit-tested.
String polarCorrectorDirection(AppLocalizations l, String? axis, double arcmin, {required bool southern}) {
  if (axis == 'altitude') return arcmin >= 0 ? l.polarCorrectorUp : l.polarCorrectorDown;
  return (arcmin >= 0) != southern ? l.polarCorrectorEast : l.polarCorrectorWest;
}

/// The motors' line while they work ("Moving azimuth 6.1′ east"), or null
/// in the states that have none. Pure — unit-tested.
String? polarCorrectorWorkLine(AppLocalizations l, PolarCorrectorRun run, {required bool southern}) {
  String move(String Function(String, String, String) words) {
    final a = run.arcmin ?? 0;
    return words(_axisWord(l, run.axis), a.abs().toStringAsFixed(1),
        polarCorrectorDirection(l, run.axis, a, southern: southern));
  }

  return switch (run.state) {
    PolarCorrectorStates.probing => move(l.polarCorrectorTestMove),
    PolarCorrectorStates.moving => move(l.polarCorrectorMoving),
    PolarCorrectorStates.settling =>
      l.polarCorrectorSettling(polarToleranceText(run.seconds ?? 0)),
    PolarCorrectorStates.measuring => l.polarCorrectorMeasuring,
    _ => null,
  };
}

/// Why the motors stopped, in a sentence. A cause this build does not know
/// says so plainly with the device's text when it gave one. Pure — unit-tested.
String polarCorrectorStopText(AppLocalizations l, PolarCorrectorRun run) {
  final text = run.deviceText;
  final unknown = text == null ? l.polarCorrectorStopUnknown : l.polarCorrectorStopUnknownText(text);
  return switch (run.cause) {
    PolarCorrectorCauses.wrongDirection when run.axis != null =>
      l.polarCorrectorStopWrongDirection(_axisWord(l, run.axis)),
    PolarCorrectorCauses.wrongScale when run.axis != null =>
      l.polarCorrectorStopWrongScale(_axisWord(l, run.axis)),
    PolarCorrectorCauses.deviceFault when text != null => l.polarCorrectorStopDeviceFault(text),
    PolarCorrectorCauses.notCalibrated when text != null => l.polarCorrectorStopNotCalibrated(text),
    PolarCorrectorCauses.deviceRefused when text != null => l.polarCorrectorStopDeviceRefused(text),
    PolarCorrectorCauses.diverging => l.polarCorrectorStopDiverging,
    PolarCorrectorCauses.timeout => l.polarCorrectorStopTimeout,
    PolarCorrectorCauses.linkLost => l.polarCorrectorStopLinkLost,
    PolarCorrectorCauses.mountTracking => l.polarCorrectorStopMountTracking,
    PolarCorrectorCauses.mountSlewing => l.polarCorrectorStopMountSlewing,
    PolarCorrectorCauses.stoppedByUser => l.polarCorrectorStopByUser,
    _ => unknown,
  };
}

/// Why a present Polar Corrector cannot be driven. Pure.
String polarCorrectorNotDrivableText(AppLocalizations l, PolarCorrector d) =>
    d.notDrivableReason == 'device_runs_its_own_loop'
        ? l.polarCorrectorNotDrivableLoop
        : l.polarCorrectorNotDrivableInfo;

/// The block is optional content: it shows only on a successful read that
/// found a Polar Corrector, and draws nothing while loading or after a
/// failed read (Settings › Devices says a read failed).
bool polarCorrectorShown(AsyncValue<PolarCorrector?> read) =>
    switch (read) { AsyncData(value: PolarCorrector(present: true)) => true, _ => false };

class PolarCorrectorBlock extends ConsumerWidget {
  const PolarCorrectorBlock({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final read = ref.watch(polarCorrectorProvider);
    if (!polarCorrectorShown(read)) return const SizedBox.shrink();
    final device = read.requireValue!;
    final l10n = context.l10n;
    final live = ref.watch(polarAlignLiveProvider);
    // The run belongs to a running routine; its end sends no corrector event.
    final run = live.isActive ? live.corrector : null;
    final settings = ref.watch(polarAlignSettingsProvider).value ?? const PolarAlignSettings();
    final southern = ref.watch(siteSettingsProvider).latitudeDeg < 0;
    final busy = ref.watch(polarAlignActionProvider).busy;
    final corrector = ref.read(polarCorrectorProvider.notifier);

    void command(String action, Future<void> Function() op) => ref
        .read(polarAlignActionProvider.notifier)
        .run((_) => op(), describe: (e) => friendlyError(l10n, e, action: action));

    Text line(String text, {Color color = AraColors.textPrimary, Key? key}) =>
        Text(text, key: key, style: kSimpleText.copyWith(color: color));

    final az = device.azimuthPositionArcmin, alt = device.altitudePositionArcmin;
    final position = Text(
      az == null && alt == null
          ? l10n.polarCorrectorNoPosition
          : l10n.polarCorrectorPosition(formatArcmin(az), formatArcmin(alt)),
      key: const Key('polar-corrector-position'),
      style: AraText.caption,
    );

    // [working] blinks the dot (DESIGN.md status rule); [ring] is changing.
    Widget status(String text, Color color, {bool working = false, bool ring = false}) => Semantics(
          liveRegion: true,
          child: StatusBlink(
            on: working,
            child: Row(
              key: const Key('polar-corrector-status'),
              spacing: AraSpace.s8,
              children: [
                StatusDot(color: color, ring: ring),
                Expanded(child: line(text, color: color)),
              ],
            ),
          ),
        );

    Widget stopMotors(Future<void> Function() op) => FilledButton(
          key: const Key('polar-corrector-stop'),
          style: FilledButton.styleFrom(
            backgroundColor: AraColors.emergencyStop,
            foregroundColor: AraColors.onEmergencyStop,
            minimumSize: const Size(0, 44),
          ),
          // Stop never waits on another call: it halts motors that turn the mount.
          onPressed: () => command(l10n.polarCorrectorActionStop, op),
          child: Text(l10n.polarCorrectorStopMotors),
        );

    Widget home({required bool enabled}) => OutlinedButton(
          key: const Key('polar-corrector-home'),
          style: OutlinedButton.styleFrom(minimumSize: const Size(0, 44)),
          onPressed: enabled && !busy ? () => command(l10n.polarCorrectorActionHome, corrector.home) : null,
          child: Text(l10n.polarCorrectorHome),
        );

    final List<Widget> body;
    if (!device.drivable) {
      body = [
        line('${polarCorrectorNotDrivableText(l10n, device)} ${l10n.polarCorrectorTurnByHand}',
            color: AraColors.textSecondary, key: const Key('polar-corrector-not-drivable')),
      ];
    } else if (!settings.polarCorrectorAuto || run?.state == PolarCorrectorStates.manual) {
      body = [line(l10n.polarCorrectorByHand, color: AraColors.textSecondary), position];
    } else if (run?.state == PolarCorrectorStates.stopped) {
      final lost = run!.cause == PolarCorrectorCauses.linkLost;
      body = [
        // The one line that must be heard: the motors stopped, and why.
        Semantics(
          liveRegion: true,
          child: line(polarCorrectorStopText(l10n, run),
              color: AraColors.accentBusy, key: const Key('polar-corrector-cause')),
        ),
        line(l10n.polarCorrectorCarryOn),
        if (!lost) position,
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(child: home(enabled: !lost)),
            Expanded(
              child: FilledButton(
                key: const Key('polar-corrector-resume'),
                style: FilledButton.styleFrom(
                  backgroundColor: AraColors.accentConnected,
                  foregroundColor: AraColors.bgPrimary,
                  minimumSize: const Size(0, 44),
                ),
                // Offered after a lost link too: only the daemon can tell whether the device answers again.
                onPressed: busy ? null : () => command(l10n.polarCorrectorActionResume, corrector.resumeInRoutine),
                child: Text(l10n.polarCorrectorResume),
              ),
            ),
          ],
        ),
        if (lost) Text(l10n.polarCorrectorLinkLostCaption, style: AraText.caption),
      ];
    } else if (run != null && run.motorsBusy) {
      final settling = run.state == PolarCorrectorStates.settling;
      final measuring = run.state == PolarCorrectorStates.measuring;
      body = [
        status(
          polarCorrectorWorkLine(l10n, run, southern: southern)!,
          settling ? AraColors.accentInfo : measuring ? AraColors.textSecondary : AraColors.accentConnected,
          working: !settling,
          ring: settling,
        ),
        position,
        stopMotors(corrector.stopInRoutine),
      ];
    } else if (run?.state == PolarCorrectorStates.converged) {
      body = [
        line(
          l10n.polarCorrectorConverged(run!.moves, polarToleranceText(settings.targetToleranceArcmin)),
          color: AraColors.accentConnected,
          key: const Key('polar-corrector-converged'),
        ),
        position,
      ];
    } else if (device.moving) {
      // Outside a correction only Home moves it (Ara has no jog pad).
      body = [
        status(l10n.polarCorrectorHoming, AraColors.accentConnected, working: true),
        position,
        stopMotors(corrector.stop),
      ];
    } else if (!live.isActive) {
      body = [
        line(l10n.polarCorrectorIdle(polarToleranceText(settings.targetToleranceArcmin))),
        position,
        Row(children: [Expanded(child: home(enabled: true))]),
      ];
    } else {
      // A routine before its first measurement: nothing moves yet.
      body = [position];
    }

    return Column(
      key: const Key('polar-corrector-block'),
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceBetween,
          spacing: AraSpace.s8,
          children: [
            Flexible(child: Text(l10n.polarCorrector, style: AraText.caption)),
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
/// ("Polar Corrector · Moving azimuth 6.1′ east"); null otherwise.
({String text, Color color, bool working, bool ring})? polarCorrectorStatusLine(
    AppLocalizations l, PolarAlignLive live, {required bool southern}) {
  final run = live.isActive ? live.corrector : null;
  if (run == null) return null;
  if (run.state == PolarCorrectorStates.stopped) {
    return (text: l.polarCorrectorStopped, color: AraColors.accentBusy, working: false, ring: false);
  }
  final text = polarCorrectorWorkLine(l, run, southern: southern);
  if (text == null) return null;
  return switch (run.state) {
    PolarCorrectorStates.settling => (text: text, color: AraColors.accentInfo, working: false, ring: true),
    PolarCorrectorStates.measuring => (text: text, color: AraColors.textSecondary, working: true, ring: false),
    _ => (text: text, color: AraColors.accentConnected, working: true, ring: false),
  };
}
