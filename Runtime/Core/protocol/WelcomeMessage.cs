using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo servidor quando o cliente entrar em uma sala.
    /// </summary>
    [Serializable]
    public sealed class WelcomeMessage
    {
        public int protocolVersion = 1;
        public string type = "welcome";

        public string sessionId;
        public string selfId;
        public string role;

        public PlayerData[] players;
    }
}