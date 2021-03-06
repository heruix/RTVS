﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Common.Core.IO;
using Microsoft.Common.Core.Logging;
using Microsoft.Common.Core.OS;
using Microsoft.Common.Core.Services;
using Microsoft.Common.Core.Test.Fakes.Shell;
using Microsoft.Common.Core.Test.Logging;
using Microsoft.Common.Core.Test.Stubs.Shell;
using Microsoft.Common.Core.Test.Telemetry;
using Microsoft.UnitTests.Core.Threading;
using Microsoft.UnitTests.Core.XUnit;
using Microsoft.UnitTests.Core.XUnit.MethodFixtures;
using Xunit.Sdk;

namespace Microsoft.Common.Core.Test.Fixtures {
    public class ServiceManagerFixture : IMethodFixtureFactory<IServiceContainer> {
        public IServiceContainer Dummy { get; } = new TestServiceManager(null);

        public IMethodFixture Create() => CreateFixture();

        protected virtual TestServiceManager CreateFixture() => new TestServiceManager(SetupServices).AddLog();

        protected virtual void SetupServices(IServiceManager serviceManager, ITestInput testInput) {
            serviceManager
                .AddService(UIThreadHelper.Instance)
                .AddService(new SecurityServiceStub())
                .AddService(new MaxLoggingPermissions())
                .AddService(new TelemetryTestService())
                .AddService(new FileSystem())
                .AddService(new RegistryImpl())
                .AddService(new ProcessServices())
                .AddService(new TestUIServices())
                .AddService(new TestPlatformServices());
        }

        protected class TestServiceManager : ServiceManager, IMethodFixture {
            private readonly Action<IServiceManager, ITestInput> _addServices;
            private readonly LogProxy _log;

            public TestServiceManager(Action<IServiceManager, ITestInput> addServices) {
                _addServices = addServices;
                _log = new LogProxy();
            }

            public TestServiceManager AddLog() {
                AddService(_log);
                return this;
            }

            public Task<Task<RunSummary>> InitializeAsync(ITestInput testInput, IMessageBus messageBus) {
                try {
                    var logsFolder = Path.Combine(DeployFilesFixture.TestFilesRoot, "Logs");
                    Directory.CreateDirectory(logsFolder);
                    _log.SetLog(new Logger(testInput.FileSytemSafeName, logsFolder, new MaxLoggingPermissions()));
                    _addServices(this, testInput);
                } catch (Exception) {
                    return Task.FromResult(Task.FromResult(new RunSummary {Failed = 1}));
                }

                return MethodFixtureBase.DefaultInitializeTask;
            }

            public virtual Task DisposeAsync(RunSummary result, IMessageBus messageBus) {
                if (result.Failed > 0) {
                    _log.Flush();
                }
                Dispose();
                return Task.CompletedTask;
            }
        }

        private class LogProxy : IActionLog {
            private IActionLog _log;

            public void SetLog(IActionLog log) {
                _log = log;
            }

            public void Write(LogVerbosity verbosity, MessageCategory category, string message) 
                => _log.Write(verbosity, category, message);

            public void WriteFormat(LogVerbosity verbosity, MessageCategory category, string format, params object[] arguments)
                => _log.WriteFormat(verbosity, category, format, arguments);

            public void WriteLine(LogVerbosity verbosity, MessageCategory category, string message)
                => _log.WriteLine(verbosity, category, message);

            public void Flush() => _log.Flush();

            public LogVerbosity LogVerbosity => _log.LogVerbosity;
            public string Folder => _log.Folder;
        }
    }
}