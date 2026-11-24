"""Offline therapist dashboard: one self-contained HTML file.

The pipeline embeds all session data as JSON into dashboard_template.html.
The result opens in any browser by double-click, needs no internet, no server
and no installation, and patient data never leaves the PC.
"""
from __future__ import annotations

import datetime as dt
import json
import math
from pathlib import Path

import pandas as pd

from . import __version__

TEMPLATE = Path(__file__).with_name("dashboard_template.html")

# Rows of the flowsheet. Only rows present in the data are shown.
# No "good/bad" judgement is encoded on purpose: changes are shown as direction and size only.
METRICS = [
    # key, label, group, unit, decimals, plain-language help
    ("q_VAS_craving_pre", "Craving before exposure", "Craving & questionnaires", "0–100", 0,
     "Self-rated craving on a 0–100 scale, asked right before the VR session."),
    ("q_VAS_craving_post", "Craving after exposure", "Craving & questionnaires", "0–100", 0,
     "Self-rated craving on a 0–100 scale, asked right after the VR session."),
    ("q_VAS_craving_reactivity", "Cue reactivity", "Craving & questionnaires", "points", 0,
     "Craving after minus craving before the session. Positive values mean the alcohol cues raised craving."),
    ("q_AUQ_pre", "Alcohol urge (AUQ)", "Craving & questionnaires", "8–56", 0,
     "Alcohol Urge Questionnaire total before the session. Higher means stronger urge to drink."),
    ("q_SSQ_total_post", "Cybersickness (SSQ)", "Craving & questionnaires", "score", 1,
     "Simulator Sickness Questionnaire after the session. High values can explain low engagement with the scene."),
    ("q_IPQ_post", "Presence (IPQ)", "Craving & questionnaires", "0–6", 1,
     "How present the patient felt in the virtual room. Low presence can weaken the cue exposure."),

    ("gaze_trigger_dwell_pct", "Looking at alcohol", "Attention", "% of session", 1,
     "Share of the session the patient’s head was pointed at alcohol objects (head direction, no eye tracking)."),
    ("gaze_neutral_dwell_pct", "Looking at neutral objects", "Attention", "% of session", 1,
     "Same measure for the neutral control objects, e.g. water bottles. The comparison point for alcohol."),
    ("gaze_bias_index", "Attention bias", "Attention", "0–1", 2,
     "Alcohol looking time divided by alcohol + neutral looking time. 0.50 means no preference; above 0.50 means more attention on alcohol."),
    ("gaze_trigger_first_latency_s", "First look at alcohol after", "Attention", "s", 1,
     "Seconds from session start until the patient first looked at an alcohol object."),

    ("prox_trigger_zone_pct", "Time near alcohol", "Approach", "% of session", 1,
     "Share of the session spent within the proximity radius (default 1 m) of an alcohol object."),
    ("prox_trigger_min_dist_m", "Closest distance to alcohol", "Approach", "m", 2,
     "The closest the patient’s head came to any alcohol object (floor distance)."),
    ("approach_bias_index", "Approach bias", "Approach", "0–1", 2,
     "Time near alcohol divided by time near alcohol + time near neutral objects. 0.50 means no preference."),

    ("grab_trigger_count", "Alcohol objects picked up", "Interaction", "times", 0,
     "How often an alcohol object was grabbed."),
    ("grab_trigger_hold_s", "Alcohol held in hand", "Interaction", "s", 1,
     "Total time an alcohol object was held."),
    ("grab_neutral_count", "Neutral objects picked up", "Interaction", "times", 0,
     "How often a neutral object was grabbed."),

    ("session_duration_s", "Session length", "Session", "min", 1,
     "Duration of the VR session."),
    ("head_path_horizontal_m", "Distance walked", "Session", "m", 1,
     "How far the patient moved around the room."),
]

GROUP_ORDER = ["Craving & questionnaires", "Attention", "Approach", "Interaction", "Session"]


def _num(v):
    try:
        f = float(v)
    except (TypeError, ValueError):
        return None
    return None if math.isnan(f) or math.isinf(f) else f


def _flags(row: pd.Series) -> list[str]:
    f = []
    g = row.get
    if g("link_status") == "vr_only":
        f.append("No questionnaire data linked to this session.")
    if g("link_status") == "vr+questionnaire":
        missing = [c for c in ("q_VAS_craving_pre", "q_VAS_craving_post") if c in row.index and _num(g(c)) is None]
        if missing:
            f.append("Some questionnaire values are missing or were invalid; see pipeline_log.txt.")
    if (_num(g("session_duration_s")) or 1e9) < 60:
        f.append("Session shorter than one minute.")
    if _num(g("dq_n_neutral_stimuli")) == 0:
        f.append("No neutral control objects registered, so bias values are unavailable.")
    if (_num(g("gaze_unclassified_dwell_s")) or 0) > 0:
        f.append(f"{_num(g('gaze_unclassified_dwell_s')):.1f} s of looking at objects without a category.")
    cov = _num(g("dq_head_coverage_pct"))
    if cov is not None and cov < 90:
        f.append(f"Head position known for only {cov:.0f} % of the session.")
    if (_num(g("dq_gaze_open_at_end")) or 0) > 0:
        f.append("Session ended while the patient was looking at an object.")
    return f


def build(master: pd.DataFrame, out_path: str | Path, source: str = "") -> Path:
    df = master.copy()
    if "session_id" in df:
        df = df[df["session_id"].notna()]          # questionnaire-only rows have no VR session
    df = df.sort_values(["participant_id", "session_number"])

    present = [m for m in METRICS if m[0] in df.columns and df[m[0]].notna().any()]
    # questionnaire columns we have no label for still get a row
    known = {m[0] for m in METRICS}
    for c in df.columns:
        if c.startswith("q_") and c not in known and not c.endswith(("_chg_bl", "_rci", "_reliable")) \
                and df[c].notna().any():
            present.insert(len([p for p in present if p[2] == GROUP_ORDER[0]]),
                           (c, c[2:].replace("_", " "), GROUP_ORDER[0], "score", 1, "Questionnaire score."))

    patients = {}
    for pid, g in df.groupby("participant_id"):
        sessions = []
        for _, r in g.iterrows():
            vals = {}
            for key, *_ in present:
                v = _num(r.get(key))
                if key == "session_duration_s" and v is not None:
                    v = v / 60.0
                vals[key] = v
            reliable = {key: r.get(f"{key}_reliable") for key, *_ in present
                        if isinstance(r.get(f"{key}_reliable"), str) and r.get(f"{key}_reliable")}
            sessions.append({
                "n": int(r["session_number"]),
                "id": str(r.get("session_id", "")),
                "date": str(r.get("started_at_local", ""))[:16].replace("T", " "),
                "condition": "" if pd.isna(r.get("condition")) else str(r.get("condition")),
                "values": vals,
                "reliable": reliable,
                "epochs": {
                    "gaze": [_num(r.get(f"gaze_trigger_dwell_pct_e{k}")) for k in range(1, 4)],
                    "prox": [_num(r.get(f"prox_trigger_zone_pct_e{k}")) for k in range(1, 4)],
                },
                "flags": _flags(r),
            })
        patients[str(pid)] = {"sessions": sessions}

    data = {
        "generated": dt.datetime.now().strftime("%Y-%m-%d %H:%M"),
        "version": __version__,
        "source": source,
        "groups": [gname for gname in GROUP_ORDER if any(m[2] == gname for m in present)],
        "metrics": [{"key": k, "label": l, "group": gr, "unit": u, "dec": d, "help": h}
                    for k, l, gr, u, d, h in present],
        "patients": patients,
    }
    payload = json.dumps(data, ensure_ascii=False).replace("</", "<\\/")
    html = TEMPLATE.read_text(encoding="utf-8").replace("/*__DATA__*/null", payload)
    out = Path(out_path)
    out.write_text(html, encoding="utf-8")
    return out
