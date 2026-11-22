import 'package:openastroara/models/polar_align.dart';

/// The connected mount's key in the polar-align section, as the rig names it.
const preflightMountKey = 'mount-1';

/// #2068 the rig's pre-flight list for a mount whose clearance is [confirmed]
/// (or not): the clearance check passes or fails, the rest as a fresh rig has
/// them. The run turns RA up to [reachDeg] each way (#2165: the two 10° turns
/// plus the 0.5° direction test); [parked] adds the parked mount's failing
/// check. [onAxis] (#2166): the scope points within 3° of the pole, so the
/// start pointing fails, and the rig offers [moveToStart].
PolarAlignPreflight rigPreflight({
  required bool confirmed,
  bool parked = false,
  double reachDeg = 20.5,
  bool onAxis = false,
  PolarAlignMoveToStartOffer? moveToStart,
}) => PolarAlignPreflight(
  ready: confirmed && !parked && !onAxis,
  mountKey: preflightMountKey,
  moveToStart: onAxis ? moveToStart : null,
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
          : PolarAlignPreflightStatuses.fail,
      detail: confirmed
          ? 'You confirmed this mount clears the pier.'
          : 'Not confirmed for this mount.',
    ),
    if (onAxis)
      const PolarAlignPreflightCheck(
        id: PolarAlignPreflightChecks.startPointing,
        status: PolarAlignPreflightStatuses.fail,
        detail: 'The scope points within 3° of the pole.',
      ),
    const PolarAlignPreflightCheck(
      id: 'mount_type',
      status: PolarAlignPreflightStatuses.unknown,
      detail: 'The mount does not report its type.',
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
    reachDeg: reachDeg,
  ),
);

/// The list the rig answers for [settings]: the clearance check reads the
/// connected mount's own entry, as the rig's does (#2165: confirmed while the
/// run's [reachDeg] is no bigger than the reach it was confirmed for).
PolarAlignPreflight rigPreflightFor(
  PolarAlignSettings? settings, {
  bool parked = false,
  double reachDeg = 20.5,
  bool onAxis = false,
  PolarAlignMoveToStartOffer? moveToStart,
}) {
  final entry = settings?.mountClearance[preflightMountKey];
  return rigPreflight(
    confirmed: entry != null && reachDeg <= entry.confirmedReachDeg,
    parked: parked,
    reachDeg: reachDeg,
    onAxis: onAxis,
    moveToStart: moveToStart,
  );
}
