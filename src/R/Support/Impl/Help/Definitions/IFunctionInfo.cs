﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;

namespace Microsoft.R.Support.Help {
    public interface IFunctionInfo : INamedItemInfo {
        /// <summary>
        /// Function signatures
        /// </summary>
        IReadOnlyList<ISignatureInfo> Signatures { get; }

        /// <summary>
        /// Return value description
        /// </summary>
        string ReturnValue { get; }

        /// <summary>
        /// Indicates that function is internal (has 'internal' 
        /// in its list of keywords)
        /// </summary>
        bool IsInternal { get; }
    }
}
