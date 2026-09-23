import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// §31 — where NMEA lines come from on the client computer. An interface so the
/// GPS sync logic is testable with a canned sentence stream.
abstract interface class SerialGpsSource {
  /// True when this platform can read a USB serial GPS at all (desktop).
  bool get supported;

  /// Serial ports currently present, as the OS names them
  /// (`/dev/cu.usbserial-…`, `COM3`, `/dev/ttyUSB0`). Async: on Windows this
  /// spawns `mode`, which must not block the UI isolate.
  Future<List<String>> availablePorts();

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
  Future<List<String>> availablePorts() async {
    if (!supported) return const [];
    try {
      if (Platform.isWindows) return await _windowsPorts();
      final names = await Directory('/dev')
          .list(followLinks: false)
          .map((e) => e.path)
          .where(_isCandidatePosixPort)
          .toList();
      names.sort((a, b) => _rank(a) != _rank(b) ? _rank(a).compareTo(_rank(b)) : a.compareTo(b));
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

  // `mode` with no arguments lists every device it can address. Its wording is
  // localised ("Status for device COM3:", "Status für Gerät COM3:", …), so only
  // the COMn token is matched. Cheap and needs no registry access.
  static Future<List<String>> _windowsPorts() async {
    final r = await Process.run('mode', const [], runInShell: true);
    final ports = parseWindowsModeOutput('${r.stdout}');
    return ports;
  }

  /// Pure — unit-testable: every distinct `COMn` mentioned in `mode`'s output,
  /// in numeric order, whatever language the headings are in.
  static List<String> parseWindowsModeOutput(String out) {
    final found = RegExp(r'\b(COM\d+)\b', caseSensitive: false)
        .allMatches(out)
        .map((m) => m.group(1)!.toUpperCase())
        .toSet()
        .toList();
    found.sort((a, b) => int.parse(a.substring(3)).compareTo(int.parse(b.substring(3))));
    return found;
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

  /// How long the POSIX read waits for bytes before returning empty (VTIME, in
  /// tenths of a second). A GPS dongle never pauses this long between sentence
  /// bursts, so this only ends reads on a SILENT port — which matters because
  /// dart:io cannot cancel a blocking read: without it every read of a silent or
  /// wrong port would leak a file descriptor and pin an I/O worker for good.
  /// A read that returns empty reads as end-of-stream, and `lines()` closes.
  static const int _posixReadTimeoutTenths = 20;

  /// 9600-8N1, raw, no echo, no flow control, (POSIX) `clocal` so the open
  /// never waits for a carrier line a GPS dongle does not drive, and the read
  /// timeout above (`min 0 time 20`, after `raw`, which would otherwise pin
  /// `min 1`). Windows: `mode` cannot set ReadFile timeouts, so a silent COM
  /// port is held until the dongle speaks or the app exits.
  static Future<void> _configure(String port) async {
    final ProcessResult r;
    if (Platform.isWindows) {
      r = await Process.run('mode', ['$port:', 'BAUD=9600', 'PARITY=n', 'DATA=8', 'STOP=1', 'to=off', 'xon=off', 'octs=off', 'rts=off', 'dtr=on'], runInShell: true);
    } else if (Platform.isMacOS) {
      r = await Process.run('stty', ['-f', port, '9600', 'raw', '-echo', 'clocal', 'cs8', '-parenb', '-cstopb', '-crtscts', 'min', '0', 'time', '$_posixReadTimeoutTenths']);
    } else {
      r = await Process.run('stty', ['-F', port, '9600', 'raw', '-echo', 'clocal', 'cs8', '-parenb', '-cstopb', '-crtscts', 'min', '0', 'time', '$_posixReadTimeoutTenths']);
    }
    if (r.exitCode != 0) {
      throw StateError('${'${r.stderr}'.trim().isEmpty ? r.stdout : r.stderr}'.trim());
    }
  }
}
