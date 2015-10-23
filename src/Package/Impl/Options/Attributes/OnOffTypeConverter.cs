﻿using System;
using System.ComponentModel;
using System.Globalization;

namespace Microsoft.VisualStudio.R.Package.Options.Attributes {
    internal class OnOffTypeConverter : TypeConverter {
        public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) {
            return sourceType == typeof(string) || sourceType == typeof(bool);
        }

        public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) {
            if (value is bool) {
                return value;
            }

            var s = value as string;
            if (s == null) {
                return null;
            }

            if (s.Equals(Resources.On, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }

            if (s.Equals(Resources.Off, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            return null;
        }

        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType) {
            return destinationType == typeof(string) || destinationType == typeof(bool);
        }

        public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType) {
            if (value.GetType() == destinationType)
                return value;

            if (destinationType == typeof(string) && value is bool) {
                return (bool)value ? Resources.On : Resources.Off;
            }

            if (destinationType == typeof(bool) && value is string) {
                return ConvertFrom(context, CultureInfo.CurrentUICulture, value);
            }

            return null;
        }

        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) {
            // only On/Off can be chosen
            return true;
        }

        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) {
            return true;
        }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) {
            return new StandardValuesCollection(new[] { true, false });
        }
    }
}
