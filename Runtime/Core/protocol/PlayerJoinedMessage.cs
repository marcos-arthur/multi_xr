using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo servidor quando um cliente entra em uma sala.
    /// </summary>
    [Serializable]
    public sealed class PlayerJoinedMessage
    {
        public int protocolVersion = 1;
        public string type = "player_joined";

        public string playerId;
        public string role;
        public string displayName;
        public string displayColor;
    }
}