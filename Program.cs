using System.Net;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace SnmpTc3Bridge
{
    public class SnmpJob
    {
        public string SymbolName { get; set; } = string.Empty;
        public string Oid { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Community { get; set; } = "public";
        public int PollIntervalMs { get; set; } = 5000;
    }

    internal sealed class BridgeSession : IAsyncDisposable
    {
        private readonly AdsClient _client;
        private readonly CancellationTokenSource _sessionCts;
        private readonly Task[] _pollTasks;

        public BridgeSession(AdsClient client, IEnumerable<SnmpJob> jobs, CancellationToken parentToken)
        {
            _client = client;
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _pollTasks = jobs.Select(job => RunPollLoopAsync(job, _sessionCts.Token)).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _sessionCts.Cancel();
            try
            {
                await Task.WhenAll(_pollTasks);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _sessionCts.Dispose();
            }
        }

        private async Task RunPollLoopAsync(SnmpJob job, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(job.PollIntervalMs));

            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var data = await Task.Run(() => ReadSnmpV1(job.IpAddress, job.Community, job.Oid), ct);
                    await _client.WriteValueAsync(job.SymbolName, data.ToString(), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:T}] Poll error for {job.SymbolName} ({job.IpAddress}): {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
        }

        private static ISnmpData ReadSnmpV1(string ip, string community, string oid)
        {
            var result = Messenger.Get(VersionCode.V1,
                new IPEndPoint(IPAddress.Parse(ip), 161),
                new OctetString(community),
                new List<Variable> { new Variable(new ObjectIdentifier(oid)) },
                3000);

            if (result != null && result.Count > 0)
                return result[0].Data;

            throw new Exception("Timeout or empty response.");
        }
    }

    class Program
    {
        static string? lastConnectionError;

        static async Task Main(string[] args)
        {
            string netId = args.Length > 0 ? args[0] : "127.0.0.1.1.1";
            int port = args.Length > 1 && int.TryParse(args[1], out int parsedPort) ? parsedPort : 851;

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                Console.WriteLine($"Starting bridge targeting NetID: {netId} Port: {port}");
                await RunBridgeLifecycleAsync(netId, port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Shutdown requested.");
            }
        }

        private static async Task RunBridgeLifecycleAsync(string netId, int port, CancellationToken ct)
        {
            BridgeSession? activeSession = null;
            AdsClient? adsClient = null;

            bool wasConnected = false;
            AdsState? lastAdsState = null;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (adsClient == null)
                    {
                        adsClient = new AdsClient();
                        wasConnected = false;
                        lastAdsState = null;
                    }

                    if (!adsClient.IsConnected)
                    {
                        if (wasConnected)
                        {
                            Console.WriteLine("ADS disconnected.");
                            wasConnected = false;
                            lastConnectionError = null;
                        }

                        await StopSessionAsync(activeSession);
                        activeSession = null;

                        try
                        {
                            adsClient.Connect(netId, port);

                            var stateInfo = adsClient.ReadState();
                            var currentState = stateInfo.AdsState;

                            Console.WriteLine($"ADS connected. PLC state: {currentState}");

                            wasConnected = true;
                            lastAdsState = currentState;
                            lastConnectionError = null;
                        }
                        catch (Exception ex)
                        {
                            if (lastConnectionError != ex.Message)
                            {
                                Console.WriteLine($"ADS connection failed: {ex.Message}");
                                lastConnectionError = ex.Message;
                            }

                            SafeDispose(ref adsClient);
                            await Task.Delay(5000, ct);
                            continue;
                        }
                    }

                    var state = adsClient.ReadState().AdsState;

                    if (lastAdsState != state)
                    {
                        Console.WriteLine($"PLC state changed: {state}");
                        lastAdsState = state;
                    }

                    if (state == AdsState.Run)
                    {
                        if (activeSession == null)
                        {
                            var jobs = ScanForSnmpVariables(adsClient);

                            if (jobs.Count > 0)
                            {
                                Console.WriteLine($"Starting {jobs.Count} SNMP job(s).");

                                foreach (var job in jobs)
                                {
                                    Console.WriteLine(
                                        $"Symbol: {job.SymbolName}, OID: {job.Oid}, IP: {job.IpAddress}, Community: {job.Community}, Poll: {job.PollIntervalMs} ms"
                                    );
                                }

                                activeSession = new BridgeSession(adsClient, jobs, ct);
                            }
                            else
                            {
                                Console.WriteLine("PLC RUN detected, but no SNMP jobs configured.");
                            }
                        }
                    }
                    else
                    {
                        if (activeSession != null)
                        {
                            Console.WriteLine("PLC not in RUN. Stopping SNMP polling.");
                            await StopSessionAsync(activeSession);
                            activeSession = null;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ADS runtime error: {ex.Message}");

                    await StopSessionAsync(activeSession);
                    activeSession = null;

                    SafeDispose(ref adsClient);

                    await Task.Delay(5000, ct);
                }

                await Task.Delay(2000, ct);
            }

            await StopSessionAsync(activeSession);
            SafeDispose(ref adsClient);
        }

        private static void SafeDispose(ref AdsClient? client)
        {
            if (client != null)
            {
                try { client.Dispose(); }
                catch { }
                client = null;
            }
        }

        private static async Task StopSessionAsync(BridgeSession? session)
        {
            if (session != null)
                await session.DisposeAsync();
        }

        private static List<SnmpJob> ScanForSnmpVariables(AdsClient client)
        {
            var jobList = new List<SnmpJob>();
            var settings = new SymbolLoaderSettings(SymbolsLoadMode.Flat);
            ISymbolLoader symbolLoader = SymbolLoaderFactory.Create(client, settings);

            foreach (var symbol in symbolLoader.Symbols)
            {
                var oidAttr = symbol.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("SnmpOid", StringComparison.OrdinalIgnoreCase));
                if (oidAttr == null)
                    continue;

                var ipAttr = symbol.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("SnmpIp", StringComparison.OrdinalIgnoreCase));
                if (ipAttr == null)
                    continue;

                var pollAttr = symbol.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("SnmpPollMs", StringComparison.OrdinalIgnoreCase));

                var commAttr = symbol.Attributes.FirstOrDefault(a =>
                    a.Name.Equals("SnmpCommunity", StringComparison.OrdinalIgnoreCase));

                jobList.Add(new SnmpJob
                {
                    SymbolName = symbol.InstancePath,
                    Oid = oidAttr.Value,
                    IpAddress = ipAttr.Value,
                    Community = commAttr?.Value ?? "public",
                    PollIntervalMs = int.TryParse(pollAttr?.Value, out int intervalMs)
                        ? intervalMs
                        : 5000
                });
            }

            return jobList;
        }
    }
}