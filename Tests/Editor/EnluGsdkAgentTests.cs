#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Enlu.Tests
{
    public sealed class EnluGsdkAgentTests
    {
        private static GsdkConfig RemoteConfig() => GsdkConfig.FromEnvironment(new Hashtable
        {
            ["ENLU_GSDK_URL"] = "http://127.0.0.1:1",
            ["ENLU_GSDK_TOKEN"] = "t",
            ["ENLU_INSTANCE_ID"] = "in_1",
        });

        /// <summary>A scripted node agent that records every heartbeat.</summary>
        private sealed class ScriptedAgent
        {
            public readonly List<GsdkHeartbeatRequest> Seen = new List<GsdkHeartbeatRequest>();
            public readonly Queue<Func<GsdkHeartbeatResponse>> Script = new Queue<Func<GsdkHeartbeatResponse>>();

            public Task<GsdkHeartbeatResponse> Send(GsdkHeartbeatRequest request, CancellationToken ct)
            {
                Seen.Add(request);
                var next = Script.Count > 0 ? Script.Dequeue() : () => new GsdkHeartbeatResponse();
                return Task.FromResult(next());
            }
        }

        private static GsdkHeartbeatResponse Active(string session = "ss_1")
        {
            var r = new GsdkHeartbeatResponse { Operation = GsdkOperation.Active, SessionId = session };
            r.SessionMetadata["map"] = "dunes";
            r.InitialPlayers.Add("pl_a");
            return r;
        }

        [Test]
        public void FollowsTheLifecycle()
        {
            var agent = new ScriptedAgent();
            var gsdk = new EnluGsdkAgent(RemoteConfig(), agent.Send);
            SessionInfo? allocated = null;
            var allocations = 0;
            var shutdowns = 0;
            gsdk.Allocated += s =>
            {
                allocated = s;
                allocations++;
            };
            gsdk.ShutdownRequested += () => shutdowns++;

            Assert.That(gsdk.IsLocalMode, Is.False);
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(agent.Seen[0].State, Is.EqualTo(GameServerState.Initializing));

            gsdk.ReadyForPlayers();
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(agent.Seen[1].State, Is.EqualTo(GameServerState.StandingBy));
            Assert.That(allocated, Is.Null);
            var wait = gsdk.WaitForAllocationAsync();
            Assert.That(wait.IsCompleted, Is.False);

            agent.Script.Enqueue(() => Active());
            agent.Script.Enqueue(() => Active());
            gsdk.TickAsync().GetAwaiter().GetResult();
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(allocations, Is.EqualTo(1), "allocation fires once even if the agent repeats Active");
            Assert.That(allocated!.SessionId, Is.EqualTo("ss_1"));
            Assert.That(allocated.GetMetadata("map"), Is.EqualTo("dunes"));
            Assert.That(allocated.InitialPlayers, Is.EqualTo(new[] { "pl_a" }));
            Assert.That(gsdk.State, Is.EqualTo(GameServerState.Active));
            Assert.That(agent.Seen[3].State, Is.EqualTo(GameServerState.Active));
            Assert.That(wait.GetAwaiter().GetResult().SessionId, Is.EqualTo("ss_1"));

            gsdk.UpdateConnectedPlayers(new[] { "pl_a", "", "pl_b" });
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(agent.Seen[4].Players, Is.EqualTo(new List<string> { "pl_a", "pl_b" }));

            agent.Script.Enqueue(() => new GsdkHeartbeatResponse { Operation = GsdkOperation.Terminate });
            agent.Script.Enqueue(() => new GsdkHeartbeatResponse { Operation = GsdkOperation.Terminate });
            gsdk.TickAsync().GetAwaiter().GetResult();
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(shutdowns, Is.EqualTo(1));
            Assert.That(gsdk.State, Is.EqualTo(GameServerState.Terminating));
        }

        [Test]
        public void ReportsHealth()
        {
            var agent = new ScriptedAgent();
            var healthy = true;
            var gsdk = new EnluGsdkAgent(RemoteConfig(), agent.Send) { HealthCheck = () => healthy };
            gsdk.TickAsync().GetAwaiter().GetResult();
            healthy = false;
            gsdk.TickAsync().GetAwaiter().GetResult();
            gsdk.HealthCheck = () => throw new InvalidOperationException("boom");
            var logged = new List<string>();
            var sink = EnluLog.Sink;
            EnluLog.Sink = (level, message) => logged.Add(level + " " + message);
            try
            {
                gsdk.TickAsync().GetAwaiter().GetResult();
            }
            finally
            {
                EnluLog.Sink = sink;
            }
            Assert.That(logged.Exists(l => l.StartsWith("Error") && l.Contains("boom")), Is.True);
            Assert.That(agent.Seen[0].Healthy, Is.True);
            Assert.That(agent.Seen[1].Healthy, Is.False);
            Assert.That(agent.Seen[2].Healthy, Is.False, "a throwing health check reports unhealthy");
        }

        [Test]
        public void CountsFailedHeartbeatsWithoutThrowing()
        {
            var calls = 0;
            var gsdk = new EnluGsdkAgent(RemoteConfig(), (r, ct) =>
            {
                calls++;
                if (calls <= 2) throw new EnluException("network_error", "down", 0);
                return Task.FromResult(new GsdkHeartbeatResponse());
            });
            var log = EnluLog.Sink;
            EnluLog.Sink = null;
            try
            {
                Assert.That(gsdk.TickAsync().GetAwaiter().GetResult(), Is.Null);
                Assert.That(gsdk.TickAsync().GetAwaiter().GetResult(), Is.Null);
                Assert.That(gsdk.ConsecutiveFailures, Is.EqualTo(2));
                Assert.That(gsdk.TickAsync().GetAwaiter().GetResult(), Is.Not.Null);
                Assert.That(gsdk.ConsecutiveFailures, Is.EqualTo(0));
            }
            finally
            {
                EnluLog.Sink = log;
            }
        }

        [Test]
        public void ShutdownBeforeAllocationCancelsTheWait()
        {
            var agent = new ScriptedAgent();
            var gsdk = new EnluGsdkAgent(RemoteConfig(), agent.Send);
            gsdk.ReadyForPlayers();
            var wait = gsdk.WaitForAllocationAsync();
            agent.Script.Enqueue(() => new GsdkHeartbeatResponse { Operation = GsdkOperation.Terminate });
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(() => wait.GetAwaiter().GetResult(), Throws.InstanceOf<OperationCanceledException>());

            agent.Script.Enqueue(() => Active());
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(gsdk.Session, Is.Null, "a terminating server is never allocated");
        }

        [Test]
        public void LocalModeAllocatesAfterReadyForPlayers()
        {
            var gsdk = new EnluGsdkAgent(GsdkConfig.FromEnvironment(new Hashtable()));
            gsdk.LocalMetadata["map"] = "test";
            SessionInfo? allocated = null;
            gsdk.Allocated += s => allocated = s;
            Assert.That(gsdk.IsLocalMode, Is.True);

            for (var i = 0; i < 5; i++) gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(allocated, Is.Null, "never allocated before ReadyForPlayers");

            gsdk.ReadyForPlayers();
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(allocated, Is.Null);
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(allocated, Is.Not.Null);
            Assert.That(allocated!.SessionId, Does.StartWith("local-"));
            Assert.That(allocated.GetMetadata("map"), Is.EqualTo("test"));
            Assert.That(gsdk.State, Is.EqualTo(GameServerState.Active));
            Assert.That(gsdk.SendFinalHeartbeat(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(gsdk.State, Is.EqualTo(GameServerState.Terminating));
        }

        [Test]
        public void LocalModeCanWaitForManualAllocation()
        {
            var gsdk = new EnluGsdkAgent(GsdkConfig.FromEnvironment(new Hashtable())) { LocalAutoAllocate = false };
            gsdk.ReadyForPlayers();
            for (var i = 0; i < 5; i++) gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(gsdk.State, Is.EqualTo(GameServerState.StandingBy));
        }

        [Test]
        public void DispatcherReceivesEvents()
        {
            var agent = new ScriptedAgent();
            var queued = new List<Action>();
            var gsdk = new EnluGsdkAgent(RemoteConfig(), agent.Send, queued.Add);
            var fired = false;
            gsdk.Allocated += _ => fired = true;
            gsdk.ReadyForPlayers();
            agent.Script.Enqueue(() => Active());
            gsdk.TickAsync().GetAwaiter().GetResult();
            Assert.That(fired, Is.False, "handlers wait for the dispatcher");
            Assert.That(queued.Count, Is.EqualTo(1));
            queued[0]();
            Assert.That(fired, Is.True);
        }
    }
}
