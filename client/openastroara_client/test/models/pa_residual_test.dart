import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/pa_residual.dart';
import 'package:openastroara/widgets/imaging/polar_error_rating.dart';

final _en = lookupAppLocalizations(const Locale('en'));
final _pt = lookupAppLocalizations(const Locale('pt', 'BR'));

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
        expect(paResidualReasonText(_en, u.reason), contains('comet'));
        for (final (reason, word) in [
          ('guiding_not_holding', 'not holding'),
          ('settle_failed', 'settle'),
          ('mount_not_tracking', 'not tracking'),
        ]) {
          final text = paResidualReasonText(_en, reason);
          expect(text, contains(word), reason: reason);
          expect(
            text,
            isNot(paResidualReasonText(_en, null)),
            reason: '$reason is not the fallback',
          );
        }
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
      final text = paResidualDetail(_en, PaResidual.fromJson(done)!);
      expect(text, contains('at least 48″ (± 12″)'));
      expect(text, contains('Dec drift 0.21″/min at hour angle −1.3 h'));
      expect(text, contains('the total can be larger'));
      expect(text, contains("Polar Align's figure: 41″"));
      expect(text, isNot(contains('too noisy')));
      final noisy = PaResidual.fromJson({...done, 'reliable': false})!;
      expect(paResidualDetail(_en, noisy), contains('too noisy'));
    },
  );

  test('a residual far above Polar Align says so, not that guiding was noisy (#1731)', () {
    final far = PaResidual.fromJson({
      ...done,
      'pa_error_min_arcmin': 17.0,
      'uncertainty_arcmin': 0.5,
      'align_error_arcmin': 0.9,
      'reliable': false,
    })!;
    final text = paResidualDetail(_en, far);
    expect(text, isNot(contains('too noisy')));
    expect(text, contains('Polar Align and guiding disagree'));
  });

  test("the daemon's above_align decides a disagreement; without it the same rule stands in (#2072)", () {
    final far = {
      ...done,
      'pa_error_min_arcmin': 17.0,
      'uncertainty_arcmin': 0.5,
      'align_error_arcmin': 0.9,
      'reliable': false,
    };
    expect(PaResidual.fromJson(far)!.aboveAlign, isTrue, reason: 'an older daemon: 17 − 1 > 0.9 + 2');
    expect(PaResidual.fromJson({...far, 'above_align': false})!.aboveAlign, isFalse,
        reason: "the daemon's call wins over the copy of its rule");
    expect(PaResidual.fromJson({...done, 'above_align': true})!.aboveAlign, isTrue);
    expect(PaResidual.fromJson(done)!.aboveAlign, isFalse);
  });

  test("with Polar Align's own 1σ the copy of the rule uses their combined uncertainty (#2072)", () {
    // 2.8′ apart, combined 1σ 0.58′: they disagree, where the fixed 2′ margin said they did not.
    final close = {...done, 'pa_error_min_arcmin': 3.0, 'uncertainty_arcmin': 0.5, 'align_error_arcmin': 0.2, 'reliable': true};
    expect(PaResidual.fromJson({...close, 'align_sigma_arcmin': 0.3})!.aboveAlign, isTrue);
    expect(PaResidual.fromJson(close)!.aboveAlign, isFalse, reason: 'no σ on Align: the old margin');
    // A noisy alignment (± 2.5′) covers a 4.8′ gap the fixed margin would call a disagreement.
    final noisy = {...done, 'pa_error_min_arcmin': 5.0, 'uncertainty_arcmin': 0.5, 'align_error_arcmin': 0.2, 'reliable': false};
    expect(PaResidual.fromJson({...noisy, 'align_sigma_arcmin': 2.5})!.aboveAlign, isFalse);
    expect(PaResidual.fromJson(noisy)!.aboveAlign, isTrue);
  });

  test('a disagreement gives both figures with their 1σ when Polar Align has one, in English and pt-BR (#2072)', () {
    final far = PaResidual.fromJson({
      ...done,
      'pa_error_min_arcmin': 16.54,
      'uncertainty_arcmin': 1.17,
      'align_error_arcmin': 0.2,
      'align_sigma_arcmin': 1.1,
      'reliable': false,
      'above_align': true,
    })!;
    final en = paResidualDetail(_en, far);
    expect(en, contains('Polar Align and guiding disagree (12″ ± 66″ vs at least 17′ ± 70″), by more than their combined uncertainty'));
    expect(en, contains('Run Polar Align again'));
    final pt = paResidualDetail(_pt, far);
    expect(pt, contains('Polar Align e guiagem divergem (12″ ± 66″ contra pelo menos 17′ ± 70″)'));
  });

  test('a disagreement names both causes and what to do, in English and pt-BR (#2072)', () {
    final far = PaResidual.fromJson({
      ...done,
      'pa_error_min_arcmin': 16.54,
      'uncertainty_arcmin': 1.17,
      'align_error_arcmin': 0.2,
      'reliable': false,
      'above_align': true,
    })!;
    final en = paResidualDetail(_en, far);
    expect(en, contains('Polar Align and guiding disagree (12″ vs at least 17′)'));
    expect(en, contains('Dec backlash'));
    expect(en, contains('the mount shifted since the alignment or its reference was wrong'));
    expect(en, contains('Run Polar Align again'));
    expect(en, contains("Guiding's figure"));
    final pt = paResidualDetail(_pt, far);
    expect(pt, contains('Polar Align e guiagem divergem'));
    expect(pt, contains('folga em Dec'));
    expect(pt, isNot(contains('Dec backlash')));
  });
}
