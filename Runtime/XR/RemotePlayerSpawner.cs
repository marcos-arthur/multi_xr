using System.Collections.Generic;
using com.imd.ackit.multi_xr.CustomWebSocket;
using com.imd.ackit.multi_xr.Protocol;
using UnityEngine;

namespace com.imd.ackit.multi_xr.XR
{
    /// <summary>Spawns and removes remote quest avatars from session presence and pose events.</summary>
    [DisallowMultipleComponent]
    public sealed class RemotePlayerSpawner : MonoBehaviour
    {
        [Header("Sessão")]
        [SerializeField] private WebSocketSessionClient sessionClient;

        [Header("Avatar remoto")]
        [SerializeField] private RemotePlayerAvatar avatarPrefab;
        [SerializeField] private Transform avatarParent;

        private readonly Dictionary<string, RemotePlayerAvatar> avatars = new();

        private void Awake()
        {
            if (sessionClient == null)
                sessionClient = GetComponent<WebSocketSessionClient>();

            if (sessionClient == null)
            {
                Debug.LogError("RemotePlayerSpawner requires a WebSocketSessionClient reference.", this);
                enabled = false;
                return;
            }

            sessionClient.WelcomeReceived += OnWelcomeReceived;
            sessionClient.ParticipantJoined += OnParticipantJoined;
            sessionClient.ParticipantLeft += OnParticipantLeft;
            sessionClient.PoseReceived += OnPoseReceived;
            sessionClient.ConnectionFailed.AddListener(ClearAvatars);
            sessionClient.Disconnected.AddListener(ClearAvatars);
        }

        private void OnDestroy()
        {
            if (sessionClient != null)
            {
                sessionClient.WelcomeReceived -= OnWelcomeReceived;
                sessionClient.ParticipantJoined -= OnParticipantJoined;
                sessionClient.ParticipantLeft -= OnParticipantLeft;
                sessionClient.PoseReceived -= OnPoseReceived;
                sessionClient.ConnectionFailed.RemoveListener(ClearAvatars);
                sessionClient.Disconnected.RemoveListener(ClearAvatars);
            }

            ClearAvatars();
        }

        private void OnWelcomeReceived(WelcomeMessage welcome)
        {
            ClearAvatars();

            if (welcome?.players == null)
                return;

            foreach (PlayerData player in welcome.players)
                SpawnAvatar(player);
        }

        private void OnParticipantJoined(PlayerJoinedMessage participant)
        {
            if (participant == null)
                return;

            SpawnAvatar(new PlayerData
            {
                playerId = participant.playerId,
                role = participant.role,
                displayName = participant.displayName,
                displayColor = participant.displayColor
            });
        }

        private void OnParticipantLeft(PlayerLeftMessage participant)
        {
            if (participant == null || string.IsNullOrWhiteSpace(participant.playerId))
                return;

            if (!avatars.TryGetValue(participant.playerId, out RemotePlayerAvatar avatar))
                return;

            avatars.Remove(participant.playerId);
            if (avatar != null)
                Destroy(avatar.gameObject);
        }

        private void OnPoseReceived(PlayerPoseMessage pose)
        {
            if (pose == null || string.IsNullOrWhiteSpace(pose.playerId))
                return;

            if (avatars.TryGetValue(pose.playerId, out RemotePlayerAvatar avatar) && avatar != null)
                avatar.ApplyPose(pose);
        }

        private void SpawnAvatar(PlayerData player)
        {
            // Desktop viewers have no avatar in protocol v1.
            if (player == null || player.role != "quest_player" ||
                string.IsNullOrWhiteSpace(player.playerId) || avatars.ContainsKey(player.playerId))
                return;

            if (avatarPrefab == null)
            {
                Debug.LogError("Assign a RemotePlayerAvatar prefab to RemotePlayerSpawner.", this);
                return;
            }

            Transform origin = sessionClient.NetworkOrigin;
            if (origin == null)
            {
                Debug.LogError("WebSocketSessionClient has no Network Origin assigned.", this);
                return;
            }

            Transform parent = avatarParent != null ? avatarParent : transform;
            RemotePlayerAvatar avatar = Instantiate(avatarPrefab, parent, false);
            avatar.gameObject.SetActive(false);

            if (!avatar.Initialize(player, origin))
            {
                Destroy(avatar.gameObject);
                return;
            }

            avatars.Add(player.playerId, avatar);
        }

        private void ClearAvatars()
        {
            foreach (RemotePlayerAvatar avatar in avatars.Values)
            {
                if (avatar != null)
                    Destroy(avatar.gameObject);
            }

            avatars.Clear();
        }
    }
}
