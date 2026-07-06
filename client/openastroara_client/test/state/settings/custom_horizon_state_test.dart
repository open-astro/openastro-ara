import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';

/// Overrides only the custom-horizon round-trip; every other ProfileApi member
/// keeps its real (never-called-here) implementation.
class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));

  List<CustomHorizonPoint> served = const [];
  List<CustomHorizonPoint>? putReceived;
  List<CustomHorizonPoint>? putEcho; // defaults to the received input

  @override
  Future<List<CustomHorizonPoint>> getCustomHorizon() async => served;

  @override
  Future<List<CustomHorizonPoint>> putCustomHorizon(
    List<CustomHorizonPoint> points,
  ) async {
    putReceived = points;
    return putEcho ?? points;
  }
}

void main() {
  group('CustomHorizonNotifier', () {
    late ProviderContainer container;

    setUp(() {
      container = ProviderContainer();
      addTearDown(container.dispose);
    });

    List<(double, double)> shape(List<CustomHorizonPoint> pts) => [
      for (final p in pts) (p.azimuthDeg, p.altitudeDeg),
    ];

    test('add/update keep row indices STABLE; remove drops the row', () {
      final n = container.read(customHorizonProvider.notifier);

      n.addPoint(180, 40);
      n.addPoint(10, 15);
      // NOT sorted while editing: the panel's editable rows bind to their
      // index, so reordering mid-edit would rebind a just-edited field to a
      // different vertex. Sorting happens on hydrate + the Save echo.
      expect(shape(container.read(customHorizonProvider)), [
        (180.0, 40.0),
        (10.0, 15.0),
      ]);

      // Editing an azimuth keeps the row where the user is editing it.
      n.updateAt(0, azimuthDeg: 270);
      expect(shape(container.read(customHorizonProvider)), [
        (270.0, 40.0),
        (10.0, 15.0),
      ]);

      n.removeAt(1);
      expect(shape(container.read(customHorizonProvider)), [(270.0, 40.0)]);

      // Out-of-range indices are ignored, not thrown.
      n.removeAt(5);
      n.updateAt(-1, altitudeDeg: 1);
      expect(container.read(customHorizonProvider), hasLength(1));
    });

    test(
      'hydrate sorts the served skyline; persist sends staged and adopts the echo',
      () async {
        final api = _FakeApi()
          ..served = const [
            CustomHorizonPoint(azimuthDeg: 350, altitudeDeg: 5),
            CustomHorizonPoint(azimuthDeg: 20, altitudeDeg: 30),
          ];
        final n = container.read(customHorizonProvider.notifier);

        await n.hydrateFromServer(api);
        expect(shape(container.read(customHorizonProvider)), [
          (20.0, 30.0),
          (350.0, 5.0),
        ]);

        n.addPoint(100, 12);
        // The daemon canonicalizes (here: it drops a vertex) — the echo wins.
        api.putEcho = const [
          CustomHorizonPoint(azimuthDeg: 20, altitudeDeg: 30),
        ];
        await n.persistToServer(api);

        expect(
          api.putReceived,
          hasLength(3),
          reason: 'staged skyline was sent',
        );
        expect(
          shape(container.read(customHorizonProvider)),
          [(20.0, 30.0)],
          reason: 'client adopts the daemon\'s canonical echo',
        );
      },
    );
  });
}
