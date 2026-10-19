using System.Reflection;
using JUTPS;
using JUTPS.CameraSystems;
using JUTPS.WeaponSystem;
using Mirror;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Trailer footage capture. Editor only: nothing here reaches a build or touches a scene.
///
/// Tools > Last Stand > Trailer > Start Recording records the Game view - HUD, menus and
/// all - to Recordings/ (git-ignored) at 1080p and a constant 60 FPS while you play. The
/// in-game music is muted for the take, because the trailer brings its own track, and the
/// Game view's "Mute Audio" is lifted, because it would leave the recording silent.
///
/// Start Recording (Autopilot) needs nobody at the controls: the touch controls are shown
/// as on a phone, and in a match the game's own AI plays your character while the camera
/// looks where it aims. Host a match with "Fill with AI" first.
/// </summary>
public static class TrailerCapture
{
    private const string OutputFolder = "Recordings";

    private static RecorderController controller;
    private static bool autopilot;
    private static bool wasEditorMuted;
    private static float nextSetupTime;

    [MenuItem("Tools/Last Stand/Trailer/Start Recording")]
    public static void StartRecording()
    {
        Begin("play", false);
    }

    [MenuItem("Tools/Last Stand/Trailer/Start Recording (Autopilot)")]
    public static void StartAutopilotRecording()
    {
        Begin("play", true);
    }

    [MenuItem("Tools/Last Stand/Trailer/Stop Recording")]
    public static void StopRecording()
    {
        if (controller == null)
            return;

        controller.StopRecording();
        controller = null;

        Application.onBeforeRender -= Autopilot;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        SetMusicMuted(false);
        EditorUtility.audioMasterMute = wasEditorMuted;

        Debug.Log("[TrailerCapture] Recording saved to " + OutputFolder + "/");
    }

    /// <summary>
    /// The take is saved as Recordings/&lt;takeName&gt;_&lt;time&gt;.mp4. The edit (Tools/make_trailer.py)
    /// sorts takes by that name: "ui_" takes are the menus, "play_" takes are matches.
    /// </summary>
    public static void Begin(string takeName, bool useAutopilot)
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[TrailerCapture] Enter Play mode first.");
            return;
        }

        if (controller != null)
            StopRecording();

        var movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
        movie.name = "Trailer";
        movie.Enabled = true;
        movie.EncoderSettings = new CoreEncoderSettings
        {
            Codec = CoreEncoderSettings.OutputCodec.MP4,
            EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.Custom,
            EncodingProfile = CoreEncoderSettings.H264EncodingProfile.High,
            // Mbps. The built-in "High" preset smears foliage and dark scenes.
            TargetBitRate = 40f
        };
        movie.CaptureAudio = true;
        movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = 1920, OutputHeight = 1080 };
        movie.OutputFile = OutputFolder + "/" + takeName + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
        settings.AddRecorderSettings(movie);
        settings.SetRecordModeToManual();
        settings.FrameRate = 60f;
        // Constant: every frame is rendered and kept, so cuts stay in sync with the audio.
        settings.FrameRatePlayback = FrameRatePlayback.Constant;
        settings.CapFrameRate = true;

        controller = new RecorderController(settings);
        controller.PrepareRecording();

        if (!controller.StartRecording())
        {
            controller = null;
            Debug.LogError("[TrailerCapture] The recorder did not start.");
            return;
        }

        autopilot = useAutopilot;
        nextSetupTime = 0f;
        SetMusicMuted(true);
        wasEditorMuted = EditorUtility.audioMasterMute;
        EditorUtility.audioMasterMute = false;

        Application.onBeforeRender += Autopilot;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;

        Debug.Log("[TrailerCapture] Recording " + movie.OutputFile + ".mp4");
    }

    // Leaving Play mode ends the take, so a recording is never left open.
    private static void OnPlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingPlayMode)
            StopRecording();
    }

    private static void SetMusicMuted(bool muted)
    {
        if (MusicPlayer.Instance != null && MusicPlayer.Instance.TryGetComponent(out AudioSource music))
            music.mute = muted;
    }

    // -------------------------
    // Autopilot
    // -------------------------

    private static void Autopilot()
    {
        if (!autopilot)
            return;

        LSPlayer local = LSPlayer.LocalInstance;
        TPSCameraController view = CameraManager.MainCam;

        if (Time.time >= nextSetupTime)
        {
            nextSetupTime = Time.time + 1f;
            ShowTouchControls();

            if (local != null && view != null)
                TakeOver(local, view);
        }

        if (local == null || view == null || !local.TryGetComponent(out LSBotBrain _) ||
            !local.TryGetComponent(out JUCharacterController character) || character.IsDead)
            return;

        // The AI aims with LookAtPosition, not with the camera, so the camera is turned
        // to put that point under the crosshair. Eased, because AI aim jitters several
        // times a second - and so that a recoil kick shows before it is pulled back.
        Vector3 aim = character.LookAtPosition - view.mCamera.transform.position;

        if (aim.sqrMagnitude < 1f)
            return;

        float yaw = Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Asin(aim.normalized.y) * Mathf.Rad2Deg;
        float blend = 1f - Mathf.Exp(-6f * Time.deltaTime);

        view.rotytarget = Mathf.LerpAngle(view.rotytarget, yaw, blend);
        view.rotxtarget = Mathf.LerpAngle(view.rotxtarget, pitch, blend);
    }

    /// <summary>
    /// Hands the local character to LSBotBrain, the same AI that drives the match's bots.
    /// Only in a match the editor is hosting: the brain runs on the server.
    /// </summary>
    private static void TakeOver(LSPlayer local, TPSCameraController view)
    {
        if (!NetworkServer.active || SceneManager.GetActiveScene().name != "GameScene" ||
            !local.TryGetComponent(out JUCharacterController character))
            return;

        if (!local.TryGetComponent(out LSBotBrain _))
        {
            character.IsArtificialIntelligence = true;
            character.UseDefaultControllerInput = false;

            // Full-strength AI whatever difficulty the match's bots run at: the trailer
            // wants the player winning fights.
            local.gameObject.AddComponent<LSBotBrain>().Initialise(new LSBotSettings());
        }

        // LSBotBrain unhooks the gun from the camera so a bot's shots do not shake the
        // host's view. This gun is the one on screen, so its recoil belongs there.
        Weapon weapon = character.WeaponInUseRightHand;

        if (weapon != null && weapon.CamPivot == null)
            weapon.CamPivot = view;
    }

    /// <summary>
    /// EditorTestSettings hides the touch controls for keyboard play; the trailer shows
    /// the game as it is on a phone. Only this play session is affected.
    /// </summary>
    private static void ShowTouchControls()
    {
        FieldInfo simulateMobile = typeof(JUGameManager).GetField(
            "SimulateMobileDevice", BindingFlags.NonPublic | BindingFlags.Instance);

        if (simulateMobile == null)
            return;

        foreach (JUGameManager manager in Object.FindObjectsByType<JUGameManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            simulateMobile.SetValue(manager, true);
    }
}
