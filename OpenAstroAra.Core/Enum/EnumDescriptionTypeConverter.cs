#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace OpenAstroAra.Core.Enum {

    /// <summary>
    /// TypeConverter that maps an enum value to the string in its
    /// <see cref="DescriptionAttribute"/>, when present. Used pervasively
    /// across <c>OpenAstroAra.Core/Enum/*</c> via
    /// <c>[TypeConverter(typeof(EnumDescriptionTypeConverter))]</c>.
    /// Pure System.ComponentModel + Reflection — AOT/headless-safe.
    /// </summary>
    public class EnumDescriptionTypeConverter : EnumConverter {

        public EnumDescriptionTypeConverter(Type type) : base(type) { }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) {
            if (destinationType == typeof(string) && value is System.Enum enumValue) {
                var name = enumValue.ToString();
                var field = enumValue.GetType().GetField(name);
                var attr = field?.GetCustomAttribute<DescriptionAttribute>();
                return attr?.Description ?? name;
            }
            return base.ConvertTo(context, culture, value, destinationType);
        }
    }
}