"""Import, validate and score questionnaire data.

Input is ONE long-format table (CSV or XLSX), one row per answered item:

    participant_id;session_number;timepoint;instrument;item;value
    P001;1;pre;VAS_craving;;35
    P001;1;post;VAS_craving;;52
    P001;1;pre;AUQ;1;4
    ...

* timepoint  : free label, typically "pre" / "post" (relative to the VR exposure)
* item       : item number; leave empty for single-item scales or pre-scored totals
* instrument : must exist in the codebook (templates/questionnaire_codebook.json)

The codebook defines how each instrument is scored, so scoring rules live in
ONE reviewable file instead of being hidden in code.
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import pandas as pd

REQUIRED = ["participant_id", "session_number", "timepoint", "instrument", "value"]


def load_codebook(path: str | Path) -> dict:
    with open(path, encoding="utf-8") as fh:
        cb = json.load(fh)
    return {k: v for k, v in cb.items() if not k.startswith("_")}


def load_responses(path: str | Path) -> pd.DataFrame:
    path = Path(path)
    if path.suffix.lower() in (".xlsx", ".xls"):
        df = pd.read_excel(path, dtype={"participant_id": str})
    else:
        # auto-detect ';' (German Excel) or ',' separators
        df = pd.read_csv(path, sep=None, engine="python", encoding="utf-8-sig",
                         dtype={"participant_id": str})
    df.columns = [c.strip().lower() for c in df.columns]
    missing = [c for c in REQUIRED if c not in df.columns]
    if missing:
        raise ValueError(f"Questionnaire file is missing columns: {missing}")
    if "item" not in df.columns:
        df["item"] = np.nan

    df["participant_id"] = df["participant_id"].astype(str).str.strip()
    df["timepoint"] = df["timepoint"].astype(str).str.strip().str.lower()
    df["instrument"] = df["instrument"].astype(str).str.strip()
    df["session_number"] = pd.to_numeric(df["session_number"], errors="coerce").astype("Int64")
    # accept decimal commas typed in German Excel
    df["value"] = pd.to_numeric(df["value"].astype(str).str.replace(",", ".", regex=False), errors="coerce")
    df["item"] = pd.to_numeric(df["item"], errors="coerce").astype("Int64")
    return df


def score(responses: pd.DataFrame, codebook: dict) -> tuple[pd.DataFrame, list[str]]:
    """Score all instruments.

    Returns
    -------
    scores : long table participant_id, session_number, timepoint, instrument, score, n_items
    issues : list of human-readable validation messages (nothing is silently dropped)
    """
    issues: list[str] = []
    df = responses.copy()

    unknown = sorted(set(df["instrument"]) - set(codebook))
    if unknown:
        issues.append(f"Instruments not in codebook (ignored): {unknown}")
        df = df[df["instrument"].isin(codebook)]

    bad_keys = df["session_number"].isna() | df["participant_id"].isin(["", "nan"])
    if bad_keys.any():
        issues.append(f"{int(bad_keys.sum())} row(s) without participant_id/session_number (ignored).")
        df = df[~bad_keys]

    if df["value"].isna().any():
        issues.append(f"{int(df['value'].isna().sum())} non-numeric/empty value(s) treated as missing.")

    keys = ["participant_id", "session_number", "timepoint", "instrument"]
    dup = df.duplicated(subset=keys + ["item"], keep=False)
    if dup.any():
        where = sorted({f"{r.participant_id} S{r.session_number} {r.timepoint} {r.instrument}"
                        for r in df[dup].itertuples(index=False)})
        issues.append(f"{int(dup.sum())} duplicated item responses - the LAST entry is used: {where}")
        df = df.drop_duplicates(subset=keys + ["item"], keep="last")

    rows = []
    for (pid, sess, tp, inst), g in df.groupby(keys, sort=True):
        cb = codebook[inst]
        lo, hi = cb.get("min"), cb.get("max")
        vals = g.set_index("item")["value"] if g["item"].notna().any() else g["value"].reset_index(drop=True)
        where = f"{pid} S{sess} {tp} {inst}"

        if lo is not None and hi is not None:
            out = vals[(vals < lo) | (vals > hi)]
            if len(out):
                issues.append(f"{where}: {len(out)} value(s) outside {lo}-{hi} set to missing.")
                vals = vals.where((vals >= lo) & (vals <= hi))

        method = cb.get("scoring", "single")
        n_items = int(cb.get("items", 1))

        if method in ("single", "prescored"):
            v = vals.dropna()
            s = float(v.iloc[-1]) if len(v) else np.nan
            rows.append((pid, sess, tp, inst, s, int(len(v))))
            continue

        # multi-item: reverse-score, then sum or mean with limited prorating
        vals = vals.dropna()
        expected = set(range(1, n_items + 1))
        extra = set(int(i) for i in vals.index if pd.notna(i)) - expected
        if extra:
            issues.append(f"{where}: unexpected item numbers {sorted(extra)} ignored.")
            vals = vals[[i in expected for i in vals.index]]
        rev = set(cb.get("reverse_items", []))
        if rev:
            vals = vals.copy()
            mask = [i in rev for i in vals.index]
            vals[mask] = (lo + hi) - vals[mask]

        n_missing = n_items - len(vals)
        if n_missing > int(cb.get("max_missing", 0)):
            issues.append(f"{where}: {n_missing} of {n_items} items missing (> max_missing) - score not computed.")
            s = np.nan
        elif method == "mean":
            s = float(vals.mean())
        elif method == "sum":
            # prorated sum: mean of answered items x number of items
            s = float(vals.mean() * n_items) if len(vals) else np.nan
            if n_missing:
                issues.append(f"{where}: {n_missing} item(s) missing - sum prorated.")
        else:
            raise ValueError(f"Unknown scoring method '{method}' for {inst}")
        rows.append((pid, sess, tp, inst, s, int(len(vals))))

    scores = pd.DataFrame(rows, columns=keys + ["score", "n_items"])
    scores["session_number"] = scores["session_number"].astype("Int64")
    return scores, issues


def to_wide(scores: pd.DataFrame) -> pd.DataFrame:
    """One row per participant x session.

    Columns  q_<instrument>_<timepoint>; if both 'pre' and 'post' exist also
    q_<instrument>_reactivity = post - pre  (cue-induced change, e.g. craving).
    """
    if scores.empty:
        return pd.DataFrame(columns=["participant_id", "session_number"])
    s = scores.copy()
    s["col"] = "q_" + s["instrument"] + "_" + s["timepoint"]
    wide = s.pivot_table(index=["participant_id", "session_number"], columns="col",
                         values="score", aggfunc="last").reset_index()
    wide.columns.name = None
    for inst in scores["instrument"].unique():
        pre, post = f"q_{inst}_pre", f"q_{inst}_post"
        if pre in wide and post in wide:
            wide[f"q_{inst}_reactivity"] = wide[post] - wide[pre]
    return wide
