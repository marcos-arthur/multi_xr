using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo cliente ao entrar em uma sala.
    /// </summary>
    [Serializable]
    public sealed class JoinMessage
    {
        public int protocolVersion = 1;
        public string type = "join";

        public string roomId;
        public string role;
        public string displayName;
        public string displayColor;
    }
}