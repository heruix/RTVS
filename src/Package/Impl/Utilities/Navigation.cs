﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using Microsoft.Common.Core.Shell;
using Microsoft.Languages.Editor.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.R.Package.Shell;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Microsoft.VisualStudio.R.Package.Utilities {
    public static class Navigation {
        /// <summary>
        /// Activates a text view for a text buffer, and sets the cursor to a specific location
        /// </summary>
        public static bool NavigateToTextBuffer(ITextBuffer textBuffer, int start, int length) {
            var projectionSnapshot = textBuffer.CurrentSnapshot as IProjectionSnapshot;

            if (projectionSnapshot != null) {
                // Find the main buffer for the view

                var sourcePoint = new SnapshotPoint();
                var success = true;

                try {
                    sourcePoint = projectionSnapshot.MapToSourceSnapshot(start, PositionAffinity.Successor);
                } catch (ArgumentOutOfRangeException) {
                    success = false;
                } catch (InvalidOperationException) {
                    success = false;
                }

                if (success) {
                    return NavigateToTextBuffer(sourcePoint.Snapshot.TextBuffer, sourcePoint.Position, length);
                }
            } else {
                // This is the main buffer for the view

                var textManager = VsAppShell.Current.GetService<IVsTextManager>(typeof(SVsTextManager));
                var vsTextBuffer = textBuffer.GetBufferAdapter<IVsTextBuffer>();
                var viewType = VSConstants.LOGVIEWID_TextView;

                if (vsTextBuffer != null &&
                    ErrorHandler.Succeeded(textManager.NavigateToPosition(vsTextBuffer, ref viewType, start, length))) {
                    return true;
                }
            }

            return false;
        }

        public static bool NavigateToTextView(ITextView textView, int start, int length) {
            return NavigateToTextBuffer(textView.TextBuffer, start, length);
        }

        public static bool NavigateToTextView(IVsTextView vsTextView, int start, int length) {
            var adapterService = ComponentLocator<IVsEditorAdaptersFactoryService>.Import(VsAppShell.Current.GetService<ICompositionService>());
            if (adapterService != null) {
                var textView = adapterService.GetWpfTextView(vsTextView);
                if (textView != null) {
                    return NavigateToTextView(textView, start, length);
                }
            }

            return false;
        }

        public static bool NavigateToFrame(IVsWindowFrame frame, int start, int length) {
            int hr = frame.Show();
            if (ErrorHandler.Succeeded(hr)) {
                var vsTextView = VsShellUtilities.GetTextView(frame);
                if (vsTextView != null) {
                    return NavigateToTextView(vsTextView, start, length);
                }
            }

            return false;
        }

        public static bool NavigateToFile(Uri fileUri, int start, int length, bool allowProvisionalTab) {
            if (fileUri == null || !fileUri.IsAbsoluteUri || !fileUri.IsFile) {
                Debug.Fail("Invalid fileUri: " + (fileUri != null ? fileUri.ToString() : string.Empty));
                return false;
            }

            string localPath = fileUri.LocalPath;
            if (!File.Exists(localPath)) {
                Debug.Fail("File doesn't exist: " + localPath);
                return false;
            }

            var newState = allowProvisionalTab
                ? __VSNEWDOCUMENTSTATE.NDS_Provisional
                : __VSNEWDOCUMENTSTATE.NDS_Permanent;

            using (new NewDocumentStateScope(newState, VSConstants.NewDocumentStateReason.Navigation)) {
                Guid logicalViewGuid = VSConstants.LOGVIEWID.TextView_guid;
                Microsoft.VisualStudio.OLE.Interop.IServiceProvider serviceProvider;
                IVsUIHierarchy hierarchy;
                IVsWindowFrame frame;
                uint itemId;

                var openService = VsAppShell.Current.GetService<IVsUIShellOpenDocument>(typeof(SVsUIShellOpenDocument));
                if (openService != null) {
                    int hr = openService.OpenDocumentViaProject(
                        localPath, ref logicalViewGuid, out serviceProvider, out hierarchy, out itemId, out frame);

                    if (ErrorHandler.Succeeded(hr) && frame != null) {
                        return NavigateToFrame(frame, start, length);
                    }
                }
            }

            return false;
        }
    }
}
