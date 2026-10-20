import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/guider/guider_setup_wizard.dart';

/// One Alpaca server reachable on three interfaces (loopback, LAN, hotspot),
/// serving a guide camera, an imaging camera, a mount and a rotator — the
/// shape every AlpacaBridge SBC answers discovery with.
const _aliases = ['127.0.0.1:6800', '192.168.8.118:6800', '172.24.1.1:6800'];

Map<String, String> _names(List<String> endpoints) => {
  for (final ep in endpoints) ...{
    '$ep|camera/0': 'Guide Cam',
    '$ep|camera/1': 'Imaging Cam',
    '$ep|telescope/0': 'Mount',
    '$ep|rotator/0': 'Rotator',
  },
};

Map<String, String> _ids(List<String> endpoints) => {
  for (final ep in endpoints) ...{
    '$ep|camera/0': 'GUIDE-ID',
    '$ep|camera/1': 'IMAGING-ID',
    '$ep|telescope/0': 'MOUNT-ID',
    '$ep|rotator/0': 'ROTATOR-ID',
  },
};

void main() {
  group('mergedAlpacaOptions', () {
    test(
      'one row per device across interfaces, loopback for a local guider',
      () {
        final options = mergedAlpacaOptions(
          const ['Alpaca Camera', 'None'],
          'camera',
          'Camera',
          _names(_aliases),
          uniqueIds: _ids(_aliases),
          preferLoopback: true,
        );
        expect(options, [
          'None',
          'Alpaca Camera [127.0.0.1:6800/0]',
          'Alpaca Camera [127.0.0.1:6800/1]',
        ]);
      },
    );

    test('a remote guider gets a LAN address, never the daemon loopback', () {
      final options = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        _names(_aliases),
        uniqueIds: _ids(_aliases),
        preferLoopback: false,
      );
      expect(options, [
        'Alpaca Camera [192.168.8.118:6800/0]',
        'Alpaca Camera [192.168.8.118:6800/1]',
      ]);
    });

    test('the current pick keeps its own address', () {
      final options = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        _names(_aliases),
        uniqueIds: _ids(_aliases),
        preferLoopback: true,
        keep: 'Alpaca Camera [172.24.1.1:6800/0]',
      );
      expect(options, [
        'Alpaca Camera [172.24.1.1:6800/0]',
        'Alpaca Camera [127.0.0.1:6800/1]',
      ]);
    });

    test('devices without a UniqueID are never merged', () {
      final options = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        _names(_aliases.take(2).toList()),
        preferLoopback: true,
      );
      expect(options, hasLength(4));
    });

    test('two servers with the same camera model stay two rows', () {
      final names = {
        '10.0.0.5:11111|camera/0': 'QHY5III462C',
        '10.0.0.6:11111|camera/0': 'QHY5III462C',
      };
      final ids = {
        '10.0.0.5:11111|camera/0': 'QHY-A',
        '10.0.0.6:11111|camera/0': 'QHY-B',
      };
      final options = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        names,
        uniqueIds: ids,
      );
      expect(options, hasLength(2));
    });

    test('the bare generic choice stays when nothing real is listed, or when '
        'it is the current pick', () {
      expect(
        mergedAlpacaOptions(
          const ['Alpaca Camera', 'None'],
          'camera',
          'Camera',
          const {},
        ),
        ['Alpaca Camera', 'None'],
      );
      expect(
        mergedAlpacaOptions(
          const ['Alpaca Camera', 'None'],
          'camera',
          'Camera',
          _names(_aliases),
          uniqueIds: _ids(_aliases),
          keep: 'Alpaca Camera',
        ),
        contains('Alpaca Camera'),
      );
    });

    test('mount and rotator slots collapse the same way', () {
      for (final (type, word) in [
        ('telescope', 'Mount'),
        ('rotator', 'Rotator'),
      ]) {
        final options = mergedAlpacaOptions(
          ['Alpaca $word', 'None'],
          type,
          word,
          _names(_aliases),
          uniqueIds: _ids(_aliases),
          preferLoopback: true,
        );
        expect(options, [
          'None',
          'Alpaca $word [127.0.0.1:6800/0]',
        ], reason: type);
      }
    });
  });

  group('suggestedGuideCamera', () {
    final options = mergedAlpacaOptions(
      const ['None'],
      'camera',
      'Camera',
      _names(_aliases),
      uniqueIds: _ids(_aliases),
      preferLoopback: true,
    );

    test('the other camera on the imaging camera\'s server', () {
      expect(
        suggestedGuideCamera(options, _ids(_aliases), 'IMAGING-ID'),
        'Alpaca Camera [127.0.0.1:6800/0]',
      );
      // Whichever slot holds the imaging camera, the other one is suggested.
      expect(
        suggestedGuideCamera(options, _ids(_aliases), 'GUIDE-ID'),
        'Alpaca Camera [127.0.0.1:6800/1]',
      );
    });

    test('no suggestion without a known imaging camera', () {
      expect(suggestedGuideCamera(options, _ids(_aliases), null), isNull);
      expect(
        suggestedGuideCamera(options, _ids(_aliases), 'ELSEWHERE'),
        isNull,
      );
    });

    test('no suggestion when the server has two other cameras', () {
      const names = {
        'h:1|camera/0': 'A',
        'h:1|camera/1': 'B',
        'h:1|camera/2': 'C',
      };
      const ids = {
        'h:1|camera/0': 'A',
        'h:1|camera/1': 'B',
        'h:1|camera/2': 'C',
      };
      final opts = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        names,
        uniqueIds: ids,
      );
      expect(suggestedGuideCamera(opts, ids, 'A'), isNull);
    });

    test('a camera on another server is not suggested', () {
      const names = {'a:1|camera/0': 'Imaging', 'b:1|camera/0': 'Guide'};
      const ids = {'a:1|camera/0': 'I', 'b:1|camera/0': 'G'};
      final opts = mergedAlpacaOptions(
        const [],
        'camera',
        'Camera',
        names,
        uniqueIds: ids,
      );
      expect(suggestedGuideCamera(opts, ids, 'I'), isNull);
    });

    test('a rig with only the imaging camera gets no suggestion', () {
      const names = {'a:1|camera/0': 'Imaging'};
      const ids = {'a:1|camera/0': 'I'};
      final opts = mergedAlpacaOptions(
        const ['None'],
        'camera',
        'Camera',
        names,
        uniqueIds: ids,
      );
      expect(suggestedGuideCamera(opts, ids, 'I'), isNull);
    });
  });

  group('suggestedGuiderDevice', () {
    test('the equipment step\'s mount on the camera\'s server', () {
      final options = mergedAlpacaOptions(
        const ['None'],
        'telescope',
        'Mount',
        _names(_aliases),
        onlyEndpoint: '127.0.0.1:6800',
        uniqueIds: _ids(_aliases),
        preferLoopback: true,
      );
      expect(
        suggestedGuiderDevice(
          options,
          'telescope',
          _ids(_aliases),
          'MOUNT-ID',
          '127.0.0.1:6800',
        ),
        'Alpaca Mount [127.0.0.1:6800/0]',
      );
    });

    test(
      'nothing when the device is elsewhere, unknown, or no camera server',
      () {
        final options = mergedAlpacaOptions(
          const [],
          'telescope',
          'Mount',
          _names(_aliases),
          uniqueIds: _ids(_aliases),
          preferLoopback: true,
        );
        expect(
          suggestedGuiderDevice(
            options,
            'telescope',
            _ids(_aliases),
            'MOUNT-ID',
            'other:6800',
          ),
          isNull,
        );
        expect(
          suggestedGuiderDevice(
            options,
            'telescope',
            _ids(_aliases),
            null,
            '127.0.0.1:6800',
          ),
          isNull,
        );
        expect(
          suggestedGuiderDevice(
            options,
            'telescope',
            _ids(_aliases),
            'MOUNT-ID',
            null,
          ),
          isNull,
        );
      },
    );
  });

  test('isLoopbackHost', () {
    for (final h in [
      'localhost',
      'LOCALHOST',
      '127.0.0.1',
      '127.1.2.3',
      '::1',
      '',
    ]) {
      expect(isLoopbackHost(h), isTrue, reason: h);
    }
    for (final h in ['192.168.1.10', 'openastro.local', '10.0.0.1']) {
      expect(isLoopbackHost(h), isFalse, reason: h);
    }
  });
}
