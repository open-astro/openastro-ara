import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/equipment/filter_wheel_state.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/filter_set_state.dart';
import '../../../state/settings/filter_wheel_labels_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';

/// NEXTGEN §1/§4 Filter set panel — the user's declared planning filters
/// (name + kind + effective bandwidth). Feeds filter-aware planning advice
/// and the per-filter Optimal Sub; matched to sequences by name, so names
/// should mirror the filter-wheel slot labels (the seed button copies them).
/// Deliberately separate from the equipment filter-wheel config, which must
/// round-trip NINA imports untouched. Hydrates from `filter-set` on mount;
/// persists on Save.
class FilterSetPanel extends ConsumerStatefulWidget {
  const FilterSetPanel({super.key});

  @override
  ConsumerState<FilterSetPanel> createState() => _FilterSetPanelState();
}

class _FilterSetPanelState extends ConsumerState<FilterSetPanel> {
  bool _saving = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return; // no active server — keep local defaults
    try {
      await ref.read(filterSetProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) {
        setState(() => _lastError = 'Could not load saved values: $e');
      }
    }
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(filterSetProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
          const SnackBar(content: Text('Filter set saved to daemon.')));
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  void _seedFromWheel() {
    // Prefer the connected wheel's live slot names; fall back to the §37.4
    // filter-wheel label settings (which carry sensible defaults) so the
    // button works offline too.
    final wheel = ref.read(filterWheelProvider).valueOrNull;
    final live = wheel?.slots.map((s) => s.name).toList() ?? const <String>[];
    final labels = live.any((s) => s.trim().isNotEmpty)
        ? live
        : () {
            final stored = ref.read(filterWheelLabelsProvider);
            return [
              for (var slot = 1; slot <= stored.slotCount; slot++)
                stored.labelAt(slot),
            ];
          }();
    final before = ref.read(filterSetProvider).filters.length;
    ref.read(filterSetProvider.notifier).seedFromWheelLabels(labels);
    final added = ref.read(filterSetProvider).filters.length - before;
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
        content: Text(added > 0
            ? 'Added $added filter${added == 1 ? "" : "s"} from the wheel labels — check the kinds, then Save.'
            : 'No new labels to add — set filter-wheel labels or add filters manually.')));
  }

  ProfileApi? _api() {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) return null;
    return ProfileApi(servers.last);
  }

  @override
  Widget build(BuildContext context) {
    final set = ref.watch(filterSetProvider);
    final n = ref.read(filterSetProvider.notifier);
    final dim = Theme.of(context)
        .textTheme
        .bodyMedium
        ?.copyWith(color: AraColors.textSecondary);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        Text(
          'Declare the filters you image with — planning advice (narrowband vs '
          'broadband per target) and the per-filter Optimal Sub read this list. '
          'Names are matched to sequences case-insensitively, so keep them the '
          'same as your filter-wheel slot labels. Bandwidth 0 uses the kind\'s '
          'default; dual/tri-band values are per-pixel single-line widths.',
          style: dim,
        ),
        const SizedBox(height: 12),
        for (var i = 0; i < set.filters.length; i++)
          _FilterRow(
            key: ValueKey('filter-$i-${set.filters[i].name}'),
            filter: set.filters[i],
            onChanged: (f) => n.updateAt(i, f),
            onRemove: () => n.removeAt(i),
          ),
        const SizedBox(height: 8),
        Row(
          children: [
            TextButton.icon(
              onPressed: () => n.addFilter(PlanningFilter(
                  name: _nextName(set), kind: FilterKind.l)),
              icon: const Icon(Icons.add, size: 16),
              label: const Text('Add filter'),
            ),
            const SizedBox(width: 12),
            TextButton.icon(
              onPressed: _seedFromWheel,
              icon: const Icon(Icons.download_outlined, size: 16),
              label: const Text('Seed from filter wheel labels'),
            ),
          ],
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(_lastError!,
              style: TextStyle(color: Theme.of(context).colorScheme.error)),
          const SizedBox(height: 12),
        ],
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: _saving ? null : _save,
              icon: _saving
                  ? const SizedBox(
                      width: 14,
                      height: 14,
                      child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.save, size: 16),
              label: Text(_saving ? 'Saving…' : 'Save'),
            ),
          ],
        ),
      ],
    );
  }

  /// A non-colliding placeholder name for the Add button ('Filter 1', …) —
  /// the notifier rejects duplicates, so pick the first free number.
  static String _nextName(FilterSetSettings set) {
    for (var i = set.filters.length + 1;; i++) {
      final candidate = 'Filter $i';
      if (!set.filters
          .any((f) => f.name.toLowerCase() == candidate.toLowerCase())) {
        return candidate;
      }
    }
  }
}

/// One editable filter entry: name, kind dropdown, bandwidth (0 = kind default,
/// shown as the hint so the user knows what "default" means numerically).
class _FilterRow extends StatelessWidget {
  final PlanningFilter filter;
  final ValueChanged<PlanningFilter> onChanged;
  final VoidCallback onRemove;

  const _FilterRow({
    super.key,
    required this.filter,
    required this.onChanged,
    required this.onRemove,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 4, 4, 4),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Column(
                children: [
                  EditableTextRow(
                    label: 'Name',
                    currentValue: filter.name,
                    getCanonical: () => filter.name,
                    parse: (s) {
                      if (s.trim().isNotEmpty) {
                        onChanged(filter.copyWith(name: s));
                      }
                    },
                    hint: 'match the wheel slot label',
                  ),
                  SettingsDropdownRow<FilterKind>(
                    label: 'Kind',
                    value: filter.kind,
                    items: {for (final k in FilterKind.values) k: k.label},
                    onChanged: (k) {
                      if (k != null) onChanged(filter.copyWith(kind: k));
                    },
                  ),
                  EditableNumberRow(
                    label: 'Bandwidth (nm, 0 = default '
                        '${_fmt(filter.kind.defaultBandwidthNm)})',
                    currentValue: _fmt(filter.bandwidthNm),
                    getCanonical: () => _fmt(filter.bandwidthNm),
                    parse: (s) {
                      final v = double.tryParse(s);
                      if (v != null && v >= 0) {
                        onChanged(filter.copyWith(bandwidthNm: v));
                      }
                    },
                  ),
                ],
              ),
            ),
            IconButton(
              tooltip: 'Remove filter',
              icon: const Icon(Icons.close, size: 18),
              onPressed: onRemove,
            ),
          ],
        ),
      ),
    );
  }

  static String _fmt(double v) =>
      v == v.roundToDouble() ? v.toInt().toString() : v.toString();
}
