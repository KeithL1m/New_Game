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
    ///   - Both see a "LEAVE" button.
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
        [SerializeField] private TextMeshProUGUI _waitingStatusText;
        [SerializeField] private TextMeshProUGUI _playerCountText;
        [SerializeField] private Button _startGameButton;   // host-only
        [SerializeField] private Button _leaveButton;

        private TextMeshProUGUI _startHostLabel;
        private TextMeshProUGUI _joinConfirmLabel;
        private string _startHostIdleText;
        private string _joinConfirmIdleText;

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
            _leaveButton.onClick.AddListener(OnLeaveClicked);
        }

        private void OnEnable()
        {
            if (LobbyManager.Instance == null) return;

            LobbyManager.Instance.OnHostStarted      += HandleHostStarted;
            LobbyManager.Instance.OnClientConnected  += HandleClientConnected;
            LobbyManager.Instance.OnConnectionFailed += HandleConnectionFailed;
            LobbyManager.Instance.OnHostFailed       += HandleHostFailed;
            LobbyManager.Instance.OnPlayerJoined     += HandlePlayerJoined;
            LobbyManager.Instance.OnPlayerLeft       += HandlePlayerLeft;
            LobbyManager.Instance.OnSessionEnded     += HandleSessionEnded;
            LobbyManager.Instance.OnPlayerCountChanged += HandlePlayerCountChanged;
        }

        private void OnDisable()
        {
            if (LobbyManager.Instance == null) return;

            LobbyManager.Instance.OnHostStarted      -= HandleHostStarted;
            LobbyManager.Instance.OnClientConnected  -= HandleClientConnected;
            LobbyManager.Instance.OnConnectionFailed -= HandleConnectionFailed;
            LobbyManager.Instance.OnHostFailed       -= HandleHostFailed;
            LobbyManager.Instance.OnPlayerJoined     -= HandlePlayerJoined;
            LobbyManager.Instance.OnPlayerLeft       -= HandlePlayerLeft;
            LobbyManager.Instance.OnSessionEnded     -= HandleSessionEnded;
            LobbyManager.Instance.OnPlayerCountChanged -= HandlePlayerCountChanged;
        }

        private void Start()
        {
            ShowMainPanel();
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
            // Host sees Start Game button, clients do not
            _startGameButton.gameObject.SetActive(true);
            ShowWaitingPanel();
        }

        private void HandleClientConnected()
        {
            SetJoinBusy(false);
            _waitingCodeDisplay.text = $"Code: <b>{LobbyManager.Instance.CurrentCode}</b>";
            _waitingStatusText.text  = "Waiting for host to start...";
            UpdatePlayerCount();
            // Clients never see the Start Game button
            _startGameButton.gameObject.SetActive(false);
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
        private void ShowWaitingPanel() => SetActivePanel(_waitingPanel);

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
            _playerCountText.text = $"Players: {count} / {LobbyManager.MaxPlayers}";
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
