// ============================================================
//  VRLogger.cs   (v2)
//  The central logging manager (Singleton).
//
//  HOW TO USE:  (unchanged)
//    1. Create an empty GameObject in your scene, name it "VRLogger"
//    2. Drag this script onto it
//    3. From any tracker script, call:
//         VRLogger.Instance.Log(new LogEvent(...))
//    4. At the end of a session call:
//         VRLogger.Instance.ExportData()
//       OR enable "autoExportOnQuit" in the Inspector
//
//  NEW IN v2 (all existing outputs are still written as before):
//    * Participant ID / session number / condition  -> multi-session storage
//        VRLogs/<ParticipantID>/<ParticipantID>_S03_<date>/...
//      (without a participant ID the old layout VRLogs/Session_<date>/ is kept)
//    * Optional VRLogs/session_config.json to switch patients without rebuilding
//    * Stimulus registry (StimulusObject) -> trigger vs neutral objects
//    * SessionStart / SessionEnd / Marker events
//    * Automatic feature extraction + therapist summary + participant index
//    * Safety: X (new session) exports the running session first;
//              a session can only be exported once (no duplicate rows)
// ============================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public class VRLogger : MonoBehaviour
{
   public static VRLogger Instance { get; private set; }

   /// <summary>Version of the raw log format. Bump when columns/event types change.</summary>
   public const string SCHEMA_VERSION = "2.0";

   // ── Inspector settings ───────────────────────────────────
   [Header("Session Settings")]
   [Tooltip("Unique ID for this session. Auto-generated at every session start.")]
   public string sessionID = "";

   [Header("Participant / Multi-Session")]
   [Tooltip("PSEUDONYMISED participant code, e.g. P001 (never a name!). " +
            "Overridden by session_config.json if that file exists.")]
   public string participantID = "";

   [Tooltip("Experimental / therapy condition, e.g. AlcoholCue, Neutral, Baseline.")]
   public string condition = "";

   [Tooltip("Pseudonymised therapist code (optional).")]
   public string therapistID = "";

   [Tooltip("0 = automatic: next free session number for this participant.")]
   public int sessionNumberOverride = 0;

   [Tooltip("Read VRLogs/session_config.json at every session start (push it with adb).")]
   public bool useConfigFile = true;

   [Header("Export Settings")]
   [Tooltip("Folder where JSON and CSV files will be saved.")]
   public string exportFolder = "VRLogs";          // Relative to Application.persistentDataPath

   [Tooltip("Export JSON file at end of session")]
   public bool exportJSON = true;

   [Tooltip("Export CSV file at end of session")]
   public bool exportCSV  = true;

   [Tooltip("Also export separate pre-filtered CSVs (Gaze.csv, Movement.csv, " +
            "Interaction.csv) alongside the full combined CSV, so you don't " +
            "have to filter in Excel if you just want one type of data.")]
   public bool exportFilteredByCategory = true;

   [Tooltip("Automatically export when the app quits or scene ends")]
   public bool autoExportOnQuit = true;

   [Header("Features & Summary")]
   [Tooltip("Compute behavioural features, write the therapist summary and update the participant index at export.")]
   public bool exportFeaturesAndSummary = true;

   public FeatureSettings featureSettings = new FeatureSettings();

   // ── Internal state ───────────────────────────────────────
   private List<LogEvent> _eventBuffer = new List<LogEvent>(); // All events in memory
   private float          _sessionStart;                        // Time.time when session began
   private bool           _isRunning = false;
   private bool           _exported  = false;
   private float          _frozenDuration = -1f;               // duration fixed at export
   private int            _sessionToken = 0;                   // guards coroutines across sessions
   private SessionMetadata _meta = new SessionMetadata();
   private readonly List<StimulusObject> _stimuli = new List<StimulusObject>();

   // CSV column headers — must match LogEvent.ToCSVRow() order.
   // Semicolons so Excel in German locales splits columns by default.
   private const char CSV_SEPARATOR = ';';
   private const string CSV_HEADER =
        "eventType;objectID;timestamp;posX;posY;posZ;duration;extra";
   private const string CSV_BOM = "\uFEFF";

   // ── Filter categories ─────────────────────────────────────
   private static readonly Dictionary<string, string[]> FilterCategories =
       new Dictionary<string, string[]>
   {
       { "Gaze",        new[] { "GazeEnter", "GazeExit" } },
       { "Movement",    new[] { "HeadMove", "LeftHandPos", "RightHandPos" } },
       { "Interaction", new[] { "Grab", "Release" } },
   };

   // ── Unity lifecycle ──────────────────────────────────────

   private void Awake()
   {
       if (Instance != null && Instance != this)
       {
           Destroy(gameObject);
           return;
       }
       Instance = this;
       DontDestroyOnLoad(gameObject);

       StartSession();
   }

   private void OnApplicationQuit()
   {
       if (autoExportOnQuit)
           ExportData();
   }

   // ── Public API ───────────────────────────────────────────

   /// <summary>Log any event. Ignored when no session is running.</summary>
   public void Log(LogEvent evt)
   {
       if (!_isRunning) return;
       _eventBuffer.Add(evt);
   }

   /// <summary>
   /// Starts a new session. If the current session has unexported data it is
   /// exported FIRST (before v2 those events were silently discarded).
   /// </summary>
   public void StartSession()
   {
       if (_isRunning && !_exported && _eventBuffer.Count > 0)
       {
           Debug.Log("[VRLogger] Exporting the running session before starting a new one.");
           ExportData();
       }

       _eventBuffer.Clear();
       _sessionStart   = Time.time;
       _isRunning      = true;
       _exported       = false;
       _frozenDuration = -1f;
       _sessionToken++;

       _meta     = BuildMetadata();
       sessionID = _meta.sessionID;

       // Re-announce all stimuli that are already registered (2nd+ session in one app run)
       foreach (var s in _stimuli) LogStimulus(s, "StimulusInfo");

       StartCoroutine(LogSessionStartNextFrame(_sessionToken));

       Debug.Log($"[VRLogger] Session started: {sessionID}");
   }

   /// <summary>Stops logging and exports all collected data. Safe to call twice.</summary>
   public void ExportData()
   {
       if (_exported)
       {
           Debug.Log($"[VRLogger] Session {sessionID} was already exported - skipping.");
           return;
       }

       // Final head position, then freeze the session
       Log(new LogEvent("SessionEnd", "", SessionTime, HeadPosition()));
       _frozenDuration = SessionTime;
       _isRunning = false;
       _exported  = true;

       Debug.Log($"[VRLogger] Exporting {_eventBuffer.Count} events for session {sessionID}");

       string folder = SessionFolder();
       Directory.CreateDirectory(folder);

       // 1) RAW DATA FIRST — always written, exactly as before
       if (exportJSON) WriteJSON(folder);
       if (exportCSV)  WriteCSV(folder);
       if (exportCSV && exportFilteredByCategory) WriteFilteredCSVs(folder);

       // 2) DERIVED DATA — an analysis error must never cost the raw data
       if (exportFeaturesAndSummary)
       {
           try
           {
               var features = FeatureExtractor.Extract(_eventBuffer, _frozenDuration, featureSettings);
               SessionReportWriter.Write(folder,
                                         _meta.HasParticipant ? ParticipantFolder() : null,
                                         LogsRoot(), _meta, features);
               Debug.Log("[VRLogger] Features, summary and participant index written.");
           }
           catch (Exception e)
           {
               Debug.LogError($"[VRLogger] Feature extraction failed (raw data is safe): {e}");
           }
       }

       Debug.Log($"[VRLogger] Data saved to: {folder}");
   }

   /// <summary>Elapsed session time in seconds (use this for timestamps).</summary>
   public float SessionTime => Time.time - _sessionStart;

   /// <summary>Session duration; fixed once the session has been exported.</summary>
   public float SessionDuration => _frozenDuration >= 0 ? _frozenDuration : SessionTime;

   public int EventCount => _eventBuffer.Count;
   public bool IsExported => _exported;
   public SessionMetadata Metadata => _meta;

   /// <summary>
   /// Free-text marker for phases/annotations, e.g. LogMarker("InstructionGiven").
   /// Appears as eventType "Marker" with objectID = label.
   /// </summary>
   public void LogMarker(string label)
   {
       Log(new LogEvent("Marker", label, SessionTime, HeadPosition()));
   }

   // ── Stimulus registry ────────────────────────────────────

   public void RegisterStimulus(StimulusObject s)
   {
       if (s == null || _stimuli.Contains(s)) return;
       _stimuli.Add(s);
       LogStimulus(s, "StimulusInfo");
   }

   public void UnregisterStimulus(StimulusObject s) => _stimuli.Remove(s);

   public void LogStimulus(StimulusObject s, string eventType)
   {
       if (!_isRunning || s == null) return;
       Log(new LogEvent(eventType, s.StimulusID, SessionTime, s.transform.position, 0f, s.BuildExtra()));
       s.MarkPositionLogged();
   }

   /// <summary>
   /// Ad-hoc filtered CSV for any set of eventTypes. Example:
   ///   VRLogger.Instance.ExportFiltered(new[] { "GazeExit" }, "GazeOnly");
   /// </summary>
   public void ExportFiltered(string[] eventTypes, string label)
   {
       var matching = _eventBuffer.FindAll(evt => Array.IndexOf(eventTypes, evt.eventType) >= 0);

       string folder = SessionFolder();
       Directory.CreateDirectory(folder);

       string path = Path.Combine(folder, $"{sessionID}_{label}.csv");
       File.WriteAllText(path, CSV_BOM + BuildCSVContent(matching), Encoding.UTF8);
       Debug.Log($"[VRLogger] Ad-hoc filtered CSV written: {path} ({matching.Count} rows)");
   }

   // ── Metadata & folders ───────────────────────────────────

   private string LogsRoot() => Path.Combine(Application.persistentDataPath, exportFolder);
   private string ParticipantFolder() => Path.Combine(LogsRoot(), _meta.participantID);

   private string SessionFolder() =>
       _meta.HasParticipant ? Path.Combine(ParticipantFolder(), sessionID)
                            : Path.Combine(LogsRoot(), sessionID);       // legacy layout

   private SessionMetadata BuildMetadata()
   {
       var m = new SessionMetadata
       {
           schemaVersion  = SCHEMA_VERSION,
           featureVersion = FeatureExtractor.FEATURE_VERSION,
           participantID  = participantID,
           condition      = condition,
           therapistID    = therapistID,
           sessionNumber  = sessionNumberOverride,
           startedAtLocal = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
           startedAtUtc   = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
           device         = SystemInfo.deviceModel,
           appVersion     = Application.version,
           sceneName      = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
       };

       // Optional config file overrides the Inspector
       if (useConfigFile)
       {
           string cfgPath = Path.Combine(LogsRoot(), "session_config.json");
           try
           {
               if (File.Exists(cfgPath))
               {
                   var cfg = JsonUtility.FromJson<SessionConfig>(File.ReadAllText(cfgPath));
                   if (!string.IsNullOrEmpty(cfg.participantID)) m.participantID = cfg.participantID;
                   if (!string.IsNullOrEmpty(cfg.condition))     m.condition     = cfg.condition;
                   if (!string.IsNullOrEmpty(cfg.therapistID))   m.therapistID   = cfg.therapistID;
                   if (!string.IsNullOrEmpty(cfg.notes))         m.notes         = cfg.notes;
                   if (cfg.sessionNumber > 0)                    m.sessionNumber = cfg.sessionNumber;
                   Debug.Log($"[VRLogger] session_config.json loaded (participant {m.participantID}).");
               }
           }
           catch (Exception e)
           {
               Debug.LogWarning($"[VRLogger] Could not read {cfgPath}: {e.Message}");
           }
       }

       m.participantID = SanitiseId(m.participantID);
       string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);

       if (m.HasParticipant)
       {
           if (m.sessionNumber <= 0)
               m.sessionNumber = NextSessionNumber(Path.Combine(LogsRoot(), m.participantID));
           m.sessionID = $"{m.participantID}_S{m.sessionNumber:00}_{stamp}";
       }
       else
       {
           m.sessionNumber = 0;
           m.sessionID = "Session_" + stamp;                  // legacy naming
       }
       return m;
   }

   /// <summary>Highest existing _Sxx_ folder number for this participant + 1.</summary>
   private static int NextSessionNumber(string participantFolder)
   {
       int max = 0;
       if (Directory.Exists(participantFolder))
       {
           var rx = new Regex(@"_S(\d+)_");
           foreach (var dir in Directory.GetDirectories(participantFolder))
           {
               var match = rx.Match(Path.GetFileName(dir));
               if (match.Success && int.TryParse(match.Groups[1].Value, out int n))
                   max = Math.Max(max, n);
           }
       }
       return max + 1;
   }

   /// <summary>Only letters, digits, '-' and '_' (safe for folder names and CSV).</summary>
   private static string SanitiseId(string id)
   {
       if (string.IsNullOrEmpty(id)) return "";
       return Regex.Replace(id.Trim(), @"[^A-Za-z0-9_\-]", "_");
   }

   private static Vector3 HeadPosition()
   {
       var cam = Camera.main;
       return cam != null ? cam.transform.position : Vector3.zero;
   }

   private IEnumerator LogSessionStartNextFrame(int token)
   {
       yield return null; // wait one frame so the camera rig has a tracked pose
       if (token == _sessionToken && _isRunning)
           Log(new LogEvent("SessionStart", "", SessionTime, HeadPosition(), 0f,
                            $"participant={_meta.participantID},session={_meta.sessionNumber}"));
   }

   // ── Writers (raw data) ───────────────────────────────────

   private void WriteJSON(string folder)
   {
       var export = new SessionExport
       {
           sessionID    = this.sessionID,
           exportedAt   = DateTime.Now.ToString("o"),
           totalEvents  = _eventBuffer.Count,
           durationSec  = SessionDuration,
           metadata     = _meta,
           events       = _eventBuffer
       };

       string json = JsonUtility.ToJson(export, prettyPrint: true);
       string path = Path.Combine(folder, $"{sessionID}.json");
       File.WriteAllText(path, json, Encoding.UTF8);
       Debug.Log($"[VRLogger] JSON written: {path}");
   }

   private void WriteCSV(string folder)
   {
       string path = Path.Combine(folder, $"{sessionID}.csv");
       File.WriteAllText(path, CSV_BOM + BuildCSVContent(_eventBuffer), Encoding.UTF8);
       Debug.Log($"[VRLogger] CSV written: {path}");
   }

   private void WriteFilteredCSVs(string folder)
   {
       foreach (var category in FilterCategories)
       {
           string categoryName = category.Key;
           string[] eventTypes = category.Value;

           var matching = _eventBuffer.FindAll(evt => Array.IndexOf(eventTypes, evt.eventType) >= 0);
           if (matching.Count == 0) continue;

           string path = Path.Combine(folder, $"{sessionID}_{categoryName}.csv");
           File.WriteAllText(path, CSV_BOM + BuildCSVContent(matching), Encoding.UTF8);
           Debug.Log($"[VRLogger] Filtered CSV written: {path} ({matching.Count} rows)");
       }
   }

   private string BuildCSVContent(List<LogEvent> events)
   {
       var ic = System.Globalization.CultureInfo.InvariantCulture;
       var sb = new StringBuilder();

       // Same 4 metadata rows as v1 (so existing scripts keep working) ...
       sb.AppendLine($"# SessionID{CSV_SEPARATOR}{sessionID}");
       sb.AppendLine($"# ExportedAt{CSV_SEPARATOR}{DateTime.Now:o}");
       sb.AppendLine($"# TotalEvents{CSV_SEPARATOR}{events.Count}");
       sb.AppendLine($"# DurationSec{CSV_SEPARATOR}{SessionDuration.ToString("F2", ic)}");
       // ... plus v2 rows
       sb.AppendLine($"# ParticipantID{CSV_SEPARATOR}{_meta.participantID}");
       sb.AppendLine($"# SessionNumber{CSV_SEPARATOR}{_meta.sessionNumber}");
       sb.AppendLine($"# Condition{CSV_SEPARATOR}{_meta.condition}");
       sb.AppendLine($"# SchemaVersion{CSV_SEPARATOR}{SCHEMA_VERSION}");
       sb.AppendLine();

       sb.AppendLine(CSV_HEADER);
       foreach (var evt in events)
           sb.AppendLine(evt.ToCSVRow());

       return sb.ToString();
   }

   // ── JSON wrapper class ───────────────────────────────────
   [System.Serializable]
   private class SessionExport
   {
       public string          sessionID;
       public string          exportedAt;
       public int             totalEvents;
       public float           durationSec;
       public SessionMetadata metadata;
       public List<LogEvent>  events;
   }
}
