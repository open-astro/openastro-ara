import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// §31 — where NMEA lines come from on the client computer. An interface so the
/// GPS sync logic is testable with a canned sentence stream.
abstract interface class SerialGpsSource {
  /// True when this platform can read a USB serial GPS at all (desktop).
  bool get supported;

  /// Serial ports currently present, as the OS names them
  /// (`/dev/cu.usbserial-…`, `COM3`, `/dev/ttyUSB0`).
  List<String> availablePorts();

  /// NMEA lines from [port] at 9600-8N1 until the subscription is cancelled.
  /// Errors (port busy, unplugged mid-read) surface on the stream.
  Stream<String> lines(String port);
}

/// The real thing, with no native code of our own and no third-party serial
/// library: the OS configures the port (`stty` on macOS/Linux, `mode` on
/// Windows) and dart:io streams the device file. Read-only NMEA at 9600-8N1
/// — the default virtually every USB GPS dongle ships with, and what the
/// daemon's Pi-side reader uses — needs nothing more. Keeps the app free of
/// LGPL serial bindings and of a CocoaPods/CMake native build.
class OsSerialGpsSource implements SerialGpsSource {
  const OsSerialGpsSource();

  @override
  bool get supported => Platform.isMacOS || Platform.isWindows || Platform.isLinux;

  @override
  List<String> availablePorts() {
    if (!supported) return const [];
    try {
      if (Platform.isWindows) return _windowsPorts();
      final names = Directory('/dev')
          .listSync(followLinks: false)
          .map((e) => e.path)
          .where(_isCandidatePosixPort)
          .toList()
        ..sort((a, b) => _rank(a).compareTo(_rank(b)) != 0 ? _rank(a).compareTo(_rank(b)) : a.compareTo(b));
      return names;
    } catch (_) {
      return const [];
    }
  }

  static bool _isCandidatePosixPort(String p) {
    final n = p.split('/').last;
    if (Platform.isMacOS) {
      // cu.* are the call-out devices (open without waiting for carrier); tty.* would block.
      return n.startsWith('cu.') && !n.startsWith('cu.Bluetooth') && !n.startsWith('cu.debug');
    }
    return n.startsWith('ttyUSB') || n.startsWith('ttyACM');
  }

  static int _rank(String p) {
    final l = p.toLowerCase();
    if (l.contains('usbserial') || l.contains('usbmodem') || l.contains('ttyusb') || l.contains('ttyacm')) return 0;
    return 1;
  }

  // `mode` with no arguments lists every device it can address; COM ports show
  // as "Status for device COMn:". Cheap and needs no registry access.
  static List<String> _windowsPorts() {
    final r = Process.runSync('mode', const [], runInShell: true);
    final out = '${r.stdout}';
    final ports = RegExp(r'Status for device (COM\d+):').allMatches(out).map((m) => m.group(1)!).toSet().toList()..sort();
    return ports;
  }

  @override
  Stream<String> lines(String port) {
    late StreamController<String> controller;
    StreamSubscription<String>? sub;
    controller = StreamController<String>(
      onListen: () async {
        try {
          await _configure(port);
          final path = Platform.isWindows ? r'\\.\' + port : port;
          sub = File(path)
              .openRead()
              .transform(const Utf8Decoder(allowMalformed: true))
              .transform(const LineSplitter())
              .listen(controller.add, onError: controller.addError, onDone: controller.close);
        } catch (e) {
          controller.addError(StateError('could not open $port: $e'));
          await controller.close();
        }
      },
      onCancel: () async {
        await sub?.cancel(); // closes the device file
      },
    );
    return controller.stream;
  }

  /// 9600-8N1, raw, no echo, no flow control, and (POSIX) `clocal` so the
  /// open never waits for a carrier line a GPS dongle does not drive.
  static Future<void> _configure(String port) async {
    final ProcessResult r;
    if (Platform.isWindows) {
      r = await Process.run('mode', ['$port:', 'BAUD=9600', 'PARITY=n', 'DATA=8', 'STOP=1', 'to=off', 'xon=off', 'octs=off', 'rts=off', 'dtr=on'], runInShell: true);
    } else if (Platform.isMacOS) {
      r = await Process.run('stty', ['-f', port, '9600', 'raw', '-echo', 'clocal', 'cs8', '-parenb', '-cstopb', '-crtscts']);
    } else {
      r = await Process.run('stty', ['-F', port, '9600', 'raw', '-echo', 'clocal', 'cs8', '-parenb', '-cstopb', '-crtscts']);
    }
    if (r.exitCode != 0) {
      throw StateError('${'${r.stderr}'.trim().isEmpty ? r.stdout : r.stderr}'.trim());
    }
  }
}
