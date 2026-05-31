#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;

namespace OpenAstroAra.Core.Utility.SerialCommunication {

    public class SerialPortProvider : ISerialPortProvider {
        /**
         * The WMI-based Arduino-Leonardo DTR-enable detection was Windows-only.
         * On the headless server target (linux-arm64) USB serial devices appear
         * as /dev/ttyACM* (Leonardo CDC) and /dev/ttyUSB* (FTDI etc.); the
         * Leonardo runs without the DTR-on-open quirk so the dtrEnableValue
         * lookup degenerates to the constructor-supplied `dtrEnable` flag —
         * which the caller can still flip per device.
         **/

        public SerialPortProvider() { }

        public ISerialPort GetSerialPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits,
            Handshake handShake, bool dtrEnable, string newLine, int readTimeout, int writeTimeout) {
            if (string.IsNullOrEmpty(portName)) return null;
            return new SerialPortWrapper {
                PortName = portName,
                BaudRate = baudRate,
                Parity = parity,
                DataBits = dataBits,
                StopBits = stopBits,
                Handshake = handShake,
                DtrEnable = dtrEnable,
                NewLine = newLine,
                ReadTimeout = readTimeout,
                WriteTimeout = writeTimeout
            };
        }

        public ReadOnlyCollection<string> GetPortNames(string deviceQuery = null, bool addDivider = true, bool addGenericPorts = true) {
            return new ReadOnlyCollection<string>(SerialPort.GetPortNames().OrderBy(s => s).ToList());
        }
    }
}