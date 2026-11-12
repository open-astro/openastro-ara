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

  /// The camera can hold a set-point (CanSetCCDTemperature). A camera that
  /// can't, or a dumb on/off cooler, gets no Cool Camera step: it would only
  /// wait out a minute for a temperature nothing regulates (#1577).
  final bool coolerRegulates;

  /// Whether a rig without a wheel stops for the user to change the filter.
  /// Simple Mode's plans don't: the glass in the camera is the session's.
  final bool pauseForFilterSwap;

  /// A flat panel the run can light. Without one, a Flat row first waits for
  /// the user to put a screen or light box over the scope (#1757).
  final bool flatPanel;

  const RigCapabilities({
    this.focuser = false,
    this.filterWheel = false,
    this.rotator = false,
    this.guider = false,
    this.coolerRegulates = false,
    this.pauseForFilterSwap = true,
    this.flatPanel = false,
  });

  static const everything = RigCapabilities(
    focuser: true,
    filterWheel: true,
    rotator: true,
    guider: true,
    coolerRegulates: true,
    flatPanel: true,
  );

  static const nothing = RigCapabilities();

  RigCapabilities copyWith({
    bool? focuser,
    bool? filterWheel,
    bool? rotator,
    bool? guider,
    bool? coolerRegulates,
    bool? pauseForFilterSwap,
    bool? flatPanel,
  }) => RigCapabilities(
    focuser: focuser ?? this.focuser,
    filterWheel: filterWheel ?? this.filterWheel,
    rotator: rotator ?? this.rotator,
    guider: guider ?? this.guider,
    coolerRegulates: coolerRegulates ?? this.coolerRegulates,
    pauseForFilterSwap: pauseForFilterSwap ?? this.pauseForFilterSwap,
    flatPanel: flatPanel ?? this.flatPanel,
  );

  @override
  String toString() =>
      'RigCapabilities(focuser: $focuser, filterWheel: $filterWheel, '
      'rotator: $rotator, guider: $guider, coolerRegulates: $coolerRegulates)';
}
