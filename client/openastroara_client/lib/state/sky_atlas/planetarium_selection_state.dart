import 'package:flutter_riverpod/flutter_riverpod.dart';

/// What the planetarium has selected, as its page reports it in Simple Mode
/// (`?bare=1` hides the page's own info panel and posts a `selection` event).
/// [raHours]/[decDeg] are JNOW, ready to slew to; [raJ2000Hours]/
/// [decJ2000Deg] are what plate-solve centring takes.
class PlanetariumSelection {
  final String name;
  final String kind;
  final double raHours;
  final double decDeg;
  final double raJ2000Hours;
  final double decJ2000Deg;

  /// Altitude now, degrees; at or below 0 the object is under the horizon.
  final double? altDeg;

  /// Altitude at transit from this site; at or below 0 it never rises here.
  final double maxAltDeg;

  /// Tonight's rise, when the page could work it out.
  final DateTime? rise;

  const PlanetariumSelection({
    required this.name,
    required this.kind,
    required this.raHours,
    required this.decDeg,
    required this.raJ2000Hours,
    required this.decJ2000Deg,
    required this.maxAltDeg,
    this.altDeg,
    this.rise,
  });

  bool get neverRises => maxAltDeg <= 0;
  bool get belowHorizon => (altDeg ?? 0) <= 0;

  /// One page payload, or null for "nothing selected" or a payload without a
  /// position (nothing to slew to).
  static PlanetariumSelection? tryFromJson(Object? json) {
    if (json is! Map) return null;
    double? n(String k) => (json[k] as num?)?.toDouble();
    final ra = n('raHours'), dec = n('decDeg'), maxAlt = n('maxAltDeg');
    if (ra == null || dec == null || maxAlt == null) return null;
    final riseMs = n('riseMs');
    return PlanetariumSelection(
      name: json['name'] is String ? json['name'] as String : 'Object',
      kind: json['kind'] is String ? json['kind'] as String : '',
      raHours: ra,
      decDeg: dec,
      // An older page sends JNOW only; close enough to start a centring.
      raJ2000Hours: n('raJ2000Hours') ?? ra,
      decJ2000Deg: n('decJ2000Deg') ?? dec,
      altDeg: n('altDeg'),
      maxAltDeg: maxAlt,
      rise: riseMs == null
          ? null
          : DateTime.fromMillisecondsSinceEpoch(riseMs.round()),
    );
  }
}

class PlanetariumSelectionNotifier extends Notifier<PlanetariumSelection?> {
  @override
  PlanetariumSelection? build() => null;
  void set(PlanetariumSelection? s) => state = s;
}

final planetariumSelectionProvider =
    NotifierProvider<PlanetariumSelectionNotifier, PlanetariumSelection?>(
      PlanetariumSelectionNotifier.new,
    );

/// A search typed outside the planetarium (Simple Mode's Target panel), run
/// by the planetarium with its own search (catalogs, the daemon's resolver,
/// the engine). A bus like `planetariumCommandProvider`: every request
/// notifies, even the same text twice; consume it with `ref.listen`.
class PlanetariumSearchNotifier extends Notifier<String?> {
  @override
  String? build() => null;
  void search(String query) => state = query;
  void clear() => state = null;

  @override
  bool updateShouldNotify(String? previous, String? next) => next != null;
}

final planetariumSearchProvider =
    NotifierProvider<PlanetariumSearchNotifier, String?>(
      PlanetariumSearchNotifier.new,
    );
