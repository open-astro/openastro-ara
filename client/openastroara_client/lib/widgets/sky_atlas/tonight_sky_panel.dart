import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';

/// §36/§25.5 Tonight's Sky — a ranked side list of the best targets for the
/// active profile's site and optical train, by the server's transparent 0–100
/// "worth shooting tonight" score (§36.8). Each row shows the score, the framing
/// fit against your rig, tonight's dark window/transit/hours, a "why" score
/// breakdown on expand, a recentre-the-planetarium action, and an add-to-sequence
/// action. **Tapping the row frames the object**: it highlights the row (via
/// [selectedTonightObjectProvider], so you can see which row drove the atlas)
/// and sends a `frame: true` goto that opens the planetarium's framing overlay
/// with the box landed on the object; the crosshair icon stays centre-only.
/// Both ride the `StellariumServer` loopback: a `goto` command written to
/// [planetariumCommandProvider] is forwarded by `StellariumView` to the page's
/// `/aracmd` handler (the §36 native webview has no Dart→page JS bridge, and
/// `skyTargetProvider` isn't read by the planetarium). The panel docks beside
/// the planetarium in `tonightsSky` mode.
/// §36.8 slice-4 — where "now" falls against an object's dark window, driving
/// the timing line's at-a-glance treatment: **open** (shoot it now — green),
/// **upcoming** (neutral, the default look), **passed** (dimmed — tonight's
/// chance is gone), **none** (no window on the wire). Pure and
/// instant-parameterised so it unit-tests without a clock seam. Boundary
/// instants count as open (isBefore/isAfter are strict) — an exposure started
/// in the window's last minute is still in the window.
enum TonightWindowState { none, upcoming, open, passed }

TonightWindowState windowStateFor(TonightSkyObject o, DateTime nowUtc) {
  final start = o.windowStartUtc;
  final end = o.windowEndUtc;
  if (start == null || end == null) return TonightWindowState.none;
  if (nowUtc.isBefore(start)) return TonightWindowState.upcoming;
  if (nowUtc.isAfter(end)) return TonightWindowState.passed;
  return TonightWindowState.open;
}

class TonightSkyPanel extends ConsumerWidget {
  const TonightSkyPanel({super.key});

  // Wider than the old 300 px: the equipment-aware row carries a score badge, a
  // framing chip and a timing line, and the design explicitly allows a bigger
  // panel ("It's ok if the screen is bigger").
  static const double width = 340;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(tonightSkyProvider);
    // Drives the tune button's active tint; the ranking refetch itself rides
    // tonightSkyProvider's own watch of the overrides.
    final overridesActive = ref.watch(
      tonightSkyOverridesProvider.select((o) => o.isActive),
    );
    // Read once, before the async.when: if tonightSkyProvider settles (empty)
    // while savedServersProvider is still loading, reading it inside the data
    // callback would briefly mis-report "no server". Watching it here makes the
    // empty-state advice deterministic.
    final hasServer = ref
        .watch(savedServersProvider)
        .maybeWhen(data: (list) => list.isNotEmpty, orElse: () => false);
    final theme = Theme.of(context);

    // Material (not a bare Container colour) so the rows' ink splashes have a
    // Material ancestor to paint on — a ColoredBox between them would hide them.
    return Material(
      color: AraColors.bgPanel,
      child: SizedBox(
        width: width,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 16, 8, 8),
              child: Row(
                children: [
                  Expanded(
                    child: Text(
                      "Tonight's Sky",
                      style: theme.textTheme.titleSmall,
                    ),
                  ),
                  IconButton(
                    // §36.8 slice 4b — what-if optics/mosaic overrides. The
                    // active tint (plus tooltip) is the "your list is NOT your
                    // profile's rig right now" reminder.
                    tooltip: overridesActive
                        ? 'Optics / mosaic overrides active'
                        : 'What-if optics / mosaic',
                    icon: Icon(
                      Icons.tune,
                      size: 18,
                      color: overridesActive ? AraColors.accentInfo : null,
                    ),
                    onPressed: () => showDialog<void>(
                      context: context,
                      builder: (_) => const TonightOverridesDialog(),
                    ),
                  ),
                  IconButton(
                    tooltip: 'Refresh',
                    icon: const Icon(Icons.refresh, size: 18),
                    onPressed: () => ref.invalidate(tonightSkyProvider),
                  ),
                ],
              ),
            ),
            Expanded(
              child: async.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _Message(
                  message: 'Could not load Tonight\'s Sky.',
                  onRetry: () => ref.invalidate(tonightSkyProvider),
                ),
                data: (objects) {
                  if (objects.isEmpty) {
                    // Distinguish "no server" (offline ranking found no usable
                    // cached site) from "connected, but nothing's up / no site
                    // set" — the advice differs. Offline WITH a cached site
                    // still ranks locally, so an empty offline list means the
                    // site never made it into this device's cache.
                    return _Message(
                      message: hasServer
                          ? 'Nothing well-placed tonight. Set your site location in '
                                'Settings → Safety → Site, then refresh.'
                          : 'Tonight\'s Sky needs your site location. Pick a '
                                'cached profile from the launchpad, or connect '
                                'to your server once so it can be cached for '
                                'offline planning.',
                    );
                  }
                  return ListView.separated(
                    padding: const EdgeInsets.only(bottom: 12),
                    itemCount: objects.length,
                    separatorBuilder: (_, _) => const Divider(
                      height: 1,
                      thickness: 1,
                      color: AraColors.border,
                    ),
                    // Key by object id so the row's expand/collapse state follows the
                    // object, not the list slot, when the ranking reorders on refresh.
                    itemBuilder: (_, i) => _ObjectRow(
                      key: ValueKey(objects[i].id),
                      object: objects[i],
                    ),
                  );
                },
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ObjectRow extends ConsumerStatefulWidget {
  final TonightSkyObject object;
  const _ObjectRow({super.key, required this.object});

  @override
  ConsumerState<_ObjectRow> createState() => _ObjectRowState();
}

class _ObjectRowState extends ConsumerState<_ObjectRow> {
  bool _busy = false;
  bool _showReasons = false;

  TonightSkyObject get _object => widget.object;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final mag = _object.magnitude;
    final magText = mag == null ? 'mag —' : 'mag ${mag.toStringAsFixed(1)}';
    final subtitle =
        '${_typeLabel(_object.type)} · $magText · '
        'max ${_object.maxAltitudeDeg.toStringAsFixed(0)}°';
    final timing = _timingLine(_object);
    // Best-window highlight (slice 4): advise-don't-dictate — the row is never
    // hidden or re-ranked, the timing line just reads green "now · …" while the
    // window is open and dims once it has fully passed.
    final windowState = windowStateFor(_object, DateTime.now().toUtc());
    final framingLabel = _framingLabel(_object.framing);
    final reasons = _object.scoreReasons;
    final hasReasons = reasons != null && reasons.isNotEmpty;
    // Watch (not read) so the autoDispose sequence API stays alive while the
    // panel is shown — a bare read would let it dispose (closing its Dio) before
    // an in-flight create() resolves.
    final canAdd = ref.watch(sequenceApiProvider) != null;
    // select() so a selection change rebuilds only the two rows whose
    // highlight actually flipped, not every visible row.
    final selected = ref.watch(
      selectedTonightObjectProvider.select((id) => id == _object.id),
    );

    return InkWell(
      onTap: _frameOnAtlas,
      child: Container(
        // The highlight marks WHICH row drove the atlas (the framing box may sit
        // in a starfield with no obvious landmark) — same selected-row treatment
        // as the command palette / settings nav.
        color: selected ? AraColors.selectionBg.withValues(alpha: 0.25) : null,
        padding: const EdgeInsets.fromLTRB(12, 10, 8, 6),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // No badge for a scoreless object (a pre-§36.8 server) — better a
                // missing badge than a misleading "0".
                if (_object.score != null) ...[
                  _ScoreBadge(score: _object.score!),
                  const SizedBox(width: 10),
                ],
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(_object.name, style: theme.textTheme.bodyMedium),
                      const SizedBox(height: 2),
                      Text(
                        subtitle,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: 6),
                Text(
                  '${_object.altitudeDeg.toStringAsFixed(0)}°',
                  style: theme.textTheme.bodyMedium,
                ),
              ],
            ),
            if (framingLabel != null ||
                timing != null ||
                _object.filterAdvice != null ||
                _object.moonUpFraction != null) ...[
              const SizedBox(height: 6),
              Row(
                children: [
                  if (framingLabel != null)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: _FramingChip(framing: _object.framing),
                    ),
                  if (_object.filterAdvice != null)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: _FilterAdviceChip(advice: _object.filterAdvice!),
                    ),
                  if (_object.moonUpFraction != null)
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: _MoonChip(object: _object),
                    ),
                  if (timing != null)
                    Expanded(
                      child: Text(
                        windowState == TonightWindowState.open
                            ? 'now · $timing'
                            : timing,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: switch (windowState) {
                            TonightWindowState.open =>
                              AraColors.accentConnected,
                            TonightWindowState.passed => AraColors.textDisabled,
                            _ => AraColors.textSecondary,
                          },
                          fontWeight: windowState == TonightWindowState.open
                              ? FontWeight.w600
                              : null,
                        ),
                      ),
                    ),
                ],
              ),
            ],
            Row(
              children: [
                _busy
                    ? const Padding(
                        padding: EdgeInsets.all(10),
                        child: SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        ),
                      )
                    : IconButton(
                        iconSize: 18,
                        visualDensity: VisualDensity.compact,
                        tooltip: 'Add to a new sequence',
                        icon: const Icon(Icons.playlist_add),
                        onPressed: canAdd ? _addToSequence : null,
                      ),
                IconButton(
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  tooltip: 'Centre the planetarium on this object',
                  icon: const Icon(Icons.my_location),
                  onPressed: _recentre,
                ),
                const Spacer(),
                if (hasReasons)
                  TextButton(
                    style: TextButton.styleFrom(
                      visualDensity: VisualDensity.compact,
                      foregroundColor: AraColors.textSecondary,
                    ),
                    onPressed: () =>
                        setState(() => _showReasons = !_showReasons),
                    child: Text(_showReasons ? 'Hide' : 'Why?'),
                  ),
              ],
            ),
            if (hasReasons && _showReasons)
              Padding(
                padding: const EdgeInsets.fromLTRB(4, 0, 4, 6),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    for (final r in reasons)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 2),
                        child: Text(
                          '• $r',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: AraColors.textSecondary,
                          ),
                        ),
                      ),
                    // NEXTGEN §1 — the filter-advice explanation + the per-filter
                    // Optimal Sub, tucked into the Why? breakdown so the row stays
                    // one-glance. The attribution is a design-doc requirement of
                    // the permission to use the criterion.
                    if (_object.adviceReason != null)
                      Padding(
                        padding: const EdgeInsets.only(top: 4, bottom: 2),
                        child: Text(
                          _object.adviceReason!,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: AraColors.textSecondary,
                          ),
                        ),
                      ),
                    if (_object.optimalSubS != null) ...[
                      Padding(
                        padding: const EdgeInsets.only(bottom: 2),
                        child: Text(
                          'Optimal sub ≈ ${_formatSub(_object.optimalSubS!)}',
                          style: theme.textTheme.bodySmall,
                        ),
                      ),
                      Text(
                        'Sub-exposure criterion popularised by Dr. Robin Glover (SharpCap)',
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: AraColors.textDisabled,
                          fontSize: 10,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }

  /// Row tap: highlight this row and FRAME the object — a `goto` with
  /// `frame: true` makes the page open the framing overlay and land the box on
  /// the object (vs the crosshair icon's centre-only goto), so one tap goes
  /// from "that looks good tonight" to a positioned frame ready for Create Run.
  void _frameOnAtlas() {
    ref.read(selectedTonightObjectProvider.notifier).select(_object.id);
    ref.read(planetariumCommandProvider.notifier).send({
      'type': 'goto',
      'ra': _object.raDeg,
      'dec': _object.decDeg,
      'name': _object.name,
      'frame': true,
    });
  }

  /// Centre the planetarium on this object. Writes a `goto` command (J2000
  /// RA/Dec in degrees) to [planetariumCommandProvider]; `StellariumView`
  /// forwards it over the loopback server to the page's `/aracmd` handler, which
  /// points the view at the coordinates directly (no name lookup). The provider
  /// always notifies, so recentring the same object twice still re-centres.
  /// The name rides along so the page's frame-target label follows the view:
  /// without it, a recentre after a framed goto leaves the framing overlay's
  /// "Create Run" carrying the PREVIOUS object's name with this one's
  /// coordinates (the page keeps the old name when a goto brings none).
  void _recentre() => ref.read(planetariumCommandProvider.notifier).send({
    'type': 'goto',
    'ra': _object.raDeg,
    'dec': _object.decDeg,
    'name': _object.name,
  });

  /// Create a full imaging run for this object — cool/unpark/track/slew/
  /// autofocus + a Take-Exposure loop sized to tonight's remaining dark window
  /// from the user's Imaging Defaults ([createImagingRun]) — then jump to the
  /// Run tab with it selected. Mirrors the `createSequenceFromTemplate`
  /// mounted/ref ordering.
  Future<void> _addToSequence() async {
    if (_busy) return;
    final messenger = ScaffoldMessenger.of(context);
    final name = _object.name;
    setState(() => _busy = true);
    ImagingRunResult? result;
    try {
      result = await createImagingRun(
        ref,
        raDeg: _object.raDeg,
        decDeg: _object.decDeg,
        targetName: name,
        remainingDarkHours: _object.remainingHours,
      );
    } catch (e, st) {
      debugPrint('[planning] create-run failed: $e\n$st');
      if (mounted) {
        showImagingRunFeedback(messenger, targetName: name, failed: true);
      }
      return;
    } finally {
      if (mounted) setState(() => _busy = false);
    }
    // Gate on mounted before touching ref: the row can scroll out of the
    // ListView (disposing it) during the create await, leaving ref defunct.
    if (!mounted) return;
    showImagingRunFeedback(messenger, targetName: name, result: result);
  }

  // Friendly names for both the starter catalog's plain types and the OpenNGC
  // codes the real catalog (and now the starter's emission targets) carry.
  static String _typeLabel(String t) => switch (t) {
    'galaxy' || 'G' => 'Galaxy',
    'GPair' => 'Galaxy pair',
    'GTrpl' => 'Galaxy triplet',
    'GGroup' => 'Galaxy group',
    'nebula' || 'Neb' => 'Nebula',
    'HII' || 'EmN' => 'Emission nebula',
    'RfN' => 'Reflection nebula',
    'PN' => 'Planetary nebula',
    'SNR' => 'Supernova remnant',
    'cluster' => 'Cluster',
    'OCl' => 'Open cluster',
    'GCl' => 'Globular cluster',
    'Cl+N' => 'Cluster + nebula',
    '' => 'Object',
    _ => t[0].toUpperCase() + t.substring(1),
  };

  /// Compact local-time timing summary, e.g. "20:14–04:32 · 6.0 h dark ·
  /// 3.2 h left · transit 01:10". Null when the server sent no timing at all,
  /// so the row drops the line rather than showing an empty one.
  static String? _timingLine(TonightSkyObject o) {
    final parts = <String>[];
    final start = o.windowStartUtc;
    final end = o.windowEndUtc;
    if (start != null && end != null) {
      parts.add('${_hhmm(start)}–${_hhmm(end)}');
    }
    if (o.integrationHours > 0) {
      parts.add('${o.integrationHours.toStringAsFixed(1)} h dark');
    }
    if (o.remainingHours > 0) {
      parts.add('${o.remainingHours.toStringAsFixed(1)} h left');
    }
    final transit = o.transitUtc;
    if (transit != null) parts.add('transit ${_hhmm(transit)}');
    return parts.isEmpty ? null : parts.join(' · ');
  }

  /// HH:mm in the user's local timezone (the wire instants are UTC).
  static String _hhmm(DateTime utc) {
    final t = utc.toLocal();
    return '${t.hour.toString().padLeft(2, '0')}:'
        '${t.minute.toString().padLeft(2, '0')}';
  }

  /// Short chip label for the framing fit; null for `unknown` (no chip — we
  /// don't clutter the row with a non-signal). Shares one source of truth with
  /// the chip's colour via [_FramingChip.styleFor].
  static String? _framingLabel(TonightFraming f) => _FramingChip.styleFor(f).$1;

  /// Format the Optimal-Sub seconds figure the way imagers say it: whole
  /// seconds below 2 minutes ("90 s"), minutes above ("10 min") — narrowband
  /// floors under a dark sky routinely run to many minutes.
  static String _formatSub(double seconds) => seconds < 120
      ? '${seconds.round()} s'
      : '${(seconds / 60).toStringAsFixed(seconds >= 600 ? 0 : 1)} min';
}

/// 0–100 worth-score badge, colour-graded so the eye can triage at a glance:
/// green (strong), blue (decent), muted (low — still shown, just ranked down,
/// per the advise-don't-dictate intent).
class _ScoreBadge extends StatelessWidget {
  final double score;
  const _ScoreBadge({required this.score});

  @override
  Widget build(BuildContext context) {
    final s = score.clamp(0, 100).round();
    // Colour off the SAME rounded value the badge shows, so e.g. 69.6 doesn't
    // render "70" yet paint with the sub-70 colour.
    final color = s >= 70
        ? AraColors.accentConnected
        : s >= 40
        ? AraColors.accentInfo
        : AraColors.textSecondary;
    return Container(
      width: 38,
      height: 38,
      alignment: Alignment.center,
      decoration: BoxDecoration(
        // A tinted fill keyed to the score colour, with a matching border —
        // legible on the dark panel without shouting.
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: color.withValues(alpha: 0.6)),
      ),
      child: Text(
        '$s',
        style: Theme.of(context).textTheme.titleSmall?.copyWith(
          color: color,
          fontWeight: FontWeight.w600,
        ),
      ),
    );
  }
}

/// Framing-fit chip. `good` reads as a positive accent; `tooSmall`/`tooBig` are
/// a warn/muted amber — they're advice, not a hard "no".
class _FramingChip extends StatelessWidget {
  final TonightFraming framing;
  const _FramingChip({required this.framing});

  /// The single source of truth for both label and colour. `unknown` yields a
  /// null label — the parent gates on that to render no chip — so the label is
  /// defined exactly once and the chip + the row's gate can't drift apart.
  static (String?, Color) styleFor(TonightFraming f) => switch (f) {
    TonightFraming.good => ('Fills frame', AraColors.accentConnected),
    TonightFraming.goodFit => ('Good fit', AraColors.accentInfo),
    TonightFraming.tooSmall => ('Small', AraColors.accentBusy),
    TonightFraming.tooBig => ('Too big', AraColors.accentBusy),
    TonightFraming.unknown => (null, AraColors.textSecondary),
  };

  @override
  Widget build(BuildContext context) {
    final (label, color) = styleFor(framing);
    // The parent only builds a chip when styleFor's label is non-null (not `unknown`).
    // Self-guard anyway — release-safe (an assert is stripped) — so a future call site
    // that bypasses the gate renders nothing rather than a styled-but-blank chip.
    if (label == null) return const SizedBox.shrink();
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: color.withValues(alpha: 0.5)),
      ),
      child: Text(
        label, // non-null: the early return above bails on a null label
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: color,
          fontWeight: FontWeight.w500,
        ),
      ),
    );
  }
}

/// NEXTGEN §1 — the recommended-filter-approach chip ("Narrowband" /
/// "OSC + dual-band" / "Broadband"), rendered beside the framing chip.
/// Narrowband/duoband get the info accent (they're the time-savers worth
/// noticing); broadband stays neutral. The one-line why lives in the
/// Why? breakdown, not a tooltip, so it works on touch too.
class _FilterAdviceChip extends StatelessWidget {
  final TonightFilterAdvice advice;
  const _FilterAdviceChip({required this.advice});

  @override
  Widget build(BuildContext context) {
    final color = advice == TonightFilterAdvice.broadband
        ? AraColors.textSecondary
        : AraColors.accentInfo;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: color.withValues(alpha: 0.5)),
      ),
      child: Text(
        advice.label,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: color,
          fontWeight: FontWeight.w500,
        ),
      ),
    );
  }
}

/// §36.8 slice-4 — at-a-glance moon context for the object's dark window.
/// **Moonless** (the best news on the panel, green) when the moon stays below
/// the true horizon for the whole window; otherwise the separation and the
/// disc illumination, tinted amber only when the moon is both close (<30°)
/// and bright (>40% lit). Advisory only — an objectionable moon never hides
/// or down-ranks the object (advise-don't-dictate).
class _MoonChip extends StatelessWidget {
  final TonightSkyObject object;
  const _MoonChip({required this.object});

  @override
  Widget build(BuildContext context) {
    final moonless = (object.moonUpFraction ?? 0) <= 0;
    final sep = object.moonSeparationDeg;
    final illum = object.moonIlluminationPct;
    final String label;
    final Color color;
    if (moonless) {
      label = 'Moonless';
      color = AraColors.accentConnected;
    } else {
      // Either measurement can be absent independently (defensive parse) —
      // show what we have rather than dropping the chip.
      label =
          '☾'
          '${sep == null ? '' : ' ${sep.toStringAsFixed(0)}°'}'
          '${illum == null ? '' : ' · ${illum.toStringAsFixed(0)}%'}';
      final harsh = (sep ?? 180) < 30 && (illum ?? 0) > 40;
      color = harsh ? AraColors.accentBusy : AraColors.textSecondary;
    }
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: color.withValues(alpha: 0.5)),
      ),
      child: Text(
        label,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: color,
          fontWeight: FontWeight.w500,
        ),
      ),
    );
  }
}

/// §36.8 slice 4b — the what-if optics + mosaic form. Every optics field is
/// optional: blank means "use the active profile's value" (the server merges
/// per-field, so overriding just the reducer is a valid what-if). Mosaic tiles
/// enlarge the framing FOV per axis — "would the Veil fit as a 2×2 at this
/// focal length?". Apply re-ranks Tonight's Sky against the overridden train;
/// Reset returns to the profile's rig. Validation mirrors the server's request
/// guards ([TonightOverrides.maxReducer] / [TonightOverrides.maxMosaicTiles])
/// so an accepted form can't come back as a 400.
class TonightOverridesDialog extends ConsumerStatefulWidget {
  const TonightOverridesDialog({super.key});

  @override
  ConsumerState<TonightOverridesDialog> createState() =>
      _TonightOverridesDialogState();
}

class _TonightOverridesDialogState
    extends ConsumerState<TonightOverridesDialog> {
  final _form = GlobalKey<FormState>();
  late final TextEditingController _focal;
  late final TextEditingController _reducer;
  late final TextEditingController _sensorW;
  late final TextEditingController _sensorH;
  late final TextEditingController _pixel;
  late final TextEditingController _mosaicX;
  late final TextEditingController _mosaicY;

  @override
  void initState() {
    super.initState();
    // Pre-fill from the current overrides so re-opening the dialog edits what
    // is applied instead of presenting a misleading blank form.
    final o = ref.read(tonightSkyOverridesProvider);
    String num(double? v) => v == null ? '' : _trimmed(v);
    _focal = TextEditingController(text: num(o.focalLengthMm));
    _reducer = TextEditingController(text: num(o.reducer));
    _sensorW = TextEditingController(text: o.sensorW?.toString() ?? '');
    _sensorH = TextEditingController(text: o.sensorH?.toString() ?? '');
    _pixel = TextEditingController(text: num(o.pixelUm));
    _mosaicX = TextEditingController(text: o.mosaicX.toString());
    _mosaicY = TextEditingController(text: o.mosaicY.toString());
  }

  /// "0.7" not "0.700000", "530" not "530.0" — the text a user would type.
  static String _trimmed(double v) =>
      v == v.roundToDouble() ? v.round().toString() : v.toString();

  @override
  void dispose() {
    _focal.dispose();
    _reducer.dispose();
    _sensorW.dispose();
    _sensorH.dispose();
    _pixel.dispose();
    _mosaicX.dispose();
    _mosaicY.dispose();
    super.dispose();
  }

  /// Blank is valid (= profile value); anything typed must parse per [check].
  static String? _optValidator(String? raw, String? Function(double) check) {
    final text = raw?.trim() ?? '';
    if (text.isEmpty) return null;
    final v = double.tryParse(text);
    if (v == null || !v.isFinite) return 'Not a number';
    return check(v);
  }

  static String? _positive(double v) => v > 0 ? null : 'Must be > 0';

  static String? _positiveInt(double v) =>
      v > 0 && v == v.roundToDouble() ? null : 'Whole pixels > 0';

  static String? _reducerRange(double v) =>
      v > 0 && v <= TonightOverrides.maxReducer
          ? null
          : 'In (0, ${TonightOverrides.maxReducer.round()}]';

  /// Mosaic tiles are required (they carry the 1 = no-mosaic default).
  static String? _mosaicValidator(String? raw) {
    final v = int.tryParse(raw?.trim() ?? '');
    return v != null && v >= 1 && v <= TonightOverrides.maxMosaicTiles
        ? null
        : '1–${TonightOverrides.maxMosaicTiles}';
  }

  static double? _optDouble(TextEditingController c) {
    final text = c.text.trim();
    return text.isEmpty ? null : double.parse(text);
  }

  static int? _optInt(TextEditingController c) {
    final text = c.text.trim();
    return text.isEmpty ? null : double.parse(text).round();
  }

  void _apply() {
    if (!(_form.currentState?.validate() ?? false)) return;
    ref.read(tonightSkyOverridesProvider.notifier).set(TonightOverrides(
          focalLengthMm: _optDouble(_focal),
          reducer: _optDouble(_reducer),
          sensorW: _optInt(_sensorW),
          sensorH: _optInt(_sensorH),
          pixelUm: _optDouble(_pixel),
          mosaicX: int.parse(_mosaicX.text.trim()),
          mosaicY: int.parse(_mosaicY.text.trim()),
        ));
    Navigator.of(context).pop();
  }

  void _reset() {
    ref.read(tonightSkyOverridesProvider.notifier).clear();
    Navigator.of(context).pop();
  }

  Widget _field(
    TextEditingController controller,
    String label, {
    String? suffix,
    required String? Function(String?) validator,
  }) {
    return TextFormField(
      controller: controller,
      decoration: InputDecoration(
        labelText: label,
        suffixText: suffix,
        hintText: 'profile',
        isDense: true,
      ),
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      autovalidateMode: AutovalidateMode.onUserInteraction,
      validator: validator,
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('What-if optics'),
      content: SizedBox(
        width: 320,
        child: Form(
          key: _form,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Re-rank tonight\'s targets against a different rig. Blank '
                  'fields keep your profile\'s values.',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
                const SizedBox(height: 8),
                _field(_focal, 'Focal length', suffix: 'mm',
                    validator: (v) => _optValidator(v, _positive)),
                _field(_reducer, 'Reducer / barlow', suffix: '×',
                    validator: (v) => _optValidator(v, _reducerRange)),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: _field(_sensorW, 'Sensor width', suffix: 'px',
                          validator: (v) => _optValidator(v, _positiveInt)),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: _field(_sensorH, 'Sensor height', suffix: 'px',
                          validator: (v) => _optValidator(v, _positiveInt)),
                    ),
                  ],
                ),
                _field(_pixel, 'Pixel size', suffix: 'µm',
                    validator: (v) => _optValidator(v, _positive)),
                const SizedBox(height: 12),
                Text('Mosaic panels', style: theme.textTheme.labelMedium),
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Expanded(
                      child: TextFormField(
                        controller: _mosaicX,
                        decoration: const InputDecoration(
                          labelText: 'Wide',
                          isDense: true,
                        ),
                        keyboardType: TextInputType.number,
                        autovalidateMode: AutovalidateMode.onUserInteraction,
                        validator: _mosaicValidator,
                      ),
                    ),
                    const Padding(
                      padding: EdgeInsets.symmetric(horizontal: 8),
                      child: Text('×'),
                    ),
                    Expanded(
                      child: TextFormField(
                        controller: _mosaicY,
                        decoration: const InputDecoration(
                          labelText: 'High',
                          isDense: true,
                        ),
                        keyboardType: TextInputType.number,
                        autovalidateMode: AutovalidateMode.onUserInteraction,
                        validator: _mosaicValidator,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
      actions: [
        TextButton(onPressed: _reset, child: const Text('Reset')),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(onPressed: _apply, child: const Text('Apply')),
      ],
    );
  }
}

class _Message extends StatelessWidget {
  final String message;
  final VoidCallback? onRetry;
  const _Message({required this.message, this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall,
            ),
            if (onRetry != null) ...[
              const SizedBox(height: 12),
              TextButton(onPressed: onRetry, child: const Text('Retry')),
            ],
          ],
        ),
      ),
    );
  }
}
