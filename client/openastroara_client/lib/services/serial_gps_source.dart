import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_libserialport/flutter_libserialport.dart';

/// §31 — where NMEA lines come from on the client computer. An interface so the
/// GPS sync logic is testable with a canned sentence stream, and so the serial
/// plugin (desktop-only FFI) never has to load in tests or on mobile.
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

/// The real thing: libserialport via flutter_libserialport. 9600-8N1 is the
/// NMEA 0183 default virtually every USB GPS dongle ships with (the daemon's
/// Pi-side reader uses the same).
class LibSerialPortGpsSource implements SerialGpsSource {
  const LibSerialPortGpsSource();

  @override
  bool get supported => Platform.isMacOS || Platform.isWindows || Platform.isLinux;

  @override
  List<String> availablePorts() {
    if (!supported) return const [];
    try {
      final ports = SerialPort.availablePorts;
      // Prefer the names a GPS dongle actually appears under; keep the rest so a
      // user with an odd adapter can still pick it.
      ports.sort((a, b) => _rank(a).compareTo(_rank(b)));
      return ports;
    } catch (_) {
      return const [];
    }
  }

  static int _rank(String p) {
    final l = p.toLowerCase();
    if (l.contains('usbserial') || l.contains('usbmodem') || l.contains('ttyusb') || l.contains('ttyacm')) {
      return 0;
    }
    if (l.startsWith('com')) return 1;
    return 2;
  }

  @override
  Stream<String> lines(String port) {
    late StreamController<String> controller;
    SerialPort? sp;
    SerialPortReader? reader;
    StreamSubscription<String>? sub;

    Future<void> stop() async {
      // Order matters: the reader isolate must be out of its read before the
      // port handle is closed and disposed under it.
      await sub?.cancel();
      reader?.close();
      if (sp != null && sp!.isOpen) sp!.close();
      sp?.dispose();
    }

    controller = StreamController<String>(
      onListen: () {
        try {
          sp = SerialPort(port);
          if (!sp!.openRead()) {
            controller.addError(StateError('could not open $port: ${SerialPort.lastError?.message ?? 'unknown error'}'));
            controller.close();
            return;
          }
          final cfg = sp!.config
            ..baudRate = 9600
            ..bits = 8
            ..parity = SerialPortParity.none
            ..stopBits = 1
            ..setFlowControl(SerialPortFlowControl.none);
          sp!.config = cfg;
          cfg.dispose(); // the port copies the config on assignment
          reader = SerialPortReader(sp!, timeout: 1000);
          sub = reader!.stream
              .map<List<int>>((chunk) => chunk)
              .transform(const Utf8Decoder(allowMalformed: true))
              .transform(const LineSplitter())
              .listen(controller.add, onError: controller.addError, onDone: controller.close);
        } catch (e) {
          controller.addError(e);
          controller.close();
        }
      },
      onCancel: stop,
    );
    return controller.stream;
  }
}
