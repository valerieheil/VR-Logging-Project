"""Run the full offline pipeline.

    python run_pipeline.py --logs C:\\VRLogs --out C:\\VRResults
    python run_pipeline.py --logs C:\\VRLogs --questionnaires q.csv --codebook templates/questionnaire_codebook.json --out results

Outputs (in --out):
    master_dataset.csv            one row per participant x session  (',' separator, '.' decimals -> R/Python/SPSS)
    master_dataset_excel_DE.csv   same, ';' separator and ',' decimals -> opens directly in German Excel
    master_dataset.xlsx           sheets: sessions, trends, associations, questionnaire_scores, feature_codebook
    longitudinal_trends.csv       per participant x variable: baseline, latest, change, slope
    associations.csv              behaviour x questionnaire: repeated-measures r, between-person r, FDR q
    questionnaire_scores_long.csv scored instruments (long format)
    dashboard.html                offline therapist dashboard (open by double-click)
    reports/<ID>_report.html      therapist report per participant
    pipeline_log.txt              every warning / validation message of this run
"""
from __future__ import annotations

import argparse
import datetime as dt
import sys
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).parent))
from vrlog import __version__  # noqa: E402
from vrlog import analysis, dashboard, questionnaires, report  # noqa: E402
from vrlog.sessions import KEY_FEATURES, load_sessions  # noqa: E402


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--logs", required=True, help="VRLogs folder pulled from the headset")
    ap.add_argument("--out", required=True, help="output folder")
    ap.add_argument("--questionnaires", help="questionnaire responses (CSV or XLSX, long format)")
    ap.add_argument("--codebook", default=str(Path(__file__).parent / "templates" / "questionnaire_codebook.json"))
    args = ap.parse_args(argv)

    out = Path(args.out)
    (out / "reports").mkdir(parents=True, exist_ok=True)
    log = [f"vrlog pipeline {__version__} - run {dt.datetime.now().isoformat(timespec='seconds')}",
           f"logs: {args.logs}", f"questionnaires: {args.questionnaires}", ""]

    # 1) behavioural features
    feats, w = load_sessions(args.logs)
    log += ["[sessions]"] + w + [f"{len(feats)} session(s) from {feats['participant_id'].nunique()} participant(s)", ""]

    # 2) questionnaires
    q_wide, q_long, codebook = pd.DataFrame(), pd.DataFrame(), {}
    if args.questionnaires:
        codebook = questionnaires.load_codebook(args.codebook)
        resp = questionnaires.load_responses(args.questionnaires)
        q_long, issues = questionnaires.score(resp, codebook)
        q_wide = questionnaires.to_wide(q_long)
        log += ["[questionnaires]"] + issues + [f"{len(q_long)} scored administrations", ""]

    # 3) merge
    master, issues = analysis.merge(feats, q_wide)
    log += ["[merge]"] + issues + [""]

    q_cols = [c for c in master.columns if c.startswith("q_")]
    q_base = [c for c in q_cols if not c.endswith("_reactivity")]
    behaviour = [c for c in KEY_FEATURES if c in master]

    # 4) longitudinal
    master = analysis.add_change_from_baseline(master, behaviour + q_cols)
    master = analysis.reliable_change(master, codebook)
    trends = analysis.trends(master, behaviour + q_cols)

    # 5) associations (only meaningful with questionnaires and >= 2 participants)
    assoc = analysis.associations(master, behaviour, q_cols) if q_cols else pd.DataFrame()
    if not assoc.empty:
        log += ["[associations]",
                f"{len(assoc)} behaviour x questionnaire pairs; FDR (Benjamini-Hochberg) applied across all pairs.",
                "Exploratory: interpret effect sizes and CIs, not p-values alone.", ""]

    # whole-number columns (counts, session numbers) as integers, not 224.0
    for c in master.columns:
        col = master[c]
        if pd.api.types.is_float_dtype(col) and col.notna().any() and (col.dropna() % 1 == 0).all():
            master[c] = col.astype("Int64")

    # 6) exports
    master.to_csv(out / "master_dataset.csv", index=False)
    master.to_csv(out / "master_dataset_excel_DE.csv", index=False, sep=";", decimal=",", encoding="utf-8-sig")
    trends.to_csv(out / "longitudinal_trends.csv", index=False)
    if not assoc.empty:
        assoc.to_csv(out / "associations.csv", index=False)
    if not q_long.empty:
        q_long.to_csv(out / "questionnaire_scores_long.csv", index=False)

    codebook_csv = Path(args.logs) / "feature_codebook.csv"
    with pd.ExcelWriter(out / "master_dataset.xlsx") as xw:
        master.to_excel(xw, sheet_name="sessions", index=False)
        trends.to_excel(xw, sheet_name="trends", index=False)
        if not assoc.empty:
            assoc.to_excel(xw, sheet_name="associations", index=False)
        if not q_long.empty:
            q_long.to_excel(xw, sheet_name="questionnaire_scores", index=False)
        if codebook_csv.exists():
            pd.read_csv(codebook_csv, sep=";", encoding="utf-8-sig").to_excel(xw, sheet_name="feature_codebook", index=False)

    # 7) reports
    for pid, g in master.groupby("participant_id"):
        report.participant_report(pid, g, q_base, report.quality_flags(g), out / "reports")

    # 8) offline therapist dashboard (single HTML file, no internet needed)
    dash = dashboard.build(master, out / "dashboard.html", source=Path(args.logs).name)
    log += ["[dashboard]", f"written: {dash.name}", ""]

    (out / "pipeline_log.txt").write_text("\n".join(log), encoding="utf-8")
    print("\n".join(log))
    print(f"Done. Results in {out.resolve()}")


if __name__ == "__main__":
    main()
