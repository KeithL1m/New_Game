using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SyncRush
{
    /// <summary>
    /// Drives the Sync Rush lobby canvas.
    ///
    /// Panel flow:
    ///   MainPanel  ──[Host]──>  HostPanel  ──[Start Lobby]──>  WaitingPanel
    ///              ──[Join]──>  JoinPanel  ──[Join]─────────>  WaitingPanel
    ///
    /// Relay generates the join code when the host starts the lobby; the host
    /// no longer types one. Both Start Lobby and Join wait on network requests,
    /// so their buttons show progress and lock until the request resolves.
    ///
    /// WaitingPanel:
    ///   - Host sees a "START GAME" button — calls LobbyManager.StartGame()
    ///     which uses NGO NetworkSceneManager to load GameScene for everyone.
    ///   - Clients see "Waiting for host to start..." — no start button.
    ///   - Both see a "LEAVE" button, and a "COPY" button that puts the code on the clipboard
    ///     so it can be pasted to friends.
    ///   - Host sees a Mode dropdown (GameModes.All); everyone sees the mode after the player count.
    ///   - In a mode where players choose teams (Teams), one column per team with a JOIN button and
    ///     the team's players. Joining players are auto-assigned (NetworkPlayer); the buttons let
    ///     them switch if there's room. Otherwise (Free-for-all) a single list of players in their colour.
    ///
    /// Scene separation:
    ///   This canvas lives in LobbyScene only. GameScene has no lobby UI.
    ///   NGO keeps NetworkManager alive across the scene load via DontDestroyOnLoad.
    /// </summary>
    public class LobbyUI : MonoBehaviour
    {
        // ── Panel references ──────────────────────────────────────────────────
        [Header("Panels")]
        [SerializeField] private GameObject _mainPanel;
        [SerializeField] private GameObject _hostPanel;
        [SerializeField] private GameObject _joinPanel;
        [SerializeField] private GameObject _waitingPanel;

        // ── Main panel ────────────────────────────────────────────────────────
        [Header("Main Panel")]
        [SerializeField] private Button _hostButton;
        [SerializeField] private Button _joinButton;

        // ── Host panel ────────────────────────────────────────────────────────
        [Header("Host Panel")]
        [SerializeField] private Button _startHostButton;
        [SerializeField] private Button _hostBackButton;
        [SerializeField] private TextMeshProUGUI _hostErrorText;

        // ── Join panel ────────────────────────────────────────────────────────
        [Header("Join Panel")]
        [SerializeField] private TMP_InputField _joinCodeInput;
        [SerializeField] private Button _joinConfirmButton;
        [SerializeField] private Button _joinBackButton;
        [SerializeField] private TextMeshProUGUI _joinErrorText;

        // ── Waiting panel ─────────────────────────────────────────────────────
        [Header("Waiting Panel")]
        [SerializeField] private TextMeshProUGUI _waitingCodeDisplay;
        [SerializeField] private Button _copyCodeButton;
        [SerializeField] private TextMeshProUGUI _waitingStatusText;
        [SerializeField] private TextMeshProUGUI _playerCountText;
        [SerializeField] private Button _startGameButton;   // host-only
        [SerializeField] private Button _leaveButton;

        [Header("Mode & teams")]
        [Tooltip("Host only: picks GameModes.All[index].")]
        [SerializeField] private TMP_Dropdown _modeDropdown;
        [Tooltip("Index = team id (0 blue, 1 red). Shown in modes where players choose teams.")]
        [SerializeField] private Button[] _teamButtons;
        [SerializeField] private TextMeshProUGUI[] _teamLists;
        [Tooltip("Shown instead of the team columns in modes where the server assigns teams (Free-for-all).")]
        [SerializeField] private TextMeshProUGUI _playerList;

        private TextMeshProUGUI _startHostLabel;
        private TextMeshProUGUI _joinConfirmLabel;
        private string _startHostIdleText;
        private string _joinConfirmIdleText;

        private const float CopiedFeedbackSeconds = 1.5f;
        private TextMeshProUGUI _copyCodeLabel;
        private string _copyCodeIdleText;

        // ── Unity ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            // Main panel
            _hostButton.onClick.AddListener(ShowHostPanel);
            _joinButton.onClick.AddListener(ShowJoinPanel);

            // Host panel
            _startHostButton.onClick.AddListener(OnStartHostClicked);
            _hostBackButton.onClick.AddListener(OnHostBackClicked);
            _startHostLabel = _startHostButton.GetComponentInChildren<TextMeshProUGUI>();
            _joinConfirmLabel = _joinConfirmButton.GetComponentInChildren<TextMeshProUGUI>();
            if (_startHostLabel != null) _startHostIdleText = _startHostLabel.text;
            if (_joinConfirmLabel != null) _joinConfirmIdleText = _joinConfirmLabel.text;

            // Join panel
            _joinConfirmButton.onClick.AddListener(OnJoinClicked);
            _joinBackButton.onClick.AddListener(OnJoinBackClicked);
            _joinCodeInput.onValueChanged.AddListener(val =>
            {
                string upper = val.ToUpper();
                if (upper != val) _joinCodeInput.SetTextWithoutNotify(upper);
                if (upper.Length > LobbyManager.CodeLength)
                    _joinCodeInput.SetTextWithoutNotify(upper[..LobbyManager.CodeLength]);
            });

            // Waiting panel
            _startGameButton.onClick.AddListener(OnStartGameClicked);
            _copyCodeButton.onClick.AddListener(OnCopyCodeClicked);
            _copyCodeLabel = _copyCodeButton.GetComponentInChildren<TextMeshProUGUI>();
            if (_copyCodeLabel != null) _copyCodeIdleText = _copyCodeLabel.text;
            _leaveButton.onClick.AddListener(OnLeaveClicked);

            for (int i = 0; i < _teamButtons.Length; i++)
            {
                int team = i;
                _teamButtons[i].onClick.AddListener(() => NetworkPlayer.Local?.RequestTeam(team));
            }

            _modeDropdown.ClearOptions();
            _modeDropdown.AddOptions(System.Array.ConvertAll(GameModes.All, m => m.DisplayName).ToList());
            _modeDropdown.onValueChanged.AddListener(i => LobbyManager.Instance.SetMode(GameModes.All[i].Id));
        }

        // Unity doesn't order Awake/OnEnable across objects, so LobbyUI.OnEnable can run before
        // LobbyManager.Awake has set Instance. Subscribing only in OnEnable then silently missed
        // every event (Start Lobby succeeded but the UI never left the Host panel). Start always
        // runs after every Awake, so it retries; _subscribedTo stops a double subscription.
        private LobbyManager _subscribedTo;

        private void OnEnable()
        {
            Subscribe();
            NetworkPlayer.OnRosterChanged += RefreshTeams;
            GameModes.OnChanged += RefreshTeams;
        }

        private void Start()
        {
            Subscribe();
            ShowMainPanel();
        }

        private void Subscribe()
        {
            var lm = LobbyManager.Instance;
            if (_subscribedTo != null || lm == null) return;

            lm.OnHostStarted        += HandleHostStarted;
            lm.OnClientConnected    += HandleClientConnected;
            lm.OnConnectionFailed   += HandleConnectionFailed;
            lm.OnHostFailed         += HandleHostFailed;
            lm.OnPlayerJoined       += HandlePlayerJoined;
            lm.OnPlayerLeft         += HandlePlayerLeft;
            lm.OnSessionEnded       += HandleSessionEnded;
            lm.OnPlayerCountChanged += HandlePlayerCountChanged;
            _subscribedTo = lm;
        }

        private void OnDisable()
        {
            NetworkPlayer.OnRosterChanged -= RefreshTeams;
            GameModes.OnChanged -= RefreshTeams;

            var lm = _subscribedTo;
            if (lm == null) return;

            lm.OnHostStarted        -= HandleHostStarted;
            lm.OnClientConnected    -= HandleClientConnected;
            lm.OnConnectionFailed   -= HandleConnectionFailed;
            lm.OnHostFailed         -= HandleHostFailed;
            lm.OnPlayerJoined       -= HandlePlayerJoined;
            lm.OnPlayerLeft         -= HandlePlayerLeft;
            lm.OnSessionEnded       -= HandleSessionEnded;
            lm.OnPlayerCountChanged -= HandlePlayerCountChanged;
            _subscribedTo = null;
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnStartHostClicked()
        {
            ClearHostError();
            SetHostBusy(true);
            LobbyManager.Instance.StartHost();
        }

        private void OnHostBackClicked()
        {
            // Cancel a Start Lobby still waiting on Relay, same as Join's Back.
            LobbyManager.Instance.CancelPendingConnection();
            ShowMainPanel();
        }

        private void OnJoinClicked()
        {
            string code = _joinCodeInput.text.Trim().ToUpper();
            if (!LobbyManager.IsValidCode(code))
            {
                ShowJoinError($"Code must be exactly {LobbyManager.CodeLength} letters or numbers.");
                return;
            }
            ClearJoinError();
            SetJoinBusy(true);
            LobbyManager.Instance.JoinAsClient(code);
        }

        private void OnStartGameClicked()
        {
            // Only the host can start — button is hidden for clients anyway
            LobbyManager.Instance.StartGame();
        }

        private void OnCopyCodeClicked()
        {
            string code = LobbyManager.Instance.CurrentCode;
            if (string.IsNullOrEmpty(code)) return;

            GUIUtility.systemCopyBuffer = code;
            if (_copyCodeLabel == null) return;
            _copyCodeLabel.text = "COPIED!";
            CancelInvoke(nameof(ResetCopyLabel));
            Invoke(nameof(ResetCopyLabel), CopiedFeedbackSeconds);
        }

        private void ResetCopyLabel()
        {
            if (_copyCodeLabel != null) _copyCodeLabel.text = _copyCodeIdleText;
        }

        private void OnLeaveClicked()
        {
            LobbyManager.Instance.Disconnect();
        }

        private void OnJoinBackClicked()
        {
            // A Join attempt starts connecting immediately on click, so leaving this
            // panel before it resolves must cancel it — otherwise the NetworkManager
            // is left listening as a client and the next Host attempt silently fails.
            LobbyManager.Instance.CancelPendingConnection();
            ShowMainPanel();
        }

        // ── LobbyManager event handlers ───────────────────────────────────────

        private void HandleHostStarted(string code)
        {
            SetHostBusy(false);
            _waitingCodeDisplay.text = $"Your Code: <b>{code}</b>";
            _waitingStatusText.text  = "Waiting for players...";
            UpdatePlayerCount();
            // Host sees Start Game button and picks the mode, clients do not
            _startGameButton.gameObject.SetActive(true);
            _modeDropdown.gameObject.SetActive(true);
            ShowWaitingPanel();
        }

        private void HandleClientConnected()
        {
            SetJoinBusy(false);
            _waitingCodeDisplay.text = $"Code: <b>{LobbyManager.Instance.CurrentCode}</b>";
            _waitingStatusText.text  = "Waiting for host to start...";
            UpdatePlayerCount();
            // Clients never see the Start Game button or the mode picker
            _startGameButton.gameObject.SetActive(false);
            _modeDropdown.gameObject.SetActive(false);
            ShowWaitingPanel();
        }

        private void HandleConnectionFailed(string reason)
        {
            SetJoinBusy(false);
            ShowJoinError(reason);
        }

        private void HandleHostFailed(string reason)
        {
            SetHostBusy(false);
            ShowHostError(reason);
        }

        private void HandlePlayerJoined(ulong clientId)
        {
            UpdatePlayerCount();
            if (LobbyManager.Instance.IsHost)
                _waitingStatusText.text = $"Player {clientId} joined. Waiting for more...";
        }

        private void HandlePlayerLeft(ulong clientId)
        {
            UpdatePlayerCount();
        }

        private void HandlePlayerCountChanged(int count)
        {
            UpdatePlayerCount();
        }

        private void HandleSessionEnded()
        {
            ShowMainPanel();
        }

        // ── Panel switching ───────────────────────────────────────────────────

        private void ShowMainPanel()
        {
            SetActivePanel(_mainPanel);
            _joinCodeInput.text = string.Empty;
            SetHostBusy(false);
            SetJoinBusy(false);
            ClearHostError();
            ClearJoinError();
        }

        private void ShowHostPanel()    => SetActivePanel(_hostPanel);
        private void ShowJoinPanel()    => SetActivePanel(_joinPanel);
        private void ShowWaitingPanel()
        {
            SetActivePanel(_waitingPanel);
            RefreshTeams();
        }

        private void SetActivePanel(GameObject active)
        {
            _mainPanel.SetActive(active    == _mainPanel);
            _hostPanel.SetActive(active    == _hostPanel);
            _joinPanel.SetActive(active    == _joinPanel);
            _waitingPanel.SetActive(active == _waitingPanel);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdatePlayerCount()
        {
            int count = LobbyManager.Instance.PlayerCount;
            _playerCountText.text = $"Players: {count} / {LobbyManager.MaxPlayers}  ·  {NetworkPlayer.ModeLabel}";
        }

        /// <summary>
        /// Shows the current mode's lobby: team columns with JOIN buttons if players choose teams
        /// (a JOIN button is enabled only if this player could switch: not already on it, and it
        /// has room; the server re-checks), otherwise one list of players in their colours. Also
        /// syncs the host's dropdown and the mode shown next to the player count.
        /// </summary>
        private void RefreshTeams()
        {
            if (LobbyManager.Instance != null) UpdatePlayerCount();

            var mode = GameModes.Current;
            _modeDropdown.SetValueWithoutNotify(System.Array.IndexOf(GameModes.All, mode));

            bool columns = mode.PlayersChooseTeams;
            _playerList.gameObject.SetActive(!columns);
            for (int team = 0; team < _teamButtons.Length; team++)
            {
                bool show = columns && team < mode.TeamCount;
                _teamButtons[team].gameObject.SetActive(show);
                if (team < _teamLists.Length) _teamLists[team].gameObject.SetActive(show);
            }

            var local = NetworkPlayer.Local;
            if (!columns)
            {
                var list = new System.Text.StringBuilder();
                foreach (var p in NetworkPlayer.All)
                {
                    if (p.TeamId < 0 || p.TeamId >= NetworkPlayer.TeamColors.Length) continue;
                    string hex = ColorUtility.ToHtmlStringRGB(NetworkPlayer.TeamColors[p.TeamId]);
                    list.Append($"<color=#{hex}>{p.DisplayName}{(p == local ? " (you)" : "")}</color>\n");
                }
                _playerList.text = list.Length > 0 ? list.ToString().TrimEnd() : "-";
                return;
            }

            var names = new System.Text.StringBuilder();
            for (int team = 0; team < _teamLists.Length && team < mode.TeamCount; team++)
            {
                names.Clear();
                foreach (var p in NetworkPlayer.All)
                {
                    if (p.TeamId != team) continue;
                    names.Append(p.DisplayName);
                    if (p == local) names.Append(" (you)");
                    names.Append('\n');
                }
                _teamLists[team].text = names.Length > 0 ? names.ToString().TrimEnd() : "-";

                if (team >= _teamButtons.Length) continue;
                bool onTeam = local != null && local.TeamId == team;
                bool full = NetworkPlayer.CountOnTeam(team) >= mode.MaxTeamSize;
                _teamButtons[team].interactable = local != null && !onTeam && !full;

                var label = _teamButtons[team].GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                    label.text = onTeam ? $"{NetworkPlayer.TeamNames[team]} (YOU)"
                               : full   ? $"{NetworkPlayer.TeamNames[team]} (FULL)"
                                        : $"JOIN {NetworkPlayer.TeamNames[team]}";
            }
        }

        private void SetHostBusy(bool busy)
            => SetBusy(_startHostButton, _startHostLabel, busy ? "CREATING..." : _startHostIdleText, busy);

        private void SetJoinBusy(bool busy)
            => SetBusy(_joinConfirmButton, _joinConfirmLabel, busy ? "CONNECTING..." : _joinConfirmIdleText, busy);

        private static void SetBusy(Button button, TextMeshProUGUI label, string text, bool busy)
        {
            button.interactable = !busy;
            if (label != null && text != null) label.text = text;
        }

        private void ShowHostError(string msg)  => SetError(_hostErrorText, msg);
        private void ClearHostError()           => SetError(_hostErrorText, string.Empty);
        private void ShowJoinError(string msg)  => SetError(_joinErrorText, msg);
        private void ClearJoinError()           => SetError(_joinErrorText, string.Empty);

        private static void SetError(TextMeshProUGUI label, string msg)
        {
            if (label == null) return;
            label.text = msg;
            label.gameObject.SetActive(!string.IsNullOrEmpty(msg));
        }
    }
}
