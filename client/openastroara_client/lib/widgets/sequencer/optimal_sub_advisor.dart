import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart'
    show slewScopeToRaDecType, switchFilterType;
import '../../models/sequence/nina_dom.dart';
import '../../services/optimal_sub_api.dart' show OptimalSubResult;
import '../../state/settings/camera_electronics_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../util/optimal_sub_local.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import '../../util/input_coordinates.dart';

// TakeExposure (the node the advisor attaches to) and SwitchFilter (the
// sibling it scans for) come from the instruction catalog's named constants;
// the two below are advisor-only scan targets with no catalog entry.
const String _deepSkyObjectContainerType =
    'OpenAstroAra.Sequencer.Container.DeepSkyObjectContainer, OpenAstroAra.Sequencer';
const String _centerAndRotateType =
    'OpenAstroAra.Sequencer.SequenceItem.Platesolving.CenterAndRotate, OpenAstroAra.Sequencer';

/// The filter name a TakeExposure at [path] runs under, or null: the nearest
/// preceding SwitchFilter, level by level from the root (NINA executes a
/// SequentialContainer top-to-bottom, so at every ancestor level the last
/// filter switch before the branch the exposure sits in is in effect —
/// deeper/later wins). A switch that's a sibling of the exposure's CONTAINER
/// counts too: the smart-target plan emits `Switch Filter → "Ha Imaging"
/// [Take Exposure]`, where the switch never shares a parent with the
/// exposure. Pure — exposed for tests.
String? filterNameForExposure(Map<String, dynamic> body, NodePath path) {
  String? name;
  for (var depth = 0; depth < path.length; depth++) {
    final parent = nodeAt(body, path.sublist(0, depth));
    if (parent == null) return name;
    final siblings = childrenOf(parent);
    final index = path[depth];
    for (var i = 0; i < index && i < siblings.length; i++) {
      final sibling = siblings[i];
      if (sibling[r'$type'] == switchFilterType) {
        final n = _filterInfoName(sibling['Filter']);
        if (n != null) name = n;
      }
    }
  }
  return name;
}

/// The display name inside a `FilterInfo` value — the daemon serialises the
/// backing field as `_name` (what [buildFilterInfo] writes), while some NINA
/// exports carry a public `Name`. Either counts.
String? _filterInfoName(Object? filter) {
  if (filter is! Map) return null;
  final n = filter['_name'] ?? filter['Name'];
  return n is String && n.trim().isNotEmpty ? n.trim() : null;
}

/// §3.1 — the target position (J2000 decimal DEGREES) in effect for a
/// TakeExposure at [path], or null when the sequence names none. Pure —
/// exposed for tests.
///
/// Mirrors NINA's top-to-bottom execution the same way [filterNameForExposure]
/// does, level by level from the root: a Deep Sky Object container's
/// `Target.InputCoordinates` applies to everything inside it, and a preceding
/// `SlewScopeToRaDec` / `CenterAndRotate` sibling's `Coordinates` applies to
/// what follows. Deeper context wins over outer, and among siblings the last
/// one before the exposure wins. A zeroed position (RA 0h 0m 0s, Dec +0° 0′ 0″
/// — the catalog default of a freshly-added instruction) is treated as
/// not-set-yet rather than "the celestial equator at RA 0", and a slew with
/// `Inherited: true` is skipped entirely — NINA resolves it from the enclosing
/// target at runtime, so its own `Coordinates` may be stale leftovers from
/// before the toggle (the DSO container's Target is the truth in that case).
({double raDeg, double decDeg})? targetPositionForExposure(
  Map<String, dynamic> body,
  NodePath path,
) {
  ({double raDeg, double decDeg})? found = _dsoTarget(body);
  for (var depth = 0; depth < path.length; depth++) {
    final parent = nodeAt(body, path.sublist(0, depth));
    if (parent == null) return found;
    final siblings = childrenOf(parent);
    final index = path[depth];
    for (var i = 0; i < index && i < siblings.length; i++) {
      final sibling = siblings[i];
      final type = sibling[r'$type'];
      if ((type == slewScopeToRaDecType || type == _centerAndRotateType) &&
          sibling['Inherited'] != true) {
        found =
            _nonZero(degFromInputCoordinates(sibling['Coordinates'])) ?? found;
      }
    }
    if (index < siblings.length) {
      // The container the path descends into next (its own Target outranks a
      // same-level preceding slew — it wraps the exposure more tightly).
      found = _dsoTarget(siblings[index]) ?? found;
    }
  }
  return found;
}

({double raDeg, double decDeg})? _dsoTarget(Map<String, dynamic> node) {
  if (node[r'$type'] != _deepSkyObjectContainerType) return null;
  final target = node['Target'];
  if (target is! Map) return null;
  return _nonZero(degFromInputCoordinates(target['InputCoordinates']));
}

({double raDeg, double decDeg})? _nonZero(
  ({double raDeg, double decDeg})? pos,
) => pos != null && pos.raDeg == 0 && pos.decDeg == 0 ? null : pos;

/// NEXTGEN §5 — the per-filter "Optimal Sub" advisor under a TakeExposure's
/// fields: the Glover read-noise floor (criterion popularised by Dr. Robin
/// Glover, SharpCap — attributed per the recorded permission) intersected
/// with the saturation ceiling, computed CLIENT-SIDE (PORT_DECISIONS
/// 2026-07-15) for the exposure's filter. Apply writes a **plain number** into the standard
/// `ExposureTime` field via [SequenceEditorController.setNodeField] — no new
/// instruction types, no `$type` churn — so the sequence stays NINA-valid.
///
/// Shows a quiet one-line message when the inputs can't compute (e.g.
/// aperture not set up), with a jump to the Settings panel that fixes it.
class OptimalSubAdvisor extends ConsumerStatefulWidget {
  const OptimalSubAdvisor({
    super.key,
    required this.path,
    required this.filterName,
    this.targetPosition,
  });

  /// The TakeExposure node's path (the Apply target).
  final NodePath path;

  /// The filter in effect for this exposure (see [filterNameForExposure]);
  /// null → the daemon's broadband default (flagged as assumed).
  final String? filterName;

  /// The target position in effect for this exposure, J2000 decimal DEGREES
  /// (see [targetPositionForExposure]) — opts into the §3.1 star-detectability
  /// figures. Null → the pure Glover window, exactly as before.
  final ({double raDeg, double decDeg})? targetPosition;

  @override
  ConsumerState<OptimalSubAdvisor> createState() => _OptimalSubAdvisorState();
}

class _OptimalSubAdvisorState extends ConsumerState<OptimalSubAdvisor> {
  OptimalSubResult? _result;
  String? _unavailable; // the daemon's 400 detail (e.g. "set up optics")
  bool _loading = false;
  bool _hidden = false; // 404 — older daemon without the endpoint
  Object? _error;
  // Monotonic fetch sequence: didUpdateWidget re-fetches on a filter change, so
  // two requests can be in flight and resolve out of order — only the latest
  // one may write state, or a slow stale response would overwrite a newer one.
  int _fetchSeq = 0;

  @override
  void initState() {
    super.initState();
    _fetch();
  }

  @override
  void didUpdateWidget(covariant OptimalSubAdvisor old) {
    super.didUpdateWidget(old);
    if (old.filterName != widget.filterName ||
        old.targetPosition != widget.targetPosition) {
      _fetch();
    }
  }

  /// Computes locally (PORT_DECISIONS 2026-07-15 — planning math lives in the
  /// client; no daemon round-trip). Kept async-shaped and sequence-guarded so
  /// didUpdateWidget's re-fetch semantics are unchanged.
  Future<void> _fetch() async {
    final seq = ++_fetchSeq;
    setState(() {
      _loading = true;
      _error = null;
      _unavailable = null;
      _hidden = false;
    });
    try {
      final outcome = resolveOptimalSubLocal(
        optics: ref.read(opticsSettingsProvider),
        electronics: ref.read(cameraElectronicsProvider),
        site: ref.read(siteSettingsProvider),
        filterSet: ref.read(filterSetProvider),
        filterName: widget.filterName,
        // Already DEGREES (targetPositionForExposure converts NINA's RA hours).
        raDeg: widget.targetPosition?.raDeg,
        decDeg: widget.targetPosition?.decDeg,
      );
      if (!mounted || seq != _fetchSeq) return; // superseded
      setState(() {
        switch (outcome) {
          case LocalOptimalSubSuccess(:final result):
            _result = result;
          case LocalOptimalSubUnavailable(:final message):
            _unavailable = message;
            _result = null;
        }
        _loading = false;
      });
    } catch (e) {
      if (!mounted || seq != _fetchSeq) return;
      setState(() {
        _error = e;
        _loading = false;
      });
    }
  }

  void _apply() {
    final r = _result;
    if (r == null) return;
    // Whole seconds, a plain double into the standard NINA field — the entire
    // NINA-fidelity contract of this advisor.
    ref
        .read(sequenceEditorProvider.notifier)
        .setNodeField(
          widget.path,
          'ExposureTime',
          r.recommendedSec.roundToDouble(),
        );
  }

  @override
  Widget build(BuildContext context) {
    // Recompute when the planning inputs change (a fixed filter set / optics
    // edit in Settings must refresh the advice without a manual Retry). listen
    // + _fetch rather than computing in build: _fetch owns the guarded state
    // machine (loading/unavailable/error) the render below consumes.
    ref.listen(opticsSettingsProvider, (_, _) => _fetch());
    ref.listen(cameraElectronicsProvider, (_, _) => _fetch());
    ref.listen(filterSetProvider, (_, _) => _fetch());
    ref.listen(siteSettingsProvider, (_, _) => _fetch());
    if (_hidden) return const SizedBox.shrink();
    final theme = Theme.of(context);
    final dim = theme.textTheme.bodySmall?.copyWith(
      color: AraColors.textSecondary,
    );

    final Widget content;
    if (_loading) {
      content = Row(
        children: [
          const SizedBox(
            width: 12,
            height: 12,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
          const SizedBox(width: 8),
          Text('Computing optimal sub…', style: dim),
        ],
      );
    } else if (_unavailable case final unavailable?) {
      // A 400 is always a fixable-configuration story (optics geometry or the
      // filter set) — pair the daemon's explanation with the jump to the panel
      // that fixes it, instead of leaving the user to hunt through Settings.
      // Retry matters here: the Run tab lives in an IndexedStack, so coming
      // back from Settings does NOT remount this widget — without it the stale
      // "can't compute" would stick around after the user fixed the setup.
      final filterProblem = unavailable.toLowerCase().contains('filter');
      content = Row(
        children: [
          Expanded(child: Text(unavailable, style: dim)),
          TextButton(
            style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
            onPressed: () => openSettingsPanel(
              ref,
              filterProblem ? 'img.filterset' : 'img.optics',
            ),
            child: Text(filterProblem ? 'Open filter set' : 'Open optics'),
          ),
          TextButton(
            style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
            onPressed: _fetch,
            child: const Text('Retry'),
          ),
        ],
      );
    } else if (_error != null) {
      content = Row(
        children: [
          Expanded(
            child: Text("Couldn't compute the optimal sub.", style: dim),
          ),
          TextButton(
            style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
            onPressed: _fetch,
            child: const Text('Retry'),
          ),
        ],
      );
    } else if (_result case final r?) {
      final saturationLimited = !r.viable;
      final starLimited = r.limitingBound == 'starfloor';
      // Lead with the ACTION ("use N s per sub"), not the maths. The window
      // bounds read as jargon ("8 s – 24 min") — demote them to a plain-
      // language explanation line below the headline.
      final headline =
          'Suggested exposure: ${_fmt(r.recommendedSec)} per sub';
      final explain = saturationLimited
          ? 'Your sky is bright enough here that longer subs just saturate — '
                'keep them at ${_fmt(r.recommendedSec)} or shorter.'
          : starLimited
          ? 'Long enough to register plenty of stars per frame; going much '
                'longer only risks losing more to a ruined frame.'
          : 'At this length the sky already swamps the camera\'s read noise — '
                'longer subs won\'t improve the final stack, they just cost '
                'more per lost frame.';
      final assumed = r.assumedDefaults ?? const [];
      content = Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  headline,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: saturationLimited || starLimited
                        ? AraColors.accentBusy
                        : AraColors.textPrimary,
                  ),
                ),
              ),
              TextButton(
                style: TextButton.styleFrom(
                  visualDensity: VisualDensity.compact,
                ),
                onPressed: _apply,
                child: const Text('Apply'),
              ),
            ],
          ),
          Text(explain, style: dim),
          if (widget.filterName != null)
            Text('For filter "${widget.filterName}"', style: dim),
          // §3.1 — the daemon's ready-made star-detectability line (predicted
          // stars/sub, thin-field warning, or the set-up-your-sensor hint).
          if (r.starReason case final starReason?) Text(starReason, style: dim),
          if (assumed.isNotEmpty)
            Text(
              'Using generic defaults for ${assumed.map(_assumedLabel).join(", ")} — '
              'set them in Settings → Imaging for an exact number.',
              style: dim,
            ),
          Text(
            'Criterion popularised by Dr. Robin Glover (SharpCap)',
            style: theme.textTheme.bodySmall?.copyWith(
              color: AraColors.textDisabled,
              fontSize: 10,
            ),
          ),
        ],
      );
    } else {
      content = const SizedBox.shrink();
    }

    return Container(
      margin: const EdgeInsets.only(top: 4),
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: AraColors.border),
      ),
      child: content,
    );
  }

  /// Seconds below 2 minutes ("90 s"), minutes above ("10 min").
  static String _fmt(double seconds) => seconds < 120
      ? '${seconds.round()} s'
      : '${(seconds / 60).toStringAsFixed(seconds >= 600 ? 0 : 1)} min';

  static String _assumedLabel(String field) => switch (field) {
    'read_noise_e' => 'read noise',
    'full_well_e' => 'full well',
    'quantum_efficiency' => 'QE',
    'filter_bandwidth_nm' => 'filter bandwidth',
    _ => field,
  };
}
