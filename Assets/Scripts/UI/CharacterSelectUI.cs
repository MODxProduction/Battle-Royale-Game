using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// Character screen: every playable body stands idling in a window, can be dragged
/// round to be seen from all sides, and the one picked is saved to the player profile.
/// LSNetworkManager sends that pick to the server when the player connects.
///
/// The bodies are the plain imported models - no weapons, no armour - filmed by a camera
/// of its own into a render texture, on a stage far below the menu so nothing else gets
/// in shot. The stage only exists while the screen is open.
///
/// Built in code in the style of the Host / Join / Settings popups, like MatchResultUI.
/// </summary>
[DisallowMultipleComponent]
public class CharacterSelectUI : MonoBehaviour
{
    [Serializable]
    public struct Character
    {
        [Tooltip("Must match the character prefab's name in LSNetworkManager's Character Prefabs.")]
        public string name;

        [Tooltip("The imported model: the body alone.")]
        public GameObject model;
    }

    [SerializeField] private Character[] characters;

    [Tooltip("Plays the idle animations on the preview.")]
    [SerializeField] private RuntimeAnimatorController idleController;

    [Tooltip("The same asset the Settings popup uses, so neither overwrites the other's change.")]
    [SerializeField] private PlayerProfileSO playerProfile;

    [Header("Look - same assets as the Host / Join / Settings popups")]
    [SerializeField] private TMP_FontAsset font;

    [Tooltip("MUIP Rounded/Standard button, the one the other popups use.")]
    [SerializeField] private GameObject buttonPrefab;

    [SerializeField] private int sortingOrder = 500;

    [Header("Preview")]
    [SerializeField] private float degreesPerPixel = 0.4f;
    [SerializeField] private Color backdropColor = new Color(0.09f, 0.11f, 0.135f, 1f);

    private static readonly Vector3 StagePosition = new Vector3(0f, -1000f, 0f);
    private const float CameraFieldOfView = 30f;

    private RectTransform root;
    private RawImage preview;
    private UISelectionCard[] cards;

    private GameObject stage;
    private Camera stageCamera;
    private RenderTexture renderTexture;
    private GameObject shownModel;
    private float yaw;

    private int shownIndex;
    private int savedIndex = -1;

    public bool IsOpen => root != null && root.gameObject.activeSelf;

    public void Open()
    {
        if (characters == null || characters.Length == 0)
        {
            Debug.LogWarning("[CharacterSelectUI] No characters assigned.");
            return;
        }

        if (playerProfile == null)
            playerProfile = ScriptableObject.CreateInstance<PlayerProfileSO>();

        playerProfile.LoadProfile();
        savedIndex = Array.FindIndex(characters, c => c.name == playerProfile.characterName);

        if (root == null)
            Build();

        root.gameObject.SetActive(true);
        BuildStage();
        Show(Mathf.Max(savedIndex, 0));
    }

    public void Close()
    {
        if (root != null)
            root.gameObject.SetActive(false);

        DestroyStage();
    }

    private void OnDestroy()
    {
        DestroyStage();
    }

    private void Confirm()
    {
        savedIndex = shownIndex;
        playerProfile.characterName = characters[shownIndex].name;
        playerProfile.SaveProfile();
        Close();
    }

    private void Show(int index)
    {
        shownIndex = index;

        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].SetSelected(i == index);
            cards[i].SetContent(characters[i].name.ToUpperInvariant(), i == savedIndex ? "SELECTED" : "");
        }

        if (shownModel != null)
            Destroy(shownModel);

        shownModel = Instantiate(characters[index].model, stage.transform);
        shownModel.transform.localPosition = Vector3.zero;
        shownModel.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

        if (shownModel.TryGetComponent(out Animator animator))
        {
            animator.runtimeAnimatorController = idleController;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        Frame(shownModel);
    }

    /// <summary>Whole body in shot, whatever its height.</summary>
    private void Frame(GameObject model)
    {
        Bounds bounds = new Bounds(model.transform.position, Vector3.zero);

        foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
            bounds.Encapsulate(r.bounds);

        float halfHeight = bounds.extents.y * 1.12f;
        float distance = halfHeight / Mathf.Tan(CameraFieldOfView * 0.5f * Mathf.Deg2Rad);
        Vector3 target = new Vector3(StagePosition.x, bounds.center.y, StagePosition.z);

        stageCamera.transform.position = target + Vector3.forward * distance;
        stageCamera.transform.LookAt(target);
    }

    private void Rotate(BaseEventData data)
    {
        yaw -= ((PointerEventData)data).delta.x * degreesPerPixel;

        if (shownModel != null)
            shownModel.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // -------------------------
    // Stage
    // -------------------------

    private void BuildStage()
    {
        if (stage != null)
            return;

        stage = new GameObject("CharacterSelectStage");
        stage.transform.position = StagePosition;

        var cameraObject = new GameObject("Camera");
        cameraObject.transform.SetParent(stage.transform, false);
        stageCamera = cameraObject.AddComponent<Camera>();
        stageCamera.fieldOfView = CameraFieldOfView;
        stageCamera.nearClipPlane = 0.1f;
        stageCamera.farClipPlane = 20f;
        stageCamera.clearFlags = CameraClearFlags.SolidColor;
        stageCamera.backgroundColor = backdropColor;
        cameraObject.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;

        Rect area = preview.rectTransform.rect;
        renderTexture = new RenderTexture(Mathf.RoundToInt(area.width * 1.5f), Mathf.RoundToInt(area.height * 1.5f), 24)
        {
            antiAliasing = 4
        };
        stageCamera.targetTexture = renderTexture;
        preview.texture = renderTexture;

        // Key light from the front and above, so the face is lit whichever way the menu's sun points.
        var lightObject = new GameObject("Key Light");
        lightObject.transform.SetParent(stage.transform, false);
        lightObject.transform.rotation = Quaternion.Euler(25f, 160f, 0f);
        Light key = lightObject.AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1.1f;
    }

    private void DestroyStage()
    {
        if (stage != null)
            Destroy(stage);

        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
        }

        stage = null;
        shownModel = null;
        renderTexture = null;
    }

    // -------------------------
    // Layout
    // -------------------------

    private void Build()
    {
        LSUITheme.EnsureEventSystem();

        root = LSUITheme.Canvas("CharacterSelectCanvas", transform, sortingOrder);

        RectTransform dim = LSUITheme.Panel("Dim", root, LSUITheme.Dim, true);
        LSUITheme.Stretch(dim);

        RectTransform window = LSUITheme.Panel("Window", root, LSUITheme.Window, true);
        LSUITheme.Place(window, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                        new Vector2(1500f, 860f), new Vector2(0.5f, 0.5f));

        TextMeshProUGUI title = LSUITheme.Label("Title", window, font, "SELECT CHARACTER", 54f, 8f,
                                                LSUITheme.Text, TextAlignmentOptions.Center);
        LSUITheme.PlaceTop(title.rectTransform, -40f, 70f);

        TextMeshProUGUI subtitle = LSUITheme.Label("Subtitle", window, font, "DRAG TO ROTATE", 22f, 6f,
                                                   LSUITheme.Muted, TextAlignmentOptions.Center);
        LSUITheme.PlaceTop(subtitle.rectTransform, -112f, 30f);

        RectTransform accent = LSUITheme.Panel("Accent", window, LSUITheme.Accent);
        LSUITheme.Place(accent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f),
                        new Vector2(120f, 4f), new Vector2(0.5f, 1f));

        // Preview, left.
        RectTransform frame = LSUITheme.Panel("PreviewFrame", window, LSUITheme.BoxBorder);
        LSUITheme.Place(frame, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(60f, -180f),
                        new Vector2(860f, 620f), new Vector2(0f, 1f));

        var previewObject = new GameObject("Preview", typeof(RectTransform));
        previewObject.transform.SetParent(frame, false);
        preview = previewObject.AddComponent<RawImage>();
        RectTransform previewRect = preview.rectTransform;
        LSUITheme.Stretch(previewRect);
        previewRect.offsetMin = new Vector2(2f, 2f);
        previewRect.offsetMax = new Vector2(-2f, -2f);

        var drag = new EventTrigger.Entry { eventID = EventTriggerType.Drag };
        drag.callback.AddListener(Rotate);
        previewObject.AddComponent<EventTrigger>().triggers.Add(drag);

        // Character cards, right.
        cards = new UISelectionCard[characters.Length];

        for (int i = 0; i < characters.Length; i++)
            cards[i] = BuildCard(window, i);

        // Buttons, bottom right. MUIP sizes a button to its label a frame late, so a
        // layout row lines them up instead of fixed positions.
        RectTransform buttonRow = LSUITheme.Panel("Buttons", window, Color.clear);
        LSUITheme.Place(buttonRow, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-60f, 60f),
                        new Vector2(460f, 60f), new Vector2(1f, 0f));

        HorizontalLayoutGroup row = buttonRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.childAlignment = TextAnchor.MiddleRight;
        row.spacing = 20f;
        row.childControlWidth = row.childControlHeight = false;
        row.childForceExpandWidth = row.childForceExpandHeight = false;

        LSUITheme.Button(buttonPrefab, buttonRow, "CloseButton", "CLOSE", null, LSUITheme.ButtonIdle, Close);
        LSUITheme.Button(buttonPrefab, buttonRow, "SelectButton", "SELECT", null, LSUITheme.ButtonGreen, Confirm);

        // Tapping outside the window closes it, like the other popups.
        Button backdrop = dim.gameObject.AddComponent<Button>();
        backdrop.transition = Selectable.Transition.None;
        backdrop.onClick.AddListener(Close);
    }

    private UISelectionCard BuildCard(RectTransform window, int index)
    {
        RectTransform border = LSUITheme.Panel("Card_" + characters[index].name, window, LSUITheme.BoxBorder, true);
        LSUITheme.Place(border, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-60f, -180f - index * 125f),
                        new Vector2(460f, 105f), new Vector2(1f, 1f));

        RectTransform inner = LSUITheme.Panel("Inner", border, LSUITheme.BoxInner);
        LSUITheme.Stretch(inner);
        inner.offsetMin = new Vector2(2f, 2f);
        inner.offsetMax = new Vector2(-2f, -2f);

        TextMeshProUGUI name = LSUITheme.Label("Name", inner, font, "", 34f, 6f,
                                               LSUITheme.Text, TextAlignmentOptions.Left);
        LSUITheme.Place(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(28f, 0f),
                        new Vector2(-56f, 50f), new Vector2(0f, 0.5f));

        TextMeshProUGUI tag = LSUITheme.Label("Tag", inner, font, "", 18f, 4f,
                                              LSUITheme.Accent, TextAlignmentOptions.Right);
        LSUITheme.Place(tag.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-28f, 0f),
                        new Vector2(-56f, 30f), new Vector2(1f, 0.5f));

        border.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;
        UISelectionCard card = border.gameObject.AddComponent<UISelectionCard>();
        card.Bind(inner.GetComponent<Image>(), border.GetComponent<Image>(), name, tag, null);
        card.Button.onClick.AddListener(() => Show(index));

        return card;
    }
}
