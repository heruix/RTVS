﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Common.Core.Services;
using Microsoft.Common.Core.Shell;
using Microsoft.Languages.Editor.Controller.Constants;
using Microsoft.R.Components.ContentTypes;
using Microsoft.UnitTests.Core.XUnit;
using Xunit;

namespace Microsoft.R.Editor.Application.Test.Formatting {
    [ExcludeFromCodeCoverage]
    [Collection(CollectionNames.NonParallel)]
    public class FormatSelectionTest {
        private readonly ICoreShell _coreShell;
        private readonly EditorHostMethodFixture _editorHost;

        public FormatSelectionTest(IServiceContainer services, EditorHostMethodFixture editorHost) {
            _coreShell = services.GetService<ICoreShell>();
            _editorHost = editorHost;
        }

        [Test]
        [Category.Interactive]
        public async Task R_FormatSelection01() {
            string content = "\nwhile (TRUE) {\n        if(x>1) {\n   }\n}";
            string expected = "\nwhile (TRUE) {\n    if (x > 1) {\n    }\n}";
            using (var script = await _editorHost.StartScript(_coreShell, content, RContentTypeDefinition.ContentType)) {
                script.Select(20, 18);
                script.Execute(VSConstants.VSStd2KCmdID.FORMATSELECTION, 50);
                string actual = script.EditorText;

                actual.Should().Be(expected);
            }
        }
    }
}
