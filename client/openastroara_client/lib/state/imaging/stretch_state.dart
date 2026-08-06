import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §65 manual screen-stretch override for the imaging viewer — the
/// PixInsight-style black/midtone/white triplet, normalized 0..1.
/// Null means "auto" (the profile's default stretch, usually auto-STF);
/// the histogram strip's sliders set it and its Auto button clears it.
/// Session-scoped on purpose: a stretch is a way of LOOKING at tonight's
/// data, not a setting worth persisting.
class StretchOverride {
  final double black;
  final double mid;
  final double white;
  const StretchOverride({
    required this.black,
    required this.mid,
    required this.white,
  });
}

class StretchOverrideNotifier extends Notifier<StretchOverride?> {
  @override
  StretchOverride? build() => null;

  void set(StretchOverride value) => state = value;

  /// Back to the default stretch.
  void resetToAuto() => state = null;
}

final stretchOverrideProvider =
    NotifierProvider<StretchOverrideNotifier, StretchOverride?>(
        StretchOverrideNotifier.new);
