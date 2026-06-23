/// Client model for a §36 sky-catalog object served by
/// `GET /api/v1/data-manager/{packageId}/catalog` — the daemon's `CatalogObjectDto`
/// (snake_case wire: `name`, `ra_deg`, `dec_deg`, `magnitude`). Defensive parse:
/// a row without a usable position is dropped rather than throwing, so one bad
/// entry can't sink the whole overlay.
library;

class CatalogObject {
  final String name;
  final double raDeg;
  final double decDeg;

  /// Apparent magnitude, or null when the catalog row carries none (e.g. an
  /// OpenNGC DSO with neither V- nor B-Mag). Drives nothing structural; the
  /// overlay may use it for sizing/labels.
  final double? magnitude;

  const CatalogObject({
    required this.name,
    required this.raDeg,
    required this.decDeg,
    this.magnitude,
  });

  /// Parses one wire row, or returns null when it lacks a finite position — RA/Dec
  /// are the only fields the overlay can't do without, so a positionless row is
  /// unplaceable and skipped.
  static CatalogObject? tryFromJson(Map<String, dynamic> json) {
    final ra = _finite(json['ra_deg']);
    final dec = _finite(json['dec_deg']);
    if (ra == null || dec == null) return null;
    return CatalogObject(
      name: json['name'] is String ? json['name'] as String : '',
      raDeg: ra,
      decDeg: dec,
      magnitude: _finite(json['magnitude']),
    );
  }

  static double? _finite(dynamic v) {
    final d = v is num ? v.toDouble() : null;
    return (d != null && d.isFinite) ? d : null;
  }

  @override
  bool operator ==(Object other) =>
      other is CatalogObject &&
      other.name == name &&
      other.raDeg == raDeg &&
      other.decDeg == decDeg &&
      other.magnitude == magnitude;

  @override
  int get hashCode => Object.hash(name, raDeg, decDeg, magnitude);
}
