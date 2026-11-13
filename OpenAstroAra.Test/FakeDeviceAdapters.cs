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
using System.Collections.Generic;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>ADR 0011 rule 4 — the test adapter a device lifecycle runs against instead of a port.
    /// <see cref="Answers"/> drives the §42.3 probe (<see cref="ProbePermits"/> gates its failures);
    /// <see cref="ReadFails"/> makes the essential read throw; <see cref="HoldReads"/> holds it.</summary>
    internal class FakeDeviceAdapter : IDeviceAdapter {
        private int _connects;
        public volatile bool Answers = true;
        public volatile bool ReadFails;
        // When set, the essential read throws this instead of the generic failure (ASCOM.NotImplementedException: the driver lacks the read).
        public volatile Exception? ReadThrows;
        // When set, the essential read waits for it: a refresh tick stays in flight.
        public ManualResetEventSlim? HoldReads;
        // When set, each failed essential read first waits for a permit, so a test lets exactly N of them through.
        public SemaphoreSlim? ReadPermits;
        // When set, each failed probe first waits for a permit, so a test lets exactly N of them through.
        public SemaphoreSlim? ProbePermits;
        private int _failedProbes;
        public int FailedProbes => Volatile.Read(ref _failedProbes);
        private int _failedReads;
        public int FailedReads => Volatile.Read(ref _failedReads);
        public int Connects => Volatile.Read(ref _connects);
        private int _disconnects;
        public int Disconnects => Volatile.Read(ref _disconnects);
        public bool Disposed { get; private set; }

        // When set, records "connect" / "disconnect" in call order, so a test can assert which came first.
        public Action<string>? Trace;

        public void Connect() { Trace?.Invoke("connect"); Interlocked.Increment(ref _connects); }
        public bool Connected {
            get {
                if (Answers) {
                    return true;
                }
                ProbePermits?.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref _failedProbes);
                return false;
            }
        }
        public virtual void Stop() { }
        public void Disconnect() { Trace?.Invoke("disconnect"); Interlocked.Increment(ref _disconnects); }
        public void Dispose() => Disposed = true;

        protected void ThrowIfReadFails() {
            HoldReads?.Wait(TimeSpan.FromSeconds(10));
            if (ReadThrows is { } thrown) {
                Interlocked.Increment(ref _failedReads);
                throw thrown;
            }
            if (ReadFails) {
                ReadPermits?.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Increment(ref _failedReads);
                throw new InvalidOperationException("CAA not responding");
            }
        }

        // The rest of a failing read pass: the same failure, without a second permit or count.
        protected void ThrowIfPassFails() {
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

    internal sealed class FakeFlatDevice : FakeDeviceAdapter, IFlatDeviceDevice {
        public CoverStatus CoverState { get { ThrowIfReadFails(); return CoverStatus.Closed; } }
        public CalibratorStatus CalibratorState => CalibratorStatus.Off;
        public int Brightness => 0;
        public int MaxBrightness => 255;
        public void OpenCover() { }
        public void CloseCover() { }
        public void CalibratorOn(int brightness) { }
        public void CalibratorOff() { }
    }

    internal sealed class FakeObservingConditionsDevice : FakeDeviceAdapter, IObservingConditionsDevice {
        // The first sensor is the gated read of a pass; the others fail with it.
        public double Temperature { get { ThrowIfReadFails(); return 12.5; } }
        public double Humidity { get { ThrowIfPassFails(); return 40; } }
        public double DewPoint { get { ThrowIfPassFails(); return 1; } }
        public double Pressure { get { ThrowIfPassFails(); return 1010; } }
        public double CloudCover { get { ThrowIfPassFails(); return 5; } }
        public double WindSpeed { get { ThrowIfPassFails(); return 2; } }
        public double WindGust { get { ThrowIfPassFails(); return 3; } }
        public double WindDirection { get { ThrowIfPassFails(); return 90; } }
        public double RainRate { get { ThrowIfPassFails(); return 0; } }
        public double SkyQuality { get { ThrowIfPassFails(); return 21; } }
        public double SkyTemperature { get { ThrowIfPassFails(); return -20; } }
    }

    internal sealed class FakeSwitchDevice : FakeDeviceAdapter, ISwitchDevice {
        public short MaxSwitch { get { ThrowIfReadFails(); return 2; } }
        public string GetSwitchName(short id) => $"Port {id}";
        public string GetSwitchDescription(short id) => $"Port {id} description";
        public double GetSwitchValue(short id) => 1;
        public double MinSwitchValue(short id) => 0;
        public double MaxSwitchValue(short id) => 1;
        public double SwitchStep(short id) => 1;
        public bool CanWrite(short id) => true;
        public void SetSwitchValue(short id, double value) { }
    }

    internal sealed class FakeSafetyMonitorDevice : FakeDeviceAdapter, ISafetyMonitorDevice {
        public volatile bool Safe = true;
        public bool IsSafe { get { ThrowIfReadFails(); return Safe; } }
    }

    internal sealed class FakeFilterWheelDevice : FakeDeviceAdapter, IFilterWheelDevice {
        public short Position { get { ThrowIfReadFails(); return 1; } set { } }
        public string[] Names => ["L", "R"];
        public int[] FocusOffsets => [0, 0];
    }

    internal sealed class FakeFocuserDevice : FakeDeviceAdapter, IFocuserDevice {
        public bool IsMoving => false;
        public int Position { get { ThrowIfReadFails(); return 500; } }
        public double Temperature => 8;
        public bool TempComp { get; set; }
        public int MaxStep => 1000;
        public double StepSize => 5;
        public bool TempCompAvailable => true;
        public bool Absolute => true;
        public void Move(int position) { }
        public void Halt() { }
    }
    /// <summary>ADR 0011 rule 4 — a mount for the lifecycle tests. <see cref="Latched"/> is AlpacaBridge's
    /// "Mount communications compromised": it keeps answering <c>Connected</c> while every read fails.
    /// <see cref="ReadFails"/> fails the position read the generic way.</summary>
    internal sealed class FakeTelescopeDevice : FakeDeviceAdapter, ITelescopeDevice {
        public const string LatchMessage = "Mount communications compromised";

        public volatile bool Latched;
        public volatile bool IsSlewing;
        private int _aborts;
        public int Aborts => Volatile.Read(ref _aborts);
        private int _moveAxisStops;
        public int MoveAxisStops => Volatile.Read(ref _moveAxisStops);
        public bool TrackingOn { get; set; } = true;

        private void ThrowIfLatched() {
            if (Latched) {
                throw new InvalidOperationException(LatchMessage);
            }
        }

        public bool Slewing {
            get {
                ThrowIfLatched();
                return IsSlewing;
            }
        }
        public double RightAscension { get { ThrowIfLatched(); ThrowIfReadFails(); return 5.0; } }
        public double Declination { get { ThrowIfLatched(); return 30.0; } }
        public double TargetRightAscension => throw new InvalidOperationException("no target set");
        public double TargetDeclination => throw new InvalidOperationException("no target set");
        public DriveRate TrackingRate { get => DriveRate.Sidereal; set { } }
        public bool Tracking { get { ThrowIfLatched(); return TrackingOn; } set => TrackingOn = value; }
        public bool AtPark { get { ThrowIfLatched(); return false; } }
        public bool AtHome { get { ThrowIfLatched(); return false; } }
        public bool IsPulseGuiding => false;
        public PointingState SideOfPier { get => PointingState.Normal; set { } }
        public EquatorialCoordinateType EquatorialSystem => EquatorialCoordinateType.Topocentric;
        public bool CanSlewAsync => true;
        public bool CanSlew => true;
        public bool CanSync => true;
        public bool CanPark => true;
        public bool CanUnpark => true;
        public bool CanSetTracking => true;
        public bool CanPulseGuide => true;
        public bool CanFindHome => true;
        public bool CanSetPierSide => false;
        public double FocalLength => 0.5;
        public double ApertureDiameter => 0.1;
        public ITrackingRates TrackingRates => new FakeTrackingRates();
        public IList<string> SupportedActions => new List<string>();
        public double SiteLatitude { get; set; }
        public double SiteLongitude { get; set; }
        public double SiteElevation { get; set; }
        public bool CanMoveAxis(TelescopeAxis axis) => true;
        public IAxisRates AxisRates(TelescopeAxis axis) => new FakeAxisRates();
        public string Action(string name, string parameters) => throw new ASCOM.NotImplementedException(name);
        public PointingState DestinationSideOfPier(double raHours, double decDegrees) => PointingState.Normal;
        public void MoveAxis(TelescopeAxis axis, double rate) { if (rate == 0) { Interlocked.Increment(ref _moveAxisStops); } }
        public override void Stop() => AbortSlew();
        public void AbortSlew() => Interlocked.Increment(ref _aborts);
        public void Park() { }
        public void Unpark() { }
        public void FindHome() { }
        public void SlewToCoordinatesAsync(double raHours, double decDegrees) { }
        public void SyncToCoordinates(double raHours, double decDegrees) { }

        private sealed class FakeTrackingRates : ITrackingRates {
            private readonly DriveRate[] _rates = [DriveRate.Sidereal];
            public int Count => _rates.Length;
            public DriveRate this[int index] => _rates[index];
            public System.Collections.IEnumerator GetEnumerator() => _rates.GetEnumerator();
            public void Dispose() { }
        }

        private sealed class FakeAxisRates : IAxisRates {
            private readonly IRate[] _rates = [new FakeRate(0.1, 4.0)];
            public int Count => _rates.Length;
            public IRate this[int index] => _rates[index];
            public System.Collections.IEnumerator GetEnumerator() => _rates.GetEnumerator();
            public void Dispose() { }
        }

        private sealed class FakeRate(double min, double max) : IRate {
            public double Minimum { get; set; } = min;
            public double Maximum { get; set; } = max;
            public void Dispose() { }
        }
    }
}
