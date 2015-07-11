﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Languages.Editor;
using Microsoft.Languages.Editor.Controller.Command;
using Microsoft.Languages.Editor.Controller.Constants;
using Microsoft.Languages.Editor.EditorHelpers;
using Microsoft.Languages.Editor.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;

namespace Microsoft.R.Editor.Outline
{
    internal sealed class ROutlineTagsCommandHandler : ViewCommand
    {
        private static CommandId[] _commandIds = new CommandId[]
        {
            new CommandId(VSConstants.VSStd2K, (int)VSConstants.VSStd2KCmdID.COLLAPSETAG),
            new CommandId(VSConstants.VSStd2K, (int)VSConstants.VSStd2KCmdID.UNCOLLAPSETAG),
        };

        private IOutliningManager _outliningManager;

        internal ROutlineTagsCommandHandler(ITextView textView) :
            base(textView, _commandIds, false)
        {
        }

        private IOutliningManager OutliningManager
        {
            get
            {
                if (_outliningManager == null)
                {
                    IOutliningManagerService outliningManagerService = EditorShell.ExportProvider.GetExport<IOutliningManagerService>().Value;
                    _outliningManager = outliningManagerService.GetOutliningManager(TextView);
                }

                return _outliningManager;
            }
        }

        private IEnumerable<ICollapsible> CollapsibleHtmlRegions
        {
            get
            {
                SnapshotSpan snapSpan = new SnapshotSpan(TextView.TextSnapshot, new Span(TextView.Caret.Position.BufferPosition, 0));
                IEnumerable<ICollapsible> allRegions = OutliningManager.GetAllRegions(snapSpan);
                return allRegions.Where(c => c.IsCollapsible && (c.Tag is ROutliningRegionTag));
            }
        }

        private IEnumerable<ICollapsed> CollapsedHtmlRegions
        {
            get
            {
                SnapshotSpan snapSpan = new SnapshotSpan(TextView.TextSnapshot, new Span(TextView.Caret.Position.BufferPosition, 0));
                IEnumerable<ICollapsed> collapsedRegions = OutliningManager.GetCollapsedRegions(snapSpan);
                return collapsedRegions.Where(c => (c.Tag is ROutliningRegionTag));
            }
        }

        #region ICommand
        public override CommandStatus Status(Guid group, int id)
        {
            if (group == VSConstants.VSStd2K)
            {
                VSConstants.VSStd2KCmdID vsCmdID = (VSConstants.VSStd2KCmdID)id;

                switch (vsCmdID)
                {
                    case VSConstants.VSStd2KCmdID.COLLAPSETAG:
                        return CollapsibleHtmlRegions.Any() ? CommandStatus.SupportedAndEnabled : CommandStatus.Invisible;
                    case VSConstants.VSStd2KCmdID.UNCOLLAPSETAG:
                        return CollapsedHtmlRegions.Any() ? CommandStatus.SupportedAndEnabled : CommandStatus.Invisible;
                }
            }

            return CommandStatus.NotSupported;
        }

        public override CommandResult Invoke(Guid group, int id, object inputArg, ref object outputArg)
        {
            if (group == VSConstants.VSStd2K)
            {
                VSConstants.VSStd2KCmdID vsCmdID = (VSConstants.VSStd2KCmdID)id;

                switch (vsCmdID)
                {
                    case VSConstants.VSStd2KCmdID.COLLAPSETAG:
                    {
                        ICollapsible maxCollapsibleRegion = null;
                        int maxStart = 0;
                        foreach (ICollapsible curRegion in CollapsibleHtmlRegions)
                        {
                            int curStart = curRegion.Extent.GetCurrentStart();
                            if ((maxCollapsibleRegion == null) || (curStart > maxStart))
                            {
                                maxStart = curStart;
                                maxCollapsibleRegion = curRegion;
                            }
                        }

                        if (maxCollapsibleRegion != null)
                        {
                            OutliningManager.TryCollapse(maxCollapsibleRegion);
                        }

                        return CommandResult.Executed;
                    }

                    case VSConstants.VSStd2KCmdID.UNCOLLAPSETAG:
                    {
                        ICollapsed minCollapsedRegion = null;
                        int minStart = Int32.MaxValue;
                        foreach (ICollapsed curRegion in CollapsedHtmlRegions)
                        {
                            int curStart = curRegion.Extent.GetCurrentStart();
                            if ((minCollapsedRegion == null) || (curStart < minStart))
                            {
                                minStart = curStart;
                                minCollapsedRegion = curRegion;
                            }
                        }

                        if (minCollapsedRegion != null)
                        {
                            OutliningManager.Expand(minCollapsedRegion);
                        }

                        return CommandResult.Executed;
                    }
                }
            }

            return CommandResult.NotSupported;
        }

        #endregion
    }
}