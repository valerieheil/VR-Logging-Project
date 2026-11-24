# VR Exposure Therapy – Behavioural Logging and Clinical Monitoring

A research prototype for virtual reality cue-exposure therapy in alcohol use disorder. Patients explore a virtual apartment containing alcohol-related objects (beer bottles) and matched neutral control objects (water bottles) on a Meta Quest headset. The system records their behaviour automatically, turns it into clinically interpretable measures, links sessions of the same patient over time, merges them with questionnaire scores, and presents the result to therapists in an offline dashboard.

**Research question:** How can automatically extracted behavioural metrics from VR exposure sessions be transformed into clinically interpretable information that complements traditional clinical assessments and supports longitudinal monitoring and therapist decision-making?

> This is a research and development project. It is not a medical device or a certified therapeutic system.

## What the system does

**In the headset (Unity):**
- Logs behaviour as timestamped events: head position, hand positions, head-direction gaze on objects, grabbing and releasing objects, and the position and category of every stimulus object.
- Marks each object as **Trigger** (alcohol cue), **Neutral** (control) or **Other**, so that attention and approach can be measured relative to a neutral reference.
- Stores sessions per pseudonymised patient (`VRLogs/P001/P001_S03_<date>/`), numbered automatically.
- At the end of a session, computes more than 60 behavioural features and writes a plain-language summary for the therapist, including change since the first and previous session.

**On the PC (Python):**
- Imports, validates and scores questionnaires (e.g. craving VAS, AUQ, SSQ, IPQ).
- Merges behaviour and questionnaires per session, and computes change from baseline, per-patient trends and the Reliable Change Index.
- Computes repeated-measures correlations between behaviour and questionnaires, with false-discovery-rate correction.
- Exports CSV (international and German Excel format) and XLSX.
- Generates an **offline therapist dashboard** (`dashboard.html`) and printable per-patient reports.

## Measured behaviour

| Domain | Examples | Construct |
|---|---|---|
| Attention | time looking at alcohol vs neutral objects, attention bias index, first look at alcohol | attentional bias |
| Approach | closest distance to alcohol, time within 1 m, approach bias index, hand reach | approach / avoidance |
| Interaction | number of grabs, time holding alcohol objects | engagement with cues |
| Within session | alcohol-related looking and proximity in the beginning, middle and end | within-session change |
| Data quality | registered stimuli, tracking coverage, open episodes | validity of the session |

Full definitions, formulas and limitations: [`docs/FEATURE_DEFINITIONS.md`](docs/FEATURE_DEFINITIONS.md).

**Important limitation:** Meta Quest 2/3 have no eye tracking. "Gaze" in this project means the direction the head is pointing (a ray from the headset hitting an object's collider), not eye movements.

## Repository structure

```
Assets/
  Scripts/
    Logging/    VRLogger, SessionController, SessionMetadata, LogEvent
    Tracking/   GazeTracker, GrabTracker, MovementTracker, StimulusObject
    Analysis/   FeatureExtractor, SessionReportWriter
  Editor/       Unity menu tools (furniture colliders, bottle water + label)
  Models/       incl. WaterBottle (neutral control object)
  Scenes/       ExposureScene
Analysis/       Python pipeline: questionnaires, statistics, exports, dashboard
docs/           feature definitions, dashboard, research plan, system comparison, changelog
```

## Requirements

- Unity 6.3 LTS (6000.3.9f1), Android build support
- Meta XR SDK (Core, Interaction SDK), Universal Render Pipeline
- Meta Quest 2 or 3 (Quest Link or standalone APK)
- Python 3.10+ for the analysis pipeline (`pip install -r Analysis/requirements.txt`)

## Running a session

1. Open the project in Unity and open **ExposureScene**.
2. Set the patient for the next session, either in the `VRLogger` Inspector or with a config file pushed to the headset:
   ```
   adb push session_config.json /sdcard/Android/data/<app id>/files/VRLogs/
   ```
   ```json
   { "participantID": "P001", "condition": "AlcoholCue", "therapistID": "T01", "sessionNumber": 0 }
   ```
   `sessionNumber: 0` means automatic numbering. Use pseudonymised codes only, never names.
3. Run the scene via Quest Link or build to the headset.
4. Controller buttons: **A** (right) exports the session, **X** (left) starts a new session (the running one is exported first). In the Editor, keys A, X and M (marker) work too.

Setup details for new scenes and objects: [`SETUP_GUIDE.txt`](SETUP_GUIDE.txt) (if present) and [`docs/CHANGELOG_v2.md`](docs/CHANGELOG_v2.md).

## Analysing data

```
adb pull /sdcard/Android/data/<app id>/files/VRLogs C:\VRLogs
python Analysis/run_pipeline.py --logs C:\VRLogs --questionnaires questionnaires.xlsx --out C:\VRResults
```

Then open `C:\VRResults\dashboard.html` by double-click. It works without internet.

Questionnaire input format and scoring rules: `Analysis/templates/`. Dashboard use and design rationale: [`docs/DASHBOARD.md`](docs/DASHBOARD.md).

## Output per session

| File | Content |
|---|---|
| `<SessionID>.csv / .json` | all raw events |
| `<SessionID>_Gaze / _Movement / _Interaction.csv` | events filtered by type |
| `<SessionID>_features.csv / .json` | computed features with units and descriptions |
| `<SessionID>_summary.txt` | therapist summary |
| `<PatientID>_sessions.csv` | all sessions of one patient |
| `feature_codebook.csv` | data dictionary |

CSV files use `;` as column separator and `.` as decimal separator.

## Data protection

Use pseudonymised patient codes only. Session data, results and `dashboard.html` contain patient data and must stay on the clinic computer: they are excluded via `.gitignore` and must never be committed to this repository. Delete session data from the headset after pulling it.

## Planned work

- Usability evaluation of the dashboard with therapists
- Reliability and validity study of the behavioural measures
- Gaze and movement heatmaps, session replay
- Multiple exposure scenarios and configurable object placement
- Eye tracking on headsets that support it; optional physiological measures

## Documentation

- [`docs/FEATURE_DEFINITIONS.md`](docs/FEATURE_DEFINITIONS.md) – feature formulas, rationale and limitations
- [`docs/DASHBOARD.md`](docs/DASHBOARD.md) – therapist dashboard
- [`docs/RESEARCH_PLAN_NOTES.md`](docs/RESEARCH_PLAN_NOTES.md) – milestones, study design and analysis plan
- [`docs/COMPARISON_WITH_EXISTING_SYSTEMS.md`](docs/COMPARISON_WITH_EXISTING_SYSTEMS.md) – comparison with existing VR logging frameworks
- [`docs/CHANGELOG_v2.md`](docs/CHANGELOG_v2.md) – changes from the first version

## Technologies

Unity · C# · Meta XR SDK · Meta Interaction SDK · Universal Render Pipeline · Python (pandas, NumPy, SciPy, Matplotlib)

## Disclaimer

This project is intended solely for research and development in VR-based exposure therapy. The behavioural measures are descriptive and not validated diagnostic instruments. It is not a medical treatment or a certified therapeutic system.
