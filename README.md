# Quantum Train-Test Split — Setup Guide
## Q# Library → Azure Function App → ZenML Python Step

---

## Project Structure

```
quantum-traintestsplit/
│
├── Library.qs                     ← Q# quantum operations (QRNG)
├── QuantumSplitFunction.cs        ← C# Azure Function (HTTP endpoints)
├── Program.cs                     ← Isolated worker entry point
├── QuantumTrainTestSplit.csproj   ← Project file (Q# + Azure Functions)
├── host.json                      ← Azure Functions runtime config
├── local.settings.json            ← Local dev environment variables
│
└── zenml_quantum_split_step.py    ← ZenML Python step (calls Azure Fn)
```

---

## Step 1 — Prerequisites

Install in order:

1. **Visual Studio 2022** (Community is free)
   - Workload: "Azure development"
   - Workload: ".NET desktop development"

2. **QDK (Quantum Development Kit) VS extension**
   - Extensions → Manage Extensions → search "Microsoft Quantum Development Kit"
   - Install → restart VS

3. **Azure Functions Core Tools v4**
   ```
   npm install -g azure-functions-core-tools@4
   ```

4. **.NET 8 SDK**
   - https://dotnet.microsoft.com/download/dotnet/8.0

---

## Step 2 — Open in Visual Studio

1. Open `QuantumTrainTestSplit.csproj` in Visual Studio 2022
2. NuGet packages will restore automatically (takes ~2 min first time)
3. Build → Build Solution  (`Ctrl+Shift+B`)
   - The Q# compiler compiles `Library.qs` and generates C# shims
   - You will see `GenerateRandomSeed`, `GenerateTrainTestSplit` etc.
     appear as callable C# classes

---

## Step 3 — Run locally

1. Set `QuantumTrainTestSplit` as startup project
2. Press `F5` (or Run → Start Debugging)
3. Azure Functions Core Tools starts — you will see:

```
Functions:
  GenerateQuantumSeed:   [GET,POST] http://localhost:7071/api/quantum-seed
  GenerateQuantumSplit:  [POST]     http://localhost:7071/api/quantum-split
  GenerateQuantumSeeds:  [POST]     http://localhost:7071/api/quantum-seeds
```

4. Test with curl or Postman:

```bash
# Single seed
curl http://localhost:7071/api/quantum-seed

# Full split
curl -X POST http://localhost:7071/api/quantum-split \
  -H "Content-Type: application/json" \
  -d '{"total_samples": 500000, "test_ratio_pct": 20}'

# k-fold seeds
curl -X POST http://localhost:7071/api/quantum-seeds \
  -H "Content-Type: application/json" \
  -d '{"count": 5}'
```

Expected response:
```json
{
  "quantum_seed": 1748392016,
  "train_count": 400000,
  "test_count": 100000,
  "total_samples": 500000,
  "test_ratio_pct": 20,
  "source": "quantum-qrng",
  "simulator": "FullStateSimulator"
}
```

---

## Step 4 — Run ZenML Python step

```bash
# Install Python dependencies
pip install zenml scikit-learn pandas numpy requests

# Set Azure Function URL (local dev)
export QUANTUM_FUNCTION_URL=http://localhost:7071/api/quantum-split

# Quick test (no ZenML needed)
python zenml_quantum_split_step.py

# Run full ZenML pipeline
python -c "from zenml_quantum_split_step import quantum_nids_pipeline; quantum_nids_pipeline()"
```

---

## Step 5 — Deploy to Azure

1. In Visual Studio → right-click project → "Publish"
2. Target: Azure → Azure Function App (Windows or Linux)
3. Create new Function App:
   - Runtime: .NET 8 Isolated
   - Region: your choice
   - Plan: Consumption (serverless)

4. After deploy, get the Function URL from Azure Portal:
   ```
   https://<your-app>.azurewebsites.net/api/quantum-split?code=<your-key>
   ```

5. Update ZenML environment:
   ```bash
   export QUANTUM_FUNCTION_URL=https://<your-app>.azurewebsites.net/api/quantum-split
   export QUANTUM_FUNCTION_KEY=<your-function-key>
   ```

---

## What the Q# code does (explained simply)

```
Classical (before):     random_state = 42
                        └─ same split every run
                        └─ deterministic pseudo-random

Quantum (after):        random_state = Q# QRNG
                        └─ 31 qubits each put in superposition
                        └─ H gate: |0⟩ → (|0⟩ + |1⟩)/√2
                        └─ Measure → each collapses to 0 or 1
                        └─ 31 bits → integer seed
                        └─ true randomness from quantum mechanics
```

---

## Endpoints Summary

| Endpoint              | Method | Use case                          |
|-----------------------|--------|-----------------------------------|
| `/api/quantum-seed`   | GET/POST | Single QRNG seed for one split  |
| `/api/quantum-split`  | POST   | Full split metadata + seed        |
| `/api/quantum-seeds`  | POST   | Multiple seeds for k-fold CV      |
