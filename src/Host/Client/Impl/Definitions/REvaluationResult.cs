﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using static System.FormattableString;

namespace Microsoft.R.Host.Client {
    /// <summary>
    /// Corresponds to R <c>ParseStatus</c> enum as used by <c>Rf_eval</c>.
    /// </summary>
    public enum RParseStatus {
        Null,
        OK,
        Incomplete,
        Error,
        EOF
    }

    public struct REvaluationResult {
        /// <summary>
        /// Result of evaluation, if expression was evaluated without <see cref="REvaluationKind.Json"/>.
        /// Otherwise, <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Computed by applying <c>Rf_asChar</c> to the immediate result of evaluation, and taking the first
        /// element of the resulting character vector.
        /// </remarks>
        public string StringResult { get; }
        /// <summary>
        /// Result of evaluation, if expression was evaluated with <see cref="REvaluationKind.Json"/>.
        /// Otherwise, <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Computed by serializing the immediate result of evaluation, as if by <c>rtvs:::toJSON</c>.
        /// </remarks>
        public JToken JsonResult { get; }
        /// <summary>
        /// If evaluation failed because of an R runtime error, text of the error message.
        /// Otherwise, <see langword="null"/>.
        /// </summary>
        public string Error { get; }
        /// <summary>
        /// Status code indicating the result of parsing the expression.</summary>
        /// <remarks>
        /// For a successful evaluation, this is always <see cref="RParseStatus.OK"/>.
        /// </remarks>
        public RParseStatus ParseStatus { get; }

        public REvaluationResult(string result, string error, RParseStatus parseStatus) {
            StringResult = result;
            JsonResult = null;
            Error = error;
            ParseStatus = parseStatus;
        }

        public REvaluationResult(JToken result, string error, RParseStatus parseStatus) {
            StringResult = null;
            JsonResult = result;
            Error = error;
            ParseStatus = parseStatus;
        }

        public override string ToString() {
            var sb = new StringBuilder((StringResult ?? JsonResult ?? "").ToString());
            if (ParseStatus != RParseStatus.OK) {
                sb.AppendFormat(CultureInfo.InvariantCulture, "; {0}", ParseStatus);
            }
            if (Error != null) {
                sb.AppendFormat(CultureInfo.InvariantCulture, "; '{0}'", Error);
            }
            return sb.ToString();
        }
    }
}

