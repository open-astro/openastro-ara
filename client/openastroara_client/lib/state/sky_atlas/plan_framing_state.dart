import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §36 — the planetarium page's framing box as Flutter last heard it: the
/// page owns the geometry (the dial, the frame centre that follows the view)
/// and posts a `framing` event over the loopback channel whenever it changes.
/// The Rotate camera panel reads the planned position angle from here.
class PlanFraming {
  /// The framing overlay is switched on in the page.
  final bool on;
  final double? raDeg;
  final double? decDeg;

  /// The dialed position angle, degrees east of north, in [0, 360).
  final double rotationDeg;
  final String name;

  const PlanFraming({
    this.on = false,
    this.raDeg,
    this.decDeg,
    this.rotationDeg = 0,
    this.name = '',
  });

  static const none = PlanFraming();

  /// A target the readout can be aimed at: framing on, with a centre.
  bool get hasTarget => on && raDeg != null && decDeg != null;

  /// Parse the page's `framing` / `rotateCamera` event payload. Pure.
  static PlanFraming fromEvent(Map<String, Object?> event) {
    final rot = (event['rotationDeg'] as num?)?.toDouble() ?? 0;
    return PlanFraming(
      on: event['on'] as bool? ?? true,
      raDeg: (event['raDeg'] as num?)?.toDouble(),
      decDeg: (event['decDeg'] as num?)?.toDouble(),
      rotationDeg: rot.isFinite ? ((rot % 360) + 360) % 360 : 0,
      name: ((event['name'] as String?) ?? '').trim(),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is PlanFraming &&
      other.on == on &&
      other.raDeg == raDeg &&
      other.decDeg == decDeg &&
      other.rotationDeg == rotationDeg &&
      other.name == name;

  @override
  int get hashCode => Object.hash(on, raDeg, decDeg, rotationDeg, name);
}

class PlanFramingNotifier extends Notifier<PlanFraming> {
  @override
  PlanFraming build() => PlanFraming.none;
  void set(PlanFraming f) => state = f;
}

final planFramingProvider = NotifierProvider<PlanFramingNotifier, PlanFraming>(
  PlanFramingNotifier.new,
);
