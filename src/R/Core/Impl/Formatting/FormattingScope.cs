﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using Microsoft.Common.Core;
using Microsoft.Languages.Core.Formatting;
using Microsoft.Languages.Core.Tokens;
using Microsoft.R.Core.Tokens;

namespace Microsoft.R.Core.Formatting {
    /// <summary>
    /// Settings for formatting of { } scope.
    /// </summary>
    internal sealed class FormattingScope : IDisposable {
        private readonly RFormatOptions _options;
        private readonly TextBuilder _tb;
        private readonly int _previousIndentLevel;

        public int CloseBracePosition { get; }
        public int StartingLineIndent { get; }
        public int SuppressLineBreakCount { get; set; }

        public FormattingScope() { }

        public FormattingScope(TextBuilder tb, TokenStream<RToken> tokens, int openBraceTokenIndex, RFormatOptions options) {
            Debug.Assert(tokens[openBraceTokenIndex].TokenType == RTokenType.OpenCurlyBrace);

            _options = options;
            _tb = tb;

            var position = tokens.Position;
            tokens.Position = openBraceTokenIndex;

            CloseBracePosition = TokenBraceCounter<RToken>.GetMatchingBrace(tokens,
                new RToken(RTokenType.OpenCurlyBrace), new RToken(RTokenType.CloseCurlyBrace),
                new RTokenTypeComparer());

            tokens.Position = position;

            _previousIndentLevel = tb.IndentBuilder.IndentLevel;
            StartingLineIndent = CurrentLineIndent(tb);
            if (StartingLineIndent > 0) {
                tb.IndentBuilder.SetIndentLevelForSize(StartingLineIndent + _options.IndentSize);
            } else {
                tb.IndentBuilder.NewIndentLevel();
            }
        }

        private int CurrentLineIndent(TextBuilder tb) {
            for (int i = tb.Length - 1; i >= 0; i--) {
                if (CharExtensions.IsLineBreak(tb.Text[i])) {
                    return IndentBuilder.TextIndentInSpaces(tb.Text.Substring(i + 1), _options.TabSize);
                }
            }
            return 0;
        }

        public void Dispose() {
            _tb.IndentBuilder.SetIndentLevel(_previousIndentLevel);
        }
    }
}
