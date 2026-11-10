import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/pa_residual.dart';
import 'package:openastroara/widgets/imaging/polar_error_rating.dart';

void main() {
  const done = <String, dynamic>{
    'id': '7d1c',
    'status': 'done',
    'started_utc': '2026-10-08T21:00:00Z',
    'completed_utc': '2026-10-08T21:06:00Z',
    'sample_seconds': 300.0,
    'target_seconds': 300.0,
    'frames': 118,
    'drift_arcsec_per_min': -0.21,
    'pa_error_min_arcmin': 0.8,
    'uncertainty_arcmin': 0.2,
    'reliable': true,
    'hour_angle_hours': -1.25,
    'dec_deg': 44.2,
    'align_error_arcmin': 0.68,
    'align_ended_utc': '2026-10-08T20:21:00Z',
    'session_id': null,
    'reason': null,
  };

  group('PaResidual.fromJson', () {
    test('parses a finished measurement', () {
      final r = PaResidual.fromJson(done)!;
      expect(r.status, PaResidualStatus.done);
      expect(r.paErrorMinArcmin, 0.8);
      expect(r.uncertaintyArcmin, 0.2);
      expect(r.reliable, isTrue);
      expect(r.hourAngleHours, -1.25);
      expect(r.alignErrorArcmin, 0.68);
      expect(r.alignEndedUtc, DateTime.utc(2026, 10, 8, 20, 21));
      expect(r.frames, 118);
    });

    test(
      'measuring and unavailable carry no figure; idle reads as nothing',
      () {
        final m = PaResidual.fromJson(const {
          'id': 'a',
          'status': 'measuring',
          'sample_seconds': 60.0,
          'target_seconds': 300.0,
          'frames': 24,
        })!;
        expect(m.status, PaResidualStatus.measuring);
        expect(m.sampleSeconds, 60);
        final u = PaResidual.fromJson(const {
          'id': 'b',
          'status': 'unavailable',
          'reason': 'lock_shift',
        })!;
        expect(u.reason, 'lock_shift');
        expect(PaResidual.reasonText(u.reason), contains('comet'));
        expect(PaResidual.fromJson(const {'status': 'idle'}), isNull);
        expect(PaResidual.fromJson(null), isNull);
        expect(
          PaResidual.fromJson(const {'id': 'c', 'status': 'done'}),
          isNull,
          reason: 'done needs its figure',
        );
      },
    );

    test('rides along on the guider status', () {
      final s = GuiderStatus.fromJson(const {
        'device_id': 'PHD2_Single',
        'name': 'PHD2',
        'state': 'connected',
        'runtime': {'state': 'guiding', 'pa_residual': done},
      });
      expect(s.paResidual?.paErrorMinArcmin, 0.8);
      expect(
        GuiderStatus.fromJson(const {
          'name': 'PHD2',
          'state': 'connected',
          'runtime': {'state': 'guiding'},
        }).paResidual,
        isNull,
      );
    });
  });

  test(
    'the tooltip says it is a lower bound and compares with Polar Align',
    () {
      final text = paResidualDetail(PaResidual.fromJson(done)!);
      expect(text, contains('at least 48″ (± 12″)'));
      expect(text, contains('Dec drift 0.21″/min at hour angle −1.3 h'));
      expect(text, contains('the total can be larger'));
      expect(text, contains('Polar Align measured 41″'));
      expect(text, isNot(contains('too noisy')));
      final noisy = PaResidual.fromJson({...done, 'reliable': false})!;
      expect(paResidualDetail(noisy), contains('too noisy'));
    },
  );
}
