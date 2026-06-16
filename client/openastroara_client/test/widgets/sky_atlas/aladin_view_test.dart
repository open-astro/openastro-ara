import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/fov_box.dart';
import 'package:openastroara/widgets/sky_atlas/aladin_view.dart';

void main() {
  group('fovBoxScript', () {
    test('a null box clears the overlay', () {
      expect(fovBoxScript(null), 'window.araClearFovBox && window.araClearFovBox();');
    });

    test('a box becomes a numeric araSetFovBox call (no injection surface)', () {
      expect(
        fovBoxScript(const FovBox(widthDeg: 1.346, heightDeg: 0.9, rotationDeg: 45)),
        'window.araSetFovBox && window.araSetFovBox(1.346000, 0.900000, 45.000000);',
      );
    });

    test('a negative rotation formats correctly', () {
      expect(
        fovBoxScript(const FovBox(widthDeg: 2, heightDeg: 1, rotationDeg: -30)),
        'window.araSetFovBox && window.araSetFovBox(2.000000, 1.000000, -30.000000);',
      );
    });
  });

  group('gotoScript', () {
    test('wraps a plain target as a single JSON-encoded argument', () {
      expect(gotoScript('M31'), 'window.araGoto && window.araGoto("M31");');
    });

    test('encodes coordinates verbatim', () {
      expect(
        gotoScript('10 41 04 +41 16 09'),
        'window.araGoto && window.araGoto("10 41 04 +41 16 09");',
      );
    });

    test('escapes quotes and backslashes so a target cannot break out of the string', () {
      // A target containing a double-quote must be escaped, not terminate the
      // JS string literal — otherwise it could inject arbitrary script.
      expect(gotoScript('M31"); alert(1)//'), r'window.araGoto && window.araGoto("M31\"); alert(1)//");');
    });

    test('escapes a lone backslash', () {
      expect(gotoScript(r'a\b'), r'window.araGoto && window.araGoto("a\\b");');
    });
  });
}
