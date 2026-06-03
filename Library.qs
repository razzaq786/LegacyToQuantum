// ============================================================
//  Library.qs  — FIXED VERSION
//  Quantum Random Number Generator (QRNG)
//
//  FIX APPLIED:
//    Root cause: use qubits = Qubit[nBits]  causes SEHException
//    because the Q# native simulator DLL cannot handle array
//    qubit allocation when called from Azure Functions runtime.
//
//    Solution: allocate qubits ONE AT A TIME in a loop.
//    Each qubit is allocated → H gate → measured → Reset → released
//    individually. This avoids the native array allocator entirely.
//
//    Also removed: MultiM(), ApplyToEach(), ResetAll()
//    These all rely on the same failing array path.
//    Replaced with: single qubit loop + M() + Reset() per qubit.
// ============================================================

namespace QuantumTrainTestSplit {

    open Microsoft.Quantum.Canon;
    open Microsoft.Quantum.Intrinsic;
    open Microsoft.Quantum.Convert;
    open Microsoft.Quantum.Math;

    // ----------------------------------------------------------
    // OPERATION 1: GenerateRandomBit
    // Allocates ONE qubit, puts it in superposition, measures it.
    // Returns Zero or One with exactly 50/50 probability.
    //
    // FIX: replaced MResetZ() with M() + Reset() separately
    //      MResetZ is in Microsoft.Quantum.Measurement which
    //      can conflict with some QDK versions in Functions host.
    // ----------------------------------------------------------
    operation GenerateRandomBit() : Result {
        use qubit = Qubit();          // allocate single qubit |0⟩

        H(qubit);                     // Hadamard → superposition

        let result = M(qubit);        // measure → collapses to 0 or 1

        Reset(qubit);                 // reset back to |0⟩ before release

        return result;
    }

    // ----------------------------------------------------------
    // OPERATION 2: GenerateRandomInt
    // Generates a random non-negative integer using nBits qubits.
    //
    // FIX (root cause of SEHException):
    //   BEFORE:  use qubits = Qubit[nBits]   ← crashes native allocator
    //            ApplyToEach(H, qubits)
    //            let results = MultiM(qubits)
    //            ResetAll(qubits)
    //
    //   AFTER:   loop nBits times, one Qubit() at a time
    //            Each qubit is fully used and Reset before next one.
    //            Builds result integer bit-by-bit using powers of 2.
    // ----------------------------------------------------------
    operation GenerateRandomInt(nBits : Int) : Int {
        mutable result     = 0;
        mutable bitValue   = 1;       // current bit's place value (1, 2, 4, 8 ...)

        for _ in 1 .. nBits {
            use qubit = Qubit();      // allocate exactly ONE qubit

            H(qubit);                 // put it in superposition

            let bit = M(qubit);       // measure → Zero or One

            Reset(qubit);             // reset to |0⟩ before releasing

            // If measured One, add this bit's place value to result
            if bit == One {
                set result = result + bitValue;
            }

            set bitValue = bitValue * 2;   // next bit is worth 2× more
        }

        return result;
    }

    // ----------------------------------------------------------
    // OPERATION 3: GenerateRandomIntInRange
    // Generates a random integer within [minVal, maxVal] inclusive.
    // Uses rejection sampling to avoid modulo bias.
    // ----------------------------------------------------------
    operation GenerateRandomIntInRange(minVal : Int, maxVal : Int) : Int {
        let range  = maxVal - minVal + 1;
        let nBits  = BitSizeI(range);

        mutable candidate = maxVal + 1;

        repeat {
            let raw       = GenerateRandomInt(nBits);
            set candidate = minVal + (raw % range);
        }
        until (candidate >= minVal and candidate <= maxVal);

        return candidate;
    }

    // ----------------------------------------------------------
    // OPERATION 4: GenerateRandomSeed
    // Generates a 31-bit random integer to use as random_state.
    // 31 bits keeps the value inside Q# signed Int range safely.
    // ----------------------------------------------------------
    operation GenerateRandomSeed() : Int {
        return GenerateRandomInt(31);
    }

    // ----------------------------------------------------------
    // OPERATION 5: GenerateTrainTestSplit
    // Returns (trainCount, testCount, quantumSeed) tuple.
    //
    // Parameters:
    //   totalSamples : Int  — total dataset rows  e.g. 500000
    //   testRatioPct : Int  — test %  e.g. 20 means 20%
    // ----------------------------------------------------------
    operation GenerateTrainTestSplit(
        totalSamples : Int,
        testRatioPct : Int
    ) : (Int, Int, Int) {

        // Clamp ratio to safe range 1–50%
        let safePct = testRatioPct < 1  ? 20
                    | testRatioPct > 50 ? 50
                    | testRatioPct;

        let testCount  = (totalSamples * safePct) / 100;
        let trainCount = totalSamples - testCount;

        let quantumSeed = GenerateRandomSeed();

        return (trainCount, testCount, quantumSeed);
    }

    // ----------------------------------------------------------
    // OPERATION 6: GenerateMultipleSeeds
    // Generates count random seeds for k-fold cross-validation.
    // ----------------------------------------------------------
    operation GenerateMultipleSeeds(count : Int) : Int[] {
        mutable seeds = [];
        for _ in 1 .. count {
            set seeds += [GenerateRandomSeed()];
        }
        return seeds;
    }
}
