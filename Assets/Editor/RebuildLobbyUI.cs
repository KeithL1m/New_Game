using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using SyncRush;

public class RebuildLobbyUI
{
    public static void Execute()
    {
        // ── Clean up existing LobbyCanvas ────────────────────────────────────
        var existing = GameObject.Find("LobbyCanvas");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
            Debug.Log("Removed old LobbyCanvas.");
        }

        // ── EventSystem ──────────────────────────────────────────────────────
        if (Object.FindFirstObjectByType<EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            Undo.RegisterCreatedObjectUndo(esGO, "Create EventSystem");
        }
        else
        {
            // Ensure StandaloneInputModule is replaced with InputSystemUIInputModule
            var es = Object.FindFirstObjectByType<EventSystem>();
            var oldModule = es.GetComponent<StandaloneInputModule>();
            if (oldModule != null)
                Object.DestroyImmediate(oldModule);
            if (es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() == null)
                es.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        // ── Find Liberation Sans SDF (non-fallback) ──────────────────────────
        TMP_FontAsset font = null;
        foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var candidate = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (candidate != null && !path.Contains("Fallback"))
            {
                font = candidate;
                break;
            }
        }
        if (font == null)
        {
            Debug.LogError("No non-fallback TMP_FontAsset found. Import TMP Essentials first.");
            return;
        }
        Debug.Log($"Using font: {font.name} at {AssetDatabase.GetAssetPath(font)}");

        // ── Root Canvas ──────────────────────────────────────────────────────
        var canvasGO = new GameObject("LobbyCanvas");
        Undo.RegisterCreatedObjectUndo(canvasGO, "Create LobbyCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGO.AddComponent<GraphicRaycaster>();

        // ── LobbyManager ─────────────────────────────────────────────────────
        var nm = GameObject.Find("NetworkManager");
        if (nm != null && nm.GetComponent<LobbyManager>() == null)
            Undo.AddComponent<LobbyManager>(nm);

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════

        TextMeshProUGUI MakeTMP(string name, Transform parent, string text,
                                float fontSize, Color color, Vector2 pos, Vector2 size,
                                TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = font;
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
            EditorUtility.SetDirty(go);
            return tmp;
        }

        GameObject MakePanel(string name, Transform parent, Color bg, bool active = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = bg;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            go.SetActive(active);
            EditorUtility.SetDirty(go);
            return go;
        }

        Button MakeButton(string name, Transform parent, string label,
                          Vector2 pos, Vector2 size, Color btnColor)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = btnColor;
            var btn = go.AddComponent<Button>();
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            // Label child — stretch to fill button
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(go.transform, false);
            var tmp = labelGO.AddComponent<TextMeshProUGUI>();
            tmp.font = font;
            tmp.text = label;
            tmp.fontSize = 28;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            var lrt = labelGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            EditorUtility.SetDirty(go);
            return btn;
        }

        TMP_InputField MakeInputField(string name, Transform parent,
                                      string placeholder, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.15f, 0.15f, 0.2f);
            var field = go.AddComponent<TMP_InputField>();
            field.characterLimit = LobbyManager.CodeLength;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            // Text Area
            var areaGO = new GameObject("Text Area");
            areaGO.transform.SetParent(go.transform, false);
            areaGO.AddComponent<RectMask2D>();
            var art = areaGO.GetComponent<RectTransform>();
            art.anchorMin = Vector2.zero;
            art.anchorMax = Vector2.one;
            art.offsetMin = new Vector2(10, 4);
            art.offsetMax = new Vector2(-10, -4);

            // Placeholder
            var phGO = new GameObject("Placeholder");
            phGO.transform.SetParent(areaGO.transform, false);
            var phTMP = phGO.AddComponent<TextMeshProUGUI>();
            phTMP.font = font;
            phTMP.text = placeholder;
            phTMP.fontSize = 28;
            phTMP.color = new Color(0.5f, 0.5f, 0.5f);
            phTMP.alignment = TextAlignmentOptions.Center;
            phTMP.enableWordWrapping = false;
            var phrt = phGO.GetComponent<RectTransform>();
            phrt.anchorMin = Vector2.zero;
            phrt.anchorMax = Vector2.one;
            phrt.offsetMin = phrt.offsetMax = Vector2.zero;

            // Input Text
            var inputGO = new GameObject("Text");
            inputGO.transform.SetParent(areaGO.transform, false);
            var inputTMP = inputGO.AddComponent<TextMeshProUGUI>();
            inputTMP.font = font;
            inputTMP.fontSize = 28;
            inputTMP.color = Color.white;
            inputTMP.alignment = TextAlignmentOptions.Center;
            inputTMP.enableWordWrapping = false;
            var irt = inputGO.GetComponent<RectTransform>();
            irt.anchorMin = Vector2.zero;
            irt.anchorMax = Vector2.one;
            irt.offsetMin = irt.offsetMax = Vector2.zero;

            field.textViewport = art;
            field.textComponent = inputTMP;
            field.placeholder = phTMP;
            EditorUtility.SetDirty(go);
            return field;
        }

        TextMeshProUGUI MakeErrorLabel(string name, Transform parent, Vector2 pos)
        {
            var tmp = MakeTMP(name, parent, "", 20, new Color(1f, 0.35f, 0.35f),
                              pos, new Vector2(600, 36));
            tmp.gameObject.SetActive(false);
            return tmp;
        }

        Color bg = new Color(0.08f, 0.08f, 0.12f, 0.97f);
        Color blue = new Color(0.2f, 0.6f, 1f);
        Color grey = new Color(0.25f, 0.25f, 0.3f);
        Color red  = new Color(0.75f, 0.18f, 0.18f);
        Color white = Color.white;

        // ════════════════════════════════════════════════════════════════════
        // MAIN PANEL
        // ════════════════════════════════════════════════════════════════════
        var mainPanel = MakePanel("MainPanel", canvasGO.transform, bg);
        MakeTMP("Title",    mainPanel.transform, "SYNC RUSH",            72, white, new Vector2(0, 160),  new Vector2(800, 110));
        MakeTMP("Subtitle", mainPanel.transform, "A physics comedy race", 26, new Color(0.7f,0.7f,0.7f), new Vector2(0, 90), new Vector2(600, 44));
        var hostBtn = MakeButton("HostButton", mainPanel.transform, "HOST GAME", new Vector2(0, -20),  new Vector2(320, 70), blue);
        var joinBtn = MakeButton("JoinButton", mainPanel.transform, "JOIN GAME", new Vector2(0, -110), new Vector2(320, 70), blue);

        // ════════════════════════════════════════════════════════════════════
        // HOST PANEL
        // ════════════════════════════════════════════════════════════════════
        var hostPanel = MakePanel("HostPanel", canvasGO.transform, bg, false);
        MakeTMP("HostTitle",        hostPanel.transform, "CREATE LOBBY",                          48, white, new Vector2(0, 160), new Vector2(600, 70));
        MakeTMP("HostInstructions", hostPanel.transform, "Start a lobby to get a 6-character join code.\nShare it with your friends.", 22, new Color(0.7f,0.7f,0.7f), new Vector2(0, 80), new Vector2(700, 70));
        var hostErrorText  = MakeErrorLabel("HostErrorText",  hostPanel.transform, new Vector2(0, -68));
        var startHostBtn   = MakeButton("StartHostButton",    hostPanel.transform, "START LOBBY", new Vector2(0, -150), new Vector2(320, 70), blue);
        var hostBackBtn    = MakeButton("HostBackButton",     hostPanel.transform, "BACK",        new Vector2(0, -240), new Vector2(200, 55), grey);

        // ════════════════════════════════════════════════════════════════════
        // JOIN PANEL
        // ════════════════════════════════════════════════════════════════════
        var joinPanel = MakePanel("JoinPanel", canvasGO.transform, bg, false);
        MakeTMP("JoinTitle",        joinPanel.transform, "JOIN LOBBY",                              48, white, new Vector2(0, 160), new Vector2(600, 70));
        MakeTMP("JoinInstructions", joinPanel.transform, "Enter the 6-character code from your host.", 22, new Color(0.7f,0.7f,0.7f), new Vector2(0, 90), new Vector2(700, 44));
        var joinCodeInput  = MakeInputField("JoinCodeInput",   joinPanel.transform, "Enter code",  new Vector2(0, 10),   new Vector2(400, 65));
        var joinErrorText  = MakeErrorLabel("JoinErrorText",   joinPanel.transform, new Vector2(0, -48));
        var joinConfirmBtn = MakeButton("JoinConfirmButton",   joinPanel.transform, "JOIN",         new Vector2(0, -130), new Vector2(320, 70), blue);
        var joinBackBtn    = MakeButton("JoinBackButton",      joinPanel.transform, "BACK",         new Vector2(0, -220), new Vector2(200, 55), grey);

        // ════════════════════════════════════════════════════════════════════
        // WAITING PANEL
        // ════════════════════════════════════════════════════════════════════
        var waitingPanel = MakePanel("WaitingPanel", canvasGO.transform, bg, false);
        MakeTMP("WaitingTitle",   waitingPanel.transform, "LOBBY",                  48, white,                    new Vector2(0, 200), new Vector2(400, 70));
        var codeDisplay     = MakeTMP("CodeDisplay",    waitingPanel.transform, "Your Code: ------", 34, white,   new Vector2(0, 120), new Vector2(700, 55));
        var statusText      = MakeTMP("StatusText",     waitingPanel.transform, "Waiting for players...", 22, new Color(0.7f,0.7f,0.7f), new Vector2(0, 55), new Vector2(700, 40));
        var playerCountText = MakeTMP("PlayerCountText",waitingPanel.transform, "Players: 0 / 4",  28, white,    new Vector2(0, 0),   new Vector2(400, 50));
        var startGameBtn    = MakeButton("StartGameButton", waitingPanel.transform, "START GAME",  new Vector2(0, -80),  new Vector2(320, 70), new Color(0.1f, 0.7f, 0.3f));
        var leaveBtn        = MakeButton("LeaveButton", waitingPanel.transform, "LEAVE LOBBY",     new Vector2(0, -165), new Vector2(280, 60), red);

        // ── Wire LobbyUI ──────────────────────────────────────────────────────
        var lobbyUI = canvasGO.AddComponent<LobbyUI>();
        var so = new SerializedObject(lobbyUI);

        so.FindProperty("_mainPanel").objectReferenceValue    = mainPanel;
        so.FindProperty("_hostPanel").objectReferenceValue    = hostPanel;
        so.FindProperty("_joinPanel").objectReferenceValue    = joinPanel;
        so.FindProperty("_waitingPanel").objectReferenceValue = waitingPanel;

        so.FindProperty("_hostButton").objectReferenceValue   = hostBtn;
        so.FindProperty("_joinButton").objectReferenceValue   = joinBtn;

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

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

        Debug.Log("Lobby UI rebuilt successfully.");
    }
}
