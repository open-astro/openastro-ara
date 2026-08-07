import 'dart:io';

import 'package:dio/dio.dart';

/// Turns whatever went wrong into a sentence a person can act on.
///
/// Raw exception text ("DioException [connection timeout]: The request
/// connection took longer than 0:00:03.000000") tells an astrophotographer
/// standing in the dark exactly nothing. Every screen that catches an error
/// should render this instead — the server's own message when it sent one
/// (those are already written for people), otherwise a plain description of
/// what failed and what to check.
///
/// [action] names what was being attempted, lower-case and without a period:
/// "save your settings", "load the library". It appears as `Couldn't <action>`.
String friendlyError(Object error, {String action = 'do that'}) {
  final prefix = "Couldn't $action";

  if (error is DioException) {
    // The server speaks human in its Problem responses — prefer its words.
    final data = error.response?.data;
    if (data is Map && data['detail'] is String) {
      final detail = (data['detail'] as String).trim();
      if (detail.isNotEmpty) {
        return detail;
      }
    }
    if (data is Map && data['title'] is String) {
      final title = (data['title'] as String).trim();
      if (title.isNotEmpty) {
        return title;
      }
    }

    return switch (error.type) {
      DioExceptionType.connectionTimeout ||
      DioExceptionType.connectionError =>
        "$prefix — your rig didn't answer. Check it's powered on and on the "
            'same network as this computer.',
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout =>
        '$prefix — your rig took too long to answer. It may be busy; try again '
            'in a moment.',
      DioExceptionType.badCertificate =>
        "$prefix — the connection wasn't trusted.",
      DioExceptionType.cancel => '$prefix — the request was cancelled.',
      DioExceptionType.badResponse => switch (error.response?.statusCode ?? 0) {
          404 => "$prefix — your rig doesn't have that. It may be running an "
              'older version of Ara.',
          409 => "$prefix — something else is using it right now.",
          422 => "$prefix — your rig refused those values.",
          >= 500 => '$prefix — your rig hit an error. Support → Logs has the '
              'details.',
          _ => '$prefix — your rig refused the request.',
        },
      _ => '$prefix — the connection failed. Check your rig is reachable.',
    };
  }

  if (error is SocketException) {
    return "$prefix — your rig didn't answer. Check it's powered on and on the "
        'same network as this computer.';
  }
  if (error is FileSystemException) {
    final reason = error.osError?.message;
    return '$prefix — a file problem got in the way'
        '${reason == null ? '.' : ': $reason.'}';
  }
  if (error is FormatException) {
    return "$prefix — your rig sent something Ara didn't understand. It may be "
        'running a different version.';
  }
  // StateError carries our own written-for-people messages (e.g. "Connect to
  // your rig first.") — pass those straight through.
  if (error is StateError) {
    return error.message;
  }
  return '$prefix.';
}
