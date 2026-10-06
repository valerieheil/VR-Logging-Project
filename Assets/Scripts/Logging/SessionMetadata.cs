// ============================================================
//  SessionMetadata.cs   (NEW in v2)
//  Describes WHO / WHEN / WHAT for one session, so that sessions
//  of the same patient can be linked over multiple therapy visits.
//
//  The metadata is written into:
//    - the session JSON (field "metadata")
//    - the "#" header lines of every session CSV
//    - the per-session features CSV/JSON
//    - the participant-level index  VRLogs/<ID>/<ID>_sessions.csv
//
//  DATA PROTECTION: participantID must be a PSEUDONYMISED code
//  (e.g. "P001"). Never enter names, birth dates or anything that
//  identifies a person directly. The key linking codes to people
//  belongs on the clinic side, not on the headset.
// ============================================================

using System;

[Serializable]
public class SessionMetadata
{
    public string schemaVersion  = "";   // version of the log format
    public string featureVersion = "";   // version of the feature definitions
    public string participantID  = "";   // pseudonymised, e.g. "P001"
    public int    sessionNumber  = 0;    // 1, 2, 3 ... per participant
    public string sessionID      = "";   // e.g. "P001_S03_2026-10-06_143022"
    public string condition      = "";   // e.g. "AlcoholCue", "Neutral", "Baseline"
    public string therapistID    = "";   // pseudonymised
    public string notes          = "";   // free text (no identifying info!)
    public string startedAtLocal = "";   // ISO 8601, local time
    public string startedAtUtc   = "";   // ISO 8601, UTC
    public string device         = "";   // SystemInfo.deviceModel
    public string appVersion     = "";   // Application.version
    public string sceneName      = "";   // active Unity scene

    public bool HasParticipant => !string.IsNullOrEmpty(participantID);
}

/// <summary>
/// Optional file  &lt;persistentDataPath&gt;/VRLogs/session_config.json
/// Lets the therapist set the patient for the NEXT session without
/// rebuilding the app:
///     adb push session_config.json /sdcard/Android/data/&lt;app id&gt;/files/VRLogs/
/// Empty fields fall back to the values set in the VRLogger Inspector.
/// Example:
/// { "participantID":"P001", "condition":"AlcoholCue",
///   "therapistID":"T01", "sessionNumber":0, "notes":"" }
/// sessionNumber 0 = automatic (next free number for this participant).
/// </summary>
[Serializable]
public class SessionConfig
{
    public string participantID = "";
    public string condition     = "";
    public string therapistID   = "";
    public int    sessionNumber = 0;
    public string notes         = "";
}
