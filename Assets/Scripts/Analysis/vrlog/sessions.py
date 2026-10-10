"""Load behavioural features written by SessionReportWriter.cs."""
from __future__ import annotations

from pathlib import Path
import pandas as pd

META_COLUMNS = [
    "participant_id", "session_number", "session_id", "condition", "therapist_id",
    "started_at_local", "started_at_utc", "device", "app_version", "scene",
    "schema_version", "feature_version",
]

# Features shown in reports / used for associations by default.
KEY_FEATURES = {
    "gaze_trigger_dwell_pct": "Trigger gaze (% of session)",
    "gaze_bias_index": "Gaze bias index (0.5 = none)",
    "gaze_trigger_first_latency_s": "First look at trigger (s)",
    "prox_trigger_zone_pct": "Time near triggers (%)",
    "prox_trigger_min_dist_m": "Closest distance to trigger (m)",
    "approach_bias_index": "Approach bias index (0.5 = none)",
    "grab_trigger_count": "Trigger grabs (n)",
    "grab_trigger_hold_s": "Trigger hold time (s)",
}


def load_sessions(logs_root: str | Path) -> tuple[pd.DataFrame, list[str]]:
    """Read every *_features.csv below logs_root (one row per session).

    Returns (dataframe, list of warnings). Sessions recorded without a
    participant ID are kept but flagged, because they cannot be linked
    to questionnaires or to other sessions.
    """
    root = Path(logs_root)
    files = sorted(root.rglob("*_features.csv"))
    warnings: list[str] = []
    if not files:
        raise FileNotFoundError(f"No *_features.csv files found below {root}")

    frames = []
    for f in files:
        try:
            frames.append(pd.read_csv(f, sep=";", encoding="utf-8-sig",
                                      dtype={c: str for c in META_COLUMNS if c != "session_number"}))
        except Exception as exc:  # unreadable file: report, never crash the whole run
            warnings.append(f"Could not read {f}: {exc}")
    df = pd.concat(frames, ignore_index=True, sort=False)

    df = df.drop_duplicates(subset="session_id", keep="last")
    df["participant_id"] = df["participant_id"].fillna("").astype(str)
    df["session_number"] = pd.to_numeric(df["session_number"], errors="coerce").astype("Int64")

    no_pid = df["participant_id"].eq("") | df["session_number"].isna()
    if no_pid.any():
        warnings.append(f"{int(no_pid.sum())} session(s) without participant ID/session number "
                        "(legacy layout) - excluded from linking: "
                        + ", ".join(df.loc[no_pid, "session_id"].astype(str)))
        df = df.loc[~no_pid].copy()

    dup = df.duplicated(subset=["participant_id", "session_number"], keep=False)
    if dup.any():
        warnings.append("Duplicate participant/session numbers (check sessionNumber override): "
                        + ", ".join(df.loc[dup, "session_id"].astype(str)))

    versions = df["feature_version"].dropna().unique()
    if len(versions) > 1:
        warnings.append(f"Mixed feature versions in data: {list(versions)} - compare with care.")

    return df.sort_values(["participant_id", "session_number"]).reset_index(drop=True), warnings
