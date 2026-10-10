"""Merge behaviour with questionnaires, longitudinal change, associations."""
from __future__ import annotations

import numpy as np
import pandas as pd
from scipy import stats

KEYS = ["participant_id", "session_number"]


# ─────────────────────────────────────────────────────────────
#  Merge
# ─────────────────────────────────────────────────────────────
def merge(features: pd.DataFrame, q_wide: pd.DataFrame) -> tuple[pd.DataFrame, list[str]]:
    """Outer-join on participant_id + session_number and REPORT mismatches.

    Unmatched rows are kept (a session without questionnaire is still a
    valid behavioural observation) but listed, because silent mismatches
    are the most common source of error when linking data sources.
    """
    issues = []
    if q_wide is None or q_wide.empty:
        return features.copy(), ["No questionnaire data supplied."]
    m = features.merge(q_wide, on=KEYS, how="outer", indicator=True)
    only_vr = m.loc[m["_merge"] == "left_only", KEYS]
    only_q = m.loc[m["_merge"] == "right_only", KEYS]
    if len(only_vr):
        issues.append(f"{len(only_vr)} VR session(s) without questionnaire data: "
                      + ", ".join(f"{p} S{s}" for p, s in only_vr.itertuples(index=False)))
    if len(only_q):
        issues.append(f"{len(only_q)} questionnaire session(s) without VR data: "
                      + ", ".join(f"{p} S{s}" for p, s in only_q.itertuples(index=False)))
    m = m.rename(columns={"_merge": "link_status"})
    m["link_status"] = m["link_status"].map(
        {"both": "vr+questionnaire", "left_only": "vr_only", "right_only": "questionnaire_only"})
    return m.sort_values(KEYS).reset_index(drop=True), issues


# ─────────────────────────────────────────────────────────────
#  Longitudinal
# ─────────────────────────────────────────────────────────────
def add_change_from_baseline(df: pd.DataFrame, variables: list[str]) -> pd.DataFrame:
    """Adds <var>_chg_bl = value - value in the participant's first session with data."""
    out = df.sort_values(KEYS).copy()
    for v in variables:
        if v not in out:
            continue
        first = out.groupby("participant_id")[v].transform(
            lambda s: s.dropna().iloc[0] if s.notna().any() else np.nan)
        out[f"{v}_chg_bl"] = out[v] - first
    return out


def trends(df: pd.DataFrame, variables: list[str]) -> pd.DataFrame:
    """Per participant and variable: baseline, latest, change and OLS slope per session.

    The slope is descriptive (n is small per person); it is NOT a test of
    treatment effect. Use mixed models across participants for inference.
    """
    rows = []
    for pid, g in df.sort_values(KEYS).groupby("participant_id"):
        for v in variables:
            if v not in g:
                continue
            d = g[["session_number", v]].dropna()
            if d.empty:
                continue
            x = d["session_number"].astype(float).to_numpy()
            y = d[v].astype(float).to_numpy()
            slope = np.polyfit(x, y, 1)[0] if len(d) >= 3 and np.ptp(x) > 0 else np.nan
            rows.append({
                "participant_id": pid, "variable": v, "n_sessions": len(d),
                "first_session": int(x[0]), "last_session": int(x[-1]),
                "baseline": y[0], "latest": y[-1], "change": y[-1] - y[0],
                "slope_per_session": slope,
            })
    return pd.DataFrame(rows)


def reliable_change(df: pd.DataFrame, codebook: dict) -> pd.DataFrame:
    """Jacobson & Truax (1991) Reliable Change Index vs. the participant's baseline.

    Only for instruments whose codebook entry provides "rci": {"sd": ..., "reliability": ...}
    (SD and test-retest reliability from a normative / validation sample).
    RCI = (x_t - x_baseline) / S_diff,  S_diff = sqrt(2) * SD * sqrt(1 - r).
    |RCI| > 1.96 -> change unlikely to be measurement error (p < .05).
    """
    out = df.copy()
    for inst, cb in codebook.items():
        rci = cb.get("rci")
        if not rci:
            continue
        s_diff = np.sqrt(2) * rci["sd"] * np.sqrt(1 - rci["reliability"])
        for col in [c for c in out.columns if c.startswith(f"q_{inst}_") and
                    not c.endswith(("_reactivity", "_chg_bl", "_rci", "_reliable"))]:
            base = out.sort_values(KEYS).groupby("participant_id")[col].transform(
                lambda s: s.dropna().iloc[0] if s.notna().any() else np.nan)
            out[f"{col}_rci"] = (out[col] - base) / s_diff
            out[f"{col}_reliable"] = np.where(out[f"{col}_rci"].isna(), "",
                                     np.where(out[f"{col}_rci"] <= -1.96, "reliable decrease",
                                     np.where(out[f"{col}_rci"] >= 1.96, "reliable increase", "no reliable change")))
    return out


# ─────────────────────────────────────────────────────────────
#  Associations
# ─────────────────────────────────────────────────────────────
def rmcorr(df: pd.DataFrame, x: str, y: str, subject: str = "participant_id") -> dict:
    """Repeated-measures correlation (Bakdash & Marusich, 2017).

    Common within-person association of x and y across sessions, removing
    between-person differences. Equivalent to the ANCOVA formulation
    y ~ subject + x ; df = N - k - 1 (N observations, k participants).
    """
    d = df[[subject, x, y]].dropna()
    d = d[d.groupby(subject)[x].transform("size") >= 2]
    k, n = d[subject].nunique(), len(d)
    res = {"x": x, "y": y, "n_obs": n, "n_participants": k,
           "r_rm": np.nan, "df": n - k - 1, "p": np.nan, "ci_low": np.nan, "ci_high": np.nan}
    if k < 2 or n - k - 1 < 3:
        return res
    xc = d[x] - d.groupby(subject)[x].transform("mean")
    yc = d[y] - d.groupby(subject)[y].transform("mean")
    sxx, syy = float((xc ** 2).sum()), float((yc ** 2).sum())
    if sxx == 0 or syy == 0:
        return res
    r = float((xc * yc).sum() / np.sqrt(sxx * syy))
    dof = n - k - 1
    t = r * np.sqrt(dof / max(1e-12, 1 - r ** 2))
    p = 2 * stats.t.sf(abs(t), dof)
    z, se = np.arctanh(np.clip(r, -0.999999, 0.999999)), 1 / np.sqrt(dof - 1)
    res.update(r_rm=r, df=dof, p=p, ci_low=np.tanh(z - 1.96 * se), ci_high=np.tanh(z + 1.96 * se))
    return res


def between_corr(df: pd.DataFrame, x: str, y: str, subject: str = "participant_id") -> dict:
    """Pearson correlation of participant MEANS (between-person association)."""
    m = df.groupby(subject)[[x, y]].mean().dropna()
    if len(m) < 4 or m[x].std() == 0 or m[y].std() == 0:
        return {"r_between": np.nan, "p_between": np.nan, "n_between": len(m)}
    r, p = stats.pearsonr(m[x], m[y])
    return {"r_between": r, "p_between": p, "n_between": len(m)}


def fdr_bh(p: pd.Series) -> pd.Series:
    """Benjamini-Hochberg adjusted p-values (q-values); NaN-safe."""
    p = pd.Series(p, dtype=float)
    ok = p.notna()
    q = pd.Series(np.nan, index=p.index)
    if ok.sum() == 0:
        return q
    pv = p[ok].to_numpy()
    order = np.argsort(pv)
    ranked = pv[order] * len(pv) / (np.arange(len(pv)) + 1)
    ranked = np.minimum.accumulate(ranked[::-1])[::-1]
    adj = np.empty_like(pv)
    adj[order] = np.minimum(ranked, 1)
    q[ok] = adj
    return q


def associations(df: pd.DataFrame, behaviour: list[str], scores: list[str]) -> pd.DataFrame:
    rows = []
    for x in behaviour:
        for y in scores:
            if x in df and y in df:
                rows.append({**rmcorr(df, x, y), **between_corr(df, x, y)})
    out = pd.DataFrame(rows)
    if not out.empty:
        out["q_fdr"] = fdr_bh(out["p"])
    return out
