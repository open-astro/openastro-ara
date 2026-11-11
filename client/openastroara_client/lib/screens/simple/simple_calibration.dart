import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/calibration/calibration_models.dart';
import '../../models/sequence/imaging_run_body.dart' show PlanFrameType;
import '../../models/sequence/sequence_summary.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/guider/guider_calibration_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/simple_calibration_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_run_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import 'simple_preview_panel.dart' show SimpleShutterButton, formatExposure;
import 'simple_device_windows.dart' show SimpleWindowButton;
import 'simple_shell.dart' show SimplePanel, SimpleSegments;

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);

/// The button's words for what [need] holds.
String calibrationTakeLabel(AppLocalizations l, CalibrationNeed need) =>
    need.flats && (need.darks || need.bias)
    ? l.calibrationTakeFlatsAndDarks
    : need.flats
    ? l.calibrationTakeFlats
    : l.calibrationTakeDarks;

/// What [session] still lacks, in a line: the filters without flats, then
/// darks and bias.
String calibrationMissingLine(
  AppLocalizations l,
  CalibrationSession session,
  CalibrationNeed need,
) {
  final noFlats = [
    for (final f in session.filtersUsed)
      if (f.matchingFlatFrames == 0) f.filterName,
  ];
  return [
    if (need.flats)
      noFlats.isEmpty ? l.calibrationFlats : l.calibrationFlatsFor(noFlats.join(', ')),
    if (need.darks) l.calibrationDarks,
    if (need.bias) l.calibrationBias,
  ].join(' · ');
}

/// Night done's calibration row (#1563): what tonight's lights are missing
/// and one button that shoots it, flats first; its progress while it runs.
/// Nothing when the rig has no calibration session for the run.
class SimpleNightCalibration extends ConsumerWidget {
  const SimpleNightCalibration({
    super.key,
    required this.startUtc,
    required this.endUtc,
  });

  /// The run's start and end: the session whose lights fall in them counts.
  final DateTime startUtc;
  final DateTime endUtc;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final sessions = ref.watch(simpleCalibrationSessionsProvider);
    final session = sessions == null
        ? null
        : calibrationSessionWithin(sessions, startUtc, endUtc);
    if (session == null) return const SizedBox.shrink();
    final calibration = ref.read(simpleCalibrationProvider.notifier);
    final run = ref.watch(simpleCalibrationProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = run != null && info?.sequenceId == run.sequenceId;
    final need = calibrationNeed(session);

    Widget take() => FilledButton.tonal(
      key: const Key('simple-calibration-take'),
      onPressed: () => calibration.shoot(session, need),
      child: Text(calibrationTakeLabel(l, need)),
    );

    final (String line, String? sub, Widget? action) = switch (info?.state) {
      final s? when ours && s.isActive => (
        l.calibrationTaking(info!.framesCaptured, run.total),
        null,
        OutlinedButton(
          key: const Key('simple-calibration-stop'),
          onPressed: calibration.stop,
          child: Text(l.deviceStop),
        ),
      ),
      SequenceRunState.completed when ours => (l.calibrationDone, null, null),
      _ when ours => (
        l.calibrationStopped(info!.framesCaptured, run.total),
        need.any ? calibrationMissingLine(l, session, need) : null,
        need.any ? take() : null,
      ),
      _ when need.any => (
        l.calibrationMissingTonight,
        calibrationMissingLine(l, session, need),
        take(),
      ),
      _ => (l.calibrationOnFile, null, null),
    };
    return Row(
      key: const Key('simple-calibration'),
      spacing: AraSpace.s12,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(line, style: kSimpleText),
              if (sub != null) Text(sub, style: _dim),
            ],
          ),
        ),
        ?action,
      ],
    );
  }
}

/// The Calibration mode's tab (#1757): flats, darks and bias, Guide Camera.
enum SimpleCalibrationTab { flats, darks, guide }

final simpleCalibrationTabProvider =
    NotifierProvider<SimpleCalibrationTabNotifier, SimpleCalibrationTab>(
      SimpleCalibrationTabNotifier.new,
    );

class SimpleCalibrationTabNotifier extends Notifier<SimpleCalibrationTab> {
  @override
  SimpleCalibrationTab build() => SimpleCalibrationTab.flats;
  void set(SimpleCalibrationTab t) => state = t;
}

/// The session Calibration works on: the newest plan's.
CalibrationSession? simpleLatestCalibrationSession(WidgetRef ref) =>
    ref.watch(simpleCalibrationSessionsProvider)?.firstOrNull;

/// How many sets [session] lacks: each filter without flats, then darks and
/// bias. Pure — unit-tested.
int calibrationMissingCount(CalibrationSession session) {
  final need = calibrationNeed(session);
  final noFlats = session.filtersUsed.where((f) => f.matchingFlatFrames == 0).length;
  return (need.flats ? math.max(1, noFlats) : 0) +
      (need.darks ? 1 : 0) +
      (need.bias ? 1 : 0);
}

/// The calibration run Simple Mode started, while it runs: frames taken and
/// planned.
({int done, int total})? simpleCalibrationProgress(WidgetRef ref) {
  final run = ref.watch(simpleCalibrationProvider);
  final info = ref.watch(sequenceRunStateProvider).value;
  if (run == null ||
      info?.sequenceId != run.sequenceId ||
      !(info?.state?.isActive ?? false)) {
    return null;
  }
  return (done: info!.framesCaptured, total: run.total);
}

/// The settings chip: the progress, what is missing, or Ready.
String simpleCalibrationSummary(WidgetRef ref, AppLocalizations l) {
  if (simpleCalibrationProgress(ref) case final p?) {
    return '${p.done} / ${p.total}';
  }
  final session = simpleLatestCalibrationSession(ref);
  if (session == null) return l.calibrationChipNoLights;
  final missing = calibrationMissingCount(session);
  return missing == 0 ? l.calibrationChipReady : l.calibrationChipMissing(missing);
}

/// Whether the Plan's own run is going: calibration would have to wait for it.
bool _planRunning(WidgetRef ref) {
  final info = ref.watch(sequenceRunStateProvider).value;
  return (info?.state?.isActive ?? false) &&
      info?.sequenceId == ref.watch(simpleRunProvider).sequenceId;
}

/// Shoots [need] for [session], or, while the Plan runs, asks first and
/// offers the Queue for the darks and bias instead (the rig runs one sequence
/// at a time).
Future<void> simpleCalibrationShoot(
  BuildContext context,
  WidgetRef ref,
  CalibrationSession session,
  CalibrationNeed need, {
  bool reshoot = false,
}) async {
  final calibration = ref.read(simpleCalibrationProvider.notifier);
  if (!_planRunning(ref)) {
    await calibration.shoot(session, need, reshoot: reshoot);
    return;
  }
  final l = context.l10n;
  ref
      .read(simpleMessagesProvider.notifier)
      .show(
        SimpleMessageKind.question,
        l.calibrationPlanRunning,
        origin: SimpleStatusOrigin.calibration,
        actions: [
          SimpleMessageAction(l.calibrationNotNow, () {}),
          if (need.darks || need.bias)
            SimpleMessageAction(
              l.calibrationAddToQueue,
              () => calibration.queue(session, need),
            ),
        ],
      );
}

/// The Calibration mode's shutter: shoots everything missing; its red square
/// stops it.
class SimpleCalibrationShutter extends ConsumerWidget {
  const SimpleCalibrationShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final running = simpleCalibrationProgress(ref) != null;
    final session = simpleLatestCalibrationSession(ref);
    final need = session == null ? null : calibrationNeed(session);
    final camera = ref.watch(cameraStatusProvider).value?.isConnected ?? false;
    return SimpleShutterButton(
      start: true,
      running: running,
      label: running ? l.calibrationShutterStop : l.calibrationShutterShoot,
      whyOff: !camera
          ? l.calibrationConnectCamera
          : session == null
          ? l.calibrationNoLightsYet
          : l.calibrationNothingMissing,
      onPressed: running
          ? ref.read(simpleCalibrationProvider.notifier).stop
          : !camera || session == null || !need!.any
          ? null
          : () => simpleCalibrationShoot(context, ref, session, need),
    );
  }
}

/// The Calibration mode's panel beside the rail (#1757): Flats, Darks & bias
/// and Guide Camera, each shooting only its own sets.
class SimpleCalibrationPanel extends ConsumerWidget {
  const SimpleCalibrationPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final tab = ref.watch(simpleCalibrationTabProvider);
    final progress = simpleCalibrationProgress(ref);
    return SimplePanel(
      key: const Key('simple-calibration-panel'),
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s16,
        vertical: AraSpace.s12,
      ),
      child: SizedBox(
        width: 340,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Text(
              l.shellModeCalibration,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
            if (progress != null) ...[
              Text(l.calibrationTaking(progress.done, progress.total), style: kSimpleText),
              LinearProgressIndicator(
                value: progress.total == 0 ? null : progress.done / progress.total,
              ),
            ],
            if (_planRunning(ref))
              Text(
                l.calibrationPlanRunningNote,
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            SimpleSegments<SimpleCalibrationTab>(
              keyPrefix: 'simple-calibration-tab',
              shrinkToFit: true,
              group: l.calibrationTabsGroup,
              options: [
                (SimpleCalibrationTab.flats, l.calibrationTabFlats, 'flats'),
                (SimpleCalibrationTab.darks, l.calibrationTabDarks, 'darks'),
                (SimpleCalibrationTab.guide, l.calibrationTabGuide, 'guide'),
              ],
              selected: tab,
              onPick: ref.read(simpleCalibrationTabProvider.notifier).set,
            ),
            switch (tab) {
              SimpleCalibrationTab.flats => const _FlatsTab(),
              SimpleCalibrationTab.darks => const _DarksTab(),
              SimpleCalibrationTab.guide => const _GuideCameraTab(),
            },
          ],
        ),
      ),
    );
  }
}

/// A section's dim label over its rows (the mock's `.sect`).
Widget _section(String text) => Padding(
  padding: const EdgeInsets.only(top: AraSpace.s8),
  child: Text(text, style: kSimpleCaption),
);

/// [rows] with a hairline between them, as the mock lists its sets.
Widget _rows(List<Widget> rows) => Column(
  crossAxisAlignment: CrossAxisAlignment.stretch,
  children: [
    for (var i = 0; i < rows.length; i++) ...[
      if (i > 0) const Divider(height: 1, color: AraColors.border),
      ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 44),
        child: Align(alignment: Alignment.centerLeft, child: rows[i]),
      ),
    ],
  ],
);

/// A set's row: what it is, the line under it, its pill and, when given, an
/// action after it.
Widget _setRow(
  String name,
  String? why, {
  required bool covered,
  String? tag,
  Widget? trailing,
  Key? key,
}) => Semantics(
  key: key,
  container: true,
  child: Padding(
    padding: const EdgeInsets.symmetric(vertical: 6),
    child: Row(
      spacing: AraSpace.s8,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(name, style: kSimpleText),
              if (why != null) Text(why, style: _dim),
            ],
          ),
        ),
        SimpleSetTag(covered: covered, label: tag),
        ?trailing,
      ],
    ),
  ),
);

/// A row with a label and an On / Off pair (DESIGN.md: an on/off is a pair
/// of named segments, never a bare toggle).
Widget _onOffRow(
  AppLocalizations l,
  String label, {
  required String id,
  required bool on,
  required ValueChanged<bool>? onPick,
}) => Row(
  spacing: AraSpace.s8,
  children: [
    Expanded(child: Text(label, style: kSimpleText)),
    SizedBox(
      width: 140,
      child: SimpleSegments<bool>(
        keyPrefix: id,
        group: label,
        options: [(true, l.deviceOn, 'on'), (false, l.deviceOff, 'off')],
        selected: on,
        onPick: onPick,
      ),
    ),
  ],
);

/// A set's Covered or Missing in its state colour, outlined like a pill
/// (mock `design/preview/simple-calibration-mock.html`): the outline, not a
/// fill, so it reads over any frame without competing with the buttons.
class SimpleSetTag extends StatelessWidget {
  const SimpleSetTag({super.key, required this.covered, this.label});

  final bool covered;

  /// Words other than Covered / Missing ("On file").
  final String? label;

  @override
  Widget build(BuildContext context) {
    final colour = covered ? AraColors.accentConnected : AraColors.accentBusy;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        border: Border.all(color: colour),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(
        label ??
            (covered
                ? context.l10n.calibrationCovered
                : context.l10n.calibrationMissing),
        style: kSimpleCaption.copyWith(color: colour),
      ),
    );
  }
}

/// Calibration actions need the main camera and no calibration run going.
bool _canShoot(WidgetRef ref) =>
    (ref.watch(cameraStatusProvider).value?.isConnected ?? false) &&
    simpleCalibrationProgress(ref) == null;

class _FlatsTab extends ConsumerWidget {
  const _FlatsTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final session = simpleLatestCalibrationSession(ref);
    if (session == null) return Text(l.calibrationNoLightsYet, style: _dim);
    final panel = ref.watch(flatPanelProvider).value?.isConnected ?? false;
    final light = ref.watch(simpleFlatLightProvider);
    final missing = !session.matchingFlatsAvailable;
    final can = _canShoot(ref);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        _section(l.calibrationFlatsForSession(session.targetName)),
        _rows([
          for (final f in session.filtersUsed)
            _setRow(
              f.filterName.isEmpty ? '—' : f.filterName,
              f.flatsNote ??
                  switch (f.matchingFlatsNewestUtc) {
                    final at? => l.calibrationFlatsFrom(
                      MaterialLocalizations.of(
                        context,
                      ).formatShortMonthDay(at.toLocal()),
                    ),
                    null => l.calibrationNoFlats,
                  },
              covered: f.matchingFlatFrames > 0,
              key: Key('simple-calibration-flat-${f.filterName}'),
            ),
        ]),
        _section(l.calibrationLightGroup),
        SimpleSegments<SimpleFlatLight>(
          keyPrefix: 'simple-calibration-light',
          group: l.calibrationLightGroup,
          options: [
            (SimpleFlatLight.front, l.calibrationLightFront, 'front'),
            (SimpleFlatLight.sky, l.calibrationLightSky, 'sky'),
          ],
          selected: light,
          onPick: ref.read(simpleFlatLightProvider.notifier).set,
        ),
        Text(
          light == SimpleFlatLight.sky
              ? l.calibrationLightSkyNote
              : panel
              ? l.calibrationLightPanelNote
              : l.calibrationLightScreenNote,
          style: _dim,
        ),
        SimpleWindowButton(
          key: const Key('simple-calibration-shoot-flats'),
          label: missing ? l.calibrationShootFlats : l.calibrationReshootFlats,
          onTap: can
              ? () => simpleCalibrationShoot(
                  context,
                  ref,
                  session,
                  (flats: true, darks: false, bias: false),
                  reshoot: !missing,
                )
              : null,
        ),
      ],
    );
  }
}

class _DarksTab extends ConsumerWidget {
  const _DarksTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final session = simpleLatestCalibrationSession(ref);
    if (session == null) return const _DarkLibraryBuilder();
    final need = calibrationNeed(session);
    final darks = (flats: false, darks: need.darks, bias: need.bias);
    final can = _canShoot(ref) && darks.any;
    final panel = ref.watch(flatPanelProvider).value;
    final cover = (panel?.isConnected ?? false) && panel!.hasCover;
    final exposures = {
      for (final f in session.filtersUsed) f.meanExposureSeconds,
    }.toList()..sort();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        _section(l.calibrationDarks),
        _rows([
          _setRow(
            exposures
                .map(
                  (s) => formatExposure(
                    Duration(milliseconds: (s * 1000).round()),
                  ),
                )
                .join(' · '),
            session.coolerSetpointC == null
                ? null
                : '${session.coolerSetpointC!.toStringAsFixed(0)} °C',
            covered: !need.darks,
            key: const Key('simple-calibration-darks'),
          ),
        ]),
        _section(l.calibrationBias),
        _rows([
          _setRow(
            l.calibrationBiasForLights,
            null,
            covered: !need.bias,
            key: const Key('simple-calibration-bias'),
          ),
        ]),
        Text(
          cover ? l.calibrationCoverNote : l.calibrationLensCapNote,
          style: _dim,
        ),
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: SimpleWindowButton(
                key: const Key('simple-calibration-shoot-darks'),
                label: l.calibrationShootNow,
                onTap: can
                    ? () => simpleCalibrationShoot(context, ref, session, darks)
                    : null,
              ),
            ),
            Expanded(
              child: SimpleWindowButton(
                key: const Key('simple-calibration-queue-darks'),
                outlined: true,
                label: l.calibrationAddToQueue,
                onTap: darks.any
                    ? () => ref
                          .read(simpleCalibrationProvider.notifier)
                          .queue(session, darks)
                    : null,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

/// No plan lights yet: darks for exposures picked here, prefilled from the
/// queued plan, at the profile's cooler target (a dark library reused for
/// weeks).
class _DarkLibraryBuilder extends ConsumerStatefulWidget {
  const _DarkLibraryBuilder();

  @override
  ConsumerState<_DarkLibraryBuilder> createState() =>
      _DarkLibraryBuilderState();
}

class _DarkLibraryBuilderState extends ConsumerState<_DarkLibraryBuilder> {
  static const _choices = [30.0, 60.0, 120.0, 180.0, 300.0, 600.0];
  Set<double>? _picked;

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final queued = {
      for (final s in ref.watch(simpleRunProvider).steps)
        if (s.frameType == PlanFrameType.light) s.exposureSeconds,
    };
    final picked = _picked ?? (queued.isEmpty ? {120.0, 300.0} : queued);
    final gain =
        ref.watch(simpleRunGainProvider) ??
        ref.watch(exposureControllerProvider).gain;
    final temp = ref.watch(imagingDefaultsProvider).coolerTargetC;
    final options = {..._choices, ...picked}.toList()..sort();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text(l.calibrationNoLightsYet, style: _dim),
        _section(l.calibrationLibraryExposures),
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [
            for (final s in options)
              FilterChip(
                key: Key('simple-calibration-exposure-${s.round()}'),
                label: Text(
                  formatExposure(Duration(milliseconds: (s * 1000).round())),
                ),
                selected: picked.contains(s),
                showCheckmark: false,
                selectedColor: AraColors.selectionBg,
                onSelected: (on) => setState(
                  () =>
                      _picked = on ? {...picked, s} : ({...picked}..remove(s)),
                ),
              ),
          ],
        ),
        Text(
          l.calibrationLibraryGainTemp(gain, '${temp.toStringAsFixed(0)} °C'),
          style: _dim,
        ),
        SimpleWindowButton(
          key: const Key('simple-calibration-build-library'),
          label: l.calibrationBuildLibrary,
          onTap: _canShoot(ref) && picked.isNotEmpty
              ? () => ref
                    .read(simpleCalibrationProvider.notifier)
                    .buildLibrary(
                      exposures: picked.toList()..sort(),
                      gain: gain,
                      temperatureC: temp,
                      lensCapPrompt: l.calibrationLensCapPrompt,
                    )
              : null,
        ),
      ],
    );
  }
}

/// The Guider's own calibration: its dark library and bad-pixel map, laid
/// out as the mock's Guide Camera tab.
class _GuideCameraTab extends ConsumerWidget {
  const _GuideCameraTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l10n;
    final guider = ref.watch(guiderStatusProvider).value;
    if (guider == null || !guider.isConnected) {
      return Text(l.calibrationGuideNoGuider, style: _dim);
    }
    final cal = ref.watch(guiderCalibrationProvider);
    final status = cal.value?.status;
    final notifier = ref.read(guiderCalibrationProvider.notifier);
    final busy = cal.isLoading;
    final guiding = guider.isBusy;
    // Building covers the guide scope and shoots with it: never under guiding.
    void ask(Future<void> Function() build) => ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.question,
          l.calibrationGuideCover,
          origin: SimpleStatusOrigin.calibration,
          actions: [
            SimpleMessageAction(l.calibrationNotNow, () {}),
            SimpleMessageAction(l.calibrationGuideCovered, build),
          ],
        );
    final darks = status?.darkLibraryExists ?? false;
    final defects = status?.defectMapExists ?? false;
    final min = status?.darkMinExposureSecondsLoaded,
        max = status?.darkMaxExposureSecondsLoaded;
    // The guider reports 9999 / -9999 with nothing loaded.
    final covers = min != null && max != null && min <= max
        ? l.calibrationGuideCovers(_secs(min), _secs(max))
        : l.calibrationGuideReady;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        _section(l.calibrationGuideDarks),
        _rows([
          _setRow(
            darks ? covers : l.calibrationGuideNotBuilt,
            null,
            covered: darks,
            tag: darks ? l.calibrationOnFileTag : null,
            key: const Key('simple-calibration-guide-darks'),
          ),
          if (darks)
            _onOffRow(
              l,
              l.calibrationGuideUse,
              id: 'simple-calibration-guide-darks-use',
              on: status!.darkLibraryLoaded,
              onPick: busy ? null : notifier.setDarkLibraryEnabled,
            ),
        ]),
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: SimpleWindowButton(
                key: const Key('simple-calibration-guide-build-darks'),
                label: darks
                    ? l.calibrationGuideRebuild
                    : l.calibrationGuideBuildDarks,
                onTap: busy || guiding
                    ? null
                    : () => ask(notifier.buildDarkLibrary),
              ),
            ),
            if (darks)
              Expanded(
                child: SimpleWindowButton(
                  key: const Key('simple-calibration-guide-delete-darks'),
                  outlined: true,
                  label: l.calibrationGuideDelete,
                  onTap: busy
                      ? null
                      : () => notifier.deleteCalibrationFiles(defectmap: false),
                ),
              ),
          ],
        ),
        _section(l.calibrationGuideDefect),
        _rows([
          _setRow(
            defects ? l.calibrationGuideReady : l.calibrationGuideNotBuilt,
            null,
            covered: defects,
            tag: defects ? l.calibrationOnFileTag : null,
            key: const Key('simple-calibration-guide-defects'),
          ),
          if (defects)
            _onOffRow(
              l,
              l.calibrationGuideUse,
              id: 'simple-calibration-guide-defects-use',
              on: status!.defectMapLoaded,
              onPick: busy ? null : notifier.setDefectMapEnabled,
            ),
        ]),
        SimpleWindowButton(
          key: const Key('simple-calibration-guide-build-defects'),
          label: defects
              ? l.calibrationGuideRebuildDefects
              : l.calibrationGuideBuildDefects,
          onTap: busy || guiding ? null : () => ask(notifier.buildDefectMap),
        ),
        Text(
          guiding ? l.calibrationGuideStopFirst : l.calibrationGuideCoverNote,
          style: guiding
              ? kSimpleText.copyWith(color: AraColors.accentBusy)
              : _dim,
        ),
        if (cal.hasError)
          Text(
            friendlyError(l, cal.error!, action: l.calibrationActionGuide),
            style: kSimpleText.copyWith(color: AraColors.errorText),
          ),
      ],
    );
  }

  static String _secs(double s) =>
      formatExposure(Duration(milliseconds: (s * 1000).round()));
}
