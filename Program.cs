using System.Net;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.Ads.TypeSystem;
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
        public int PollIntervalMs { get; set; } = 5000; // Default 5s
    }

    class Program
    {

        static async Task Main(string[] args)
        {
            // Default values if no arguments provided
            string netId = args.Length > 0 ? args[0] : "127.0.0.1.1.1";
            int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 851;

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                Console.WriteLine($"Starting Bridge targeting NetID: {netId} Port: {port}");
                await RunBridgeAsync(netId, port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Shutdown requested.");
            }
        }

        static async Task RunBridgeAsync(string netId, int port, CancellationToken ct)
        {
            using var adsClient = new AdsClient();

            while (!adsClient.IsConnected && !ct.IsCancellationRequested)
            {
                try
                {
                    adsClient.Connect(netId, port);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Connection failed: {ex.Message}. Retrying...");
                }

                if (!adsClient.IsConnected)
                    await Task.Delay(5000, ct);
            }

            // Discover Jobs
            var jobs = ScanForSnmpVariables(adsClient);
            if (jobs.Count > 0)
            {
                Console.WriteLine($"Discovered {jobs.Count} SNMP jobs:");
                foreach (var job in jobs)
                    Console.WriteLine($"- {job.SymbolName}: OID={job.Oid}, IP={job.IpAddress}, Poll={job.PollIntervalMs}ms");
            }
            if (jobs.Count == 0)
            {
                Console.WriteLine("No SNMP jobs found. Ensure symbols have correct attributes.");
                return;
            }

            // Each job runs on its own cadence
            var tasks = jobs.Select(job => RunPollLoopAsync(adsClient, job, ct));

            await Task.WhenAll(tasks);
        }

        private static async Task RunPollLoopAsync(IAdsConnection client, SnmpJob job, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(job.PollIntervalMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    // SNMP Read
                    var data = await Task.Run(() => ReadSnmpV1(job.IpAddress, job.Community, job.Oid), ct);

                    // ADS Write
                    await client.WriteValueAsync(job.SymbolName, data.ToString(), ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:T}] Error {job.SymbolName} ({job.IpAddress}): {ex.Message}");
                    await Task.Delay(2000, ct);
                }
            }
        }

        static List<SnmpJob> ScanForSnmpVariables(AdsClient client)
        {
            var jobList = new List<SnmpJob>();
            var settings = new SymbolLoaderSettings(SymbolsLoadMode.Flat);
            ISymbolLoader symbolLoader = SymbolLoaderFactory.Create(client, settings);

            foreach (var symbol in symbolLoader.Symbols)
            {
                var oidAttr = symbol.Attributes.FirstOrDefault(a => a.Name.Equals("SnmpOid", StringComparison.OrdinalIgnoreCase));
                if (oidAttr == null) continue;

                var ipAttr = symbol.Attributes.FirstOrDefault(a => a.Name.Equals("SnmpIp", StringComparison.OrdinalIgnoreCase));
                var pollAttr = symbol.Attributes.FirstOrDefault(a => a.Name.Equals("SnmpPollMs", StringComparison.OrdinalIgnoreCase));
                var commAttr = symbol.Attributes.FirstOrDefault(a => a.Name.Equals("SnmpCommunity", StringComparison.OrdinalIgnoreCase));

                if (ipAttr != null)
                {
                    jobList.Add(new SnmpJob
                    {
                        SymbolName = symbol.InstancePath,
                        Oid = oidAttr.Value,
                        IpAddress = ipAttr.Value,
                        Community = commAttr?.Value ?? "public",
                        PollIntervalMs = int.TryParse(pollAttr?.Value, out int ms) ? ms : 5000
                    });
                }
            }
            return jobList;
        }

        static ISnmpData ReadSnmpV1(string ip, string community, string oid)
        {
            // Increased timeout for production reliability
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
}