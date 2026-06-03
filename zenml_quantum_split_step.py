# ============================================================
#  zenml_quantum_split_step.py
#  ZenML @step  —  Train-Test Split using Quantum QRNG seed
#
#  This is the MIGRATED step that replaces classical random_state=42
#  with a true quantum random seed fetched from the Azure Function.
#
#  Drop-in replacement: only the seed source changes.
#  All other ZenML artifacts, preprocessing, and SVM remain classical.
# ============================================================

import os
import requests
import numpy as np
import pandas as pd
from sklearn.model_selection import train_test_split
from typing import Tuple, Annotated

from zenml import step, pipeline
from zenml.logger import get_logger

logger = get_logger(__name__)

# ── Azure Function URL ──────────────────────────────────────
# Set QUANTUM_FUNCTION_URL in your environment or ZenML secret store.
# Example local dev:  http://localhost:7071/api/quantum-split
# Example deployed:   https://<your-app>.azurewebsites.net/api/quantum-split
QUANTUM_FUNCTION_URL = os.environ.get(
    "QUANTUM_FUNCTION_URL",
    "http://localhost:7071/api/quantum-split"   # default = local dev
)

# Azure Function key (set QUANTUM_FUNCTION_KEY in environment)
QUANTUM_FUNCTION_KEY = os.environ.get("QUANTUM_FUNCTION_KEY", "")


# ──────────────────────────────────────────────────────────────
# HELPER: Call Azure Function to get quantum random seed
# ──────────────────────────────────────────────────────────────
def get_quantum_seed(total_samples: int, test_ratio_pct: int = 20) -> dict:
    """
    POST to Azure Function → calls Q# GenerateTrainTestSplit → returns seed.

    Returns dict with keys:
        quantum_seed  : int   — use as random_state in train_test_split
        train_count   : int   — expected training samples
        test_count    : int   — expected test samples
        source        : str   — "quantum-qrng"
    """
    headers = {"Content-Type": "application/json"}

    # Include function key if deployed to Azure (not needed for local dev)
    if QUANTUM_FUNCTION_KEY:
        headers["x-functions-key"] = QUANTUM_FUNCTION_KEY

    payload = {
        "total_samples":  total_samples,
        "test_ratio_pct": test_ratio_pct
    }

    try:
        response = requests.post(
            QUANTUM_FUNCTION_URL,
            json=payload,
            headers=headers,
            timeout=30        # Q# simulation is fast but give headroom
        )
        response.raise_for_status()
        result = response.json()

        logger.info(
            f"[QRNG] Quantum seed received: {result['quantum_seed']} "
            f"(source: {result['source']})"
        )
        return result

    except requests.exceptions.ConnectionError:
        # Fallback: classical seed if Azure Function unreachable
        logger.warning(
            "[QRNG] Azure Function unreachable — falling back to classical seed."
        )
        return {
            "quantum_seed": 42,
            "train_count": total_samples - (total_samples * test_ratio_pct // 100),
            "test_count": total_samples * test_ratio_pct // 100,
            "source": "classical-fallback"
        }

    except Exception as e:
        logger.error(f"[QRNG] Unexpected error: {e}")
        raise


# ──────────────────────────────────────────────────────────────
# ZENML STEP 1 (Legacy — Classical):
#   classical_train_test_split
#   Uses fixed random_state=42  ← what we are REPLACING
# ──────────────────────────────────────────────────────────────
@step
def classical_train_test_split(
    dataset: pd.DataFrame,
    target_column: str = "Label",
    test_size: float = 0.2,
) -> Tuple[
    Annotated[np.ndarray, "X_train"],
    Annotated[np.ndarray, "X_test"],
    Annotated[np.ndarray, "y_train"],
    Annotated[np.ndarray, "y_test"],
]:
    """
    LEGACY (classical) train-test split step.
    Uses fixed seed random_state=42  — NOT truly random.
    """
    X = dataset.drop(columns=[target_column]).values
    y = dataset[target_column].values

    X_train, X_test, y_train, y_test = train_test_split(
        X, y,
        test_size=test_size,
        random_state=42,          # ← classical pseudo-random, always same split
        stratify=y
    )

    logger.info(
        f"[Classical] Split — Train: {len(X_train)}, Test: {len(X_test)}, "
        f"seed: 42 (fixed)"
    )

    return X_train, X_test, y_train, y_test


# ──────────────────────────────────────────────────────────────
# ZENML STEP 2 (Migrated — Quantum):
#   quantum_train_test_split
#   Fetches true quantum random seed from Azure Function (Q# QRNG)
#   Everything else is identical to the classical step above.
# ──────────────────────────────────────────────────────────────
@step
def quantum_train_test_split(
    dataset: pd.DataFrame,
    target_column: str = "Label",
    test_size: float = 0.2,
) -> Tuple[
    Annotated[np.ndarray, "X_train"],
    Annotated[np.ndarray, "X_test"],
    Annotated[np.ndarray, "y_train"],
    Annotated[np.ndarray, "y_test"],
]:
    """
    MIGRATED (quantum) train-test split step.

    Migration change:
        BEFORE:  random_state = 42                  (classical, fixed, deterministic)
        AFTER:   random_state = quantum_seed         (quantum, true random, per-run)

    The quantum seed comes from Q# running on Azure:
        1. Q# allocates 31 qubits
        2. Hadamard gate → each qubit enters superposition
        3. Measurement collapses each qubit to 0 or 1 (true random)
        4. 31 bits combined → random integer seed
        5. Seed returned to this Python step via HTTP
        6. sklearn train_test_split uses the quantum seed

    All other behavior (preprocessing, SVM, evaluation) is unchanged.
    """
    X = dataset.drop(columns=[target_column]).values
    y = dataset[target_column].values

    total = len(X)
    test_ratio_pct = int(test_size * 100)

    # ── Call Azure Function → Q# QRNG ──
    qrng_result = get_quantum_seed(
        total_samples=total,
        test_ratio_pct=test_ratio_pct
    )

    quantum_seed = qrng_result["quantum_seed"]
    source       = qrng_result["source"]

    # ── Perform split using quantum seed ──
    X_train, X_test, y_train, y_test = train_test_split(
        X, y,
        test_size=test_size,
        random_state=quantum_seed,    # ← quantum random seed replaces 42
        stratify=y
    )

    logger.info(
        f"[Quantum] Split — Train: {len(X_train)}, Test: {len(X_test)}, "
        f"seed: {quantum_seed} (source: {source})"
    )

    return X_train, X_test, y_train, y_test


# ──────────────────────────────────────────────────────────────
# ZENML PIPELINE — Quantum-augmented NIDS pipeline
# Only the split step changes; all others remain classical.
# ──────────────────────────────────────────────────────────────
@pipeline(name="quantum_nids_pipeline", enable_cache=False)
def quantum_nids_pipeline():
    """
    Hybrid quantum-classical ZenML pipeline.

    Classical steps (unchanged from legacy):
        load_data → preprocess → evaluate → deploy

    Migrated step (quantum):
        train_test_split  ← now uses Q# QRNG via Azure Function
    """
    from zenml_steps import (    # your existing classical steps
        load_cic_ids2017,
        preprocess_features,
        train_svm_classifier,
        evaluate_model
    )

    # Step 1 — Load CIC-IDS2017 dataset (classical, unchanged)
    dataset = load_cic_ids2017()

    # Step 2 — Preprocess features (classical, unchanged)
    processed = preprocess_features(dataset=dataset)

    # Step 3 — Train-Test Split (MIGRATED → quantum seed)
    X_train, X_test, y_train, y_test = quantum_train_test_split(
        dataset=processed,
        target_column="Label",
        test_size=0.2
    )

    # Step 4 — Train SVM (classical, unchanged)
    model = train_svm_classifier(
        X_train=X_train,
        y_train=y_train
    )

    # Step 5 — Evaluate (classical, unchanged)
    evaluate_model(
        model=model,
        X_test=X_test,
        y_test=y_test
    )


# ──────────────────────────────────────────────────────────────
# Quick test — call Azure Function directly (no ZenML)
# Run:  python zenml_quantum_split_step.py
# ──────────────────────────────────────────────────────────────
if __name__ == "__main__":
    print("\n── QRNG Test ──────────────────────────────────")
    result = get_quantum_seed(total_samples=500_000, test_ratio_pct=20)
    print(f"  Quantum seed  : {result['quantum_seed']}")
    print(f"  Train samples : {result['train_count']:,}")
    print(f"  Test  samples : {result['test_count']:,}")
    print(f"  Source        : {result['source']}")
    print("───────────────────────────────────────────────\n")
