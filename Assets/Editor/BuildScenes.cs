using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using SyncRush;

/// <summary>
/// Builds LobbyScene and GameScene from the existing NGO_Setup content.
///
/// LobbyScene: NetworkManager, LobbyCanvas, EventSystem, static camera
/// GameScene:  Platform, Directional Light, Main Camera (with PlayerCameraController)
/// </summary>
public class BuildScenes
{
    public static void Execute()
    {
        // ── Find Liberation Sans font ─────────────────────────────────────────
        TMP_FontAsset font = null;
        foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.Contains("Fallback"))
            {
                font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                break;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // LOBBY SCENE
        // ════════════════════════════════════════════════════════════════════
        var lobbyScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        lobbyScene.name = "LobbyScene";

        // Directional Light
        var lightGO = new GameObject("Directional Light");
        var light = lightGO.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1f;
        lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // Static camera for lobby (no PlayerCameraController)
        var lobbyCamera = new GameObject("Main Camera");
        lobbyCamera.tag = "MainCamera";
        lobbyCamera.AddComponent<Camera>();
        lobbyCamera.AddComponent<AudioListener>();
        lobbyCamera.transform.position = new Vector3(0, 1, -10);

        // NetworkManager
        var nmGO = new GameObject("NetworkManager");
        var nm = nmGO.AddComponent<Unity.Netcode.NetworkManager>();
        nmGO.AddComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
        var lobbyManager = nmGO.AddComponent<LobbyManager>();

        // Assign the NetworkPrefabsList to NetworkManager
        var prefabsList = AssetDatabase.LoadAssetAtPath<Unity.Netcode.NetworkPrefabsList>(
            "Assets/Prefabs/NetworkPrefabsList.asset");
        if (prefabsList != null)
        {
            var so = new SerializedObject(nm);
            var networkPrefabsSO = so.FindProperty("NetworkConfig.Prefabs.NetworkPrefabsLists");
            if (networkPrefabsSO != null)
            {
                networkPrefabsSO.arraySize = 1;
                networkPrefabsSO.GetArrayElementAtIndex(0).objectReferenceValue = prefabsList;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // EventSystem with New Input System module
        var esGO = new GameObject("EventSystem");
        esGO.AddComponent<EventSystem>();
        esGO.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

        // ── Build LobbyCanvas ─────────────────────────────────────────────────
        BuildLobbyCanvas(lobbyManager, font);

        EditorSceneManager.SaveScene(lobbyScene, "Assets/Scenes/LobbyScene.unity");
        Debug.Log("LobbyScene saved.");

        // ════════════════════════════════════════════════════════════════════
        // GAME SCENE
        // ════════════════════════════════════════════════════════════════════
        var gameScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        gameScene.name = "GameScene";

        // Directional Light
        var gameLightGO = new GameObject("Directional Light");
        var gameLight = gameLightGO.AddComponent<Light>();
        gameLight.type = LightType.Directional;
        gameLight.intensity = 1f;
        gameLightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        // Main Camera with PlayerCameraController
        var gameCameraGO = new GameObject("Main Camera");
        gameCameraGO.tag = "MainCamera";
        var gameCam = gameCameraGO.AddComponent<Camera>();
        gameCam.fieldOfView = 60f;
        gameCameraGO.AddComponent<AudioListener>();
        gameCameraGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
        var camController = gameCameraGO.AddComponent<PlayerCameraController>();
        // Set default camera values via SerializedObject
        var camSO = new SerializedObject(camController);
        camSO.FindProperty("_distance").floatValue        = 10f;
        camSO.FindProperty("_height").floatValue          = 5.5f;
        camSO.FindProperty("_followSmoothing").floatValue = 8f;
        camSO.FindProperty("_mouseSensitivity").floatValue = 0.15f;
        camSO.FindProperty("_stickSensitivity").floatValue = 90f;
        camSO.FindProperty("_collisionRadius").floatValue  = 0.2f;
        camSO.ApplyModifiedPropertiesWithoutUndo();
        gameCameraGO.transform.position = new Vector3(0, 1, -10);

        // Platform — recreate as a simple plane since ProBuilder mesh can't be
        // trivially duplicated via script; the user can replace with their ProBuilder mesh
        var platformGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
        platformGO.name = "Platform";
        platformGO.transform.position = new Vector3(0, -2.58f, 0);
        platformGO.transform.localScale = new Vector3(5f, 1f, 5f);
        // Add NetworkObject so NGO can track it
        platformGO.AddComponent<Unity.Netcode.NetworkObject>();

        EditorSceneManager.SaveScene(gameScene, "Assets/Scenes/GameScene.unity");
        Debug.Log("GameScene saved.");

        // ── Add both scenes to Build Settings ────────────────────────────────
        var scenes = new EditorBuildSettingsScene[]
        {
            new EditorBuildSettingsScene("Assets/Scenes/LobbyScene.unity", true),
            new EditorBuildSettingsScene("Assets/Scenes/GameScene.unity",  true),
        };
        EditorBuildSettings.scenes = scenes;
        Debug.Log("Build Settings updated: LobbyScene (0), GameScene (1).");

        // ── Reload LobbyScene as active ───────────────────────────────────────
        EditorSceneManager.OpenScene("Assets/Scenes/LobbyScene.unity");
        Debug.Log("Scene split complete. Open GameScene to move your Platform mesh across.");
    }

    // ── Builds the full LobbyCanvas in the current scene ─────────────────────
    private static void BuildLobbyCanvas(LobbyManager lobbyManager, TMP_FontAsset font)
    {
        var canvasGO = new GameObject("LobbyCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        Color bg   = new Color(0.08f, 0.08f, 0.12f, 0.97f);
        Color blue = new Color(0.2f, 0.6f, 1f);
        Color grey = new Color(0.25f, 0.25f, 0.3f);
        Color red  = new Color(0.75f, 0.18f, 0.18f);
        Color green = new Color(0.1f, 0.7f, 0.3f);

        // ── Helpers ───────────────────────────────────────────────────────────
        TextMeshProUGUI MakeTMP(string name, Transform parent, string text,
                                float fontSize, Color color, Vector2 pos, Vector2 size,
                                TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = false;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return tmp;
        }

        GameObject MakePanel(string name, Transform parent, Color bgColor, bool active = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = bgColor;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.SetActive(active);
            return go;
        }

        Button MakeButton(string name, Transform parent, string label,
                          Vector2 pos, Vector2 size, Color btnColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = btnColor;
            var btn = go.AddComponent<Button>();
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(go.transform, false);
            var tmp = labelGO.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = label;
            tmp.fontSize = 28;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            return btn;
        }

        TMP_InputField MakeInputField(string name, Transform parent,
                                      string placeholder, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.2f);
            var field = go.AddComponent<TMP_InputField>();
            field.characterLimit = LobbyManager.CodeLength;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var areaGO = new GameObject("Text Area");
            areaGO.transform.SetParent(go.transform, false);
            areaGO.AddComponent<RectMask2D>();
            var art = areaGO.GetComponent<RectTransform>();
            art.anchorMin = Vector2.zero; art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(10, 4); art.offsetMax = new Vector2(-10, -4);
            var phGO = new GameObject("Placeholder");
            phGO.transform.SetParent(areaGO.transform, false);
            var phTMP = phGO.AddComponent<TextMeshProUGUI>();
            if (font != null) phTMP.font = font;
            phTMP.text = placeholder; phTMP.fontSize = 28;
            phTMP.color = new Color(0.5f, 0.5f, 0.5f);
            phTMP.alignment = TextAlignmentOptions.Center;
            phTMP.enableWordWrapping = false;
            var phrt = phGO.GetComponent<RectTransform>();
            phrt.anchorMin = Vector2.zero; phrt.anchorMax = Vector2.one;
            phrt.offsetMin = phrt.offsetMax = Vector2.zero;
            var inputGO = new GameObject("Text");
            inputGO.transform.SetParent(areaGO.transform, false);
            var inputTMP = inputGO.AddComponent<TextMeshProUGUI>();
            if (font != null) inputTMP.font = font;
            inputTMP.fontSize = 28; inputTMP.color = Color.white;
            inputTMP.alignment = TextAlignmentOptions.Center;
            inputTMP.enableWordWrapping = false;
            var irt = inputGO.GetComponent<RectTransform>();
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
            irt.offsetMin = irt.offsetMax = Vector2.zero;
            field.textViewport = art;
            field.textComponent = inputTMP;
            field.placeholder = phTMP;
            return field;
        }

        TextMeshProUGUI MakeErrorLabel(string name, Transform parent, Vector2 pos)
        {
            var tmp = MakeTMP(name, parent, "", 20, new Color(1f, 0.35f, 0.35f),
                              pos, new Vector2(600, 36));
            tmp.gameObject.SetActive(false);
            return tmp;
        }

        Color subColor = new Color(0.7f, 0.7f, 0.7f);

        // ── Main Panel ────────────────────────────────────────────────────────
        var mainPanel = MakePanel("MainPanel", canvasGO.transform, bg);
        MakeTMP("Title",    mainPanel.transform, "SYNC RUSH",            72, Color.white, new Vector2(0, 160), new Vector2(800, 110));
        MakeTMP("Subtitle", mainPanel.transform, "A physics comedy race", 26, subColor,   new Vector2(0, 90),  new Vector2(600, 44));
        var hostBtn = MakeButton("HostButton", mainPanel.transform, "HOST GAME", new Vector2(0, -20),  new Vector2(320, 70), blue);
        var joinBtn = MakeButton("JoinButton", mainPanel.transform, "JOIN GAME", new Vector2(0, -110), new Vector2(320, 70), blue);

        // ── Host Panel ────────────────────────────────────────────────────────
        var hostPanel = MakePanel("HostPanel", canvasGO.transform, bg, false);
        MakeTMP("HostTitle",        hostPanel.transform, "CREATE LOBBY", 48, Color.white, new Vector2(0, 160), new Vector2(600, 70));
        MakeTMP("HostInstructions", hostPanel.transform, "Choose a 6-character code.\nShare it with your friends.", 22, subColor, new Vector2(0, 80), new Vector2(700, 70));
        var hostCodeInput  = MakeInputField("HostCodeInput",  hostPanel.transform, "e.g. ABC123", new Vector2(0, -10),  new Vector2(400, 65));
        var hostErrorText  = MakeErrorLabel("HostErrorText",  hostPanel.transform, new Vector2(0, -68));
        var startHostBtn   = MakeButton("StartHostButton",    hostPanel.transform, "START LOBBY", new Vector2(0, -150), new Vector2(320, 70), blue);
        var hostBackBtn    = MakeButton("HostBackButton",     hostPanel.transform, "BACK",        new Vector2(0, -240), new Vector2(200, 55), grey);

        // ── Join Panel ────────────────────────────────────────────────────────
        var joinPanel = MakePanel("JoinPanel", canvasGO.transform, bg, false);
        MakeTMP("JoinTitle",        joinPanel.transform, "JOIN LOBBY", 48, Color.white, new Vector2(0, 160), new Vector2(600, 70));
        MakeTMP("JoinInstructions", joinPanel.transform, "Enter the 6-character code from your host.", 22, subColor, new Vector2(0, 90), new Vector2(700, 44));
        var joinCodeInput  = MakeInputField("JoinCodeInput",   joinPanel.transform, "Enter code",  new Vector2(0, 10),   new Vector2(400, 65));
        var joinErrorText  = MakeErrorLabel("JoinErrorText",   joinPanel.transform, new Vector2(0, -48));
        var joinConfirmBtn = MakeButton("JoinConfirmButton",   joinPanel.transform, "JOIN",         new Vector2(0, -130), new Vector2(320, 70), blue);
        var joinBackBtn    = MakeButton("JoinBackButton",      joinPanel.transform, "BACK",         new Vector2(0, -220), new Vector2(200, 55), grey);

        // ── Waiting Panel ─────────────────────────────────────────────────────
        var waitingPanel = MakePanel("WaitingPanel", canvasGO.transform, bg, false);
        MakeTMP("WaitingTitle",   waitingPanel.transform, "LOBBY",               48, Color.white, new Vector2(0, 200), new Vector2(400, 70));
        var codeDisplay     = MakeTMP("CodeDisplay",    waitingPanel.transform, "Your Code: ------", 34, Color.white, new Vector2(0, 120), new Vector2(700, 55));
        var statusText      = MakeTMP("StatusText",     waitingPanel.transform, "Waiting for players...", 22, subColor, new Vector2(0, 55), new Vector2(700, 40));
        var playerCountText = MakeTMP("PlayerCountText",waitingPanel.transform, "Players: 0 / 4", 28, Color.white, new Vector2(0, 0), new Vector2(400, 50));
        // Start Game — host only (shown/hidden by LobbyUI at runtime)
        var startGameBtn    = MakeButton("StartGameButton", waitingPanel.transform, "START GAME",  new Vector2(0, -80),  new Vector2(320, 70), green);
        var leaveBtn        = MakeButton("LeaveButton",     waitingPanel.transform, "LEAVE LOBBY", new Vector2(0, -165), new Vector2(280, 60), red);

        // ── Wire LobbyUI ──────────────────────────────────────────────────────
        var lobbyUI = canvasGO.AddComponent<LobbyUI>();
        var so = new SerializedObject(lobbyUI);
        so.FindProperty("_mainPanel").objectReferenceValue    = mainPanel;
        so.FindProperty("_hostPanel").objectReferenceValue    = hostPanel;
        so.FindProperty("_joinPanel").objectReferenceValue    = joinPanel;
        so.FindProperty("_waitingPanel").objectReferenceValue = waitingPanel;
        so.FindProperty("_hostButton").objectReferenceValue   = hostBtn;
        so.FindProperty("_joinButton").objectReferenceValue   = joinBtn;
        so.FindProperty("_hostCodeInput").objectReferenceValue    = hostCodeInput;
        so.FindProperty("_startHostButton").objectReferenceValue  = startHostBtn;
        so.FindProperty("_hostBackButton").objectReferenceValue   = hostBackBtn;
        so.FindProperty("_hostErrorText").objectReferenceValue    = hostErrorText;
        so.FindProperty("_joinCodeInput").objectReferenceValue     = joinCodeInput;
        so.FindProperty("_joinConfirmButton").objectReferenceValue = joinConfirmBtn;
        so.FindProperty("_joinBackButton").objectReferenceValue    = joinBackBtn;
        so.FindProperty("_joinErrorText").objectReferenceValue     = joinErrorText;
        so.FindProperty("_waitingCodeDisplay").objectReferenceValue = codeDisplay;
        so.FindProperty("_waitingStatusText").objectReferenceValue  = statusText;
        so.FindProperty("_playerCountText").objectReferenceValue    = playerCountText;
        so.FindProperty("_startGameButton").objectReferenceValue    = startGameBtn;
        so.FindProperty("_leaveButton").objectReferenceValue        = leaveBtn;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
