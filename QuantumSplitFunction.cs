// ============================================================
//  QuantumSplitFunction.cs
//  Azure Function App — HTTP Trigger
//
//  Exposes two endpoints:
//    POST /api/quantum-seed        → returns single random seed
//    POST /api/quantum-split       → returns full split metadata
//
//  Called by ZenML Python step to replace classical random_state=42
// ============================================================

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Quantum.Simulation.Simulators;

// Q# namespace — generated from Library.qs at build time
using QuantumTrainTestSplit;

namespace QuantumAzureFunction
{
    // ----------------------------------------------------------
    // Request / Response DTOs
    // ----------------------------------------------------------

    /// <summary>Request body for /api/quantum-split</summary>
    public class SplitRequest
    {
        [JsonPropertyName("total_samples")]
        public int TotalSamples { get; set; } = 1000;

        [JsonPropertyName("test_ratio_pct")]
        public int TestRatioPct { get; set; } = 20;
    }

    /// <summary>Request body for /api/quantum-seeds (k-fold)</summary>
    public class SeedsRequest
    {
        [JsonPropertyName("count")]
        public int Count { get; set; } = 5;
    }

    /// <summary>Response body returned to ZenML Python step</summary>
    public class SplitResponse
    {
        [JsonPropertyName("quantum_seed")]
        public long QuantumSeed { get; set; }

        [JsonPropertyName("train_count")]
        public int TrainCount { get; set; }

        [JsonPropertyName("test_count")]
        public int TestCount { get; set; }

        [JsonPropertyName("total_samples")]
        public int TotalSamples { get; set; }

        [JsonPropertyName("test_ratio_pct")]
        public int TestRatioPct { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "quantum-qrng";

        [JsonPropertyName("simulator")]
        public string Simulator { get; set; } = "FullStateSimulator";
    }

    /// <summary>Response for k-fold seeds endpoint</summary>
    public class SeedsResponse
    {
        [JsonPropertyName("seeds")]
        public long[] Seeds { get; set; } = [];

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = "quantum-qrng";
    }

    // ----------------------------------------------------------
    // Azure Function Class
    // ----------------------------------------------------------
    public class QuantumSplitFunction
    {
        private readonly ILogger _logger;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented      = true,
            PropertyNameCaseInsensitive = true
        };

        public QuantumSplitFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<QuantumSplitFunction>();
        }

        // ──────────────────────────────────────────────────────
        // ENDPOINT 1: POST /api/quantum-seed
        // Returns a single 31-bit quantum random integer.
        // ZenML step uses this as  random_state=seed
        //
        // Request body: (none required)
        // Response:     { "quantum_seed": 1748392016, "source": "quantum-qrng" }
        // ──────────────────────────────────────────────────────
        [Function("GenerateQuantumSeed")]
        public async Task<HttpResponseData> GenerateQuantumSeed(
            [HttpTrigger(AuthorizationLevel.Function, "post", "get",
                         Route = "quantum-seed")]
            HttpRequestData req)
        {
            _logger.LogInformation("QRNG seed request received.");

            long seed;

            try
            {
                // ── Run Q# operation on full-state quantum simulator ──
                // QuantumSimulator simulates ideal quantum hardware locally.
                // For real quantum hardware swap with AzureQuantumMachine.
                using var sim = new QuantumSimulator();

                // Call GenerateRandomSeed() from Library.qs
                // Q# returns QArray-mapped types; long is safe for Int
                seed = await GenerateRandomSeed.Run(sim);

                _logger.LogInformation("Q# QRNG generated seed: {Seed}", seed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Q# simulator error in GenerateQuantumSeed");
                var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errResp.WriteStringAsync(
                    JsonSerializer.Serialize(new { error = ex.Message }));
                return errResp;
            }

            // Build and return JSON response
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                quantum_seed = seed,
                source       = "quantum-qrng",
                simulator    = "FullStateSimulator"
            }, _jsonOpts));

            return response;
        }

        // ──────────────────────────────────────────────────────
        // ENDPOINT 2: POST /api/quantum-split
        // Returns full train-test split metadata using quantum seed.
        //
        // Request body:
        //   {
        //     "total_samples": 500000,
        //     "test_ratio_pct": 20
        //   }
        //
        // Response:
        //   {
        //     "quantum_seed": 1748392016,
        //     "train_count": 400000,
        //     "test_count":  100000,
        //     "total_samples": 500000,
        //     "test_ratio_pct": 20,
        //     "source": "quantum-qrng"
        //   }
        // ──────────────────────────────────────────────────────
        [Function("GenerateQuantumSplit")]
        public async Task<HttpResponseData> GenerateQuantumSplit(
            [HttpTrigger(AuthorizationLevel.Function, "post",
                         Route = "quantum-split")]
            HttpRequestData req)
        {
            _logger.LogInformation("Quantum split request received.");

            // ── Parse request body ──
            SplitRequest splitReq;
            try
            {
                var bodyJson = await req.ReadAsStringAsync() ?? "{}";
                splitReq = JsonSerializer.Deserialize<SplitRequest>(
                               bodyJson, _jsonOpts)
                           ?? new SplitRequest();
            }
            catch (JsonException)
            {
                splitReq = new SplitRequest();  // use defaults on bad JSON
            }

            _logger.LogInformation(
                "Split params — TotalSamples: {T}, TestRatio: {R}%",
                splitReq.TotalSamples, splitReq.TestRatioPct);

            long seed;
            long trainCount;
            long testCount;

            try
            {
                // ── Run Q# GenerateTrainTestSplit operation ──
                using var sim = new QuantumSimulator();

                var result = await GenerateTrainTestSplit.Run(
                    sim,
                    (long)splitReq.TotalSamples,
                    (long)splitReq.TestRatioPct
                );

                // Q# tuple return → C# ValueTuple
                (trainCount, testCount, seed) = result;

                _logger.LogInformation(
                    "Q# split result — Train: {Tr}, Test: {Te}, Seed: {S}",
                    trainCount, testCount, seed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Q# simulator error in GenerateQuantumSplit");
                var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errResp.WriteStringAsync(
                    JsonSerializer.Serialize(new { error = ex.Message }));
                return errResp;
            }

            // ── Build response ──
            var resp = new SplitResponse
            {
                QuantumSeed  = seed,
                TrainCount   = (int)trainCount,
                TestCount    = (int)testCount,
                TotalSamples = splitReq.TotalSamples,
                TestRatioPct = splitReq.TestRatioPct
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(
                JsonSerializer.Serialize(resp, _jsonOpts));

            return response;
        }

        // ──────────────────────────────────────────────────────
        // ENDPOINT 3: POST /api/quantum-seeds
        // Returns N quantum seeds for k-fold cross-validation.
        //
        // Request body:  { "count": 5 }
        // Response:      { "seeds": [12345, 67890, ...], "count": 5 }
        // ──────────────────────────────────────────────────────
        [Function("GenerateQuantumSeeds")]
        public async Task<HttpResponseData> GenerateQuantumSeeds(
            [HttpTrigger(AuthorizationLevel.Function, "post",
                         Route = "quantum-seeds")]
            HttpRequestData req)
        {
            _logger.LogInformation("Quantum k-fold seeds request received.");

            SeedsRequest seedsReq;
            try
            {
                var bodyJson = await req.ReadAsStringAsync() ?? "{}";
                seedsReq = JsonSerializer.Deserialize<SeedsRequest>(
                               bodyJson, _jsonOpts)
                           ?? new SeedsRequest();
            }
            catch (JsonException)
            {
                seedsReq = new SeedsRequest();
            }

            // Safety cap — don't run hundreds of Q# operations in one call
            var count = Math.Clamp(seedsReq.Count, 1, 20);

            long[] seeds;

            try
            {
                using var sim = new QuantumSimulator();

                // Call GenerateMultipleSeeds from Library.qs
                var qResult = await GenerateMultipleSeeds.Run(sim, (long)count);

                // Convert QArray<long> → long[]
                seeds = qResult.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Q# simulator error in GenerateQuantumSeeds");
                var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errResp.WriteStringAsync(
                    JsonSerializer.Serialize(new { error = ex.Message }));
                return errResp;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(
                new SeedsResponse { Seeds = seeds, Count = seeds.Length },
                _jsonOpts));

            return response;
        }
    }
}
