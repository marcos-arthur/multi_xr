using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo servidor à todos os clientes.
    /// </summary>
    [Serializable]
    public sealed class PlayerPoseMessage
    {
        public int protocolVersion = 1;
        public string type = "player_pose";

        public string playerId;
        public int sequence;
        public PoseData root;
        public PoseData head;
        public PoseData leftHand;
        public PoseData rightHand;
    }
}