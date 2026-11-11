using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo servidor quando um cliente sai de uma sala.
    /// </summary>
    [Serializable]
    public sealed class PlayerLeftMessage
    {
        public int protocolVersion = 1;
        public string type = "player_left";

        public string playerId;
        public string reason;
    }
}