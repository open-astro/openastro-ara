import 'dart:ui' show PlatformDispatcher, ErrorCallback;

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';

import '../widgets/client_error_fallback.dart';
import 'client_error_log.dart';

/// Routes every error the framework can hand us into a [ClientErrorLog]
/// (#1111). Installed once by `main.dart` before `runApp`.
///
/// Four hooks, each chained onto whatever was there before so the debug
/// console keeps behaving exactly as it did:
/// - [FlutterError.onError] — build/layout/paint assertions, the red-screen
///   class of failure. The prior handler (`presentError` by default) still
///   runs, so the console dump is unchanged.
/// - [PlatformDispatcher.onError] — uncaught async errors, which used to
///   vanish into a stderr nobody was watching.
/// - [debugPrint] — every line is copied into the log's ring buffer so each
///   entry carries the last few hundred lines of console context.
/// - [ErrorWidget.builder] — untouched in debug (the red box with the
///   message is the right tool there); in release it becomes
///   [ClientErrorFallback], which uses no inherited lookups, so a failure
///   near the root can't cascade into a second one while being displayed.
///
/// [install] returns a handle whose [ClientErrorHandlers.uninstall] restores
/// the previous globals, which is what tests use.
class ClientErrorHandlers {
  ClientErrorHandlers._(
    this.log,
    this._priorOnError,
    this._priorPlatformOnError,
    this._priorDebugPrint,
    this._priorErrorWidgetBuilder,
  );

  final ClientErrorLog log;
  final FlutterExceptionHandler? _priorOnError;
  final ErrorCallback? _priorPlatformOnError;
  final DebugPrintCallback _priorDebugPrint;
  final ErrorWidgetBuilder _priorErrorWidgetBuilder;

  /// Installs the hooks. [releaseFallback] forces the release
  /// [ErrorWidget.builder] on (tests use it; production follows
  /// [kReleaseMode]).
  static ClientErrorHandlers install(
    ClientErrorLog log, {
    bool releaseFallback = kReleaseMode,
  }) {
    final handlers = ClientErrorHandlers._(
      log,
      FlutterError.onError,
      PlatformDispatcher.instance.onError,
      debugPrint,
      ErrorWidget.builder,
    );

    // Capture the priors as locals so the closures below don't depend on the
    // handle's fields being restored in a particular order.
    final priorOnError = handlers._priorOnError;
    final priorPlatformOnError = handlers._priorPlatformOnError;
    final priorDebugPrint = handlers._priorDebugPrint;

    FlutterError.onError = (FlutterErrorDetails details) {
      // Record first: the prior handler's console dump goes through
      // debugPrint and would otherwise land in THIS entry's ring snapshot,
      // duplicating the error text.
      log.record(
        'flutter_error',
        details.exception,
        stack: details.stack,
        context: details.context?.toDescription(),
        library: details.library,
      );
      priorOnError?.call(details);
    };

    PlatformDispatcher.instance.onError = (Object error, StackTrace stack) {
      log.record('uncaught', error, stack: stack);
      if (priorPlatformOnError != null) {
        return priorPlatformOnError(error, stack);
      }
      // Returning true tells the engine the error is handled, which also
      // suppresses its own print — so print it ourselves (debugPrint still
      // writes in release, so the console line exists in every mode).
      debugPrint('Uncaught error: $error\n$stack');
      return true;
    };

    debugPrint = (String? message, {int? wrapWidth}) {
      if (message != null) log.notePrint(message);
      priorDebugPrint(message, wrapWidth: wrapWidth);
    };

    if (releaseFallback) {
      ErrorWidget.builder = (FlutterErrorDetails details) =>
          const ClientErrorFallback();
    }
    return handlers;
  }

  /// Logs an error the app caught itself but wants on record — a stream
  /// listener's failure, say. Convenience over [ClientErrorLog.record].
  Future<void> reportError(
    Object error, {
    StackTrace? stack,
    String? context,
  }) => log.record('reported', error, stack: stack, context: context);

  /// Puts every global back the way [install] found it.
  void uninstall() {
    FlutterError.onError = _priorOnError;
    PlatformDispatcher.instance.onError = _priorPlatformOnError;
    debugPrint = _priorDebugPrint;
    ErrorWidget.builder = _priorErrorWidgetBuilder;
  }
}
