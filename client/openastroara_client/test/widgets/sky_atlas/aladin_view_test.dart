import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/fov_box.dart';
import 'package:openastroara/widgets/sky_atlas/aladin_view.dart';

void main() {
  group('fovBoxScript', () {
    test('a null box clears the overlay', () {
      expect(fovBoxScript(null), 'window.araClearFovBox && window.araClearFovBox();');
    });

    test('a single box becomes a 1×1 numeric araSetFovBox call (no injection surface)', () {
      expect(
        fovBoxScript(const FovBox(widthDeg: 1.346, heightDeg: 0.9, rotationDeg: 45)),
        'window.araSetFovBox && window.araSetFovBox(1.346000, 0.900000, 45.000000, 1, 1, 0.000000);',
      );
    });

    test('a negative rotation formats correctly', () {
      expect(
        fovBoxScript(const FovBox(widthDeg: 2, heightDeg: 1, rotationDeg: -30)),
        'window.araSetFovBox && window.araSetFovBox(2.000000, 1.000000, -30.000000, 1, 1, 0.000000);',
      );
    });

    test('a mosaic passes cols/rows/overlap through', () {
      expect(
        fovBoxScript(const FovBox(
            widthDeg: 1, heightDeg: 1, rotationDeg: 0, cols: 2, rows: 3, overlapPct: 10)),
        'window.araSetFovBox && window.araSetFovBox(1.000000, 1.000000, 0.000000, 2, 3, 10.000000);',
      );
    });
  });

  group('inlineAladinJs (§36.1 offline bundling)', () {
    test('replaces the placeholder with the engine JS and drops the CDN src', () {
      final html = inlineAladinJs('/* ENGINE */');
      expect(html, contains('<script>/* ENGINE */</script>'));
      expect(html, isNot(contains('__ALADIN_LITE_JS__')));
      // The CDN script-src dependency is gone — the engine is bundled.
      expect(html, isNot(contains('aladin.cds.unistra.fr/AladinLite/api')));
    });

    test('passes \$-dense minified JS through verbatim (no interpolation)', () {
      // Minified Aladin is full of `$` identifiers and `${...}` template
      // literals; replaceFirst must insert them literally, not interpret them.
      const minified = r'const $a=1,b$=2;let s=`x${$a}y`;function $$(){return b$}';
      final html = inlineAladinJs(minified);
      expect(html, contains('<script>$minified</script>'));
    });

    test('inlines only once (single placeholder) even if JS contains the token', () {
      // A pathological engine string that itself mentions the placeholder must
      // not trigger a second substitution.
      final html = inlineAladinJs('x __ALADIN_LITE_JS__ y');
      // Exactly one inline <script>…</script> engine block.
      expect('<script>x __ALADIN_LITE_JS__ y</script>'.allMatches(html).length, 1);
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
