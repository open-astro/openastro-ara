import 'dart:io';

import 'package:dio/dio.dart';
import '../l10n/l10n.dart';
import 'gps_site_fill.dart' show clientPlatform, thisDeviceLabel;

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
/// "save your settings", "load the library". It appears as `Couldn't <action>`
/// in [l]'s language (the action should be in it too).
String friendlyError(AppLocalizations l, Object error, {String? action}) {
  final prefix = l.errorCouldnt(action ?? l.errorActionDefault);

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
        '$prefix — ${l.errorNoAnswer(thisDeviceLabel(clientPlatform))}',
      DioExceptionType.sendTimeout ||
      DioExceptionType.receiveTimeout =>
        '$prefix — ${l.errorTooSlow}',
      DioExceptionType.badCertificate =>
        '$prefix — ${l.errorUntrusted}',
      DioExceptionType.cancel => '$prefix — ${l.errorCancelled}',
      DioExceptionType.badResponse => switch (error.response?.statusCode ?? 0) {
          404 => '$prefix — ${l.errorNotFound}',
          409 => '$prefix — ${l.errorBusy}',
          422 => '$prefix — ${l.errorRefusedValues}',
          >= 500 => '$prefix — ${l.errorServer}',
          _ => '$prefix — ${l.errorRefused}',
        },
      _ => '$prefix — ${l.errorConnectionFailed}',
    };
  }

  if (error is SocketException) {
    return '$prefix — ${l.errorNoAnswer(thisDeviceLabel(clientPlatform))}';
  }
  if (error is FileSystemException) {
    final reason = error.osError?.message;
    return '$prefix — ${l.errorFile}'
        '${reason == null ? '.' : ': $reason.'}';
  }
  if (error is FormatException) {
    return '$prefix — ${l.errorUnreadable}';
  }
  // StateError carries our own written-for-people messages (e.g. "Connect to
  // your rig first.") — pass those straight through.
  if (error is StateError) {
    return error.message;
  }
  return '$prefix.';
}
