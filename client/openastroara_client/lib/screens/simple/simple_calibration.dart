import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/calibration/calibration_models.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/calibration/calibration_state.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/guider/guider_calibration_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/simple_calibration_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_run_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/inline_choices.dart' show InlineConfirm;
import 'simple_preview_panel.dart' show SimpleShutterButton, formatExposure;
import 'simple_device_windows.dart' show SimpleWindowButton;
import 'simple_shell.dart' show SimplePanel, SimpleSegments, simpleRailScale;

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);

String _secs(double s) =>
    formatExposure(Duration(milliseconds: (s * 1000).round()));

/// The filters of [session] whose lights have no flats.
List<String> _noFlats(CalibrationSession session) => [
  for (final f in session.filtersUsed)
    if (f.matchingFlatFrames == 0) f.filterName,
];

/// Night done's calibration rows (#1563, round 2): flats every night, and
/// darks only when the camera's library lacks tonight's; each with its own
/// button, then the run's progress. Nothing when the rig has no calibration
/// session for the run.
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
    final info = ref.watch(simpleCalibrationRunStateProvider).value;
    final ours = run != null && info?.sequenceId == run.sequenceId;
    final need = calibrationNeed(session);

    Widget row(String line, String? sub, Widget? action, {Key? key}) => Row(
      key: key,
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

    if (ours && (info?.state?.isActive ?? false)) {
      return row(
        l.calibrationTaking(info!.framesCaptured, run.total),
        null,
        OutlinedButton(
          key: const Key('simple-calibration-stop'),
          onPressed: calibration.stop,
          child: Text(l.deviceStop),
        ),
        key: const Key('simple-calibration'),
      );
    }
    if (ours && info?.state == SequenceRunState.completed) {
      return row(
        l.calibrationDone,
        null,
        null,
        key: const Key('simple-calibration'),
      );
    }
    final darks = need.darks || need.bias;
    return Column(
      key: const Key('simple-calibration'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        if (ours)
          Text(
            l.calibrationStopped(info?.framesCaptured ?? 0, run.total),
            style: _dim,
          ),
        if (need.flats)
          row(
            l.calibrationFlatsMissing,
            _noFlats(session).join(', '),
            FilledButton.tonal(
              key: const Key('simple-calibration-take-flats'),
              onPressed: () => calibration.shoot(session, (
                flats: true,
                darks: false,
                bias: false,
              )),
              child: Text(l.calibrationTakeFlats),
            ),
          )
        else
          row(l.calibrationFlatsOnFile, null, null),
        if (darks)
          row(
            l.calibrationDarksMissingFromLibrary,
            [
              if (need.darks) l.calibrationDarks,
              if (need.bias) l.calibrationBias,
            ].join(' · '),
            OutlinedButton(
              key: const Key('simple-calibration-take-darks'),
              onPressed: () => calibration.shoot(session, (
                flats: false,
                darks: need.darks,
                bias: need.bias,
              )),
              child: Text(l.calibrationTakeDarks),
            ),
          ),
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

/// The calibration run Simple Mode started, while it runs: frames taken and
/// planned.
({int done, int total})? simpleCalibrationProgress(WidgetRef ref) {
  final run = ref.watch(simpleCalibrationProvider);
  final info = ref.watch(simpleCalibrationRunStateProvider).value;
  if (run == null ||
      info?.sequenceId != run.sequenceId ||
      !(info?.state?.isActive ?? false)) {
    return null;
  }
  return (done: info!.framesCaptured, total: run.total);
}

/// Whether the Plan's own run is going: calibration would have to wait for it.
bool _planRunning(WidgetRef ref) {
  final info = ref.watch(sequenceRunStateProvider).value;
  return (info?.state?.isActive ?? false) &&
      info?.sequenceId == ref.watch(simpleRunProvider).sequenceId;
}

/// What the Calibration mode does on its current tab: the chip's words, the
/// shutter's caption, the action (null: nothing to do, with [whyOff] saying
/// why) and, while the Plan runs, the Queue as the alternative.
typedef SimpleCalibrationJob = ({
  String chip,
  String caption,
  VoidCallback? run,
  String? whyOff,
  VoidCallback? queue,
});

/// The current tab's job, the same one the shutter and the tab's main button
/// do (round 2 mock, approved 2026-11-12).
SimpleCalibrationJob simpleCalibrationJob(BuildContext context, WidgetRef ref) {
  final l = context.l10n;
  final calibration = ref.read(simpleCalibrationProvider.notifier);
  SimpleCalibrationJob idle(String chip, String why) =>
      (chip: chip, caption: why, run: null, whyOff: why, queue: null);
  if (simpleCalibrationProgress(ref) case final p?) {
    return (
      chip: '${p.done} / ${p.total}',
      caption: l.calibrationShutterStop,
      run: calibration.stop,
      whyOff: null,
      queue: null,
    );
  }
  final camera = ref.watch(cameraStatusProvider).value?.isConnected ?? false;
  switch (ref.watch(simpleCalibrationTabProvider)) {
    case SimpleCalibrationTab.flats:
      final session = simpleLatestCalibrationSession(ref);
      if (session == null) {
        return idle(l.calibrationChipNoLights, l.calibrationNoLightsYet);
      }
      final missing = _noFlats(session).length;
      if (session.matchingFlatsAvailable) {
        return idle(l.calibrationChipReady, l.calibrationNothingMissing);
      }
      if (!camera) {
        return idle(l.calibrationChipMissing(missing), l.calibrationConnectCamera);
      }
      return (
        chip: l.calibrationChipMissing(missing),
        caption: l.calibrationCapShootFlats,
        run: () => calibration.shoot(session, (
          flats: true,
          darks: false,
          bias: false,
        )),
        whyOff: null,
        queue: null,
      );
    case SimpleCalibrationTab.darks:
      final need = ref.watch(simpleLibraryNeedProvider);
      final lib = ref.watch(darkLibraryStatusProvider).value;
      if (need == null) {
        return idle(l.calibrationChipNoPlan, l.calibrationLibraryNoPlan);
      }
      if (lib == null) return idle('…', l.calibrationLibraryNoPlan);
      final cover = libraryCover(lib, need, DateTime.now().toUtc());
      final refresh = cover.missing.isEmpty && cover.old.isNotEmpty;
      final exposures = cover.missing.isNotEmpty ? cover.missing : cover.old;
      if (exposures.isEmpty && !cover.biasMissing) {
        return idle(l.calibrationChipReady, l.calibrationNothingMissing);
      }
      final what = [
        if (exposures.isNotEmpty) exposures.map(_secs).join(' · '),
        if (cover.biasMissing) l.calibrationBias.toLowerCase(),
      ].join(' + ');
      final chip = cover.missing.isNotEmpty
          ? l.calibrationChipMissing(cover.missing.length)
          : refresh
          ? l.calibrationChipOld(cover.old.length)
          : l.calibrationChipMissing(1);
      if (!camera) return idle(chip, l.calibrationConnectCamera);
      final prompt = l.calibrationLensCapPrompt;
      return (
        chip: chip,
        caption: refresh
            ? l.calibrationCapRefresh(what)
            : l.calibrationCapBuild(what),
        run: () => calibration.buildLibrary(
          need,
          exposures,
          bias: cover.biasMissing,
          refresh: refresh,
          lensCapPrompt: prompt,
        ),
        whyOff: null,
        queue: () => calibration.queueLibrary(
          need,
          exposures,
          bias: cover.biasMissing,
          refresh: refresh,
          lensCapPrompt: prompt,
        ),
      );
    case SimpleCalibrationTab.guide:
      final guider = ref.watch(guiderStatusProvider).value;
      if (guider == null || !guider.isConnected) {
        return idle(l.calibrationChipNoGuider, l.calibrationGuideNoGuider);
      }
      final status = ref.watch(guiderCalibrationProvider).value?.status;
      final darks = status?.darkLibraryExists ?? false;
      final defects = status?.defectMapExists ?? false;
      if (darks && defects) {
        return idle(l.calibrationChipReady, l.calibrationNothingMissing);
      }
      final chip = l.calibrationChipMissing((darks ? 0 : 1) + (defects ? 0 : 1));
      if (guider.isBusy) return idle(chip, l.calibrationGuideStopFirst);
      final notifier = ref.read(guiderCalibrationProvider.notifier);
      return (
        chip: chip,
        caption: darks
            ? l.calibrationGuideBuildDefects
            : l.calibrationGuideBuildDarks,
        run: () => _askGuideCover(
          context,
          ref,
          darks ? notifier.buildDefectMap : notifier.buildDarkLibrary,
        ),
        whyOff: null,
        queue: null,
      );
  }
}

/// A Guide Camera build shoots darks with the guide scope: covered first.
void _askGuideCover(
  BuildContext context,
  WidgetRef ref,
  Future<void> Function() build,
) {
  final l = context.l10n;
  ref
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
}

/// Runs [job]. A sequence run goes through the queue (after a running
/// plan's targets), so nothing needs asking first.
void _runJob(BuildContext context, WidgetRef ref, SimpleCalibrationJob job) =>
    job.run?.call();

/// The settings chip: the current tab's state.
String simpleCalibrationSummary(BuildContext context, WidgetRef ref) =>
    simpleCalibrationJob(context, ref).chip;

/// The Calibration mode's shutter: the current tab's job, its caption under
/// it; its red square stops a run.
class SimpleCalibrationShutter extends ConsumerWidget {
  const SimpleCalibrationShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final job = simpleCalibrationJob(context, ref);
    final running = simpleCalibrationProgress(ref) != null;
    return Column(
      mainAxisSize: MainAxisSize.min,
      spacing: 4,
      children: [
        SimpleShutterButton(
          start: true,
          running: running,
          label: job.caption,
          whyOff: job.whyOff,
          onPressed: job.run == null ? null : () => _runJob(context, ref, job),
        ),
        // What it will do, only when it can: the chip says the state, and a
        // tap on an idle shutter says why in a pill, as the panel does.
        if (job.run != null)
          ExcludeSemantics(
            // As wide as the rail at every text size, so large text wraps
            // the same words to the same lines instead of cutting them.
            child: SizedBox(
              width: 64 * simpleRailScale(context),
              child: Text(
                job.caption,
                key: const Key('simple-calibration-shutter-caption'),
                textAlign: TextAlign.center,
                style: kSimpleCaption,
              ),
            ),
          ),
      ],
    );
  }
}

/// The Calibration mode's panel beside the rail (#1757, round 2): Flats for
/// the night, the camera's Darks & bias library, and the Guide Camera's own.
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
              Text(
                l.calibrationTaking(progress.done, progress.total),
                style: kSimpleText,
              ),
              LinearProgressIndicator(
                semanticsLabel: l.calibrationTaking(progress.done, progress.total),
                value: progress.total == 0
                    ? null
                    : progress.done / progress.total,
              ),
            ],
            if (_planRunning(ref))
              Text(
                l.calibrationPlanRunningNote,
                style: kSimpleText.copyWith(color: AraColors.accentInfo),
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
Widget _section(String text, {Key? key}) => Padding(
  key: key,
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

/// A set's row: what it is, the line under it and its pill.
Widget _setRow(
  String name,
  String? why, {
  required bool covered,
  String? tag,
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
    // As wide as its words (pt-BR's "Ligado" was cut in a fixed 140 px),
    // as Focus's Measured stars.
    SimpleSegments<bool>(
      keyPrefix: id,
      group: label,
      expand: false,
      options: [(true, l.deviceOn, 'on'), (false, l.deviceOff, 'off')],
      selected: on,
      onPick: onPick,
    ),
  ],
);

/// A set's Covered or Missing in its state colour, outlined like a pill
/// (mock `design/preview/simple-calibration-mock.html`): the outline, not a
/// fill, so it reads over any frame without competing with the buttons.
class SimpleSetTag extends StatelessWidget {
  const SimpleSetTag({super.key, required this.covered, this.label});

  final bool covered;

  /// Words other than Covered / Missing ("On file", "Old").
  final String? label;

  @override
  Widget build(BuildContext context) {
    final colour = covered ? AraColors.accentConnected : AraColors.accentBusy;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        border: Border.all(color: colour),
        borderRadius: BorderRadius.circular(AraRadius.simplePanel),
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
    final missing = _noFlats(session).length;
    final job = simpleCalibrationJob(context, ref);
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
                    // The Missing pill says it; "No flats" beside it repeats it.
                    null => null,
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
        if (missing > 0 && !session.matchingFlatsAvailable)
          SimpleWindowButton(
            key: const Key('simple-calibration-shoot-flats'),
            label: l.calibrationShootMissingFlats(missing),
            onTap: job.run == null ? null : () => _runJob(context, ref, job),
          )
        else ...[
          SimpleWindowButton(
            key: const Key('simple-calibration-shoot-flats'),
            label: l.calibrationReshootAll,
            onTap: _canShoot(ref)
                ? () => ref
                      .read(simpleCalibrationProvider.notifier)
                      .shoot(session, (
                        flats: true,
                        darks: false,
                        bias: false,
                      ), reshoot: true)
                : null,
          ),
          Text(l.calibrationReshootAllNote, style: _dim),
        ],
      ],
    );
  }
}

/// The camera's library (round 2): darks and bias on file, how old, whether
/// they cover the queued plan, and Build or Refresh. Darks and bias last
/// months and belong to the camera, not the night (user, 2026-11-11).
class _DarksTab extends ConsumerStatefulWidget {
  const _DarksTab();

  @override
  ConsumerState<_DarksTab> createState() => _DarksTabState();
}

class _DarksTabState extends ConsumerState<_DarksTab> {
  static const _choices = [30.0, 60.0, 120.0, 180.0, 300.0, 600.0];

  /// The exposures picked by hand; null: what the plan needs.
  Set<double>? _picked;

  /// Bias picked by hand; null: on when the library lacks it (#1790).
  bool? _biasPicked;

  String _age(AppLocalizations l, DateTime at) {
    final d = DateTime.now().toUtc().difference(at);
    return d.inDays < 14
        ? l.calibrationAgeDays(d.inDays)
        : d.inDays < 60
        ? l.calibrationAgeWeeks(d.inDays ~/ 7)
        : l.calibrationAgeMonths(d.inDays ~/ 30);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final lib = ref.watch(darkLibraryStatusProvider).value;
    final need = ref.watch(simpleLibraryNeedProvider);
    final camera = ref.watch(cameraStatusProvider).value;
    final now = DateTime.now().toUtc();
    final cover = lib == null || need == null
        ? null
        : libraryCover(lib, need, now);
    final job = simpleCalibrationJob(context, ref);
    final darks = lib?.entries ?? const <DarkLibraryEntry>[];
    final bias = lib?.biasEntries ?? const <BiasLibraryEntry>[];
    final planExposures = need?.exposures ?? const <double>[];
    final picked =
        _picked ??
        {
          ...?cover?.missing,
          ...?cover?.old,
          if (cover != null && cover.missing.isEmpty && cover.old.isEmpty)
            ...planExposures,
        };
    final biasOn = _biasPicked ?? cover?.biasMissing ?? true;
    // Nothing picked by hand: the shutter's own job.
    final byHand = _picked != null || _biasPicked != null;
    final canBuild = picked.isNotEmpty || biasOn;
    final temp = need?.temperatureC;
    String settings(int? gain, double? t) => [
      if (gain != null) l.calibrationGain(gain),
      if (t != null) '${t.toStringAsFixed(0)} °C',
    ].join(' · ');
    final old = <DarkLibraryEntry>{
      for (final d in darks)
        if (now.difference(d.capturedUtc) > kSimpleLibraryOldAfter) d,
    };
    final (String head, String? detail, bool ok) = switch (cover) {
      null => (l.calibrationLibraryNoPlan, null, false),
      _ when darks.isEmpty && bias.isEmpty => (
        l.calibrationLibraryEmpty,
        l.calibrationLibraryEmptyNote,
        false,
      ),
      (missing: final m, old: _, biasMissing: final b) when m.isNotEmpty || b =>
        (
          l.calibrationLibraryNeeds(
            [
              if (m.isNotEmpty) m.map(_secs).join(' · '),
              if (b) l.calibrationBias.toLowerCase(),
            ].join(' + '),
          ),
          settings(need!.gain, temp),
          false,
        ),
      (missing: _, old: final o, biasMissing: _) when o.isNotEmpty => (
        l.calibrationLibraryOld(o.map(_secs).join(' · ')),
        l.calibrationLibraryOldNote,
        false,
      ),
      _ => (
        l.calibrationLibraryCovers,
        '${planExposures.map(_secs).join(' · ')} · ${settings(need!.gain, temp)}',
        true,
      ),
    };
    // Picked: Plan's solid blue with white words.
    Widget chip(Key key, String label, bool on, ValueChanged<bool> onSelected) =>
        FilterChip(
          key: key,
          label: Text(
            label,
            style: kSimpleText.copyWith(
              color: on ? AraColors.selectionFg : AraColors.textPrimary,
            ),
          ),
          selected: on,
          showCheckmark: false,
          selectedColor: AraColors.selectionBg,
          onSelected: onSelected,
        );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        _section(
          l.calibrationLibraryCamera(lib?.camera ?? camera?.name ?? l.calibrationTabDarks),
          key: const Key('simple-calibration-library-camera'),
        ),
        Container(
          key: const Key('simple-calibration-library-cover'),
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
          decoration: BoxDecoration(
            border: Border.all(color: AraColors.border),
            borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                head,
                style: kSimpleText.copyWith(
                  fontWeight: FontWeight.w600,
                  color: ok ? AraColors.accentConnected : AraColors.accentBusy,
                ),
              ),
              if (detail != null) Text(detail, style: _dim),
            ],
          ),
        ),
        _section(l.calibrationDarks),
        if (darks.isEmpty)
          Text(l.calibrationLibraryNone, style: _dim)
        else
          _rows([
            for (final d in [...darks]
              ..sort((a, b) => b.exposureSeconds.compareTo(a.exposureSeconds)))
              _setRow(
                [_secs(d.exposureSeconds), settings(d.gain, d.temperatureC)]
                    .where((s) => s.isNotEmpty)
                    .join(' · '),
                [
                  l.calibrationLibraryAge(_age(l, d.capturedUtc), d.frameCount),
                  if (d.camera == null) l.calibrationLibraryNoCamera,
                ].join(' · '),
                covered: !old.contains(d),
                tag: old.contains(d) ? l.calibrationOld : l.calibrationOnFileTag,
              ),
          ]),
        _section(l.calibrationBias),
        if (bias.isEmpty)
          Text(l.calibrationLibraryNone, style: _dim)
        else
          _rows([
            for (final b in bias)
              _setRow(
                [
                  if (b.gain != null) l.calibrationGain(b.gain!),
                  if (b.offset != null) l.calibrationOffset(b.offset!),
                  '${b.bin}×${b.bin}',
                ].join(' · '),
                [
                  l.calibrationLibraryAge(_age(l, b.capturedUtc), b.frameCount),
                  if (b.camera == null) l.calibrationLibraryNoCamera,
                ].join(' · '),
                covered: true,
                tag: l.calibrationOnFileTag,
              ),
          ]),
        if (need != null) ...[
          _section(l.calibrationLibraryBuildSection),
          Wrap(
            spacing: AraSpace.s8,
            runSpacing: AraSpace.s8,
            children: [
              for (final s in {..._choices, ...planExposures}.toList()..sort())
                chip(
                  Key('simple-calibration-exposure-${s.round()}'),
                  _secs(s),
                  picked.contains(s),
                  (on) => setState(
                    () => _picked = on ? {...picked, s} : ({...picked}..remove(s)),
                  ),
                ),
              // Bias is shot again on request, even with some on file.
              chip(
                const Key('simple-calibration-bias'),
                l.calibrationBias,
                biasOn,
                (on) => setState(() => _biasPicked = on),
              ),
            ],
          ),
          Text(
            l.calibrationLibraryNote(settings(need.gain, temp)),
            style: _dim,
          ),
          Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(
                child: SimpleWindowButton(
                  key: const Key('simple-calibration-build-library'),
                  label: !byHand
                      ? job.caption
                      : l.calibrationBuildPicked,
                  onTap: !_canShoot(ref) || !canBuild
                      ? null
                      : !byHand
                      ? (job.run == null ? null : () => _runJob(context, ref, job))
                      : () => ref
                            .read(simpleCalibrationProvider.notifier)
                            .buildLibrary(
                              need,
                              picked.toList()..sort(),
                              bias: biasOn,
                              refresh: true,
                              lensCapPrompt: l.calibrationLensCapPrompt,
                            ),
                ),
              ),
              Expanded(
                child: SimpleWindowButton(
                  key: const Key('simple-calibration-queue-library'),
                  outlined: true,
                  label: l.calibrationAddToQueue,
                  onTap: !canBuild
                      ? null
                      : () => ref
                            .read(simpleCalibrationProvider.notifier)
                            .queueLibrary(
                              need,
                              picked.toList()..sort(),
                              bias: biasOn,
                              refresh: _picked != null,
                              lensCapPrompt: l.calibrationLensCapPrompt,
                            ),
                ),
              ),
            ],
          ),
        ],
      ],
    );
  }
}

/// The Guider's own calibration: its dark library and bad-pixel map, laid
/// out as the mock's Guide Camera tab.
class _GuideCameraTab extends ConsumerStatefulWidget {
  const _GuideCameraTab();

  @override
  ConsumerState<_GuideCameraTab> createState() => _GuideCameraTabState();
}

class _GuideCameraTabState extends ConsumerState<_GuideCameraTab> {
  /// The set whose Delete is being asked about ('darks' or 'defects').
  String? _deleteAsk;

  @override
  Widget build(BuildContext context) {
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
    final darks = status?.darkLibraryExists ?? false;
    final defects = status?.defectMapExists ?? false;
    final min = status?.darkMinExposureSecondsLoaded,
        max = status?.darkMaxExposureSecondsLoaded;
    // The guider reports 9999 / -9999 with nothing loaded.
    final covers = min != null && max != null && min <= max
        ? l.calibrationGuideCovers(_secs(min), _secs(max))
        : l.calibrationGuideReady;
    // Building covers the guide scope and shoots with it: never under guiding.
    VoidCallback? build(Future<void> Function() action) => busy || guiding
        ? null
        : () => _askGuideCover(context, ref, action);
    // A rebuild takes minutes of frames with the scope covered: Delete asks
    // first, in the block (no popups in Simple Mode).
    Widget pair(String id, String rebuild, VoidCallback? onBuild, {required bool exists, required bool darksFile}) =>
        Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Row(
              spacing: AraSpace.s8,
              children: [
                Expanded(
                  child: SimpleWindowButton(
                    key: Key('simple-calibration-guide-build-$id'),
                    label: rebuild,
                    onTap: onBuild,
                  ),
                ),
                if (exists)
                  Expanded(
                    child: SimpleWindowButton(
                      key: Key('simple-calibration-guide-delete-$id'),
                      outlined: true,
                      label: l.calibrationGuideDelete,
                      onTap: busy ? null : () => setState(() => _deleteAsk = id),
                    ),
                  ),
              ],
            ),
            if (exists && _deleteAsk == id)
              InlineConfirm(
                id: 'simple-calibration-guide-delete-$id-ask',
                title: darksFile ? l.calibrationGuideDeleteDarksTitle : l.calibrationGuideDeleteDefectsTitle,
                body: Text(l.calibrationGuideDeleteBody),
                confirmLabel: l.calibrationGuideDelete,
                cancelLabel: l.planCancel,
                style: kSimpleText,
                onConfirm: () {
                  setState(() => _deleteAsk = null);
                  notifier.deleteCalibrationFiles(darks: darksFile, defectmap: !darksFile);
                },
                onCancel: () => setState(() => _deleteAsk = null),
              ),
          ],
        );
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
        pair(
          'darks',
          darks ? l.calibrationGuideRebuild : l.calibrationGuideBuildDarks,
          build(notifier.buildDarkLibrary),
          exists: darks,
          darksFile: true,
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
        pair(
          'defects',
          defects ? l.calibrationGuideRebuild : l.calibrationGuideBuildDefects,
          build(notifier.buildDefectMap),
          exists: defects,
          darksFile: false,
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
}
