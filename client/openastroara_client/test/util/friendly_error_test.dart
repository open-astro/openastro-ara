import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/friendly_error.dart';

/// Pins the user-facing error copy: a raw DioException dump ("The request
/// connection took longer than 0:00:03.000000… RequestOptions.connectTimeout…")
/// must never reach a SnackBar — timeouts and connection failures read as
/// sentences a person can act on, and the server's own problem-detail wins when
/// it sent one.
void main() {
  test('connection timeout reads as an actionable sentence, not raw Dio text',
      () {
    final e = DioException(
      requestOptions: RequestOptions(path: '/api/v1/equipment/camera'),
      type: DioExceptionType.connectionTimeout,
      message:
          'The request connection took longer than 0:00:03.000000 and it was '
          'aborted. To get rid of this exception, try raising the '
          'RequestOptions.connectTimeout above the duration of 0:00:03.000000',
    );
    final msg = friendlyError(e, action: 'read the camera');
    expect(msg, contains("Couldn't read the camera"));
    expect(msg, contains("didn't answer"));
    expect(msg, isNot(contains('connectTimeout')));
    expect(msg, isNot(contains('0:00:03')));
    expect(msg, isNot(contains('RequestOptions')));
  });

  test('receive timeout reads as the rig being busy', () {
    final e = DioException(
      requestOptions: RequestOptions(path: '/x'),
      type: DioExceptionType.receiveTimeout,
    );
    final msg = friendlyError(e, action: 'save your settings');
    expect(msg, contains("Couldn't save your settings"));
    expect(msg, contains('took too long'));
  });

  test("the server's problem detail is surfaced verbatim (it's already human)",
      () {
    final e = DioException(
      requestOptions: RequestOptions(path: '/api/v1/equipment/camera/cooler'),
      response: Response(
        requestOptions: RequestOptions(path: '/api/v1/equipment/camera/cooler'),
        statusCode: 409,
        data: {'detail': 'this camera does not support cooling'},
      ),
    );
    expect(friendlyError(e, action: 'set the cooler'),
        'this camera does not support cooling');
  });

  test('badResponse 500 maps to a check-the-logs sentence', () {
    final e = DioException(
      requestOptions: RequestOptions(path: '/x'),
      type: DioExceptionType.badResponse,
      response: Response(
        requestOptions: RequestOptions(path: '/x'),
        statusCode: 500,
      ),
    );
    final msg = friendlyError(e, action: 'load the library');
    expect(msg, contains("Couldn't load the library"));
    expect(msg, contains('Logs'));
  });
}
