using com.imd.ackit.multi_xr.Protocol;
using UnityEngine;

namespace com.imd.ackit.multi_xr.XR
{
    /// <summary>Maps protocol poses onto the rig transforms of one remote avatar prefab.</summary>
    [DisallowMultipleComponent]
    public sealed class RemotePlayerAvatar : MonoBehaviour
    {
        [Header("Rig remoto")]
        [SerializeField] private Transform root;
        [SerializeField] private Transform head;
        [SerializeField] private Transform leftHand;
        [SerializeField] private Transform rightHand;

        [Header("Suavização")]
        [Min(0f)]
        [SerializeField] private float smoothing = 15f;
        [Min(0f)]
        [SerializeField] private float teleportDistance = 2f;

        private Transform networkOrigin;
        private bool hasReceivedPose;

        private readonly PoseTarget rootTarget = new();
        private readonly PoseTarget headTarget = new();
        private readonly PoseTarget leftHandTarget = new();
        private readonly PoseTarget rightHandTarget = new();

        public string PlayerId { get; private set; }
        public string DisplayName { get; private set; }

        internal bool Initialize(PlayerData player, Transform sharedOrigin)
        {
            if (player == null || string.IsNullOrWhiteSpace(player.playerId))
            {
                Debug.LogError("Remote avatar requires a player ID.", this);
                return false;
            }

            if (sharedOrigin == null)
            {
                Debug.LogError("Remote avatar requires the session Network Origin.", this);
                return false;
            }

            Vector3 originScale = sharedOrigin.lossyScale;
            if (Mathf.Abs(originScale.x - 1f) > 0.001f ||
                Mathf.Abs(originScale.y - 1f) > 0.001f ||
                Mathf.Abs(originScale.z - 1f) > 0.001f)
            {
                Debug.LogError("Network Origin and its parents must have unit scale (1, 1, 1).", this);
                return false;
            }

            if (root == null || head == null)
            {
                Debug.LogError("Remote avatar prefab must assign Root and Head transforms.", this);
                return false;
            }

            PlayerId = player.playerId;
            DisplayName = player.displayName;
            networkOrigin = sharedOrigin;

            rootTarget.transform = root;
            headTarget.transform = head;
            leftHandTarget.transform = leftHand;
            rightHandTarget.transform = rightHand;
            return true;
        }

        internal bool ApplyPose(PlayerPoseMessage message)
        {
            if (message == null || networkOrigin == null)
                return false;

            bool snap = !hasReceivedPose;
            if (!SetTarget(rootTarget, message.root, true, snap) ||
                !SetTarget(headTarget, message.head, true, snap))
                return false;

            // A null hand means tracking is unavailable; retain its last known pose.
            SetTarget(leftHandTarget, message.leftHand, false, snap);
            SetTarget(rightHandTarget, message.rightHand, false, snap);

            hasReceivedPose = true;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            return true;
        }

        private bool SetTarget(PoseTarget target, PoseData pose, bool required, bool snap)
        {
            if (pose == null)
                return !required;

            if (target.transform == null || pose.position == null || pose.rotation == null)
                return !required;

            Vector3 relativePosition = new(pose.position.x, pose.position.y, pose.position.z);
            Quaternion relativeRotation = new(
                pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w);

            if (!IsFinite(relativePosition.x) || !IsFinite(relativePosition.y) ||
                !IsFinite(relativePosition.z) || !IsFinite(relativeRotation.x) ||
                !IsFinite(relativeRotation.y) || !IsFinite(relativeRotation.z) ||
                !IsFinite(relativeRotation.w) || QuaternionMagnitudeSquared(relativeRotation) < 1e-12f)
                return !required;

            relativeRotation = relativeRotation.normalized;
            Vector3 worldPosition = networkOrigin.position + networkOrigin.rotation * relativePosition;
            Quaternion worldRotation = networkOrigin.rotation * relativeRotation;

            bool firstPose = !target.hasPose;
            bool largeJump = (target.transform.position - worldPosition).sqrMagnitude >
                             teleportDistance * teleportDistance;

            target.position = worldPosition;
            target.rotation = worldRotation;
            target.hasPose = true;

            if (snap || firstPose || largeJump)
            {
                target.transform.SetPositionAndRotation(worldPosition, worldRotation);
                target.isMoving = false;
            }
            else
            {
                target.isMoving = true;
            }

            return true;
        }

        private void Update()
        {
            if (!hasReceivedPose)
                return;

            float blend = smoothing <= 0f
                ? 1f
                : 1f - Mathf.Exp(-smoothing * Time.deltaTime);

            Smooth(rootTarget, blend);
            Smooth(headTarget, blend);
            Smooth(leftHandTarget, blend);
            Smooth(rightHandTarget, blend);
        }

        private static void Smooth(PoseTarget target, float blend)
        {
            if (!target.isMoving || target.transform == null)
                return;

            target.transform.position = Vector3.Lerp(target.transform.position, target.position, blend);
            target.transform.rotation = Quaternion.Slerp(target.transform.rotation, target.rotation, blend);

            if ((target.transform.position - target.position).sqrMagnitude < 1e-6f &&
                Quaternion.Angle(target.transform.rotation, target.rotation) < 0.1f)
            {
                target.transform.SetPositionAndRotation(target.position, target.rotation);
                target.isMoving = false;
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static float QuaternionMagnitudeSquared(Quaternion value) =>
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;

        private sealed class PoseTarget
        {
            public Transform transform;
            public Vector3 position;
            public Quaternion rotation;
            public bool hasPose;
            public bool isMoving;
        }
    }
}
