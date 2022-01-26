﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Libplanet.Crypto;
using Libplanet.Net;
using Libplanet.Net.Messages;
using Libplanet.Net.Transports;
using Nito.AsyncEx;
using Serilog;

namespace Libplanet.Seed.Executable.Net
{
    public class Seed
    {
        private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _peerLifetime = TimeSpan.FromSeconds(120);
        private readonly TimeSpan _pingTimeout = TimeSpan.FromSeconds(5);

        private readonly PrivateKey _privateKey;
        private readonly ITransport _transport;
        private readonly CancellationTokenSource _runtimeCancellationTokenSource;
        private readonly ILogger _logger;

        public Seed(
            PrivateKey privateKey,
            string? host,
            int? port,
            int workers,
            IceServer[] iceServers,
            AppProtocolVersion appProtocolVersion,
            string transportType)
        {
            _privateKey = privateKey;
            _runtimeCancellationTokenSource = new CancellationTokenSource();
            switch (transportType)
            {
                case "tcp":
                    _transport = new TcpTransport(
                        privateKey,
                        appProtocolVersion,
                        null,
                        host: host,
                        listenPort: port,
                        iceServers: iceServers,
                        differentAppProtocolVersionEncountered: null);
                    break;
                case "netmq":
                    _transport = new NetMQTransport(
                        privateKey,
                        appProtocolVersion,
                        null,
                        workers: workers,
                        host: host,
                        listenPort: port,
                        iceServers: iceServers,
                        differentAppProtocolVersionEncountered: null);
                    break;
                default:
                    Log.Error(
                        "-t/--transport-type must be either \"tcp\" or \"netmq\".");
                    Environment.Exit(1);
                    return;
            }

            PeerInfos = new ConcurrentDictionary<Address, PeerInfo>();
            _transport.ProcessMessageHandler.Register(ReceiveMessageAsync);

            _logger = Log.ForContext<Seed>();
        }

        public ConcurrentDictionary<Address, PeerInfo> PeerInfos { get; }

        private IEnumerable<BoundPeer> Peers =>
            PeerInfos.Values.Select(peerState => peerState.BoundPeer);

        public async Task StartAsync(
            HashSet<BoundPeer> staticPeers,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task>
            {
                StartTransportAsync(cancellationToken),
                RefreshTableAsync(cancellationToken),
            };
            if (staticPeers.Any())
            {
                tasks.Add(CheckStaticPeersAsync(staticPeers, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }

        public async Task StopAsync(TimeSpan waitFor)
        {
            await _transport.StopAsync(waitFor);
        }

        private async Task StartTransportAsync(CancellationToken cancellationToken)
        {
            await _transport.StartAsync(cancellationToken);
            Task task = _transport.StartAsync(cancellationToken);
            await _transport.WaitForRunningAsync();
            await task;
        }

        private async Task ReceiveMessageAsync(Message message)
        {
            switch (message)
            {
                case Ping ping:
                    var pong = new Pong { Identity = ping.Identity };
                    await _transport.ReplyMessageAsync(pong, _runtimeCancellationTokenSource.Token);

                    break;

                case FindNeighbors findNeighbors:
                    var neighbors = new Neighbors(Peers) { Identity = findNeighbors.Identity };
                    await _transport.ReplyMessageAsync(
                        neighbors,
                        _runtimeCancellationTokenSource.Token);
                    break;
            }

            if (message.Remote is BoundPeer boundPeer)
            {
                AddOrUpdate(boundPeer);
            }
        }

        private async Task AddPeersAsync(
            BoundPeer[] peers,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            IEnumerable<Task> tasks = peers.Select(async peer =>
                {
                    try
                    {
                        var ping = new Ping();
                        var stopwatch = new Stopwatch();
                        stopwatch.Start();
                        Message? reply = await _transport.SendMessageWithReplyAsync(
                            peer,
                            ping,
                            timeout,
                            cancellationToken);
                        TimeSpan elapsed = stopwatch.Elapsed;
                        stopwatch.Stop();

                        if (reply is Pong)
                        {
                            AddOrUpdate(peer, elapsed);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        _logger.Error(
                            e,
                            "Unexpected error occurred during ping to {Peer}.",
                            peer);
                    }
                });

            await tasks.WhenAll();
        }

        private PeerInfo AddOrUpdate(BoundPeer peer, TimeSpan? latency = null)
        {
            PeerInfo peerInfo;
            peerInfo.BoundPeer = peer;
            peerInfo.LastUpdated = DateTimeOffset.UtcNow;
            peerInfo.Latency = latency;
            _logger.Debug(
                "Update peer: {@Peer} {@LastUpdated} {@Latency}",
                peerInfo.BoundPeer,
                peerInfo.LastUpdated,
                peerInfo.Latency);
            return PeerInfos.AddOrUpdate(
                peer.Address,
                peerInfo,
                (address, info) => peerInfo);
        }

        private async Task RefreshTableAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_refreshInterval, cancellationToken);
                    IEnumerable<BoundPeer> peersToUpdate = PeerInfos.Values
                        .Where(
                            peerState => DateTimeOffset.UtcNow - peerState.LastUpdated >
                                         _peerLifetime)
                        .Select(state => state.BoundPeer);
                    await AddPeersAsync(peersToUpdate.ToArray(), _pingTimeout, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Log.Warning(
                        e,
                        "Unexpected exception occurred during {FName}().",
                        nameof(RefreshTableAsync));
                }
            }
        }

        private async Task CheckStaticPeersAsync(
            IEnumerable<BoundPeer> peers,
            CancellationToken cancellationToken)
        {
            var boundPeers = peers as BoundPeer[] ?? peers.ToArray();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    Log.Warning("Checking static peers. {@Peers}", boundPeers);
                    var peersToAdd = boundPeers.Where(peer => !Peers.Contains(peer)).ToArray();
                    if (peersToAdd.Any())
                    {
                        Log.Warning("Some of peers are not in routing table. {@Peers}", peersToAdd);
                        await AddPeersAsync(
                            peersToAdd,
                            _pingTimeout,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Log.Warning(
                        e,
                        "Unexpected exception occurred during {FName}().",
                        nameof(CheckStaticPeersAsync));
                }
            }
        }
    }
}
