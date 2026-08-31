using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SyncRush
{
    /// <summary>
    /// Drives the Sync Rush lobby canvas.
    ///
    /// Panel flow:
    ///   MainPanel  ──[Host]──>  HostPanel  ──[Start Host]──>  WaitingPanel
    ///              ──[Join]──>  JoinPanel  ──[Join]────────>  WaitingPanel
    ///
    /// Any panel has a Back button that returns to MainPanel.
    /// WaitingPanel has a Leave button that calls LobbyManager.Disconnect().
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
        [SerializeField] private TMP_InputField _hostCodeInput;
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
        [SerializeField] private Button _leaveButton;

        // ── Unity ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            // Main panel buttons
            _hostButton.onClick.AddListener(ShowHostPanel);
            _joinButton.onClick.AddListener(ShowJoinPanel);

            // Host panel buttons
            _startHostButton.onClick.AddListener(OnStartHostClicked);
            _hostBackButton.onClick.AddListener(ShowMainPanel);

            // Enforce uppercase + max length on host code input
            _hostCodeInput.onValueChanged.AddListener(val =>
            {
                string upper = val.ToUpper();
                if (upper != val) _hostCodeInput.SetTextWithoutNotify(upper);
                if (upper.Length > LobbyManager.CodeLength)
                    _hostCodeInput.SetTextWithoutNotify(upper[..LobbyManager.CodeLength]);
            });

            // Join panel buttons
            _joinConfirmButton.onClick.AddListener(OnJoinClicked);
            _joinBackButton.onClick.AddListener(ShowMainPanel);

            // Enforce uppercase + max length on join code input
            _joinCodeInput.onValueChanged.AddListener(val =>
            {
                string upper = val.ToUpper();
                if (upper != val) _joinCodeInput.SetTextWithoutNotify(upper);
                if (upper.Length > LobbyManager.CodeLength)
                    _joinCodeInput.SetTextWithoutNotify(upper[..LobbyManager.CodeLength]);
            });

            // Waiting panel
            _leaveButton.onClick.AddListener(OnLeaveClicked);
        }

        private void OnEnable()
        {
            if (LobbyManager.Instance == null) return;

            LobbyManager.Instance.OnHostStarted      += HandleHostStarted;
            LobbyManager.Instance.OnClientConnected  += HandleClientConnected;
            LobbyManager.Instance.OnConnectionFailed += HandleConnectionFailed;
            LobbyManager.Instance.OnPlayerJoined     += HandlePlayerJoined;
            LobbyManager.Instance.OnPlayerLeft       += HandlePlayerLeft;
            LobbyManager.Instance.OnSessionEnded     += HandleSessionEnded;
        }

        private void OnDisable()
        {
            if (LobbyManager.Instance == null) return;

            LobbyManager.Instance.OnHostStarted      -= HandleHostStarted;
            LobbyManager.Instance.OnClientConnected  -= HandleClientConnected;
            LobbyManager.Instance.OnConnectionFailed -= HandleConnectionFailed;
            LobbyManager.Instance.OnPlayerJoined     -= HandlePlayerJoined;
            LobbyManager.Instance.OnPlayerLeft       -= HandlePlayerLeft;
            LobbyManager.Instance.OnSessionEnded     -= HandleSessionEnded;
        }

        private void Start()
        {
            ShowMainPanel();
        }

        // ── Button handlers ───────────────────────────────────────────────────

        private void OnStartHostClicked()
        {
            string code = _hostCodeInput.text.Trim().ToUpper();

            if (!LobbyManager.IsValidCode(code))
            {
                ShowHostError($"Code must be exactly {LobbyManager.CodeLength} letters or numbers.");
                return;
            }

            ClearHostError();
            LobbyManager.Instance.StartHost(code);
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
            _joinConfirmButton.interactable = false;
            LobbyManager.Instance.JoinAsClient(code);
        }

        private void OnLeaveClicked()
        {
            LobbyManager.Instance.Disconnect();
        }

        // ── LobbyManager event handlers ───────────────────────────────────────

        private void HandleHostStarted(string code)
        {
            _waitingCodeDisplay.text = $"Your Code: <b>{code}</b>";
            _waitingStatusText.text  = "Waiting for players...";
            UpdatePlayerCount();
            ShowWaitingPanel();
        }

        private void HandleClientConnected()
        {
            _waitingCodeDisplay.text = $"Code: <b>{LobbyManager.Instance.CurrentCode}</b>";
            _waitingStatusText.text  = "Connected! Waiting for host to start...";
            UpdatePlayerCount();
            ShowWaitingPanel();
        }

        /// <summary>
        /// Hides the entire lobby canvas once the race starts.
        /// Called by RaceStateMachine when transitioning out of Lobby state.
        /// </summary>
        public void HideCanvas() => gameObject.SetActive(false);

        /// <summary>
        /// Re-shows the lobby canvas (e.g. returning to main menu after a race).
        /// </summary>
        public void ShowCanvas()
        {
            gameObject.SetActive(true);
            ShowMainPanel();
        }

        private void HandleConnectionFailed(string reason)
        {
            _joinConfirmButton.interactable = true;
            ShowJoinError(reason);
        }

        private void HandlePlayerJoined(ulong clientId)
        {
            UpdatePlayerCount();
            if (LobbyManager.Instance.IsHost)
                _waitingStatusText.text = $"Player {clientId} joined.";
        }

        private void HandlePlayerLeft(ulong clientId)
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
            _hostCodeInput.text = string.Empty;
            _joinCodeInput.text = string.Empty;
            _joinConfirmButton.interactable = true;
            ClearHostError();
            ClearJoinError();
        }

        private void ShowHostPanel()   => SetActivePanel(_hostPanel);
        private void ShowJoinPanel()   => SetActivePanel(_joinPanel);
        private void ShowWaitingPanel() => SetActivePanel(_waitingPanel);

        private void SetActivePanel(GameObject active)
        {
            _mainPanel.SetActive(active == _mainPanel);
            _hostPanel.SetActive(active == _hostPanel);
            _joinPanel.SetActive(active == _joinPanel);
            _waitingPanel.SetActive(active == _waitingPanel);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void UpdatePlayerCount()
        {
            int count = LobbyManager.Instance.ConnectedClientIds.Count;
            _playerCountText.text = $"Players: {count} / 4";
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
