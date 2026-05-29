import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §37.4 + §46.2 filter wheel slot labels. Phase 12h.2-filter-labels holds
/// state in memory; 12h.2b wires `/api/v1/profile/filter-wheel/labels` for
/// daemon round-trip. The slot count (8) matches the §46.2 reference filter
/// wheel; real hardware will report its slot count on connect and the
/// notifier resizes via `setSlotCount` in 12h.2b.

class FilterWheelLabels {
  // Index = slot number minus 1 (slot 1 is index 0). Trimmed strings; empty
  // string means slot is unused/blank.
  final List<String> labels;

  const FilterWheelLabels({
    this.labels = const ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
  });

  int get slotCount => labels.length;

  String labelAt(int slot) {
    // 1-indexed for the user-facing API; internal storage is 0-indexed.
    final i = slot - 1;
    if (i < 0 || i >= labels.length) return '';
    return labels[i];
  }

  FilterWheelLabels withLabel(int slot, String label) {
    final i = slot - 1;
    if (i < 0 || i >= labels.length) return this;
    final next = [...labels];
    next[i] = label;
    return FilterWheelLabels(labels: next);
  }
}

class FilterWheelLabelsNotifier extends Notifier<FilterWheelLabels> {
  @override
  FilterWheelLabels build() => const FilterWheelLabels();

  void setLabel(int slot, String label) {
    // Trim so trailing whitespace doesn't accidentally persist (consistent
    // with the trim-then-reject pattern used elsewhere, but here we allow
    // empty so the user can blank out an unused slot).
    state = state.withLabel(slot, label.trim());
  }
}

final filterWheelLabelsProvider =
    NotifierProvider<FilterWheelLabelsNotifier, FilterWheelLabels>(
        FilterWheelLabelsNotifier.new);
