import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/profile/profile_import_flow.dart'
    show friendlyDaemonError;

/// Pins the profile/daemon error copy: a browser CORS-masked network failure
/// (raw "XMLHttpRequest onError callback was called…" text) must never reach
/// the user — it reads as an actionable sentence instead, and the server's own
/// problem-detail still wins verbatim when it sent one.
void main() {
  test('friendlyDaemonError never leaks raw XHR/DioException text', () {
    // The browser's CORS-masked network failure: no response, raw XHR message.
    final e = DioException(
      requestOptions: RequestOptions(path: '/api/v1/profiles'),
      type: DioExceptionType.connectionError,
      message:
          'The XMLHttpRequest onError callback was called. This typically '
          'indicates an error on the network layer.',
    );
    final msg = friendlyDaemonError(e, fallback: "Couldn't load the profiles");
    expect(msg, contains("Couldn't load the profiles"));
    expect(msg, contains("didn't answer"));
    expect(msg, isNot(contains('XMLHttpRequest')));
    expect(msg, isNot(contains('onError')));
  });

  test('friendlyDaemonError surfaces the server detail verbatim', () {
    final e = DioException(
      requestOptions: RequestOptions(path: '/api/v1/profiles'),
      response: Response(
        requestOptions: RequestOptions(path: '/api/v1/profiles'),
        statusCode: 409,
        data: {'detail': 'select another profile first'},
      ),
    );
    expect(friendlyDaemonError(e, fallback: "Couldn't load the profiles"),
        'select another profile first');
  });

  test('friendlyDaemonError keeps StateError and non-Dio fallbacks', () {
    expect(friendlyDaemonError(StateError('boom')), 'boom');
    expect(friendlyDaemonError(FormatException('nope'),
        fallback: 'Something went wrong'), 'Something went wrong');
  });

  test('any fallback wording still yields a specific action, not "do that"',
      () {
    // Fallbacks that don't use the "Couldn't …" convention (Connect failed,
    // Could not load …) must still produce an action for friendlyError.
    final e = DioException(
      requestOptions: RequestOptions(path: '/api/v1/profiles'),
      type: DioExceptionType.connectionError,
      message: 'The XMLHttpRequest onError callback was called.',
    );
    final msg = friendlyDaemonError(e, fallback: 'Connect failed');
    expect(msg, startsWith("Couldn't Connect"));
    expect(msg, isNot(contains("Couldn't do that")));
    final msg2 =
        friendlyDaemonError(e, fallback: 'Could not load guider settings');
    expect(msg2, startsWith("Couldn't load guider settings"));
    expect(msg2, isNot(contains("Couldn't do that")));
  });
}
