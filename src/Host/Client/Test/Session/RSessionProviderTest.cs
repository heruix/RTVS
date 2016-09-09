﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Common.Core;
using Microsoft.R.Host.Client.Host;
using Microsoft.R.Host.Client.Session;
using Microsoft.UnitTests.Core.Threading;
using Microsoft.UnitTests.Core.XUnit;

namespace Microsoft.R.Host.Client.Test.Session {
    public class RSessionProviderTest {
        [Test]
        public void Lifecycle() {
            var sessionProvider = new RSessionProvider();
            // ReSharper disable once AccessToDisposedClosure
            Action a = () => sessionProvider.GetOrCreate(new Guid());
            a.ShouldNotThrow();

            sessionProvider.Dispose();
            a.ShouldThrow<InvalidOperationException>();
        }

        [Test]
        public void GetOrCreate() {
            using (var sessionProvider = new RSessionProvider()) {
                var guid = new Guid();
                var session1 = sessionProvider.GetOrCreate(guid);
                session1.Should().NotBeNull();

                var session2 = sessionProvider.GetOrCreate(guid);
                session2.Should().BeSameAs(session1);

                session1.Dispose();
                var session3 = sessionProvider.GetOrCreate(guid);
                session3.Should().NotBeSameAs(session1);
                session3.Id.Should().NotBe(session1.Id);
            }
        }

        [Test]
        public void ParallelAccess() {
            using (var sessionProvider = new RSessionProvider()) {
                var guids = new[] { new Guid(), new Guid() };
                ParallelTools.Invoke(100, i => {
                    var session = sessionProvider.GetOrCreate(guids[i % 2]);
                    session.Dispose();
                });
            }
        }

        [Test]
        public async Task ConnectWhenSwitching() {
            using (var sessionProvider = new RSessionProvider()) {
                var guid1 = new Guid();
                var guid2 = new Guid();
                var session1 = sessionProvider.GetOrCreate(guid1);
                var session2 = sessionProvider.GetOrCreate(guid2);
                var switchTask = sessionProvider.TrySwitchBrokerAsync(nameof(RSessionProviderTest) + nameof(ConnectWhenSwitching));

                var startHost1Task = Task.Delay(50).ContinueWith(t => session1.EnsureHostStartedAsync(new RHostStartupInfo {
                    Name = nameof(session1)
                }, null, 1000)).Unwrap();

                var startHost2Task = Task.Delay(100).ContinueWith(t => session2.EnsureHostStartedAsync(new RHostStartupInfo {
                    Name = nameof(session2)
                }, null, 1000)).Unwrap();

                await Task.WhenAll(switchTask, startHost1Task, startHost2Task);

                startHost1Task.Status.Should().Be(TaskStatus.RanToCompletion);
                startHost2Task.Status.Should().Be(TaskStatus.RanToCompletion);

                await Task.WhenAll(session1.HostStarted, session2.HostStarted);
            }
        }

        [Test]
        public async Task SwitchWhenConnecting() {
            using (var sessionProvider = new RSessionProvider()) {
                var guid = new Guid();
                var session = sessionProvider.GetOrCreate(guid);

                await sessionProvider.TrySwitchBrokerAsync(nameof(RSessionProviderTest) + nameof(SwitchWhenConnecting));

                var startHostTask = session.EnsureHostStartedAsync(new RHostStartupInfo {
                    Name = nameof(session)
                }, null, 1000);

                await Task.Yield();

                var switch1Task = sessionProvider.TrySwitchBrokerAsync(nameof(RSessionProviderTest) + nameof(SwitchWhenConnecting) + "1");
                var switch2Task = sessionProvider.TrySwitchBrokerAsync(nameof(RSessionProviderTest) + nameof(SwitchWhenConnecting) + "2");

                await Task.WhenAll(startHostTask, switch1Task, switch2Task);

                switch1Task.Status.Should().Be(TaskStatus.RanToCompletion);
                switch2Task.Status.Should().Be(TaskStatus.RanToCompletion);
                startHostTask.Status.Should().Be(TaskStatus.RanToCompletion);

                await session.HostStarted;
                session.HostStarted.Status.Should().Be(TaskStatus.RanToCompletion);
            }
        }
    }
}
