using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo cliente ao atualizar a pose.
    /// </summary>
    [Serializable]
    public sealed class PoseMessage
    {
        public int protocolVersion = 1;
        public string type = "pose";

        public int sequence;
        public PoseData root;
        public PoseData head;
        public PoseData leftHand;
        public PoseData rightHand;

        public bool IsInvalid()
        {
            return root == null || root.position == null || root.rotation == null || head == null || head.position == null || head.rotation == null;
        }
    }
}
