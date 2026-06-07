#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Headless replacements for the System.Windows.* primitives the Astrometry
// project used for pure math (no UI dependency in those use sites):
// - System.Windows.Point  → OpenAstroAra.Astrometry.Point  (2D, double-precision)
// - System.Windows.Vector → OpenAstroAra.Astrometry.Vector (2D, double-precision)
// - System.Windows.Media.Media3D.Vector3D → OpenAstroAra.Astrometry.Vector3D (3D)
// The shapes match the WPF originals (same constructor signature, same X/Y/Z,
// same operators) so the existing call sites compile unchanged.

using System;

namespace OpenAstroAra.Astrometry {

    public readonly struct Point : IEquatable<Point> {
        public double X { get; }
        public double Y { get; }
        public Point(double x, double y) { X = x; Y = y; }
        public bool Equals(Point other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Point p && Equals(p);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(Point a, Point b) => a.Equals(b);
        public static bool operator !=(Point a, Point b) => !a.Equals(b);
        public static Vector operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator +(Point a, Vector v) => new(a.X + v.X, a.Y + v.Y);
        public static Point operator -(Point a, Vector v) => new(a.X - v.X, a.Y - v.Y);
        public override string ToString() => $"({X}, {Y})";

        public static Vector Subtract(Point left, Point right) {
            throw new NotImplementedException();
        }
    }

    public readonly struct Vector : IEquatable<Vector> {
        public double X { get; }
        public double Y { get; }
        public Vector(double x, double y) { X = x; Y = y; }
        public double Length => Math.Sqrt(X * X + Y * Y);
        public bool Equals(Vector other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vector v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(Vector a, Vector b) => a.Equals(b);
        public static bool operator !=(Vector a, Vector b) => !a.Equals(b);
        public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y);
        public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y);
        public static Vector operator *(Vector v, double s) => new(v.X * s, v.Y * s);
        public static Vector operator /(Vector v, double s) => new(v.X / s, v.Y / s);

        public static Vector Add(Vector left, Vector right) {
            throw new NotImplementedException();
        }
    }

    public readonly struct Vector3D : IEquatable<Vector3D> {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator *(Vector3D v, double s) => new(v.X * s, v.Y * s, v.Z * s);
        public static double DotProduct(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vector3D CrossProduct(Vector3D a, Vector3D b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public bool Equals(Vector3D other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is Vector3D v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(Vector3D a, Vector3D b) => a.Equals(b);
        public static bool operator !=(Vector3D a, Vector3D b) => !a.Equals(b);

        public static Vector3D Add(Vector3D left, Vector3D right) {
            throw new NotImplementedException();
        }
    }
}