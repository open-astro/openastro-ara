#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NmeaParser;
using NmeaParser.Messages;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Exceptions;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGPS {

    /// <summary>
    /// NMEA GPS Class detects comport based NMEA GPS Devices
    /// Flow : construct -> Autodiscover [detect, Connect, listens to messages]
    /// </summary>
    public sealed partial class NMEAGps() : BaseINPC, IGnss, IDisposable {
        private string portName = string.Empty;
        private int baudRate;
        private System.Timers.Timer? fixTimer;
        private SerialPortDevice? currentDevice;
        private const int sentenceWait = 4;
        private TaskCompletionSource<GpsResponse>? gotGPSFix;

        private static readonly int[] baudRates = [4800, 2400, 9600, 19200, 38400, 57600, 115200];

        public string Name => "NMEA Serial GNSS";

        public void Initialize() {
            if (connected) Disconnect();
            connected = false;
            baudRate = 0;
            portName = string.Empty;
        }

        private void OnFixTimedEvent(object? source, System.Timers.ElapsedEventArgs e) {
            Disconnect();
            // Runs on a ThreadPool thread from Timer.Elapsed — a throw here is swallowed and would
            // leave GetLocation awaiting gotGPSFix.Task forever. Fault the TCS instead so the awaiter
            // observes the no-fix timeout.
            gotGPSFix?.TrySetException(new GnssNoFixException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblGnssGgaMissingError"], sentenceWait)));
        }

        private bool connected;

        public bool Connected {
            get => connected;
            set {
                connected = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        ///Checks GPS messages, transfers location to the options view
        ///if a fix is obtained
        /// </summary>
        private void Device_MessageReceived(object? sender, NmeaMessageReceivedEventArgs args) {
            var message = args.Message;
            var gpsResponse = new GpsResponse();

            if (args.Message is Gga) {
                if (((Gga)message).Quality != Gga.FixQuality.Invalid &&
                    ((Gga)message).Quality != Gga.FixQuality.Simulation &&
                    ((Gga)message).Quality != Gga.FixQuality.ManualInput) {

                    gpsResponse.HasFix = true;
                    gpsResponse.FixQuality = ((Gga)message).Quality;
                    gpsResponse.Location = new Location {
                        Longitude = ((Gga)message).Longitude,
                        Latitude = ((Gga)message).Latitude,
                        Elevation = ((Gga)message).Altitude
                    };
                }
            } else return;

            Logger.Debug($"GGA sentence received. Lat: {((Gga)message).Latitude}, Long: {((Gga)message).Longitude}, Altitude: {((Gga)message).Altitude}, " +
                        $"Quality: {((Gga)message).Quality}, HDOP: {((Gga)message).Hdop}, Talker ID: {((Gga)message).TalkerId}");

            Disconnect();
            gotGPSFix?.TrySetResult(gpsResponse);
        }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Ownership of the SerialPort is transferred to the SerialPortDevice, which is stored in currentDevice and disposed in Disconnect()/Dispose(). Disposing the SerialPort here would close the port the device still uses.")]
        public async Task<Location> GetLocation(CancellationToken token) {
            if (!await AutoDiscover(token)) {
                throw new GnssNotFoundException($"{Name} was not found on any accessible COM port");
            }

            var gpsResponse = new GpsResponse();

            var device = new SerialPortDevice(new System.IO.Ports.SerialPort(portName, baudRate));
            currentDevice = device;
            device.MessageReceived += Device_MessageReceived;
            fixTimer = new System.Timers.Timer(TimeSpan.FromSeconds(sentenceWait));
            fixTimer.Elapsed += OnFixTimedEvent;
            fixTimer.AutoReset = false;
            fixTimer.Enabled = true;
            gotGPSFix = new TaskCompletionSource<GpsResponse>();
            connected = true;
            await device.OpenAsync();

            using (token.Register(() => gotGPSFix.TrySetCanceled())) {
                gpsResponse = await gotGPSFix.Task;
            }

            if (gpsResponse.HasFix) {
                return gpsResponse.Location!;
            } else {
                throw new GnssNoFixException(string.Format(CultureInfo.CurrentCulture, Loc.Instance["LblGnssGgaQualityError"], gpsResponse.FixQuality));
            }
        }

        public void Disconnect() {
            if (currentDevice != null && currentDevice.IsOpen) currentDevice.CloseAsync();

            try {
                if (currentDevice != null) { currentDevice.MessageReceived -= Device_MessageReceived; } // unsubscribe to avoid multiple messages
                if (fixTimer != null) { fixTimer.Enabled = false; fixTimer.Dispose(); }
                currentDevice?.Dispose();
            } catch (Exception ex) {
                Logger.Error(ex.Message);
                throw;
            }

            Connected = false;
        }

        private static string[] GetComPorts() {
            return System.IO.Ports.SerialPort.GetPortNames().OrderBy(s => s).ToArray();
        }

        /// <summary>
        /// this finds a suitable comport, connects and listens for GPS sentences
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Serial-port scan boundary: the loop probes every COM port and baud rate for an NMEA GNSS device. Expected faults (timeout, non-NMEA data, access denied, IO, cancellation) are handled specifically; the trailing general catch logs any other per-port failure and continues scanning so one bad port cannot abort discovery. CA1031 sanctions general catches at such recover-and-continue boundaries.")]
        private async Task<System.IO.Ports.SerialPort?> FindPort(CancellationToken token) {
            string[] allPorts = GetComPorts();
            int[][] portRates = new int[allPorts.Length][];

            Logger.Info($"Searching for {Name} on {string.Join(", ", allPorts)}");

            // set port / baud test precedence
            for (int pnum = 0; pnum < allPorts.Length; pnum++) {
                List<int> baudRatesToTest = new(baudRates);
                portRates[pnum] = new int[7];

                string cportName = allPorts[pnum];
                using var port = new System.IO.Ports.SerialPort(cportName);
                var defaultRate = port.BaudRate;

                baudRatesToTest.Remove(defaultRate);
                baudRatesToTest.Insert(0, defaultRate);

                for (int bnum = 0; bnum < baudRatesToTest.Count; bnum++)
                    portRates[pnum][bnum] = baudRatesToTest[bnum];
            }

            // use computed precedences to test the ports
            for (int bnum = 0; bnum < 7; bnum++)
                for (var pnum = 0; pnum < allPorts.Length; pnum++) {
                    token.ThrowIfCancellationRequested();
                    string cportName = allPorts[pnum];
                    int baud = portRates[pnum][bnum];

                    Logger.Debug($"Testing port {cportName} at {baud} baud");

                    using var port = new System.IO.Ports.SerialPort(cportName);

                    port.BaudRate = baud;
                    port.ReadTimeout = 2000;
                    port.NewLine = "\r\n";

                    try {
                        port.Open();
                        if (!port.IsOpen)
                            continue;
                        try {
                            while (true) {
                                token.ThrowIfCancellationRequested();
                                var output = port.ReadLine();

                                Logger.Debug($"Output: \"{output}\"");

                                var match = NmeaSentenceStartRegex().Match(output);
                                if (!match.Success) {
                                    // Not a NMEA sentence of any kind, so move on to the next port
                                    throw new InvalidDataException();
                                }

                                // It seems we have a NMEA 0183 device. Is this line a GGA sentence?
                                match = GnssGgaRegex().Match(output);
                                if (match.Success) {
                                    Logger.Info($"Found NMEA GGA sentence on {cportName} at {baud}; Talker ID: {match.Groups[1].Value}");
                                    return new System.IO.Ports.SerialPort(cportName, baud);
                                }
                            }
                        } catch (TimeoutException) {
                            Logger.Debug($"Timed out waiting for output on {cportName} at {baud}");
                            continue;
                        } catch (InvalidDataException) {
                            Logger.Debug($"Non-GNSS output on {cportName} at {baud}");
                            continue;
                        }
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (UnauthorizedAccessException ex) {
                        Logger.Debug(ex.Message);
                    } catch (IOException ex) {
                        Logger.Debug(ex.Message);
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    } finally {
                        try {
                            port.Close();
                        } catch { }
                    }

                }
            return null;
        }

        public void Dispose() {
            if (connected) Disconnect();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// discovers the first GPS device connected to a serial port
        /// </summary>
        private async Task<bool> AutoDiscover(CancellationToken token) {
            using System.IO.Ports.SerialPort? port = await FindPort(token);

            if (port != null) { //we found a port with a GPS
                portName = port.PortName;
                baudRate = port.BaudRate;
                return true;
            } else { // no GPS found
                portName = string.Empty;
                baudRate = 0;
                return false;
            }
        }

        internal sealed class GpsResponse {
            internal bool HasFix { get; set; }
            internal Gga.FixQuality FixQuality { get; set; } = Gga.FixQuality.Invalid;
            internal Location? Location { get; set; }
        }

        [GeneratedRegex(@"^[$!]G")]
        private static partial Regex NmeaSentenceStartRegex();

        [GeneratedRegex(@"^[$!](G.)GGA")]
        private static partial Regex GnssGgaRegex();
    }
}