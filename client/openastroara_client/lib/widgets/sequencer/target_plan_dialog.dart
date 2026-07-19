import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../state/settings/camera_electronics_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/optimal_sub_local.dart';

/// What the user picked in the target-plan chooser: a per-filter plan (or
/// null for the plain default loop) and whether to guide + dither.
class TargetPlanChoice {
  final List<FilterPlanStep>? filterPlan;
  final bool guide;
  const TargetPlanChoice({this.filterPlan, required this.guide});
}

/// One selectable plan card: a label plus its ready-computed steps
/// (null steps = the basic default loop).
class _PlanOption {
  final String label;
  final String subtitle;
  final List<FilterPlanStep>? steps;
  const _PlanOption(this.label, this.subtitle, this.steps);
}

/// §Run smart targets — the plan chooser shown when a target is sent to the
/// sequencer and the user has a planning filter set to choose from. Builds
/// SHO / LRGB / single-filter options with per-filter exposures from the
/// local Glover calculation (clamped to a practical range) and frame counts
/// that split tonight's remaining dark window across the chosen filters.
///
/// Returns null when the user cancels. When the filter set offers no real
/// choice (empty, or OSC/dual-band only), callers should skip the dialog via
/// [planOptionsAvailable] and build the basic run directly.
Future<TargetPlanChoice?> showTargetPlanDialog(
  BuildContext context, {
  required String targetName,
  required double raDeg,
  required double decDeg,
  double? remainingDarkHours,
}) =>
    showDialog<TargetPlanChoice>(
      context: context,
      builder: (_) => _TargetPlanDialog(
        targetName: targetName,
        raDeg: raDeg,
        decDeg: decDeg,
        remainingDarkHours: remainingDarkHours,
      ),
    );

/// Whether [filters] offers a real filter-change choice (any mono narrowband
/// or broadband filter). OSC / dual- / tri-band shooters image through one
/// fixed filter — no plan to choose.
bool planOptionsAvailable(List<PlanningFilter> filters) => filters.any(
      (f) => switch (f.kind) {
        FilterKind.ha ||
        FilterKind.oiii ||
        FilterKind.sii ||
        FilterKind.l ||
        FilterKind.r ||
        FilterKind.g ||
        FilterKind.b =>
          true,
        FilterKind.osc || FilterKind.duo || FilterKind.tri => false,
      },
    );

class _TargetPlanDialog extends ConsumerStatefulWidget {
  const _TargetPlanDialog({
    required this.targetName,
    required this.raDeg,
    required this.decDeg,
    required this.remainingDarkHours,
  });

  final String targetName;
  final double raDeg;
  final double decDeg;
  final double? remainingDarkHours;

  @override
  ConsumerState<_TargetPlanDialog> createState() => _TargetPlanDialogState();
}

class _TargetPlanDialogState extends ConsumerState<_TargetPlanDialog> {
  int _selected = 0;
  late bool _guide;
  late final List<_PlanOption> _options;

  @override
  void initState() {
    super.initState();
    _guide = ref.read(phd2SettingsProvider).ditherEnabled;
    _options = _buildOptions();
  }

  /// A practical per-filter sub length: the local Glover recommendation,
  /// never below the user's default exposure (Glover's floor is a MINIMUM —
  /// "8 s works" is true but nobody wants 3,000 files), never above the
  /// saturation ceiling, capped at 10 min so a narrowband floor can't demand
  /// unguidable subs. Falls back to the default exposure when the setup
  /// can't compute.
  double _suggestedExposure(String filterName, double defaultExposure) {
    final outcome = resolveOptimalSubLocal(
      optics: ref.read(opticsSettingsProvider),
      electronics: ref.read(cameraElectronicsProvider),
      site: ref.read(siteSettingsProvider),
      filterSet: ref.read(filterSetProvider),
      filterName: filterName,
      raDeg: widget.raDeg,
      decDeg: widget.decDeg,
    );
    if (outcome is! LocalOptimalSubSuccess) {
      return defaultExposure > 0 ? defaultExposure : 60;
    }
    final r = outcome.result;
    var sec = math.max(r.recommendedSec, defaultExposure);
    if (r.viable) sec = math.min(sec, r.ceilingSec);
    // Floor at 30 s: past the Glover floor the quality penalty is essentially
    // flat, but a 10 s luminance plan means ~350 files/hour — hours of extra
    // stacking for no measurable gain. (The per-node advisor still shows the
    // pure floor; this floor is a PLAN practicality, not physics.) The
    // saturation ceiling still wins when the sky is truly that bright.
    sec = sec.clamp(30.0, 600.0);
    if (r.viable && r.ceilingSec < sec) sec = math.max(1.0, r.ceilingSec);
    // Round to something a human would type: nearest 10 s above a minute,
    // nearest 5 s below.
    final step = sec >= 60 ? 10 : 5;
    return math.max(step, (sec / step).round() * step).toDouble();
  }

  List<FilterPlanStep> _steps(List<PlanningFilter> picked) {
    final defaults = ref.read(imagingDefaultsProvider);
    final defaultExposure = defaults.defaultExposure.inMilliseconds /
        Duration.millisecondsPerSecond;
    // Split tonight's remaining window evenly across the chosen filters.
    final hoursEach = widget.remainingDarkHours != null
        ? widget.remainingDarkHours! / picked.length
        : null;
    return [
      for (final f in picked)
        () {
          final exp = _suggestedExposure(f.name, defaultExposure);
          return FilterPlanStep(
            filterName: f.name,
            exposureSeconds: exp,
            frameCount: defaultFrameCount(
              exp,
              remainingDarkHours: hoursEach,
              fallbackFrames: math.max(12, 60 ~/ picked.length),
            ),
          );
        }(),
    ];
  }

  static String _fmtSec(double s) =>
      s >= 120 ? '${(s / 60).toStringAsFixed(s % 60 == 0 ? 0 : 1)} min' : '${s.round()} s';

  String _subtitle(List<FilterPlanStep> steps) => steps
      .map((s) => '${s.filterName} ${_fmtSec(s.exposureSeconds)} × ${s.frameCount}')
      .join('   ·   ');

  List<_PlanOption> _buildOptions() {
    final filters = ref.read(filterSetProvider).filters;
    PlanningFilter? byKind(FilterKind k) =>
        filters.where((f) => f.kind == k).firstOrNull;

    final options = <_PlanOption>[];

    final narrow = [
      for (final k in [FilterKind.ha, FilterKind.oiii, FilterKind.sii])
        ?byKind(k),
    ];
    if (narrow.isNotEmpty) {
      final steps = _steps(narrow);
      options.add(_PlanOption(
        narrow.length == 3
            ? 'Narrowband · SHO'
            : 'Narrowband · ${narrow.map((f) => f.name).join(" / ")}',
        _subtitle(steps),
        steps,
      ));
    }

    final broad = [
      for (final k in [FilterKind.l, FilterKind.r, FilterKind.g, FilterKind.b])
        ?byKind(k),
    ];
    if (broad.isNotEmpty) {
      final steps = _steps(broad);
      options.add(_PlanOption(
        broad.length >= 4 ? 'Broadband · LRGB' : 'Broadband · ${broad.map((f) => f.name).join(" / ")}',
        _subtitle(steps),
        steps,
      ));
    }

    // Every filter singly — for the "just shoot Ha tonight" session.
    for (final f in filters) {
      if (f.kind == FilterKind.osc) continue;
      final steps = _steps([f]);
      options.add(_PlanOption('${f.name} only', _subtitle(steps), steps));
    }

    options.add(const _PlanOption(
      'Basic',
      'One loop with your Imaging Defaults — no filter changes.',
      null,
    ));
    return options;
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: Text('Plan for ${widget.targetName}'),
      content: SizedBox(
        width: 460,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Pick how tonight gets imaged — exposures come from your '
                'optics and sky, frames split the window across filters. '
                'Everything stays editable in the sequence afterwards.',
                style:
                    TextStyle(color: AraColors.textSecondary, fontSize: 12),
              ),
              const SizedBox(height: 12),
              for (var i = 0; i < _options.length; i++)
                _PlanCard(
                  option: _options[i],
                  selected: _selected == i,
                  onTap: () => setState(() => _selected = i),
                ),
              const SizedBox(height: 4),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                dense: true,
                title: const Text('Guide with PHD2',
                    style: TextStyle(
                        color: AraColors.textPrimary, fontSize: 13)),
                subtitle: Text(
                  _guide
                      ? 'Starts guiding after autofocus, dithers every '
                          '${ref.read(phd2SettingsProvider).ditherEveryNFrames} '
                          'frame(s).'
                      : 'No guiding steps in the run.',
                  style: const TextStyle(
                      color: AraColors.textSecondary, fontSize: 11.5),
                ),
                value: _guide,
                onChanged: (v) => setState(() => _guide = v),
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(TargetPlanChoice(
            filterPlan: _options[_selected].steps,
            guide: _guide,
          )),
          child: const Text('Create run'),
        ),
      ],
    );
  }
}

class _PlanCard extends StatelessWidget {
  const _PlanCard({
    required this.option,
    required this.selected,
    required this.onTap,
  });

  final _PlanOption option;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 8),
        child: InkWell(
          borderRadius: BorderRadius.circular(8),
          onTap: onTap,
          child: Container(
            width: double.infinity,
            padding: const EdgeInsets.all(10),
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(8),
              border: Border.all(
                color: selected ? AraColors.accentInfo : AraColors.border,
                width: selected ? 1.5 : 1,
              ),
              color: selected
                  ? AraColors.accentInfo.withValues(alpha: 0.08)
                  : Colors.transparent,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(option.label,
                    style: const TextStyle(
                        color: AraColors.textPrimary,
                        fontSize: 13,
                        fontWeight: FontWeight.w600)),
                const SizedBox(height: 2),
                Text(option.subtitle,
                    style: const TextStyle(
                        color: AraColors.textSecondary, fontSize: 11.5)),
              ],
            ),
          ),
        ),
      );
}
