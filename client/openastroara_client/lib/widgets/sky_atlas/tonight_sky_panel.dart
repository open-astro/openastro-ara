import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/slew_target_body.dart';
import '../../services/tonight_sky_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';

/// §36/§25.5 Tonight's Sky — a ranked side list of the best targets for the
/// active profile's site and optical train, by the server's transparent 0–100
/// "worth shooting tonight" score (§36.8). Each row shows the score, the framing
/// fit against your rig, tonight's dark window/transit/hours, a "why" score
/// breakdown on expand, a recentre-the-planetarium action, and an add-to-sequence
/// action. Recentre drives the planetarium over the `StellariumServer` loopback:
/// it writes a `goto` command to [planetariumCommandProvider], which
/// `StellariumView` forwards to the page's `/aracmd` handler (the §36 native
/// webview has no Dart→page JS bridge, and `skyTargetProvider` isn't read by the
/// planetarium). The panel docks beside the planetarium in `tonightsSky` mode.
class TonightSkyPanel extends ConsumerWidget {
  const TonightSkyPanel({super.key});

  // Wider than the old 300 px: the equipment-aware row carries a score badge, a
  // framing chip and a timing line, and the design explicitly allows a bigger
  // panel ("It's ok if the screen is bigger").
  static const double width = 340;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(tonightSkyProvider);
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
                    // Distinguish "no server" (the provider returns [] immediately) from
                    // "connected, but nothing's up / no site set" — the advice differs.
                    return _Message(
                      message: hasServer
                          ? 'Nothing well-placed tonight. Set your site location in '
                                'Settings → Safety → Site, then refresh.'
                          : 'Connect to a server to see Tonight\'s Sky.',
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
                    itemBuilder: (_, i) =>
                        _ObjectRow(key: ValueKey(objects[i].id), object: objects[i]),
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
    final subtitle = '${_typeLabel(_object.type)} · $magText · '
        'max ${_object.maxAltitudeDeg.toStringAsFixed(0)}°';
    final timing = _timingLine(_object);
    final framingLabel = _framingLabel(_object.framing);
    final reasons = _object.scoreReasons;
    final hasReasons = reasons != null && reasons.isNotEmpty;
    // Watch (not read) so the autoDispose sequence API stays alive while the
    // panel is shown — a bare read would let it dispose (closing its Dio) before
    // an in-flight create() resolves.
    final canAdd = ref.watch(sequenceApiProvider) != null;

    return Padding(
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
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: AraColors.textSecondary),
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
          if (framingLabel != null || timing != null || _object.filterAdvice != null) ...[
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
                if (timing != null)
                  Expanded(
                    child: Text(
                      timing,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
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
    );
  }

  /// Centre the planetarium on this object. Writes a `goto` command (J2000
  /// RA/Dec in degrees) to [planetariumCommandProvider]; `StellariumView`
  /// forwards it over the loopback server to the page's `/aracmd` handler, which
  /// points the view at the coordinates directly (no name lookup). The provider
  /// always notifies, so recentring the same object twice still re-centres.
  void _recentre() => ref.read(planetariumCommandProvider.notifier).send({
        'type': 'goto',
        'ra': _object.raDeg,
        'dec': _object.decDeg,
      });

  /// Create a new sequence named after this object containing a single slew to
  /// its coordinates, then surface the outcome via a SnackBar. Mirrors the
  /// `createSequenceFromTemplate` mounted/ref ordering.
  Future<void> _addToSequence() async {
    if (_busy) return;
    final api = ref.read(sequenceApiProvider);
    if (api == null) return;
    final messenger = ScaffoldMessenger.of(context);
    final name = _object.name;
    setState(() => _busy = true);
    try {
      final body = buildSlewTargetBody(
        raDeg: _object.raDeg,
        decDeg: _object.decDeg,
        targetName: name,
      );
      await api.create(name, body);
    } catch (e, st) {
      debugPrint('[planning] add-to-sequence failed: $e\n$st');
      if (mounted) {
        messenger.showSnackBar(
          const SnackBar(
            content: Text(
              "Couldn't add to a sequence. Check the connection and try again.",
            ),
            backgroundColor: AraColors.accentError,
          ),
        );
      }
      return;
    } finally {
      if (mounted) setState(() => _busy = false);
    }
    // Gate on mounted before touching ref: the row can scroll out of the
    // ListView (disposing it) during the create await, leaving ref defunct.
    if (!mounted) return;
    // Refresh the library so the new sequence shows up in the Sequencer tab.
    ref.invalidate(sequenceListProvider);
    messenger.showSnackBar(
      SnackBar(content: Text('Added "$name" to a new sequence.')),
    );
  }

  static String _typeLabel(String t) => switch (t) {
        'galaxy' => 'Galaxy',
        'nebula' => 'Nebula',
        'cluster' => 'Cluster',
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
        style: Theme.of(context)
            .textTheme
            .titleSmall
            ?.copyWith(color: color, fontWeight: FontWeight.w600),
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
        label,   // non-null: the early return above bails on a null label
        style: Theme.of(context)
            .textTheme
            .labelSmall
            ?.copyWith(color: color, fontWeight: FontWeight.w500),
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
        style: Theme.of(context)
            .textTheme
            .labelSmall
            ?.copyWith(color: color, fontWeight: FontWeight.w500),
      ),
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
