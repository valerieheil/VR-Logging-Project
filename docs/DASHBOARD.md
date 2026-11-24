# Therapist dashboard

`run_pipeline.py` writes `dashboard.html` into the output folder. It is a single self-contained file: open it by double-click in any current browser (Edge, Chrome, Firefox). It needs no internet connection, no installation and no server, and it makes no network requests, so pseudonymised patient data never leaves the clinic computer.

## How to use it

1. Pull the logs from the headset and run the pipeline (with questionnaires if available):
   `python Analysis/run_pipeline.py --logs C:\VRLogs --questionnaires q.xlsx --out C:\VRResults`
2. Open `C:\VRResults\dashboard.html`.
3. Choose a patient on the left. The newest session is selected automatically. Click any session in the timeline, a column header or a chart point to look at that session; the left/right arrow keys move between sessions.
4. **?** next to a measure explains it in plain language. **Print this page** gives a printable record for the patient file.

A link like `dashboard.html#P003` opens a patient directly. Re-run the pipeline after each session to update the file; it is regenerated from the raw logs every time.

## What it shows

**Session timeline** with a note count for sessions that have data-quality issues.

**Flowsheet**: one row per measure, one column per session, plus a small trend line. Measures are grouped into craving & questionnaires, attention, approach, interaction and session. Questionnaire values with a reliable change compared with the first session (Reliable Change Index, Jacobson & Truax, 1991) carry a small **R**; this needs SD and reliability in the questionnaire codebook.

**Selected session**: change since the previous and the first session for every measure; alcohol-related behaviour in the beginning, middle and end of the session; data notes.

**Trends**: craving before and after exposure, looking at alcohol vs neutral objects, time near alcohol, and the two bias values with the 0.50 no-preference line.

## Design decisions (for the paper)

**Flowsheet metaphor.** Clinicians already read patient charts as flowsheets (measures × visits). Using that structure lowers the learning effort and keeps every number visible rather than hidden behind charts.

**No evaluative colour coding.** Changes are shown as direction and size, never as red/green "better/worse". For several measures the clinically desirable direction is not self-evident: less looking at alcohol may indicate reduced attentional bias or avoidance, and within-session decline is not required for exposure to be effective (Craske et al., 2014). Interpretation is left to the therapist, in line with the research question's focus on supporting rather than replacing clinical decisions.

**Consistent semantic colours.** Amber always refers to alcohol objects, teal to neutral control objects, blue to questionnaires, grey to session context.

**Transparency about limits.** Plain-language definitions for every measure, explicit data-quality notes per session, empty cells instead of imputed values, and a fixed note that the measures are descriptive, head-based and not diagnostic.

**Offline by design.** A single static file avoids server setup in clinics, works on locked-down hospital computers, and keeps data under GDPR-compliant local control. It is also a frozen, reproducible snapshot of the data at generation time.

## Evaluating it (suggested)

Think-aloud sessions with 3–5 therapists working through realistic questions ("Has this patient’s craving response changed since the first session?"), followed by the System Usability Scale (Brooke, 1996) and task success/time. Use the simulated example data for this, never real patient data.

## References

Brooke, J. (1996). SUS: A "quick and dirty" usability scale. In P. W. Jordan et al. (Eds.), *Usability Evaluation in Industry* (pp. 189–194). Taylor & Francis.

Craske, M. G., Treanor, M., Conway, C. C., Zbozinek, T., & Vervliet, B. (2014). Maximizing exposure therapy: An inhibitory learning approach. *Behaviour Research and Therapy, 58*, 10–23.

Jacobson, N. S., & Truax, P. (1991). Clinical significance: A statistical approach to defining meaningful change in psychotherapy research. *Journal of Consulting and Clinical Psychology, 59*(1), 12–19.
