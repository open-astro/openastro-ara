import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/client_error_handlers.dart';
import 'package:openastroara/services/client_error_log.dart';
import 'package:openastroara/widgets/client_error_fallback.dart';

/// The handlers rewrite process-wide globals, so every test installs onto a
/// snapshot and restores it — flutter_test's own FlutterError.onError must be
/// back in place before the next test, or its failures go unreported.
void main() {
  late Directory dir;
  late ClientErrorLog log;
  late FlutterExceptionHandler? savedOnError;
  late DebugPrintCallback savedDebugPrint;
  late ErrorWidgetBuilder savedErrorWidgetBuilder;
  late ClientErrorHandlers handlers;
  final printed = <String>[];

  setUp(() {
    dir = Directory.systemTemp.createTempSync('client_error_handlers');
    log = ClientErrorLog(
      supportDir: () async => dir,
      appVersion: () async => 'v',
    );
    savedOnError = FlutterError.onError;
    savedDebugPrint = debugPrint;
    savedErrorWidgetBuilder = ErrorWidget.builder;
    printed.clear();
    // Stand-in for the framework's console handler, so the test can prove
    // the prior handler still runs (and see what it was given).
    FlutterError.onError = (d) => printed.add('prior:${d.exceptionAsString()}');
    debugPrint = (m, {wrapWidth}) => printed.add('print:$m');
  });

  tearDown(() {
    handlers.uninstall();
    FlutterError.onError = savedOnError;
    debugPrint = savedDebugPrint;
    ErrorWidget.builder = savedErrorWidgetBuilder;
    dir.deleteSync(recursive: true);
  });

  String file() =>
      File('${dir.path}/${ClientErrorLog.fileName}').readAsStringSync();

  test('FlutterError.onError records and still calls the prior handler',
      () async {
    handlers = ClientErrorHandlers.install(log);
    FlutterError.reportError(FlutterErrorDetails(
      exception: StateError('red screen'),
      stack: StackTrace.fromString('#0 somewhere'),
      library: 'widgets library',
      context: ErrorDescription('while notifying listeners'),
    ));
    await log.idle;
    expect(printed, ['prior:Bad state: red screen']);
    final text = file();
    expect(text, contains(' flutter_error ==='));
    expect(text, contains('library: widgets library'));
    expect(text, contains('context: while notifying listeners'));
    expect(text, contains('error: Bad state: red screen'));
    expect(text, contains('  #0 somewhere'));
  });

  test('debugPrint lines made before an error appear in its entry',
      () async {
    handlers = ClientErrorHandlers.install(log);
    debugPrint('site poll ok');
    debugPrint('frame preview opened');
    FlutterError.reportError(
      FlutterErrorDetails(exception: Exception('later')),
    );
    await log.idle;
    // The wrapped debugPrint still reaches the prior one…
    expect(printed, containsAll(['print:site poll ok', 'print:frame preview opened']));
    // …and the entry carries the lines in order.
    expect(file(), contains('  site poll ok\n  frame preview opened\n'));
  });

  test('PlatformDispatcher.onError records and reports handled', () async {
    final savedPlatform = PlatformDispatcher.instance.onError;
    PlatformDispatcher.instance.onError = null;
    addTearDown(() => PlatformDispatcher.instance.onError = savedPlatform);
    handlers = ClientErrorHandlers.install(log);
    final handled = PlatformDispatcher.instance.onError!(
      ArgumentError('async boom'),
      StackTrace.fromString('#0 async'),
    );
    expect(handled, isTrue);
    await log.idle;
    expect(file(), contains(' uncaught ==='));
    expect(file(), contains('error: Invalid argument(s): async boom'));
    // With no prior platform handler the failure is still printed.
    expect(printed.where((p) => p.startsWith('print:Uncaught error')), hasLength(1));
  });

  test('PlatformDispatcher.onError defers to a prior handler\'s verdict',
      () async {
    final savedPlatform = PlatformDispatcher.instance.onError;
    PlatformDispatcher.instance.onError = (e, s) => false;
    addTearDown(() => PlatformDispatcher.instance.onError = savedPlatform);
    handlers = ClientErrorHandlers.install(log);
    final handled = PlatformDispatcher.instance.onError!('x', StackTrace.empty);
    expect(handled, isFalse);
    await log.idle;
    expect(file(), contains(' uncaught ==='));
  });

  test('reportError lands as a reported entry with its context', () async {
    handlers = ClientErrorHandlers.install(log);
    await handlers.reportError(
      Exception('stream died'),
      context: 'planetarium event stream',
    );
    expect(file(), contains(' reported ==='));
    expect(file(), contains('context: planetarium event stream'));
  });

  test('ErrorWidget.builder is left alone unless the release fallback is on',
      () {
    handlers = ClientErrorHandlers.install(log);
    expect(ErrorWidget.builder, same(savedErrorWidgetBuilder));
    handlers.uninstall();

    handlers = ClientErrorHandlers.install(log, releaseFallback: true);
    final built = ErrorWidget.builder(
      FlutterErrorDetails(exception: Exception('x')),
    );
    expect(built, isA<ClientErrorFallback>());
  });

  test('uninstall restores every global', () {
    final platformBefore = PlatformDispatcher.instance.onError;
    final onErrorBefore = FlutterError.onError;
    final printBefore = debugPrint;
    handlers = ClientErrorHandlers.install(log, releaseFallback: true);
    expect(FlutterError.onError, isNot(same(onErrorBefore)));
    handlers.uninstall();
    expect(FlutterError.onError, same(onErrorBefore));
    expect(debugPrint, same(printBefore));
    expect(PlatformDispatcher.instance.onError, same(platformBefore));
    expect(ErrorWidget.builder, same(savedErrorWidgetBuilder));
    // tearDown uninstalls again; that must be harmless.
  });
}
