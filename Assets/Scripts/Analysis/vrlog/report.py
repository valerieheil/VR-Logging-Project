"""Self-contained HTML report per participant (therapist-facing).

Design intent: quiet, print-friendly, one consistent colour code across all
charts - amber for trigger-related measures, teal for neutral/control and
slate for questionnaires - so the reader learns it once.
"""
from __future__ import annotations

import base64
import html
import io
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from .sessions import KEY_FEATURES

INK, TRIGGER, NEUTRAL, QUEST, RULE, WARN = "#1F3A4D", "#B5651D", "#2E7D7A", "#4A5D8A", "#D5DCE1", "#A23B2A"

PAIRS = [  # (title, trigger column, neutral column, y-label)
    ("Gaze dwell", "gaze_trigger_dwell_pct", "gaze_neutral_dwell_pct", "% of session"),
    ("Time in proximity zone", "prox_trigger_zone_pct", "prox_neutral_zone_pct", "% of session"),
    ("Objects held", "grab_trigger_hold_s", "grab_neutral_hold_s", "seconds"),
]
SINGLES = [
    ("Gaze bias index", "gaze_bias_index", "0.5 = no preference"),
    ("Approach bias index", "approach_bias_index", "0.5 = no preference"),
    ("First look at a trigger", "gaze_trigger_first_latency_s", "seconds"),
]

CSS = f"""
:root {{ color-scheme: light; }}
body {{ font-family: "Segoe UI", system-ui, -apple-system, Roboto, sans-serif; color:{INK};
       max-width: 920px; margin: 2.5rem auto; padding: 0 1.25rem; line-height: 1.5; background:#fff; }}
h1 {{ font-size: 1.9rem; margin: 0 0 .2rem; font-weight: 600; letter-spacing:-.01em; }}
h2 {{ font-size: 1.15rem; margin: 2.2rem 0 .6rem; padding-bottom:.3rem; border-bottom: 2px solid {INK}; font-weight:600; }}
.sub {{ color:#5B6B78; margin:0 0 1.2rem; }}
.legend span {{ display:inline-block; margin-right:1.2rem; }}
.sw {{ display:inline-block; width:.8rem; height:.8rem; border-radius:2px; vertical-align:-1px; margin-right:.35rem; }}
.grid {{ display:grid; grid-template-columns: repeat(auto-fill, minmax(270px, 1fr)); gap: .8rem 1.2rem; }}
.grid img {{ width:100%; height:auto; }}
.tablewrap {{ overflow-x:auto; }}
table {{ border-collapse: collapse; font-size:.86rem; width:100%; }}
th, td {{ padding:.35rem .55rem; text-align:right; border-bottom:1px solid {RULE}; white-space:nowrap; }}
th:first-child, td:first-child {{ text-align:left; }}
thead th {{ border-bottom: 2px solid {INK}; font-weight:600; }}
ul.flags {{ padding-left:1.1rem; }} ul.flags li {{ color:{WARN}; }}
.note {{ font-size:.85rem; color:#5B6B78; border-left:3px solid {RULE}; padding:.4rem .9rem; margin-top:2rem; }}
@media print {{ body {{ margin: 0 auto; }} h2 {{ break-after: avoid; }} .grid img {{ break-inside: avoid; }} }}
"""


def _fig_to_img(fig) -> str:
    buf = io.BytesIO()
    fig.savefig(buf, format="png", dpi=140, bbox_inches="tight")
    plt.close(fig)
    return f'<img alt="" src="data:image/png;base64,{base64.b64encode(buf.getvalue()).decode()}">'


def _axes(title: str, ylabel: str):
    fig, ax = plt.subplots(figsize=(3.6, 2.4))
    ax.set_title(title, fontsize=10, color=INK, loc="left", fontweight="semibold")
    ax.set_xlabel("Session", fontsize=8, color=INK)
    ax.set_ylabel(ylabel, fontsize=8, color=INK)
    ax.tick_params(labelsize=8, colors=INK)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(RULE)
    ax.grid(axis="y", color=RULE, linewidth=.6)
    ax.xaxis.set_major_locator(matplotlib.ticker.MaxNLocator(integer=True))
    return fig, ax


def _plot(ax, x, y, color, label=None):
    m = ~np.isnan(y)
    if m.any():
        ax.plot(x[m], y[m], "-o", color=color, lw=1.8, ms=4, label=label)


def participant_report(pid: str, g: pd.DataFrame, q_cols: list[str], flags: list[str], out: Path) -> Path:
    g = g.sort_values("session_number")
    x = g["session_number"].astype(float).to_numpy()
    num = lambda c: pd.to_numeric(g[c], errors="coerce").to_numpy(dtype=float) if c in g else np.full(len(g), np.nan)

    imgs = []
    for title, tc, nc, yl in PAIRS:
        fig, ax = _axes(title, yl)
        _plot(ax, x, num(tc), TRIGGER, "trigger")
        _plot(ax, x, num(nc), NEUTRAL, "neutral")
        imgs.append(_fig_to_img(fig))
    for title, c, yl in SINGLES:
        fig, ax = _axes(title, yl)
        if "index" in c:
            ax.axhline(.5, color=RULE, lw=1.2, ls="--")
            ax.set_ylim(-.02, 1.02)
        _plot(ax, x, num(c), TRIGGER)
        imgs.append(_fig_to_img(fig))

    q_imgs = []
    instruments = sorted({c.split("_", 1)[1].rsplit("_", 1)[0] for c in q_cols})
    for inst in instruments:
        cols = [c for c in q_cols if c.startswith(f"q_{inst}_") and not c.endswith("_reactivity")]
        if not any(g[c].notna().any() for c in cols if c in g):
            continue
        fig, ax = _axes(inst.replace("_", " "), "score")
        shades = [QUEST, "#8FA3C9", "#2B3A5C", "#6E7FA6"]
        for i, c in enumerate(cols):
            _plot(ax, x, num(c), shades[i % len(shades)], c.rsplit("_", 1)[1])
        if len(cols) > 1:
            ax.legend(fontsize=7, frameon=False)
        q_imgs.append(_fig_to_img(fig))

    table_cols = ["session_number", "started_at_local", "condition", "session_duration_s"] + \
                 [c for c in KEY_FEATURES if c in g] + [c for c in q_cols if c in g]
    t = g[[c for c in table_cols if c in g]].copy()
    if "started_at_local" in t:
        t["started_at_local"] = t["started_at_local"].astype(str).str[:10]
    headers = {"session_number": "Session", "started_at_local": "Date", "condition": "Condition",
               "session_duration_s": "Duration (s)", **KEY_FEATURES}
    th = "".join(f"<th>{html.escape(headers.get(c, c))}</th>" for c in t.columns)

    def cell(v):
        if isinstance(v, (float, np.floating)):
            return "&ndash;" if np.isnan(v) else (f"{v:.0f}" if float(v).is_integer() else f"{v:.2f}")
        return html.escape("" if pd.isna(v) else str(v))
    tb = "".join("<tr>" + "".join(f"<td>{cell(v)}</td>" for v in row) + "</tr>"
                 for row in t.itertuples(index=False))

    flags_html = ("<ul class='flags'>" + "".join(f"<li>{html.escape(f)}</li>" for f in flags) + "</ul>") \
        if flags else "<p>No data-quality issues detected.</p>"

    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(pid)} - VR exposure progress</title><style>{CSS}</style></head><body>
<h1>Participant {html.escape(pid)}</h1>
<p class="sub">{len(g)} VR exposure session(s), {html.escape(str(g['started_at_local'].astype(str).str[:10].min()))}
 to {html.escape(str(g['started_at_local'].astype(str).str[:10].max()))}</p>
<p class="legend"><span><i class="sw" style="background:{TRIGGER}"></i>trigger objects</span>
<span><i class="sw" style="background:{NEUTRAL}"></i>neutral control objects</span>
<span><i class="sw" style="background:{QUEST}"></i>questionnaires</span></p>
<h2>Behaviour across sessions</h2><div class="grid">{''.join(imgs)}</div>
{'<h2>Questionnaires</h2><div class="grid">' + ''.join(q_imgs) + '</div>' if q_imgs else ''}
<h2>Session table</h2><div class="tablewrap"><table><thead><tr>{th}</tr></thead><tbody>{tb}</tbody></table></div>
<h2>Data quality</h2>{flags_html}
<p class="note">Behavioural values are descriptive indicators derived automatically from the VR log
(attention = head direction, no eye tracking). They are not validated diagnostic measures; read them
alongside clinical judgement and questionnaire scores. Within-person slopes over few sessions are
unstable and should not be interpreted as treatment effects on their own.</p>
</body></html>"""
    path = out / f"{pid}_report.html"
    path.write_text(page, encoding="utf-8")
    return path


def quality_flags(g: pd.DataFrame) -> list[str]:
    flags = []
    for r in g.itertuples(index=False):
        d = r._asdict()
        s = f"S{d.get('session_number')}"
        if d.get("link_status") == "vr_only":
            flags.append(f"{s}: no questionnaire data linked.")
        if d.get("link_status") == "questionnaire_only":
            flags.append(f"{s}: questionnaire data but no VR session found.")
            continue
        if pd.notna(d.get("session_duration_s")) and d["session_duration_s"] < 60:
            flags.append(f"{s}: session shorter than 60 s.")
        if pd.notna(d.get("dq_n_neutral_stimuli")) and d["dq_n_neutral_stimuli"] == 0:
            flags.append(f"{s}: no neutral control objects - bias indices undefined.")
        if pd.notna(d.get("gaze_unclassified_dwell_s")) and d["gaze_unclassified_dwell_s"] > 0:
            flags.append(f"{s}: {d['gaze_unclassified_dwell_s']:.1f} s gaze on unclassified objects.")
        if pd.notna(d.get("dq_head_coverage_pct")) and d["dq_head_coverage_pct"] < 90:
            flags.append(f"{s}: head position known for only {d['dq_head_coverage_pct']:.0f}% of session.")
    return flags
