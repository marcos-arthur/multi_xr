using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Envelope de mensagem da aplicação. O servidor preenche playerId ao encaminhar;
    /// to é usado pelo cliente como destinatário opcional no envio.
    /// </summary>
    [Serializable]
    public sealed class AppMessage
    {
        public int protocolVersion = 1;
        public string type = "app";

        public string playerId;
        public string channel;
        public string payload;
        /// <summary>Destinatário opcional no envio cliente → servidor.</summary>
        public string to;
    }
}
