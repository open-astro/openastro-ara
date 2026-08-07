import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/frame_naming.dart';

void main() {
  group('the builder recognizes its own output', () {
    test('every folder scheme and part combination round-trips', () {
      for (final folders in FolderScheme.values) {
        for (var mask = 0; mask < (1 << NamePart.values.length); mask++) {
          final parts = <NamePart>{
            for (var i = 0; i < NamePart.values.length; i++)
              if (mask & (1 << i) != 0) NamePart.values[i],
          };
          final model = FrameNamingModel(folders: folders, parts: parts);
          final parsed = FrameNamingModel.tryParse(model.compile());
          expect(parsed, isNotNull, reason: model.compile());
          expect(parsed!.folders, folders);
          expect(parsed.parts, parts);
        }
      }
    });

    test('the NINA-inherited default reads as the standard scheme', () {
      final parsed = FrameNamingModel.tryParse(
          r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s');
      expect(parsed, isNotNull,
          reason: 'existing profiles must open in the builder, not custom mode');
      expect(parsed!.folders, FolderScheme.nightAndType);
      expect(parsed.parts, {NamePart.filter, NamePart.exposure});
    });

    test('a hand-written template stays custom, untouched', () {
      expect(FrameNamingModel.tryParse(r'$$MJD$$_$$SQM$$_special'), isNull);
    });
  });

  group('the preview names tonight the way the server will', () {
    final captured = DateTime(2026, 8, 4, 1, 30, 5);

    test('the standard scheme reads as a project folder with nights inside',
        () {
      // Default is by-object: {target}/{night}/{type}. Calibration frames
      // (no target) drop the leading segment and group as <night>/<type>.
      final segments = previewSegments(const FrameNamingModel().compile(),
          NamingPreviewContext(captured: captured));
      expect(segments, [
        'M 31',
        '2026-08-03',
        'Light',
        '2026-08-04_01-30-05_L_180s.fits',
      ]);
    });

    test('folders by target put the project first', () {
      final segments = previewSegments(
          const FrameNamingModel(folders: FolderScheme.targetNightType)
              .compile(),
          NamingPreviewContext(captured: captured));
      expect(segments.first, 'M 31');
      expect(segments[1], '2026-08-03');
    });

    test('everything on shows temperature with its sign and padded frame nr',
        () {
      final segments = previewSegments(
          FrameNamingModel(parts: NamePart.values.toSet()).compile(),
          NamingPreviewContext(captured: captured));
      expect(segments.last,
          '2026-08-04_01-30-05_M 31_L_180s_-10C_g100_0042.fits');
    });

    test('no folders means one flat segment', () {
      final segments = previewSegments(
          const FrameNamingModel(folders: FolderScheme.none).compile(),
          NamingPreviewContext(captured: captured));
      expect(segments, hasLength(1));
    });

    test('an unknown token vanishes instead of leaking', () {
      final segments = previewSegments(
          r'$$HOCUSPOCUS$$_$$FILTER$$', NamingPreviewContext(captured: captured));
      expect(segments.single, 'L.fits');
    });
  });
}
