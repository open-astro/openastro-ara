/// What the rig actually has. An imaging run is cooked from this: a step goes
/// in only when the device it drives is part of the rig — no guider means no
/// Start Guiding and no dither trigger, no filter wheel means no Switch
/// Filter, no focuser means no autofocus step or autofocus triggers. (The
/// night of 2026-10-02 the run skipped guiding on a rig with a guider because
/// the builder was asked about filters, not about equipment.)
///
/// Pure value; [RigCapabilities.everything] keeps the historical "assume it
/// is all there" shape for callers that have no rig to read.
class RigCapabilities {
  final bool focuser;
  final bool filterWheel;
  final bool rotator;
  final bool guider;

  const RigCapabilities({
    this.focuser = false,
    this.filterWheel = false,
    this.rotator = false,
    this.guider = false,
  });

  static const everything = RigCapabilities(
    focuser: true,
    filterWheel: true,
    rotator: true,
    guider: true,
  );

  static const nothing = RigCapabilities();

  RigCapabilities copyWith({
    bool? focuser,
    bool? filterWheel,
    bool? rotator,
    bool? guider,
  }) => RigCapabilities(
    focuser: focuser ?? this.focuser,
    filterWheel: filterWheel ?? this.filterWheel,
    rotator: rotator ?? this.rotator,
    guider: guider ?? this.guider,
  );

  @override
  String toString() =>
      'RigCapabilities(focuser: $focuser, filterWheel: $filterWheel, '
      'rotator: $rotator, guider: $guider)';
}
