// ============================================================
//  SessionController.cs   (v2)
//  Start / export sessions from the Quest controllers AND (for
//  desktop testing) the keyboard.
//
//    Right controller A  (or keyboard A) -> EXPORT the current session
//    Left  controller X  (or keyboard X) -> start a NEW session
//                                          (the running one is exported first)
//    Keyboard M                          -> add a "Marker" event (testing)
//
//  v2 changes:
//   * Controller buttons via OVRInput (as described in the setup guide);
//     before, Update() returned early on the headset because there is
//     no keyboard, so only the keyboard path existed in this script.
//   * Exports are idempotent (VRLogger skips a second export), so
//     OnDestroy + OnApplicationQuit no longer write the session twice.
// ============================================================

using UnityEngine;
using UnityEngine.InputSystem;

public class SessionController : MonoBehaviour
{
    [Header("Controller Controls (Meta Quest)")]
    public bool enableControllerInput = true;

    [Header("Keyboard Controls (Editor testing)")]
    public bool enableKeyboardExport = true;
    public bool enableKeyboardNewSession = true;

    [Header("Auto Export")]
    [Tooltip("Also export when this script is destroyed (scene change / app quit)")]
    public bool exportOnDestroy = true;

    private void Start()
    {
        Debug.Log("[SessionController] Started. A = export, X = new session.");

        if (VRLogger.Instance == null)
            Debug.LogWarning("[SessionController] VRLogger.Instance is null at Start. Be sure a VRLogger exists in the scene.");
    }

    private void Update()
    {
        // ── Quest controllers ────────────────────────────────
        if (enableControllerInput)
        {
            if (OVRInput.GetDown(OVRInput.Button.One))      // A (right)
                ExportAndEnd();
            if (OVRInput.GetDown(OVRInput.Button.Three))    // X (left)
                StartNewSession();
        }

        // ── Keyboard (editor) ────────────────────────────────
        var kb = Keyboard.current;
        if (kb == null) return;

        if (enableKeyboardExport && kb.aKey.wasPressedThisFrame)
            ExportAndEnd();

        if (enableKeyboardNewSession && kb.xKey.wasPressedThisFrame)
            StartNewSession();

        if (kb.mKey.wasPressedThisFrame)
            VRLogger.Instance?.LogMarker("ManualMarker");
    }

    private void OnDestroy()
    {
        if (exportOnDestroy && VRLogger.Instance != null && VRLogger.Instance.EventCount > 0)
            VRLogger.Instance.ExportData();   // no-op if already exported
    }

    public void ExportAndEnd()
    {
        if (VRLogger.Instance == null)
        {
            Debug.LogWarning("[SessionController] VRLogger not found in scene!");
            return;
        }
        Debug.Log("[SessionController] Export requested.");
        VRLogger.Instance.ExportData();
    }

    public void StartNewSession()
    {
        Debug.Log("[SessionController] New session requested.");
        VRLogger.Instance?.StartSession();
    }
}
