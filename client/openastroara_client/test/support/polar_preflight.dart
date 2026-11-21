import 'package:openastroara/models/polar_align.dart';

/// The connected mount's key in the polar-align section, as the rig names it.
const preflightMountKey = 'mount-1';

/// #2068 the rig's pre-flight list for a mount whose clearance is [confirmed]
/// (or not): the clearance check passes, or fails for a motor start and warns
/// for a Turn RA by hand start ([handTurn], Joey 2026-11-21), the rest as a
/// fresh rig has them. The counterweight limit is [maxCwAngleDeg]; [parked]
/// adds the parked mount's failing check.
PolarAlignPreflight rigPreflight({
  required bool confirmed,
  bool handTurn = false,
  bool parked = false,
  double maxCwAngleDeg = 60,
}) => PolarAlignPreflight(
  ready: (confirmed || handTurn) && !parked,
  mountKey: preflightMountKey,
  checks: [
    const PolarAlignPreflightCheck(
      id: 'mount_connected',
      status: PolarAlignPreflightStatuses.ok,
      detail: 'The mount is connected.',
    ),
    PolarAlignPreflightCheck(
      id: 'mount_parked',
      status: parked
          ? PolarAlignPreflightStatuses.fail
          : PolarAlignPreflightStatuses.ok,
      detail: parked ? 'The mount is parked.' : 'The mount is not parked.',
    ),
    PolarAlignPreflightCheck(
      id: PolarAlignPreflightChecks.clearanceConfirmed,
      status: confirmed
          ? PolarAlignPreflightStatuses.ok
          : handTurn
          ? PolarAlignPreflightStatuses.warn
          : PolarAlignPreflightStatuses.fail,
      detail: confirmed
          ? 'You confirmed this mount clears the pier.'
          : 'Not confirmed for this mount.',
    ),
    const PolarAlignPreflightCheck(
      id: 'start_cw_angle',
      status: PolarAlignPreflightStatuses.unknown,
      detail: 'Not checked yet.',
    ),
    const PolarAlignPreflightCheck(
      id: 'mount_limits',
      status: PolarAlignPreflightStatuses.warn,
      detail: "Set the mount's own limits.",
    ),
  ],
  planned: PolarAlignPreflightPlanned(
    direction: PolarAlignDirections.east,
    turnStepDeg: 10,
    checkStepDeg: 10,
    turns: 2,
    maxSweepDeg: 20,
    maxCwAngleDeg: maxCwAngleDeg,
  ),
);

/// The list the rig answers for [settings]: the clearance check reads the
/// connected mount's own entry, as the rig's does (confirmed only for the
/// limit it was confirmed for).
PolarAlignPreflight rigPreflightFor(
  PolarAlignSettings? settings, {
  bool handTurn = false,
  bool parked = false,
}) {
  final entry = settings?.mountClearance[preflightMountKey];
  return rigPreflight(
    confirmed:
        entry != null &&
        entry.clearanceConfirmed &&
        entry.confirmedMaxCwAngleDeg == entry.maxCwAngleDeg,
    handTurn: handTurn,
    parked: parked,
    maxCwAngleDeg: entry?.maxCwAngleDeg ?? 60,
  );
}
