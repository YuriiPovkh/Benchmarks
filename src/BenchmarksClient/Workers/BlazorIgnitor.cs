﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Benchmarks.ClientJob;
using Ignitor;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BenchmarksClient.Workers
{
    public partial class BlazorIgnitor : IWorker
    {
        private ClientJob _job;
        private HttpClientHandler _httpClientHandler;
        private List<BlazorClient> _clients;
        private List<IDisposable> _recvCallbacks;
        private Stopwatch _workTimer = new Stopwatch();
        private bool _stopped;
        private SemaphoreSlim _lock = new SemaphoreSlim(1);
        private bool _detailedLatency;
        private string _scenario;
        private int _totalRequests;
        private HttpClient _httpClient;
        private CancellationTokenSource _cancelationTokenSource;

        public string JobLogText { get; set; }

        private Task InitializeJob()
        {
            _stopped = false;

            _detailedLatency = _job.Latency == null;

            Debug.Assert(_job.Connections > 0, "There must be more than 0 connections");

            // Configuring the http client to trust the self-signed certificate
            _httpClientHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            var jobLogText =
                $"[ID:{_job.Id} Connections:{_job.Connections} Duration:{_job.Duration} Method:{_job.Method} ServerUrl:{_job.ServerBenchmarkUri}";

            if (_job.Headers != null)
            {
                jobLogText += $" Headers:{JsonConvert.SerializeObject(_job.Headers)}";
            }

            if (_job.ClientProperties.TryGetValue("Scenario", out var scenario))
            {
                _scenario = scenario;
                jobLogText += $" Scenario:{scenario}";
            }
            else
            {
                throw new Exception("Scenario wasn't specified");
            }

            jobLogText += "]";
            JobLogText = jobLogText;
            if (_clients == null)
            {
                CreateConnections();
            }

            return Task.CompletedTask;
        }

        public async Task StartJobAsync(ClientJob job)
        {
            _job = job;
            Log($"Starting Job");
            await InitializeJob();
            // start connections
            var tasks = new List<Task>(_clients.Count);
            foreach (var item in _clients)
            {
                tasks.Add(item.InitializeAsync(default));
            }

            await Task.WhenAll(tasks);

            _job.State = ClientState.Running;
            _job.LastDriverCommunicationUtc = DateTime.UtcNow;
            _cancelationTokenSource = new CancellationTokenSource();
            _cancelationTokenSource.CancelAfter(TimeSpan.FromSeconds(_job.Duration));

            _workTimer.Restart();

            try
            {
                switch (_scenario)
                {
                    case "Navigator":
                        await Navigator(_cancelationTokenSource.Token);
                        break;

                    case "Clicker":
                        await Clicker(_cancelationTokenSource.Token);
                        break;

                    case "Rogue":
                        await Rogue(_cancelationTokenSource.Token);
                        break;


                    //case "Reconnects":
                    //    await Reconnects(_cancelationTokenSource.Token);
                    //    break;

                    case "BlazingPizza":
                        await BlazingPizza(_cancelationTokenSource.Token);
                        break;

                    default:
                        throw new Exception($"Scenario '{_scenario}' is not a known scenario.");
                }
            }
            catch (Exception ex)
            {
                var text = "Exception from test: " + ex.Message;
                Log(text);
                _job.Error += Environment.NewLine + text;
            }

            _cancelationTokenSource.Token.WaitHandle.WaitOne();
            await StopJobAsync();
        }

        public async Task StopJobAsync()
        {
            _cancelationTokenSource?.Cancel();

            Log($"Stopping Job: {_job.SpanId}");
            if (_stopped || !await _lock.WaitAsync(0))
            {
                // someone else is stopping, we only need to do it once
                return;
            }
            try
            {
                _stopped = true;
                _workTimer.Stop();
                CalculateStatistics();
            }
            finally
            {
                _lock.Release();
                _job.State = ClientState.Completed;
                _job.ActualDuration = _workTimer.Elapsed;
            }
        }

        // We want to move code from StopAsync into Release(). Any code that would prevent
        // us from reusing the connnections.
        public async Task DisposeAsync()
        {
            foreach (var callback in _recvCallbacks)
            {
                // stops stat collection from happening quicker than StopAsync
                // and we can do all the calculations while close is occurring
                callback.Dispose();
            }

            // stop connections
            Log("Stopping connections");
            var tasks = new List<Task>(_clients.Count);
            foreach (var item in _clients)
            {
                tasks.Add(item.DisposeAsync());
            }

            await Task.WhenAll(tasks);
            Log("Connections have been disposed");

            _httpClientHandler.Dispose();
            _httpClient.Dispose();
            // TODO: Remove when clients no longer take a long time to "cool down"
            await Task.Delay(5000);

            Log("Stopped worker");
        }

        public void Dispose()
        {
            var tasks = new List<Task>(_clients.Count);
            foreach (var item in _clients)
            {
                tasks.Add(item.DisposeAsync());
            }

            Task.WhenAll(tasks).GetAwaiter().GetResult();

            _httpClientHandler.Dispose();
        }

        private void CreateConnections()
        {
            _clients = new List<BlazorClient>(_job.Connections);


            _httpClient = new HttpClient { BaseAddress = new Uri(_job.ServerBenchmarkUri) };

            _recvCallbacks = new List<IDisposable>(_job.Connections);
            for (var i = 0; i < _job.Connections; i++)
            {
                var connection = CreateHubConnection();
                _clients.Add(new BlazorClient(connection, _job.ServerBenchmarkUri) { HttpClient = _httpClient });
            }
        }

        private HubConnection CreateHubConnection()
        {
            var baseUri = new Uri(_job.ServerBenchmarkUri);
            var builder = new HubConnectionBuilder();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHubProtocol, IgnitorMessagePackHubProtocol>());
            builder.WithUrl(new Uri(baseUri, "_blazor/"), HttpTransportType.LongPolling);

            if (_job.ClientProperties.TryGetValue("LogLevel", out var logLevel))
            {
                if (Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, result: out var level))
                {
                    builder.ConfigureLogging(builder =>
                    {
                        builder.SetMinimumLevel(level);
                    });
                }
            }

            var connection = builder.Build();
            return connection;
        }

        private void CalculateStatistics()
        {
            // RPS
            var newTotalRequests = 0;
            var min = 0;
            var max = 0;
            foreach (var client in _clients)
            {
                newTotalRequests += client.Requests;
                max = Math.Max(max, client.Requests);
                min = Math.Max(min, client.Requests);
            }

            var requestDelta = newTotalRequests - _totalRequests;
            _totalRequests = newTotalRequests;

            // Review: This could be interesting information, see the gap between most active and least active connection
            // Ideally they should be within a couple percent of each other, but if they aren't something could be wrong
            Log($"Least Requests per Connection: {min}");
            Log($"Most Requests per Connection: {max}");

            if (_workTimer.ElapsedMilliseconds <= 0)
            {
                Log("Job failed to run");
                return;
            }

            var rps = (double)requestDelta / _workTimer.ElapsedMilliseconds * 1000;
            Log($"Total RPS: {rps}");
            _job.RequestsPerSecond = rps;
            _job.Requests = requestDelta;

            // Latency
            CalculateLatency();
        }

        private void CalculateLatency()
        {
            if (_detailedLatency)
            {
                var totalCount = 0;
                var totalSum = 0.0;
                var allConnections = new List<double>();
                foreach (var client in _clients)
                {
                    totalCount += client.LatencyPerConnection.Count;
                    totalSum += client.LatencyPerConnection.Sum();

                    allConnections.AddRange(client.LatencyPerConnection);
                }

                _job.Latency.Average = totalSum / totalCount;

                // Review: Each connection can have different latencies, how do we want to deal with that?
                // We could just combine them all and ignore the fact that they are different connections
                // Or we could preserve the results for each one and record them separately
                allConnections.Sort();
                _job.Latency.Within50thPercentile = GetPercentile(50, allConnections);
                _job.Latency.Within75thPercentile = GetPercentile(75, allConnections);
                _job.Latency.Within90thPercentile = GetPercentile(90, allConnections);
                _job.Latency.Within99thPercentile = GetPercentile(99, allConnections);
                _job.Latency.MaxLatency = GetPercentile(100, allConnections);
            }
            else
            {
                var totalSum = 0.0;
                var totalCount = 0;
                foreach (var client in _clients)
                {
                    totalSum += client.LatencyAverage.sum;
                    totalCount += client.LatencyAverage.count;
                }

                if (totalCount != 0)
                {
                    totalSum /= totalCount;
                }
                _job.Latency.Average = totalSum;
            }
        }

        private double GetPercentile(int percent, List<double> sortedData)
        {
            if (percent == 100)
            {
                return sortedData[sortedData.Count - 1];
            }

            var i = ((long)percent * sortedData.Count) / 100.0 + 0.5;
            var fractionPart = i - Math.Truncate(i);

            return (1.0 - fractionPart) * sortedData[(int)Math.Truncate(i) - 1] + fractionPart * sortedData[(int)Math.Ceiling(i) - 1];
        }

        private static void Log(string message)
        {
            var time = DateTime.Now.ToString("hh:mm:ss.fff");
            Console.WriteLine($"[{time}] {message}");
        }
    }
}
