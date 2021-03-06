﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Common.Core.Shell;
using Microsoft.Languages.Core.Text;
using Microsoft.Languages.Core.Utility;
using Microsoft.Languages.Editor.Tasks;
using Microsoft.Languages.Editor.Text;
using Microsoft.Languages.Editor.Utility;
using Microsoft.R.Core.AST;
using Microsoft.VisualStudio.Text;

namespace Microsoft.R.Editor.Tree {
    /// <summary>
    /// Asynchronous text change processing task
    /// </summary>
    internal sealed class TreeUpdateTask : CancellableTask {
        #region Private members

        private static readonly Guid _treeUserId = new Guid("BE78E649-B9D4-4BC0-A332-F38A2B16CD10");
        private static int _parserDelay = 200;

        /// <summary>
        /// Owner thread - typically main thread ID
        /// </summary>
        private int _ownerThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>
        /// Editor tree that task is servicing
        /// </summary>
        private EditorTree _editorTree;

        private readonly ICoreShell _shell;

        /// <summary>
        /// Text buffer
        /// </summary>
        private ITextBuffer TextBuffer {
            get { return _editorTree.TextBuffer; }
        }

        /// <summary>
        /// Output queue of the background parser
        /// </summary>
        private ConcurrentQueue<EditorTreeChangeCollection> _backgroundParsingResults = new ConcurrentQueue<EditorTreeChangeCollection>();

        /// <summary>
        /// Pending changes since the last parse or since async parsing task started.
        /// </summary>
        private TextChange _pendingChanges = new TextChange();

        /// <summary>
        /// Time when background task requested transition to main thread.
        /// Used for debugging/profiling purposes.
        /// </summary>
        private DateTime _uiThreadTransitionRequestTime;

        /// <summary>
        /// If true the task was disposed (document was closed and tree is now orphaned).
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Prevents disposing when background task is running
        /// </summary>
        private readonly object _disposeLock = new object();

        private DateTime _lastChangeTime = DateTime.UtcNow;
        #endregion

        #region Constructors
        public TreeUpdateTask(EditorTree editorTree, ICoreShell shell) {
            _editorTree = editorTree;
            _shell = shell;
            if (_shell != null) {
                _shell.Idle += OnIdle;
            }
            
        }
        #endregion

        #region Properties
        /// <summary>
        /// Detemines if tree is 'out of date' i.e. user made changes to the document
        /// so text snapshot attached to the tree is no longer the same as ITextBuffer.CurrentSnapshot
        /// </summary>
        /// <returns></returns>
        internal bool ChangesPending {
            get { return !_pendingChanges.IsEmpty; }
        }

        /// <summary>
        /// Returns an object that describes pending changes if any, null otherwise
        /// </summary>
        internal TextChange Changes {
            get { return _pendingChanges; }
        }
        #endregion

        internal void TakeThreadOwnership() {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        internal void ClearChanges() {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            _pendingChanges.Clear();
        }

        /// <summary>
        /// Indicates that parser is suspended and tree is not 
        /// getting updated on text buffer changes. This may, for example,
        /// happen during document formatting or other massive changes.
        /// </summary>
        public bool UpdatesSuspended { get; private set; }

        /// <summary>
        /// Suspend tree updates. Typically called before massive 
        /// changes to the document.
        /// </summary>
        internal void Suspend() {
            UpdatesSuspended = true;
            TextBufferChangedSinceSuspend = false;
        }

        /// <summary>
        /// Resumes tree updates. If changes were made to the text buffer 
        /// since suspend, full parse is performed.
        /// </summary>
        internal void Resume() {
            if (UpdatesSuspended) {
                UpdatesSuspended = false;

                if (TextBufferChangedSinceSuspend) {
                    TextBufferChangedSinceSuspend = false;

                    GuardedOperations.DispatchInvoke(() =>
                        ProcessPendingTextBufferChanges(async: true),
                        DispatcherPriority.ApplicationIdle);
                }
            }
        }

        /// <summary>
        /// Indicates if text buffer changed since tree updates were suspended.
        /// </summary>
        internal bool TextBufferChangedSinceSuspend { get; private set; }

        /// <summary>
        /// Text buffer change event handler. Performs analysis of the change.
        /// If change is trivial, such as change in whitespace (excluding line 
        /// breaks that in R may be sensistive), simply applies the changes 
        /// by shifting tree elements. If some elements get deleted or otherwise 
        /// damaged, removes them from the tree right away. Non-trivial changes 
        /// are queued for background parsing which starts on next on idle.
        /// Methond must be called on a main thread only, typically from an event 
        /// handler that receives text buffer change events. 
        /// </summary>
        internal void OnTextChanges(IReadOnlyCollection<TextChangeEventArgs> textChanges) {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            _editorTree.FireOnUpdatesPending(textChanges);
            if (UpdatesSuspended) {
                this.TextBufferChangedSinceSuspend = true;
                _pendingChanges.FullParseRequired = true;
            } else {
                foreach (TextChangeEventArgs change in textChanges) {
                    _lastChangeTime = DateTime.UtcNow;
                    var context = new TextChangeContext(_editorTree, change, _pendingChanges);

                    // No need to analyze changes if full parse is already pending
                    if (!_pendingChanges.FullParseRequired) {
                        TextChangeAnalyzer.DetermineChangeType(context);
                    }

                    ProcessChange(context);
                }
            }
        }

        private void ProcessChange(TextChangeContext context) {
            _editorTree.FireOnUpdateBegin();

            if (_pendingChanges.IsSimpleChange) {
                ProcessSimpleChange(context);
            } else {
                ProcessComplexChange(context);
            }
        }

        /// <summary>
        /// Handles simple (safe) changes.
        /// </summary>
        private void ProcessSimpleChange(TextChangeContext context) {
            bool elementsRemoved = false;

            try {
                _editorTree.AcquireWriteLock();

                elementsRemoved = DeleteAndShiftElements(context);
                UpdateTreeTextSnapshot();

                // If no elements were invalidated and full parse is not required, clear pending changes
                if (!elementsRemoved) {
                    ClearChanges();
                }
            } finally {
                _editorTree.ReleaseWriteLock();
            }

            if (!elementsRemoved) {
                if (context.ChangedNode != null || context.PendingChanges.TextChangeType == TextChangeType.Trivial) {
                    _editorTree.FireOnPositionsOnlyChanged();
                }

                _editorTree.FireOnUpdateCompleted(TreeUpdateType.PositionsOnly);
            } else {
                _editorTree.FireOnUpdateCompleted(TreeUpdateType.NodesRemoved);
            }

            DebugTree.VerifyTree(_editorTree);
            Debug.Assert(_editorTree.AstRoot.Children.Count > 0);
        }

        /// <summary>
        /// Handles non-trivial changes like changes that delete elements, 
        /// change identifier names, introducing new braces: changes
        /// that cannot be handled without background parse.
        /// </summary>
        private void ProcessComplexChange(TextChangeContext context) {
            // Cancel background parse if it is running
            Cancel();

            TextChange textChange = new TextChange() {
                OldTextProvider = context.OldTextProvider,
                NewTextProvider = context.NewTextProvider
            };

            try {
                // Get write lock since there may be concurrent readers 
                // of the tree. Note that there are no concurrent writers 
                // since changes can only come from a background parser
                // and are always applied from the main thread.
               _editorTree.AcquireWriteLock();

                if (_pendingChanges.FullParseRequired) {
                    // When full parse is required, change is like replace the entire file
                    textChange.OldRange = TextRange.FromBounds(0, context.OldText.Length);
                    textChange.NewRange = TextRange.FromBounds(0, context.NewText.Length);

                    // Remove damaged elements if any and reflect text change.
                    // the tree remains usable outside of the damaged scope.
                    bool elementsChanged = _editorTree.InvalidateInRange(context.OldRange);
                    _editorTree.NotifyTextChange(context.NewStart, context.OldLength, context.NewLength);
                } else {
                    textChange.OldRange = context.OldRange;
                    textChange.NewRange = context.NewRange;

                    DeleteAndShiftElements(context);
                    Debug.Assert(_editorTree.AstRoot.Children.Count > 0);
                }

                _pendingChanges.Combine(textChange);
                _pendingChanges.Version = TextBuffer != null ? TextBuffer.CurrentSnapshot.Version.VersionNumber : 1;

                UpdateTreeTextSnapshot();
            } finally {
                // Lock must be released before firing events otherwise we may hang
                _editorTree.ReleaseWriteLock();
            }

            _editorTree.FireOnUpdateCompleted(TreeUpdateType.NodesRemoved);
        }

        private void UpdateTreeTextSnapshot() {
            if (TextBuffer != null) {
                if (_pendingChanges.OldTextProvider == null)
                    _pendingChanges.OldTextProvider = new TextProvider(_editorTree.TextSnapshot, partial: true);

                _editorTree.TextSnapshot = TextBuffer.CurrentSnapshot;
            }
        }

        // internal for unit tests
        internal bool DeleteAndShiftElements(TextChangeContext context) {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            TextChange textChange = context.PendingChanges;
            var changeType = textChange.TextChangeType;
            bool elementsChanged = false;

            if (changeType == TextChangeType.Structure) {
                IAstNode changedElement = context.ChangedNode;
                int start = context.NewStart;

                // We delete change nodes unless node is a token node 
                // which range can be modified such as string or comment
                var positionType = PositionType.Undefined;

                if (changedElement != null) {
                    IAstNode node;
                    positionType = changedElement.GetPositionNode(context.NewStart, out node);
                }

                bool deleteElements = (context.OldLength > 0) || (positionType != PositionType.Token);

                // In case of delete or replace we need to invalidate elements that were 
                // damaged by the delete operation. We need to remove elements so they 
                // won't be found by validator and it won't be looking at zombies.
                if (deleteElements) {
                    _pendingChanges.FullParseRequired = true;
                    elementsChanged = _editorTree.InvalidateInRange(context.OldRange);
                }
            }

            _editorTree.NotifyTextChange(context.NewStart, context.OldLength, context.NewLength);
            return elementsChanged;
        }

        /// <summary>
        /// Idle time event handler. Kicks background parsing if there are pending changes
        /// </summary>
        private void OnIdle(object sender, EventArgs e) {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId) {
                throw new ThreadStateException("Method should only be called on the main thread");
            }

            if (TextBuffer == null || TextBuffer.EditInProgress)
                return;

            if (_lastChangeTime != DateTime.MinValue && TimeUtility.MillisecondsSinceUtc(_lastChangeTime) > _parserDelay) {
                // Kick background parsing when idle slot comes so parser does not hit on every keystroke
                ProcessPendingTextBufferChanges(async: true);

                _lastChangeTime = DateTime.MinValue;
            }
        }

        internal void ProcessPendingTextBufferChanges(bool async) {
            // Text buffer can be null in unit tests
            if (TextBuffer != null) {
                ProcessPendingTextBufferChanges(new TextProvider(TextBuffer.CurrentSnapshot, partial: true), async);
            }
        }

        /// <summary>
        /// Processes text buffer changed accumulated so far. 
        /// Typically called on idle.
        /// </summary>
        /// <param name="newTextProvider">New text buffer content</param>
        /// <param name="async">True if processing is to be done asynchronously.
        /// Non-async processing is typically used in unit tests only.</param>
        internal void ProcessPendingTextBufferChanges(ITextProvider newTextProvider, bool async) {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            if (ChangesPending) {
                if (async && (IsTaskRunning() || _backgroundParsingResults.Count > 0)) {
                    // Try next time or we may end up spawning a lot of tasks
                    return;
                }

                // Combine changes in processing with pending changes.
                var changesToProcess = new TextChange(_pendingChanges, newTextProvider);

                // We need to signal task start here, in the main thread since it takes
                // some time before task is created and when it actually starts.
                // Therefore setting task state in the task body creates a gap
                // where we may end up spawning another task.

                base.Run((isCancelledCallback) => ProcessTextChanges(changesToProcess, async, isCancelledCallback), async);
            }
        }

        /// <summary>
        /// Main asyncronous task body
        /// </summary>
        void ProcessTextChanges(TextChange changeToProcess, bool async, Func<bool> isCancelledCallback) {
            lock (_disposeLock) {
                if (_editorTree == null || _disposed || isCancelledCallback())
                    return;

                EditorTreeChangeCollection treeChanges = null;
                // Cache id since it can change if task is canceled
                long taskId = TaskId;

                try {
                    AstRoot rootNode;

                    // We only need read lock since changes will be applied 
                    // from the main thread
                    if (async) {
                        rootNode = _editorTree.AcquireReadLock(_treeUserId);
                    } else {
                        rootNode = _editorTree.GetAstRootUnsafe();
                    }

                    treeChanges = new EditorTreeChangeCollection(changeToProcess.Version, changeToProcess.FullParseRequired);
                    TextChangeProcessor changeProcessor = new TextChangeProcessor(_editorTree, rootNode, isCancelledCallback);

                    bool fullParseRequired = changeToProcess.FullParseRequired;
                    if (fullParseRequired) {
                        changeProcessor.FullParse(treeChanges, changeToProcess.NewTextProvider);
                    } else {
                        changeProcessor.ProcessChange(changeToProcess, treeChanges);
                    }
                } finally {
                    if (async && _editorTree != null)
                        _editorTree.ReleaseReadLock(_treeUserId);
                }

                // Lock should be released at this point since actual application
                // of tree changes is going to be happen from the main thread.

                if (!isCancelledCallback() && treeChanges != null) {
                    // Queue results for the main thread application. This must be done before 
                    // signaling that the task is complete since if EnsureProcessingComplete 
                    // is waiting it will want to apply changes itself rather than wait for 
                    // the MainThread().Post to go though and hence it will need all changes
                    // stored and ready for application.

                    _backgroundParsingResults.Enqueue(treeChanges);
                }

                // Signal task complete now so if main thread is waiting
                // it can proceed and appy the changes immediately.
                SignalTaskComplete(taskId);

                if (_backgroundParsingResults.Count > 0) {
                    _uiThreadTransitionRequestTime = DateTime.UtcNow;

                    // It is OK to post results while main thread might be working
                    // on them since if if it does, by the time posted request comes
                    // queue will already be empty.
                    if (async) {
                        // Post request to apply tree changes to the main thread.
                        // This must NOT block or else task will never enter 'RanToCompletion' state.
                        _shell.MainThread().Post(ApplyBackgroundProcessingResults);
                    } else {
                        // When processing is synchronous, apply changes and fire events right away.
                        ApplyBackgroundProcessingResults();
                    }
                }
            }
        }

        /// <summary>
        /// Makes sure all pending changes are processed and applied to the tree
        /// </summary>
        internal void EnsureProcessingComplete() {
            if (_ownerThreadId != Thread.CurrentThread.ManagedThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            // We want to make sure changes that are in a background processing are applied to the tree
            // before returning. We can't wait on events since call comes on a main thread and wait 
            // will prevent WPF dispatcher call from going through.

            // this will attempt to apply changes from the background processing results queue.
            // It will discard stale changes and only apply changes if they match current
            // text buffer snapshot version. This will only apply changes are that already
            // in the queue. If background task is still running or all changes are stale
            // the tree still will be out of date.

            // Check if tree is up to date. It is up to date if there are no text buffer changes that
            // are pending for background processing.
            if (ChangesPending) {
                // If task is running, give it a chance to finish. No need to wait long
                // since even on a large file full parse rarely takes more than 50 ms.
                // Also we can't wait indefinitely since if task is *scheduled to run*
                // and then got cancelled before actually sarting, it will never complete.
                WaitForCompletion(2000);

                _uiThreadTransitionRequestTime = DateTime.UtcNow;
                ApplyBackgroundProcessingResults();

                if (ChangesPending) {
#if DEBUG
                    string originalPendingChanges = Changes.ToString();
#endif

                    // We *sometimes* still have pending changes even after calling ProcessPendingTextBufferChanges(async: false).
                    //   I'd like to determine whether this is a timing issue by retrying here multiple times and seeing if it helps.
                    int retryCount = 0;
                    while (retryCount < 10 && ChangesPending) {
                        // Changes are still pending. Even if they are already in a backround processing,
                        // process them right away here and ignore background processing results
                        ProcessPendingTextBufferChanges(async: false);
                        retryCount += 1;
                    }

#if DEBUG
                    if (retryCount == 10) {
                        string msg = string.Format(CultureInfo.InvariantCulture,
                            "Pending changes remain: ChangesPending: {0}, original:\"{1}\", new:\"{2}\"",
                            ChangesPending, originalPendingChanges, Changes.ToString());

                        // using Debugger.Break as I want all threads suspended so the state doesn't change
                        Debug.Assert(false, msg);
                    }
#endif
                }
            }

            Debug.Assert(!ChangesPending);
            Debug.Assert(_editorTree.AstRoot.Children.Count > 0);
        }

        /// <summary>
        /// Applies queued changes to the tree. Must only be called in a main thread context.
        /// </summary>
        /// <param name="o"></param>
        internal void ApplyBackgroundProcessingResults() {
            if (_ownerThreadId != Thread.CurrentThread.ManagedThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            if (_disposed)
                return;

            EditorTreeChangeCollection treeChanges;
            var eventsToFire = new List<TreeChangeEventRecord>();
            bool changed = false;
            bool fullParse = false;
            bool staleChanges = false;

            while (_backgroundParsingResults.TryDequeue(out treeChanges)) {
                // If no changes are pending, then main thread already processes
                // everything in EnsureProcessingComplete call. Changes are pending
                // until they are applied to the tree. If queue is not empty
                // it either contains stale changes or main thread had to handle
                // changes in sync per request from, say, intellisense or formatting.
                if (ChangesPending) {
                    // Check if background processing result matches current text buffer snapshot version
                    staleChanges = (TextBuffer != null && treeChanges.SnapshotVersion < TextBuffer.CurrentSnapshot.Version.VersionNumber);

                    if (!staleChanges) {
                        // We can't fire events when appying changes since listeners may
                        // attempt to access tree which is not fully updated and/or may
                        // try to acquire read lock and hang since ApplyTreeChanges
                        // hols write lock.

                        eventsToFire = ApplyTreeChanges(treeChanges);
                        fullParse = _pendingChanges.FullParseRequired;

                        // Queue must be empty by now since only most recent changes are not stale
                        // Added local variable as I hit this assert, but _backgroundParsingResults.Count was zero
                        //   by the time I broke into the debugger. If this hits again, we may need to 
                        //   think through this code and whether we need to be protecting against concurrent access.
                        int count = _backgroundParsingResults.Count;
                        Debug.Assert(count == 0);

                        // Clear pending changes as we are done
                        ClearChanges();

                        changed = true;

                        // No need for further processing as queue must be empty
                        break;
                    }
                }
            }

            if (!staleChanges) {
                // Now that tree is fully updated, fire events
                if (_editorTree != null) {
                    if (changed) {
                        _editorTree.FirePostUpdateEvents(eventsToFire, fullParse);
                        DebugTree.VerifyTree(_editorTree);
                    }

                    if (!ChangesPending) {
                        Debug.Assert(_editorTree.AstRoot.Children.Count > 0);
                    }
                }
            }
        }

        List<TreeChangeEventRecord> ApplyTreeChanges(EditorTreeChangeCollection changesToApply) {
            // Check editor tree reference since document could have been 
            // closed before parsing was completed

            if (!_disposed && _editorTree != null) {
                if (TextBuffer != null)
                    _editorTree.TextSnapshot = TextBuffer.CurrentSnapshot;

                return _editorTree.ApplyChangesFromQueue(changesToApply.ChangeQueue);
            }

            return new List<TreeChangeEventRecord>();
        }

        #region Dispose
        protected override void Dispose(bool disposing) {
            if (Thread.CurrentThread.ManagedThreadId != _ownerThreadId)
                throw new ThreadStateException("Method should only be called on the main thread");

            lock (_disposeLock) {
                if (disposing) {
                    Cancel();

                    _disposed = true;
                    if (_shell != null) {
                        _shell.Idle -= OnIdle;
                    }
                }
                base.Dispose(disposing);
            }
        }
        #endregion
    }
}
