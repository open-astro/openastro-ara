import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/nina_dom.dart';
import '../../models/server.dart';
import '../../services/optimal_sub_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';

/// The TakeExposure `$type` — the node the advisor attaches to — and the
/// SwitchFilter `$type` it scans sibling instructions for. Mirrors the
/// instruction catalog's entries (no named constants exist there).
const String takeExposureType =
    'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer';
const String _switchFilterType =
    'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer';

/// The filter name a TakeExposure at [path] runs under, or null: the nearest
/// preceding SwitchFilter among its container siblings (NINA executes a
/// SequentialContainer / SmartExposure top-to-bottom, so the last filter
/// switch before the exposure is the one in effect). Pure — exposed for tests.
String? filterNameForExposure(Map<String, dynamic> body, NodePath path) {
  if (path.isEmpty) return null;
  final parent = nodeAt(body, path.sublist(0, path.length - 1));
  if (parent == null) return null;
  final siblings = childrenOf(parent);
  final exposureIndex = path.last;
  String? name;
  for (var i = 0; i < exposureIndex && i < siblings.length; i++) {
    final sibling = siblings[i];
    if (sibling[r'$type'] == _switchFilterType) {
      final filter = sibling['Filter'];
      final n = filter is Map<String, dynamic> ? filter['Name'] : null;
      if (n is String && n.trim().isNotEmpty) name = n.trim();
    }
  }
  return name;
}

/// NEXTGEN §5 — the per-filter "Optimal Sub" advisor under a TakeExposure's
/// fields: the Glover read-noise floor (criterion popularised by Dr. Robin
/// Glover, SharpCap — attributed per the recorded permission) intersected
/// with the saturation ceiling, fetched from `GET /planning/optimal-sub` for
/// the exposure's filter. Apply writes a **plain number** into the standard
/// `ExposureTime` field via [SequenceEditorController.setNodeField] — no new
/// instruction types, no `$type` churn — so the sequence stays NINA-valid.
///
/// Renders nothing against a pre-slice-2 daemon (404) and a quiet one-line
/// message when the daemon can't compute (400 — e.g. aperture not set up).
class OptimalSubAdvisor extends ConsumerStatefulWidget {
  const OptimalSubAdvisor({super.key, required this.path, required this.filterName});

  /// The TakeExposure node's path (the Apply target).
  final NodePath path;

  /// The filter in effect for this exposure (see [filterNameForExposure]);
  /// null → the daemon's broadband default (flagged as assumed).
  final String? filterName;

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
    if (old.filterName != widget.filterName) _fetch();
  }

  Future<void> _fetch() async {
    final seq = ++_fetchSeq;
    final api = ref.read(optimalSubApiProvider);
    if (api == null) {
      setState(() => _hidden = true); // no server — nothing to advise from
      return;
    }
    setState(() {
      _loading = true;
      _error = null;
      _unavailable = null;
    });
    try {
      final result = await api.get(filter: widget.filterName);
      if (!mounted || seq != _fetchSeq) return; // superseded — drop the stale response
      setState(() {
        _result = result;
        _hidden = result == null; // 404 → feature not on this daemon
        _loading = false;
      });
    } on OptimalSubUnavailable catch (e) {
      if (!mounted || seq != _fetchSeq) return;
      setState(() {
        _unavailable = e.message;
        _result = null;
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
        .setNodeField(widget.path, 'ExposureTime', r.recommendedSec.roundToDouble());
  }

  @override
  Widget build(BuildContext context) {
    // WATCH (don't just read) the autoDispose API provider: this registered
    // listener is what keeps it — and its Dio — alive for the widget's
    // lifetime. With only the ref.read in _fetch, Riverpod would dispose the
    // provider (closing the Dio, aborting the in-flight request) right after
    // the synchronous read returned, and every fetch would die mid-flight.
    ref.watch(optimalSubApiProvider);
    if (_hidden) return const SizedBox.shrink();
    final theme = Theme.of(context);
    final dim = theme.textTheme.bodySmall?.copyWith(color: AraColors.textSecondary);

    final Widget content;
    if (_loading) {
      content = Row(children: [
        const SizedBox(
            width: 12, height: 12, child: CircularProgressIndicator(strokeWidth: 2)),
        const SizedBox(width: 8),
        Text('Computing optimal sub…', style: dim),
      ]);
    } else if (_unavailable case final unavailable?) {
      // A 400 is always a fixable-configuration story (optics geometry or the
      // filter set) — pair the daemon's explanation with the jump to the panel
      // that fixes it, instead of leaving the user to hunt through Settings.
      // Retry matters here: the Run tab lives in an IndexedStack, so coming
      // back from Settings does NOT remount this widget — without it the stale
      // "can't compute" would stick around after the user fixed the setup.
      final filterProblem = unavailable.toLowerCase().contains('filter');
      content = Row(children: [
        Expanded(child: Text(unavailable, style: dim)),
        TextButton(
          style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
          onPressed: () => openSettingsPanel(
              ref, filterProblem ? 'img.filterset' : 'img.optics'),
          child: Text(filterProblem ? 'Open filter set' : 'Open optics'),
        ),
        TextButton(
          style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
          onPressed: _fetch,
          child: const Text('Retry'),
        ),
      ]);
    } else if (_error != null) {
      content = Row(children: [
        Expanded(child: Text("Couldn't compute the optimal sub.", style: dim)),
        TextButton(
          style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
          onPressed: _fetch,
          child: const Text('Retry'),
        ),
      ]);
    } else if (_result case final r?) {
      final saturationLimited = !r.viable;
      final headline = saturationLimited
          ? 'Saturation-limited: ${_fmt(r.recommendedSec)} max '
              '(the sky fills the well before read noise is swamped)'
          : 'Optimal Sub: ${_fmt(r.recommendedSec)}'
              '  (usable window ${_fmt(r.floorSec)} – ${_fmt(r.ceilingSec)})';
      final assumed = r.assumedDefaults ?? const [];
      content = Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(children: [
            Expanded(
              child: Text(
                headline,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: saturationLimited ? AraColors.accentBusy : AraColors.textPrimary,
                ),
              ),
            ),
            TextButton(
              style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
              onPressed: _apply,
              child: const Text('Apply'),
            ),
          ]),
          if (widget.filterName != null)
            Text('For filter "${widget.filterName}"', style: dim),
          if (assumed.isNotEmpty)
            Text(
              'Using generic defaults for ${assumed.map(_assumedLabel).join(", ")} — '
              'set them in Settings → Imaging for an exact number.',
              style: dim,
            ),
          Text(
            'Criterion popularised by Dr. Robin Glover (SharpCap)',
            style: theme.textTheme.bodySmall
                ?.copyWith(color: AraColors.textDisabled, fontSize: 10),
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

/// The active server's Optimal-Sub API, or null with no server. AutoDispose +
/// close-on-dispose, mirroring `sequenceApiProvider`. NB the advisor widget
/// WATCHES this in build — autoDispose would otherwise tear the Dio down
/// (aborting the in-flight request) as soon as _fetch's read returned.
final optimalSubApiProvider = Provider.autoDispose<OptimalSubApi?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(optimalSubApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Injectable factory so widget tests can substitute a fake API without
/// touching the transport. Production leaves the default.
final optimalSubApiFactoryProvider =
    Provider<OptimalSubApi Function(AraServer)>((ref) => OptimalSubApi.new);
