#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.MetaGuide {

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix",
        Justification = "MetaGuideBaseMsg is the established base of the MetaGuide*Msg message hierarchy; it derives from EventArgs only so the listener's events can use EventHandler<TMsg>. The '...Msg' suffix is the meaningful, consistent name across all six message types; renaming the hierarchy to '...EventArgs' adds no clarity.")]
    public class MetaGuideBaseMsg : System.EventArgs {

        public override String ToString() {
            Type objType = this.GetType();
            PropertyInfo[] propertyInfoList = objType.GetProperties();
            StringBuilder result = new StringBuilder();
            foreach (PropertyInfo propertyInfo in propertyInfoList) {
                result.AppendFormat(CultureInfo.InvariantCulture, "{0}={1} ", propertyInfo.Name, propertyInfo.GetValue(this));
            }

            return result.ToString();
        }
    }
}