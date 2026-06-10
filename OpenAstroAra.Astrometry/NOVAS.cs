#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Astrometry {
    /* https://aa.usno.navy.mil/software/novas/novas_c/novasc_info.php
     * https://aa.usno.navy.mil/software/novas/novas_c/NOVAS_C3.1_Guide.pdf
     */

    public static class NOVAS {
        private const string DLLNAME = "NOVAS31lib.dll";

        private static double JPL_EPHEM_START_DATE = 2305424.5; // First date of data in the ephemerides file
        private static double JPL_EPHEM_END_DATE = 2525008.5; // Last date of data in the ephemerides file
        private static readonly Lazy<NOVAS.CatalogueEntry> dummy_star = new Lazy<NOVAS.CatalogueEntry>(() => {
            var result = NOVAS_make_cat_entry("DUMMY", "xxx", 0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, out var output);
            if (result != 0) {
                throw new InvalidOperationException($"Failed to create dummy star cat entry. Result={result}");
            }
            return output;
        });

        public static readonly string EphemerisLocation = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "External", "JPLEPH");

        static NOVAS() {
            AstrometryNativeResolver.Register(); // map the Windows DLL name to libnovas31 off-Windows
            DllLoader.LoadDll(Path.Combine("NOVAS", DLLNAME));

            short a = 0;
            if (File.Exists(EphemerisLocation)) {
                var code = EphemOpen(EphemerisLocation, ref JPL_EPHEM_START_DATE, ref JPL_EPHEM_END_DATE, ref a);
                if (code > 0) {
                    Logger.Error($"Failed to load ephemerides file due to error {code}");
                }
            } else {
                Logger.Error($"Ephemeris file not found at {EphemerisLocation}");
            }

        }

        #region "Public Methods"

        public static short Shutdown() {
            return EphemClose();
        }

        internal static short SiderealTime(double jdHigh, double jdLow, double deltaT, GstType gstType, Method method, Accuracy accuracy, ref double gst) {
            return NOVAS_SiderealTime(jdHigh, jdLow, deltaT, gstType, method, accuracy, ref gst);
        }

        public static double JulianDate(short year, short month, short day, double hour) {
            return NOVAS_JulianDate(year, month, day, hour);
        }

        public static DateTime JulianToDateTime(double jdtt) {
            return SOFA.J2000 + TimeSpan.FromDays(jdtt - SOFA.J2000JulianDate);
        }

        public static double CalDate(double jtd, ref short year, ref short month, ref short day, ref double hour) {
            return NOVAS_CalDate(jtd, ref year, ref month, ref day, ref hour);
        }

        /// <summary>
        /// Computes atmospheric refraction in zenith distance
        /// </summary>
        /// <param name="location"></param>
        /// <param name="refractionOption"></param>
        /// <param name="zdObs"></param>
        /// <returns></returns>
        internal static double Refract(ref OnSurface location, RefractionOption refractionOption, double zdObs) {
            return NOVAS_Refract(ref location, refractionOption, zdObs);
        }

        /// <summary>
        /// This function computes the apparent direction of a star or solar
        /// system body at a specified time and in a specified coordinate system
        /// </summary>
        /// <param name="jdTt"></param>
        /// <param name="celestialObject"></param>
        /// <param name="observer"></param>
        /// <param name="deltaT"></param>
        /// <param name="coordinateSystem"></param>
        /// <param name="accuracy"></param>
        /// <param name="position"></param>
        /// <returns></returns>
        internal static short Place(double jdTt, CelestialObject celestialObject, Observer observer, double deltaT, CoordinateSystem coordinateSystem, Accuracy accuracy, ref SkyPosition position) {
            lock (lockObj) {
                var err = NOVAS_Place(jdTt, ref celestialObject, ref observer, deltaT, (short)coordinateSystem, (short)accuracy, ref position);
                return err;
            }
        }

        /// <summary>
        /// This function gets apparent coordinates for the given solar system body
        /// </summary>
        /// <param name="jd_tt">Julian date</param>
        /// <param name="body">Solar system body</param>
        /// <param name="accuracy">Requested level of accuracy. Full by default</param>
        /// <returns>Apparent equatorial coordinates from an earth-based geocentric observer</returns>
        internal static Coordinates PlanetApparentCoordinates(double jdTt, Body body, Accuracy accuracy = Accuracy.Full) {
            var result = NOVAS_make_object(ObjectType.MajorPlanetSunOrMoon, (short)body, body.ToString(), dummy_star.Value, out var celestialObject);
            if (result != 0) {
                throw new InvalidOperationException($"Failed MakeObject for {body}. Result={result}");
            }

            result = NOVAS_app_planet(jdTt, celestialObject, accuracy, out var ra, out var dec, out var _);
            if (result != 0) {
                throw new InvalidOperationException($"Failed AppPlanet for {body}. Result={result}");
            }

            var referenceDateTime = JulianToDateTime(jdTt);
            return new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.JNOW, referenceDateTime);
        }

        /// <summary>
        /// This function gets the position and velocity of a solar system body represented as rectangular (cartesian) coordinates
        /// </summary>
        /// <param name="jd_tt">Julian date</param>
        /// <param name="body">Solar system body</param>
        /// <param name="origin">Origin reference</param>
        /// <returns>Rectangular position and velocity vectors</returns>
        internal static RectangularPV BodyPositionAndVelocity(double jdtt, Body body, SolarSystemOrigin origin) {
            var jd = new double[] { jdtt, 0 };
            var position = new double[3];
            var velocity = new double[3];
            var result = NOVAS_solarsystem_hp(jd, body, origin, position, velocity);
            if (result != 0) {
                throw new InvalidOperationException($"SolarSystemBodyPV failed for {body} with origin {origin}. Result={result}");
            }

            return new RectangularPV(
                new RectangularCoordinates(position[0], position[1], position[2]),
                new RectangularCoordinates(velocity[0], velocity[1], velocity[2]));
        }

        public static RectangularPV GetPositionOnEarthSurface(double jdtt, double deltaT, Angle latitude, Angle longitude, double elevation) {
            var observer = new NOVAS.Observer() {
                Where = 1,
                OnSurf = new NOVAS.OnSurface() {
                    Latitude = latitude.Degree,
                    Longitude = longitude.Degree,
                    Height = elevation
                }
            };

            var pos = new double[3];
            var vel = new double[3];
            var result = NOVAS_geo_posvel(jdtt, deltaT, NOVAS.Accuracy.Full, observer, pos, vel);
            if (result != 0) {
                throw new InvalidOperationException($"NOVAS geo_posvel failed. Result={result}");
            }
            return new RectangularPV(
                new RectangularCoordinates(pos[0], pos[1], pos[2]),
                new RectangularCoordinates(vel[0], vel[1], vel[2]));
        }

        private static readonly object lockObj = new object();

        #endregion "Public Methods"

        #region "External DLL calls"

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "cal_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern double NOVAS_CalDate(double tjd, ref short year, ref short month, ref short day, ref double hour);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "julian_date", CallingConvention = CallingConvention.Cdecl)]
        private static extern double NOVAS_JulianDate(short year, short month, short day, double hour);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "sidereal_time", CallingConvention = CallingConvention.Cdecl)]
        private static extern short NOVAS_SiderealTime(double jdHigh, double jdLow, double detlaT, GstType gstType, Method method, Accuracy accuracy, ref double gst);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "place", CallingConvention = CallingConvention.Cdecl)]
        private static extern short NOVAS_Place(double jdTt, ref CelestialObject celObject, ref Observer observer, double deltaT, short coordinateSystem, short accuracy, ref SkyPosition position);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "set_racio_file", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern void SetRACIOFile([MarshalAs(UnmanagedType.LPStr)] string Name);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "ephem_open", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern short EphemOpen([MarshalAs(UnmanagedType.LPStr)] string Ephem_Name, ref double JD_Begin, ref double JD_End, ref short DENumber);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "ephem_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern short EphemClose();

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "refract", CallingConvention = CallingConvention.Cdecl)]
        private static extern double NOVAS_Refract(ref OnSurface location, RefractionOption refractionOption, double zdObs);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "solarsystem_hp", CallingConvention = CallingConvention.Cdecl)]
        private static extern short NOVAS_solarsystem_hp(
            [In][MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] double[] tjd,
            Body body,
            SolarSystemOrigin origin,
            [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] position,
            [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] velocity);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "make_cat_entry", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern short NOVAS_make_cat_entry(
            [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_OBJ_NAME)] string star_name,
            [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_CAT_NAME)] string catalog,
            long star_num,
            double ra,
            double dec,
            double pm_ra,
            double pm_dec,
            double parallax,
            double rad_vel,
            [Out] out CatalogueEntry star);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "make_object", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern short NOVAS_make_object(
            ObjectType type,
            short number,
            [MarshalAs(UnmanagedType.LPTStr, SizeConst = SIZE_OF_OBJ_NAME)] string name,
            CatalogueEntry star_data,
            [Out] out CelestialObject cel_obj);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "app_planet", CallingConvention = CallingConvention.Cdecl)]
        private static extern short NOVAS_app_planet(
            double jd_tt,
            CelestialObject ss_body,
            Accuracy accuracy,
            [Out] out double ra,
            [Out] out double dec,
            [Out] out double dis);

        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        [DllImport(DLLNAME, EntryPoint = "geo_posvel", CallingConvention = CallingConvention.Cdecl)]
        private static extern short NOVAS_geo_posvel(
            double jdtt, double deltaT, NOVAS.Accuracy accuracy, NOVAS.Observer observer,
            [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] pos,
            [In, Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] double[] vel);


        #endregion "External DLL calls"

        #region "NOVAS Structs"

        // These are P/Invoke marshaling structs that mirror the NOVAS C 3.1 ABI byte-for-byte.
        // They are internal (not part of the public API) so the design rules for externally
        // visible types (CA1034 nested, CA1815/CA1066 value-equality, CA1051 public fields) do
        // not apply — the only one that crosses the assembly boundary, SkyPosition, is a
        // top-level public type below.

        private const int SIZE_OF_OBJ_NAME = 51;
        private const int SIZE_OF_CAT_NAME = 4;

        /// <summary>
        /// Represents "cat_entry". contains the astrometric catalog data for a celestial object; equator and
        /// equinox and units will depend on the catalog.While this structure can be used as a generic
        /// container for catalog data, all high-level NOVAS functions require ICRS catalog data with
        /// the appropriate units
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct CatalogueEntry {

            [MarshalAsAttribute(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_OBJ_NAME)]
            public string StarName;

            /// <summary>
            /// catalog designator (e.g., HIP)
            /// </summary>
            [MarshalAsAttribute(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_CAT_NAME)]
            public string Catalog;

            /// <summary>
            /// integer identifier assigned to object
            /// </summary>
            public int StarNumber;

            public double RA;
            public double Dec;

            /// <summary>
            /// ICRS proper motion in right ascension (milliarcseconds/year)
            /// </summary>
            public double ProMoRA;

            /// <summary>
            /// ICRS proper motion in declination (milliarcseconds/year)
            /// </summary>
            public double ProMoDec;

            /// <summary>
            /// parallax (milliarcseconds)
            /// </summary>
            public double Parallax;

            /// <summary>
            /// radial velocity (km/s)
            /// </summary>
            public double RadialVelocity;
        }

        /// <summary>
        /// Represents "object".
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct CelestialObject {

            /// <summary>
            /// = type of object
            /// = 0 ... major planet, Pluto, Sun, or Moon
            /// = 1 ... minor planet
            /// = 2... object located outside the solar system
            /// (star, nebula, galaxy, etc.)
            /// </summary>
            public short Type;

            /// <summary>
            ///  object number
            ///  For 'type' = 0: Mercury = 1, ..., Pluto = 9,
            ///  Sun = 10, Moon = 11
            ///  For 'type' = 1: minor planet number
            ///  For 'type' = 2: set to 0 (object is
            ///  fully specified in 'struct cat_entry')
            /// </summary>
            public short Number;

            [MarshalAsAttribute(UnmanagedType.ByValTStr, SizeConst = SIZE_OF_OBJ_NAME)]
            public string Name;

            /// <summary>
            /// basic astrometric data for any celestial object located outside the solar system; the catalog data for a star
            /// </summary>
            public CatalogueEntry Star;
        }

        /// <summary>
        /// Represents "on_surface". Contains data for the observer’s location on the surface of the Earth.
        /// The atmospheric parameters(temperature and pressure) are used only by the refraction
        /// function(refract) called from function equ2hor when ref_option = 2; dummy values can
        /// be used otherwise
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct OnSurface {
            public double Latitude;
            public double Longitude;
            public double Height;
            public double Temperature;
            public double Pressure;
        }

        /// <summary>
        /// Represents "in_space"
        /// Both vectors with respect to true equator and equinox of date
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct InSpace {

            /// <summary>
            /// geocentric position vector (x, y, z), components in km
            /// </summary>
            [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
            public double[] ScPos;

            /// <summary>
            /// geocentric velocity vector (x_dot, y_dot,z_dot), components in km/s
            /// </summary>
            [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
            public double[] ScVel;
        }

        /// <summary>
        /// Represents "observer". It is a general container for information specifying the location of the observer
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct Observer {

            /// <summary>
            /// integer code specifying location of observer
            /// = 0: observer at geocenter
            /// = 1: observer on surface of earth
            /// = 2: observer on near-earth spacecraft
            /// </summary>
            public short Where;

            /// <summary>
            /// structure containing data for an observer's location on the surface of the Earth(where = 1)
            /// </summary>
            public OnSurface OnSurf;

            /// <summary>
            /// data for an observer's location on a near-Earth spacecraft(where = 2)
            /// </summary>
            public InSpace NearEarth;
        }

        #endregion "NOVAS Structs"

        #region "NOVAS helper enums"

        // These enums mirror the NOVAS C ABI: Body/GstType are deliberately backed by short to
        // match the native parameter width. They are internal, so the externally-visible-only
        // design rules (CA1028 Int32 storage, CA1008 zero value, CA1724 name vs namespace) do
        // not apply.

        internal enum ObjectType  {
            MajorPlanetSunOrMoon = 0,
            MinorPlanet = 1,
            ObjectLocatedOutsideSolarSystem = 2
        }

        internal enum Body : short {
            Mercury = 1,
            Venus = 2,
            Earth = 3,
            Mars = 4,
            Jupiter = 5,
            Saturn = 6,
            Uranus = 7,
            Neptune = 8,
            Pluto = 9,
            Sun = 10,
            Moon = 11
        }

        internal enum CoordinateSystem  {
            GCRS = 0,
            EquinoxOfDate = 1,
            CIOOfDate = 2,
            Astrometric = 3
        }

        internal enum ObserverLocation  {
            EarthGeoCenter = 0,
            EarthSurface = 1,
            SpaceNearEarth = 2
        }

        internal enum GstType : short {
            GreenwichMeanSiderealTime = 0,
            GreenwichApparentSiderealTime = 1
        }

        internal enum Method  {
            CIOBased = 0,
            EquinoxBased = 1
        }

        internal enum Accuracy  {
            Full = 0,
            Reduced = 1
        }

        internal enum RefractionOption : int {
            NoRefraction = 0,
            StandardRefraction = 1,
            LocationRefraction = 2
        }

        internal enum SolarSystemOrigin  {
            Barycenter = 0,
            SolarCenterOfMass = 1
        }


        #endregion "NOVAS helper enums"
    }

    /// <summary>
    /// Represents sky_pos. Contains data specifying a celestial object's place on the sky,
    /// specifically the output from the NOVAS <c>place</c> function. This is the one NOVAS
    /// marshaling struct that crosses the assembly boundary (returned by AstroUtil), so it is
    /// public and top-level. It is a P/Invoke marshaling type that mirrors the native ABI:
    /// the marshaler requires direct field access (CA1051), and instances are never compared
    /// (CA1815/CA1066), so those externally-visible-type design rules are suppressed per the
    /// Microsoft guidance for each.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
        Justification = "P/Invoke marshaling struct: the native marshaler requires direct [MarshalAs] field access, so the fields cannot be properties.")]
    [SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types",
        Justification = "Marshaling container that is never compared for equality or used as a key.")]
    [SuppressMessage("Design", "CA1066:Implement IEquatable when overriding Equals",
        Justification = "Instances are never compared; the IEquatable design/performance benefit is not critical for this marshaling container.")]
    public struct SkyPosition {

        /// <summary>
        /// unit vector toward object (dimensionless)
        /// </summary>
        [MarshalAsAttribute(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.R8)]
        public double[] RHat;

        /// <summary>
        /// apparent, topocentric, or astrometric right ascension(hours)
        /// </summary>
        public double RA;

        /// <summary>
        /// apparent, topocentric, or astrometric declination(degrees)
        /// </summary>
        public double Dec;

        /// <summary>
        /// true (geometric, Euclidian) distance to solar system body or 0.0 for star(AU)
        /// </summary>
        public double Dis;

        /// <summary>
        /// radial velocity (km/s)
        /// </summary>
        public double RV;
    }
}