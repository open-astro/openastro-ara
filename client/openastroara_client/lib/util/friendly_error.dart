import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import '../l10n/l10n.dart';
import '../models/mount_status.dart' show MountStatus;
import '../services/guider_api.dart' show GuideStartFailed;
import '../services/profile_api.dart' show SectionVersionMissingException;
import '../services/refusal.dart';
import 'gps_site_fill.dart' show clientPlatform, thisDeviceLabel;

/// Turns whatever went wrong into a sentence a person can act on.
///
/// Raw exception text ("DioException [connection timeout]: The request
/// connection took longer than 0:00:03.000000") tells an astrophotographer
/// standing in the dark exactly nothing. Every screen that catches an error
/// should render this instead — the daemon's own sentence on a 400, 404, 409
/// or 422 ([daemonDetail]), otherwise a plain description of what failed and
/// what to check, in [l]'s language and never a status code (#1699).
///
/// [action] names what was being attempted, lower-case and without a period:
/// "save your settings", "load the library". It appears as `Couldn't <action>`
/// in [l]'s language (the action should be in it too).
String friendlyError(AppLocalizations l, Object error, {String? action}) {
  final own = _ownWords(l, error);
  if (own != null) return own;
  final prefix = l.errorCouldnt(action ?? l.errorActionDefault);
  final why = _why(l, error);
  return why == null ? '$prefix.' : '$prefix — $why';
}

/// Why [error] happened as a sentence of its own: a pill's detail under a
/// title that already says what failed.
String failureReason(AppLocalizations l, Object error) {
  final own = _ownWords(l, error);
  if (own != null) return own;
  final why = _why(l, error);
  if (why == null) return '${l.errorCouldnt(l.errorActionDefault)}.';
  return why[0].toUpperCase() + why.substring(1);
}

/// The first line of the daemon's own sentence, on the statuses where it is
/// written for people (400, 404 (#1764: "No guide camera is known."), 409,
/// 422, and 507: a full disk the user can free); null otherwise. A framework-shaped 400
/// detail can carry a "(Parameter 'x')" tail that is not for people.
String? daemonDetail(Object error) {
  if (error is! DioException) return null;
  final code = error.response?.statusCode;
  if (code != 400 && code != 404 && code != 409 && code != 422 && code != 507) return null;
  // application/problem+json may reach us undecoded, depending on Dio's
  // transformer.
  var data = error.response?.data;
  if (data is String) {
    try {
      data = jsonDecode(data);
    } on FormatException {
      return null;
    }
  }
  final detail = data is Map ? data['detail'] : null;
  if (detail is! String) return null;
  final line = detail.trim().split('\n').first.trim();
  return line.isEmpty ? null : line;
}

/// Words already written for people: the daemon's, the guider's, ours.
String? _ownWords(AppLocalizations l, Object error) {
  if (error is Refusal) return _refusalWords(l, error);
  final detail = daemonDetail(error);
  if (detail != null) return detail;
  // #1664 — the guider's own reason, not "Couldn't start guiding."
  if (error is GuideStartFailed) {
    final reason = error.reason;
    return reason == null ? l.guideStartFailedNoReason : l.guideStartFailed(reason);
  }
  // ADR 0015 — a section write refused before it went out: the rig sent no
  // version to send back.
  if (error is SectionVersionMissingException) return error.toString();
  // StateError carries our own written-for-people messages (e.g. "Connect to
  // your rig first.") — pass those straight through.
  if (error is StateError) return error.message;
  return null;
}

/// ADR 0022 — a known cause is worded by the app, in [l]'s language; an unknown
/// cause or none shows the daemon's own sentence, and null when it has none.
String? _refusalWords(AppLocalizations l, Refusal r) {
  final missing = r.parameters['missing'];
  return switch (r.cause) {
    RefusalCause.noCamera => l.refusalNoCamera,
    RefusalCause.cameraBusy => l.refusalCameraBusy,
    RefusalCause.gateStuck => l.refusalGateStuck,
    RefusalCause.centerInProgress => l.refusalCenterInProgress,
    RefusalCause.opticsNotSet => l.refusalOpticsNotSet(
      missing is List && missing.length == 1 ? '${missing.first}' : 'both',
    ),
    RefusalCause.solverNotSet => l.refusalSolverNotSet,
    RefusalCause.captureSettingsUnsupported =>
      l.refusalCaptureSettingsUnsupported,
    RefusalCause.storageUnavailable => l.refusalStorageUnavailable,
    RefusalCause.belowHorizonLimit => l.refusalBelowHorizonLimit(
      _degrees(r.parameters['altitude_deg']),
      _degrees(r.parameters['limit_deg']),
    ),
    RefusalCause.aboveClearanceLimit => l.refusalAboveClearanceLimit(
      _degrees(r.parameters['altitude_deg']),
      _degrees(r.parameters['limit_deg']),
    ),
    RefusalCause.counterweightLimit => l.refusalCounterweightLimit(
      _hours(r.parameters['hour_angle_hours']),
      _wholeDegrees(r.parameters['counterweight_angle_deg']),
      _wholeDegrees(r.parameters['limit_deg']),
    ),
    RefusalCause.endNightInProgress => l.refusalEndNightInProgress,
    RefusalCause.mountPositionUnverified =>
      switch (r.parameters['position_unverified_reason']) {
        'not_read_yet' => l.refusalMountPositionNotReadYet,
        MountStatus.stopUnconfirmedReason =>
          l.refusalMountPositionStopUnconfirmed,
        _ => l.refusalMountPositionUnverified,
      },
    RefusalCause.polarAlignMountParked => l.refusalPolarAlignMountParked,
    RefusalCause.polarAlignUnconfirmed => l.refusalPolarAlignUnconfirmed,
    RefusalCause.polarAlignSweepLimit => l.refusalPolarAlignSweepLimit(
      _unsignedWholeDegrees(r.parameters['offset_deg']),
    ),
    RefusalCause.polarAlignMovingMount => l.refusalPolarAlignMovingMount,
    RefusalCause.polarAlignOnAxis => l.refusalPolarAlignOnAxis,
    RefusalCause.polarAlignPointingUnknown => l.refusalPolarAlignPointingUnknown,
    RefusalCause.polarAlignPoseAmbiguous => l.refusalPolarAlignPoseAmbiguous,
    RefusalCause.polarAlignMountBusy => l.refusalPolarAlignMountBusy,
    RefusalCause.firstFlipNotCompleted => l.refusalFirstFlipNotCompleted,
    RefusalCause.mountClockNotVerified => l.refusalMountClockNotVerified,
    RefusalCause.polarAlignAlreadyRunning => l.refusalPolarAlignAlreadyRunning,
    RefusalCause.polarAlignClutchTurned => l.refusalPolarAlignClutchTurned,
    RefusalCause.polarAlignClearanceNotConfirmed =>
      l.refusalPolarAlignClearanceNotConfirmed(
        _wholeDegrees(r.parameters['max_cw_angle_deg']),
      ),
    RefusalCause.polarAlignSiteNotSet => l.refusalPolarAlignSiteNotSet,
    RefusalCause.polarAlignAltAzMount => l.refusalPolarAlignAltAzMount,
    RefusalCause.polarAlignCannotMoveAxis => l.refusalPolarAlignCannotMoveAxis,
    null => _firstLine(r.detail),
  };
}

/// A refusal's altitude or limit in degrees, one decimal; "?" when absent.
String _degrees(Object? value) => value is num ? value.toStringAsFixed(1) : '?';

/// A refusal's counterweight angle or limit in whole degrees; "?" when absent.
String _wholeDegrees(Object? value) =>
    value is num ? value.toStringAsFixed(0) : '?';

/// A refusal's hour angle in hours, one decimal; "?" when absent.
String _hours(Object? value) => value is num ? value.toStringAsFixed(1) : '?';

/// A refusal's angle in whole degrees, unsigned; "?" when absent.
String _unsignedWholeDegrees(Object? value) =>
    value is num ? value.abs().toStringAsFixed(0) : '?';

String? _firstLine(String? text) {
  final line = text?.trim().split('\n').first.trim();
  return line == null || line.isEmpty ? null : line;
}

/// The reason after "Couldn't … —", lower-case; null when nothing is known.
String? _why(AppLocalizations l, Object error) {
  if (error is DioException) {
    return switch (error.type) {
      DioExceptionType.connectionTimeout ||
      DioExceptionType.connectionError =>
        l.errorNoAnswer(thisDeviceLabel(clientPlatform)),
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout =>
        l.errorTooSlow,
      DioExceptionType.badCertificate => l.errorUntrusted,
      DioExceptionType.cancel => l.errorCancelled,
      DioExceptionType.badResponse => switch (error.response?.statusCode ?? 0) {
          404 => l.errorNotFound,
          409 => l.errorBusy,
          422 => l.errorRefusedValues,
          >= 500 => l.errorServer,
          _ => l.errorRefused,
        },
      _ => l.errorConnectionFailed,
    };
  }
  // A Refusal with no cause and no detail: say what its status says.
  if (error is Refusal) {
    return error.status == 409 ? l.errorBusy : l.errorRefusedValues;
  }
  if (error is SocketException) {
    return l.errorNoAnswer(thisDeviceLabel(clientPlatform));
  }
  if (error is FileSystemException) {
    final reason = error.osError?.message;
    return '${l.errorFile}${reason == null ? '.' : ': $reason.'}';
  }
  if (error is FormatException) return l.errorUnreadable;
  return null;
}
