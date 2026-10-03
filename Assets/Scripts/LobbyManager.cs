using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SyncRush
{
    /// <summary>
    /// Manages host/client session lifecycle for Sync Rush.
    ///
    /// Connection: Unity Relay (GDD §11 — test on real Relay from day one, never localhost).
    ///   - Every player signs in to Unity Gaming Services anonymously on first Host/Join.
    ///   - The HOST asks Relay for an allocation; Relay returns a 6-character join code.
    ///   - Clients enter that code; Relay returns the server details to connect through.
    ///   - Both sides then start NGO as before — only the transport setup changes.
    ///   - Codes are normalised to uppercase before use.
    ///
    /// Scene flow:
    ///   LobbyScene  ──[Host starts game]──>  GameScene  (NGO NetworkSceneManager)
    ///   GameScene   ──[Session ends]────────>  LobbyScene
    ///
    /// NetworkManager persists across scenes via DontDestroyOnLoad (NGO default).
    /// </summary>
    public class LobbyManager : MonoBehaviour
    {
        // ── Scene names — must match Build Settings exactly ───────────────────
        public const string LobbySceneName = "LobbyScene";
        public const string GameSceneName  = "GameScene";

        // ── Singleton ─────────────────────────────────────────────────────────
        public static LobbyManager Instance { get; private set; }

        // ── Constants ─────────────────────────────────────────────────────────
        public const int CodeLength = 6;
        public const int MaxPlayers = 4;

        // DTLS: encrypted UDP, supported on every desktop platform. ("wss" would be needed for WebGL.)
        private const string RelayConnectionType = "dtls";
        private const string ValidChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        // NGO only invokes OnClientConnectedCallback on a non-host client for its
        // OWN LocalClientId — it is never told about other peers joining or
        // leaving. So ConnectedClientIds is only ever accurate on the server
        // (host). PlayerCount is instead pushed to every peer explicitly via a
        // named message whenever the server's roster changes, so it stays
        // correct on joining clients too.
        private const string PlayerCountMessageName = "SyncRush_PlayerCount";

        // ── State ─────────────────────────────────────────────────────────────
        public string CurrentCode { get; private set; } = string.Empty;
        public bool IsHost { get; private set; }

        /// <summary>True while a Host/Join request is waiting on Unity Services or Relay.</summary>
        public bool IsBusy { get; private set; }

        // Bumped by every Host/Join/Cancel. An async request that finishes after the player
        // cancelled (or started another) sees a stale ID and drops its result.
        private int _attemptId;
        public List<ulong> ConnectedClientIds { get; private set; } = new();
        public int PlayerCount { get; private set; }

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string> OnHostStarted;          // code
        public event Action OnClientConnected;
        public event Action<string> OnConnectionFailed;     // reason (join side)
        public event Action<string> OnHostFailed;           // reason (host side)
        public event Action<ulong> OnPlayerJoined;          // clientId
        public event Action<ulong> OnPlayerLeft;            // clientId
        public event Action OnSessionEnded;
        public event Action<int> OnPlayerCountChanged;      // authoritative count, all peers

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
        /// Create a Relay allocation and start as host. Relay generates the join code,
        /// reported through OnHostStarted; failures through OnHostFailed.
        /// </summary>
        public async void StartHost()
        {
            if (IsBusy) return;
            int attempt = BeginAttempt();

            try
            {
                await EnsureSignedInAsync();
                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(MaxPlayers - 1);
                string code = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                if (attempt != _attemptId) return; // cancelled while waiting; the unused allocation just expires

                Transport.SetRelayServerData(allocation.ToRelayServerData(RelayConnectionType));
                CurrentCode = NormaliseCode(code);
                IsHost = true;

                SubscribeToNetworkManager();
                if (!NetworkManager.Singleton.StartHost())
                {
                    Debug.LogError("[LobbyManager] StartHost failed — NetworkManager refused to start (already listening?).");
                    UnsubscribeFromNetworkManager();
                    IsHost = false;
                    OnHostFailed?.Invoke("Failed to start host. Please try again.");
                    return;
                }
                RegisterPlayerCountMessageHandler();

                OnHostStarted?.Invoke(CurrentCode);
                Debug.Log($"[LobbyManager] Host started with Relay code: {CurrentCode}");
            }
            catch (Exception e)
            {
                if (attempt != _attemptId) return;
                Debug.LogException(e);
                IsHost = false;
                OnHostFailed?.Invoke(DescribeServiceError(e, joining: false));
            }
            finally
            {
                EndAttempt(attempt);
            }
        }

        /// <summary>
        /// Look up a Relay join code and connect to that host.
        /// </summary>
        public async void JoinAsClient(string code)
        {
            if (IsBusy) return;
            string normalised = NormaliseCode(code);

            if (!IsValidCode(normalised))
            {
                OnConnectionFailed?.Invoke("Invalid code. Must be 6 letters or numbers.");
                return;
            }

            int attempt = BeginAttempt();
            try
            {
                await EnsureSignedInAsync();
                JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(normalised);
                if (attempt != _attemptId) return;

                Transport.SetRelayServerData(allocation.ToRelayServerData(RelayConnectionType));
                CurrentCode = normalised;
                IsHost = false;

                SubscribeToNetworkManager();
                if (!NetworkManager.Singleton.StartClient())
                {
                    Debug.LogError("[LobbyManager] StartClient failed — NetworkManager refused to start (already listening?).");
                    UnsubscribeFromNetworkManager();
                    OnConnectionFailed?.Invoke("Failed to connect. Please try again.");
                    return;
                }
                RegisterPlayerCountMessageHandler();

                Debug.Log($"[LobbyManager] Joining with Relay code: {CurrentCode}");
            }
            catch (Exception e)
            {
                if (attempt != _attemptId) return;
                Debug.LogException(e);
                OnConnectionFailed?.Invoke(DescribeServiceError(e, joining: true));
            }
            finally
            {
                EndAttempt(attempt);
            }
        }

        /// <summary>
        /// Cancels an in-progress connection attempt (e.g. the user clicked Join then
        /// Back before it resolved) without a full Disconnect+scene reload. Safe to
        /// call even when nothing is in progress.
        /// </summary>
        public void CancelPendingConnection()
        {
            _attemptId++;
            IsBusy = false;
            ResetStaleSession();
            CurrentCode = string.Empty;
            IsHost = false;
        }

        /// <summary>
        /// Shuts down any still-listening NetworkManager left over from a previous,
        /// abandoned attempt. Without this, StartHost/StartClient silently no-op —
        /// NGO refuses to start while already listening — leaving IsHost/CurrentCode
        /// claiming a session exists when the transport never actually started it.
        /// </summary>
        private void ResetStaleSession()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
            {
                UnsubscribeFromNetworkManager();
                nm.Shutdown();
            }
        }

        /// <summary>
        /// Host-only: load the GameScene for all connected clients.
        /// NGO's NetworkSceneManager synchronises the load across all peers.
        /// </summary>
        public void StartGame()
        {
            if (!IsHost)
            {
                Debug.LogWarning("[LobbyManager] StartGame called on a non-host client — ignored.");
                return;
            }

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost)
            {
                Debug.LogError("[LobbyManager] NetworkManager is not running as host.");
                return;
            }

            Debug.Log($"[LobbyManager] Loading {GameSceneName} for all clients.");
            NetworkManager.Singleton.SceneManager.LoadScene(GameSceneName, LoadSceneMode.Single);
        }

        /// <summary>
        /// Disconnect and reset state, then return to LobbyScene.
        /// </summary>
        public void Disconnect()
        {
            _attemptId++;
            IsBusy = false;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                NetworkManager.Singleton.Shutdown();

            CurrentCode = string.Empty;
            IsHost = false;
            ConnectedClientIds.Clear();
            OnSessionEnded?.Invoke();

            // Return to lobby scene locally
            SceneManager.LoadScene(LobbySceneName);

            Debug.Log("[LobbyManager] Disconnected.");
        }

        // ── Relay / Unity Services ────────────────────────────────────────────

        private static UnityTransport Transport =>
            (UnityTransport)NetworkManager.Singleton.NetworkConfig.NetworkTransport;

        private int BeginAttempt()
        {
            ResetStaleSession();
            IsBusy = true;
            return ++_attemptId;
        }

        private void EndAttempt(int attempt)
        {
            if (attempt == _attemptId) IsBusy = false;
        }

        /// <summary>
        /// Initialises Unity Services and signs in anonymously, once per run. Relay rejects
        /// unauthenticated requests.
        /// </summary>
        private static async Task EnsureSignedInAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                options.SetProfile(AuthProfileName());
                await UnityServices.InitializeAsync(options);
            }
            else
            {
                while (UnityServices.State == ServicesInitializationState.Initializing)
                    await Task.Yield();
            }

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        /// <summary>
        /// Anonymous sign-in is cached per profile, and every Editor instance on a machine
        /// shares one PlayerPrefs store. Without separate profiles, a Multiplayer Play Mode
        /// virtual player signs in as the same player as the main Editor. Virtual players run
        /// from Library/VP/[id], so key the profile off that folder.
        /// </summary>
        private static string AuthProfileName()
        {
            const string vpMarker = "/Library/VP/";
            string dataPath = Application.dataPath.Replace('\\', '/');
            int i = dataPath.IndexOf(vpMarker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "main";

            string id = dataPath.Substring(i + vpMarker.Length).Split('/')[0];
            var profile = new System.Text.StringBuilder("vp_");
            foreach (char c in id)
                if (char.IsLetterOrDigit(c) && profile.Length < 30) // profiles: alphanumeric/-/_, max 30
                    profile.Append(c);
            return profile.ToString();
        }

        private static string DescribeServiceError(Exception e, bool joining)
        {
            if (e is RelayServiceException relay)
            {
                switch (relay.Reason)
                {
                    case RelayExceptionReason.JoinCodeNotFound:
                    case RelayExceptionReason.EntityNotFound:
                    case RelayExceptionReason.InvalidRequest when joining:
                        return "No session found for that code.";
                    case RelayExceptionReason.InactiveProject:
                    case RelayExceptionReason.Forbidden:
                        return "Relay isn't enabled for this project (Unity Cloud dashboard).";
                    default:
                        return $"Relay error: {relay.Message}";
                }
            }
            if (e is RequestFailedException) // includes AuthenticationException
                return $"Couldn't reach Unity services: {e.Message}";
            return "Something went wrong connecting. Please try again.";
        }

        // ── NetworkManager event wiring ───────────────────────────────────────

        private void SubscribeToNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            UnsubscribeFromNetworkManager(); // guard against double-subscribing if called twice
            nm.OnClientConnectedCallback  += HandleClientConnected;
            nm.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        private void UnsubscribeFromNetworkManager()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;

            nm.OnClientConnectedCallback  -= HandleClientConnected;
            nm.OnClientDisconnectCallback -= HandleClientDisconnected;
            nm.CustomMessagingManager?.UnregisterNamedMessageHandler(PlayerCountMessageName);
        }

        /// <summary>
        /// CustomMessagingManager only exists once NetworkManager.StartHost/StartClient
        /// has run (it's created in NGO's internal Initialize()), so this must be
        /// called AFTER that — unlike the connect/disconnect callbacks above, which
        /// must be subscribed BEFORE Start*, since the host's own connection fires
        /// synchronously from within StartHost().
        /// </summary>
        private void RegisterPlayerCountMessageHandler()
        {
            NetworkManager.Singleton.CustomMessagingManager
                .RegisterNamedMessageHandler(PlayerCountMessageName, HandlePlayerCountMessage);
        }

        /// <summary>
        /// Server-only: pushes the authoritative connected-player count to every
        /// peer (including itself, via NGO's host loopback). Call whenever the
        /// server's roster changes.
        /// </summary>
        private void BroadcastPlayerCount()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            // The server trusts its own count directly rather than round-tripping
            // through the message loopback — the host's own connection event fires
            // synchronously inside StartHost(), before RegisterPlayerCountMessageHandler()
            // has run, so a self-addressed message sent that early would be dropped
            // (no handler registered yet to receive it).
            PlayerCount = ConnectedClientIds.Count;
            OnPlayerCountChanged?.Invoke(PlayerCount);

            if (nm.CustomMessagingManager == null) return;
            using var writer = new FastBufferWriter(sizeof(int), Unity.Collections.Allocator.Temp);
            writer.WriteValueSafe(PlayerCount);
            nm.CustomMessagingManager.SendNamedMessageToAll(PlayerCountMessageName, writer);
        }

        private void HandlePlayerCountMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            PlayerCount = count;
            OnPlayerCountChanged?.Invoke(count);
        }

        private void HandleClientConnected(ulong clientId)
        {
            if (!ConnectedClientIds.Contains(clientId))
                ConnectedClientIds.Add(clientId);

            if (!IsHost && clientId == NetworkManager.Singleton.LocalClientId)
                OnClientConnected?.Invoke();

            OnPlayerJoined?.Invoke(clientId);
            BroadcastPlayerCount();
            Debug.Log($"[LobbyManager] Player joined: {clientId}");
        }

        private void HandleClientDisconnected(ulong clientId)
        {
            ConnectedClientIds.Remove(clientId);
            OnPlayerLeft?.Invoke(clientId);
            BroadcastPlayerCount();

            if (!IsHost && clientId == NetworkManager.Singleton.LocalClientId)
                OnConnectionFailed?.Invoke("Disconnected from host.");

            Debug.Log($"[LobbyManager] Player left: {clientId}");
        }
    }
}
