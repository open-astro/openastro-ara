#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 rule 4 — the test adapter a device lifecycle runs against instead of a port.
    /// <see cref="Answers"/> drives the §42.3 probe; <see cref="ReadFails"/> makes the essential read throw;
    /// <see cref="HoldReads"/> holds it; <see cref="HoldConnect"/> holds the connect.</summary>
    internal class FakeDeviceAdapter : IDeviceAdapter {
        private int _connects;
        public volatile bool Answers = true;
        public volatile bool ReadFails;
        // When set, the essential read waits for it: a refresh tick stays in flight.
        public ManualResetEventSlim? HoldReads;
        // When set, Connect waits for it: a newer connect can supersede this one meanwhile.
        public ManualResetEventSlim? HoldConnect;
        public int Connects => Volatile.Read(ref _connects);
        public bool Disposed { get; private set; }

        public void Connect() {
            HoldConnect?.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref _connects);
        }
        public bool Connected => Answers;
        public void Stop() { }
        public void Disconnect() { }
        public void Dispose() => Disposed = true;

        protected void ThrowIfReadFails() {
            HoldReads?.Wait(TimeSpan.FromSeconds(10));
            if (ReadFails) {
                throw new InvalidOperationException("CAA not responding");
            }
        }
    }

    internal sealed class FakeRotatorDevice : FakeDeviceAdapter, IRotatorDevice {
        public bool IsMoving => false;
        public float MechanicalPosition => 10f;
        public float Position { get { ThrowIfReadFails(); return 10f; } }
        public bool Reverse { get; set; }
        public bool CanReverse => true;
        public float StepSize => 0.1f;
        public void MoveAbsolute(float position) { }
        public void MoveMechanical(float position) { }
        public void Sync(float position) { }
        public void Halt() { }
    }

    internal sealed class FakeDomeDevice : FakeDeviceAdapter, IDomeDevice {
        public bool Slewing { get { ThrowIfReadFails(); return false; } }
        public double Azimuth => 180;
        public ShutterState ShutterStatus => ShutterState.Closed;
        public bool AtHome => false;
        public bool AtPark => true;
        public bool CanSetShutter => true;
        public bool CanSetAzimuth => true;
        public bool CanSyncAzimuth => true;
        public bool CanPark => true;
        public bool CanFindHome => true;
        public bool CanSetPark => true;
        public void SlewToAzimuth(double azimuth) { }
        public void SyncToAzimuth(double azimuth) { }
        public void Park() { }
        public void SetPark() { }
        public void FindHome() { }
        public void OpenShutter() { }
        public void CloseShutter() { }
        public void AbortSlew() { }
    }
}
