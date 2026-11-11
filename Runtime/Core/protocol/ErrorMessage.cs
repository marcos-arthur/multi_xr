using System;

namespace com.imd.ackit.multi_xr.Protocol
{
    /// <summary>
    /// Mensagem enviada pelo servidor quando um erro ocorre.
    /// </summary>
    [Serializable]
    public sealed class ErrorMessage
    {
        public int protocolVersion = 1;
        public string type = "error";

        public string code;
        public string message;
    }
}