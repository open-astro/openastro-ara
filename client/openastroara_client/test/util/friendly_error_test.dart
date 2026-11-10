import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/guider_api.dart' show GuideStartFailed;
import 'package:openastroara/util/friendly_error.dart';
import 'package:flutter/widgets.dart' show Locale;
import 'package:openastroara/l10n/l10n.dart';

/// Pins the user-facing error copy: a raw DioException dump ("The request
/// connection took longer than 0:00:03.000000… RequestOptions.connectTimeout…")
/// must never reach a SnackBar — timeouts and connection failures read as
/// sentences a person can act on, and the server's own problem-detail wins when
/// it sent one.
final _en = lookupAppLocalizations(const Locale('en'));

void main() {
  test("a start the guider gave up on reads as its reason, or points at the Guider log (#1664)", () {
    expect(friendlyError(_en, const GuideStartFailed('Stopped capturing'), action: 'start guiding'),
        "Guiding didn't start: Stopped capturing.");
    expect(friendlyError(_en, const GuideStartFailed(null), action: 'start guiding'),
        "Guiding didn't start. See the Guider log for why.");
  });

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
    final msg = friendlyError(_en, e, action: 'read the camera');
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
    final msg = friendlyError(_en, e, action: 'save your settings');
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
    expect(friendlyError(_en, e, action: 'set the cooler'),
        'this camera does not support cooling');
  });

  DioException bad(int code, [Object? data]) => DioException(
        requestOptions: RequestOptions(path: '/x'),
        type: DioExceptionType.badResponse,
        response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: code, data: data),
      );

  test("a 404 with the daemon's sentence reads as that sentence (#1764)", () {
    expect(friendlyError(_en, bad(404, {'detail': 'No guide camera is known.'}), action: 'start guiding'),
        'No guide camera is known.');
    expect(failureReason(_en, bad(404, {'detail': 'No switch \'box\' is known.'})), "No switch 'box' is known.");
  });

  test('a 404 with no detail still reads as the generic not-found sentence (#1764)', () {
    final msg = friendlyError(_en, bad(404), action: 'load the library');
    expect(msg, contains("Couldn't load the library"));
    expect(msg, contains(_en.errorNotFound));
  });

  test('the 400, 409, 422 and 507 details are unchanged, and a 500 detail is not shown (#1764)', () {
    for (final code in [400, 409, 422, 507]) {
      expect(friendlyError(_en, bad(code, {'detail': 'the rig said why'})), 'the rig said why', reason: '$code');
    }
    expect(friendlyError(_en, bad(500, {'detail': 'NullReferenceException at Foo'})), isNot(contains('NullReference')));
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
    final msg = friendlyError(_en, e, action: 'load the library');
    expect(msg, contains("Couldn't load the library"));
    expect(msg, contains('Logs'));
  });
}
