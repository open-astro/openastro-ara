import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §37.4 + §46.2 filter wheel slot labels. Phase 12h.2-filter-labels holds
/// state in memory; 12h.2b wires `/api/v1/profile/filter-wheel/labels` for
/// daemon round-trip. The slot count (8) matches the §46.2 reference filter
/// wheel; real hardware will report its slot count on connect and the
/// notifier resizes via `setSlotCount` in 12h.2b.

class FilterWheelLabels {
  // Stored unmodifiable so consumers can't mutate the slot list in place.
  // Values are trimmed at this layer so the invariant holds regardless of
  // whether they came in via the notifier setter or a future daemon-hydration
  // call site that constructs the model directly.
  final List<String> _labels;

  FilterWheelLabels({
    List<String> labels = const ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
  }) : _labels = List.unmodifiable(labels.map((s) => s.trim()));

  int get slotCount => _labels.length;

  /// 1-indexed (slot 1 is the first slot). Out-of-range returns empty.
  String labelAt(int slot) {
    final i = slot - 1;
    if (i < 0 || i >= _labels.length) return '';
    return _labels[i];
  }

  FilterWheelLabels withLabel(int slot, String label) {
    final i = slot - 1;
    if (i < 0 || i >= _labels.length) return this;
    final next = [..._labels];
    next[i] = label.trim();
    return FilterWheelLabels(labels: next);
  }

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;
    if (other is! FilterWheelLabels) return false;
    if (other._labels.length != _labels.length) return false;
    for (var i = 0; i < _labels.length; i++) {
      if (other._labels[i] != _labels[i]) return false;
    }
    return true;
  }

  @override
  int get hashCode => Object.hashAll(_labels);
}

class FilterWheelLabelsNotifier extends Notifier<FilterWheelLabels> {
  @override
  FilterWheelLabels build() => FilterWheelLabels();

  void setLabel(int slot, String label) {
    state = state.withLabel(slot, label);
  }
}

final filterWheelLabelsProvider =
    NotifierProvider<FilterWheelLabelsNotifier, FilterWheelLabels>(
        FilterWheelLabelsNotifier.new);
