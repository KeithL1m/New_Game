using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace SyncRush
{
    /// <summary>
    /// Manages host/client session lifecycle for Sync Rush.
    ///
    /// Join code design:
    ///   - The HOST creates a 6-character alphanumeric code of their choice.
    ///   - Clients enter that exact code to join.
    ///   - Codes are normalised to uppercase before storage and comparison.
    ///   - No auto-generation — the host owns the code.
    ///
    /// This is a localhost/LAN implementation. Unity Relay integration
    /// (internet play) is a Pre-Work task and will wrap this class.
    /// </summary>
    public class LobbyManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static LobbyManager Instance { get; private set; }

        // ── Constants ─────────────────────────────────────────────────────────
        public const int CodeLength = 6;
        private const string ValidChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // ── State ─────────────────────────────────────────────────────────────
        public string CurrentCode { get; private set; } = string.Empty;
        public bool IsHost { get; private set; }
        public List<ulong> ConnectedClientIds { get; private set; } = new();

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string> OnHostStarted;          // code
        public event Action OnClientConnected;
        public event Action<string> OnConnectionFailed;     // reason
        public event Action<ulong> OnPlayerJoined;          // clientId
        public event Action<ulong> OnPlayerLeft;            // clientId
        public event Action OnSessionEnded;

        // ── Unity ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            UnsubscribeFromNetworkManager();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Validates a candidate join code.
        /// Returns true if it is exactly CodeLength alphanumeric characters.
        /// </summary>
        public static bool IsValidCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            string upper = code.Trim().ToUpper();
            if (upper.Length != CodeLength) return false;
            foreach (char c in upper)
                if (ValidChars.IndexOf(c) < 0) return false;
            return true;
        }

        /// <summary>
        /// Normalises a code to uppercase with whitespace stripped.
        /// </summary>
        public static string NormaliseCode(string code)
            => code?.Trim().ToUpper() ?? string.Empty;

        /// <summary>
        /// Start as host with the given code.
        /// The code must pass IsValidCode before calling this.
        /// </summary>
        public void StartHost(string code)
        {
            CurrentCode = NormaliseCode(code);
            IsHost = true;

            SubscribeToNetworkManager();
            NetworkManager.Singleton.StartHost();

            OnHostStarted?.Invoke(CurrentCode);
            Debug.Log($"[LobbyManager] Host started with code: {CurrentCode}");
        }

        /// <summary>
        /// Attempt to join a session using the given code.
        /// For LAN this connects directly; Relay wrapping comes in Pre-Work.
        /// </summary>
        public void JoinAsClient(string code)
        {
            string normalised = NormaliseCode(code);

            if (!IsValidCode(normalised))
            {
                OnConnectionFailed?.Invoke("Invalid code. Must be 6 letters or numbers.");
                return;
            }

            CurrentCode = normalised;
            IsHost = false;

            SubscribeToNetworkManager();
            NetworkManager.Singleton.StartClient();

            Debug.Log($"[LobbyManager] Joining with code: {CurrentCode}");
        }

        /// <summary>
        /// Disconnect and reset state.
        /// </summary>
        public void Disconnect()
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.Shutdown();

            CurrentCode = string.Empty;
            IsHost = false;
            ConnectedClientIds.Clear();
            OnSessionEnded?.Invoke();

            Debug.Log("[LobbyManager] Disconnected.");
        }

        // ── NetworkManager event wiring ───────────────────────────────────────

        private void SubscribeToNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            nm.OnClientConnectedCallback    += HandleClientConnected;
            nm.OnClientDisconnectCallback   += HandleClientDisconnected;
        }

        private void UnsubscribeFromNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            nm.OnClientConnectedCallback    -= HandleClientConnected;
            nm.OnClientDisconnectCallback   -= HandleClientDisconnected;
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (!ConnectedClientIds.Contains(clientId))
                ConnectedClientIds.Add(clientId);

            // If this is the local client connecting as a non-host
            if (!IsHost && clientId == NetworkManager.Singleton.LocalClientId)
                OnClientConnected?.Invoke();

            OnPlayerJoined?.Invoke(clientId);
            Debug.Log($"[LobbyManager] Player joined: {clientId}");
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            ConnectedClientIds.Remove(clientId);
            OnPlayerLeft?.Invoke(clientId);

            // If we got disconnected as a client, treat it as a failed connection
            if (!IsHost && clientId == NetworkManager.Singleton.LocalClientId)
                OnConnectionFailed?.Invoke("Disconnected from host.");

            Debug.Log($"[LobbyManager] Player left: {clientId}");
        }
    }
}
